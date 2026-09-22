using System;
using System.Linq;
using DevExpress.Data.Filtering;
using DevExpress.ExpressApp;
using Microsoft.Extensions.Logging;

namespace XafTornado.Module.Services
{
    /// <summary>
    /// Runs a <see cref="UiRequest"/> against the application. Platform-agnostic: the Blazor and
    /// WinForms executor controllers only dispatch to their UI thread and call
    /// <see cref="Execute"/>. Every outcome is a <see cref="NavigationResult"/> the tool can relay.
    /// </summary>
    public sealed class UiRequestExecutor
    {
        private const string AiFilterKey = "AIFilter";

        private readonly XafApplication _application;
        private readonly ActiveViewContext _viewContext;
        private readonly SchemaDiscoveryService _schema;
        private readonly ILogger _logger;

        public UiRequestExecutor(XafApplication application, ActiveViewContext viewContext, SchemaDiscoveryService schema, ILogger logger)
        {
            _application = application;
            _viewContext = viewContext;
            _schema = schema;
            _logger = logger;
        }

        /// <summary>Runs a claimed request and completes it; exceptions become a failed result, never an escaped exception.</summary>
        public void Run(UiRequest request)
        {
            try
            {
                _logger?.LogInformation("[UiExecutor] {Kind} {Entity} {Key} {Criteria}", request.Kind, request.EntityName, request.KeyValue, request.Criteria);
                request.Outcome = Execute(request);
            }
            finally
            {
                request.MarkDone();
            }
        }

        public NavigationResult Execute(UiRequest request)
        {
            try
            {
                var result = request.Kind switch
                {
                    UiRequestKind.NavigateToList => NavigateToList(request.EntityName),
                    UiRequestKind.NavigateToDetail => NavigateToDetail(request.EntityName, request.KeyValue),
                    UiRequestKind.Filter => Filter(request.Criteria),
                    UiRequestKind.ClearFilter => Filter(null),
                    UiRequestKind.Refresh => Refresh(),
                    UiRequestKind.Save => Save(),
                    UiRequestKind.Close => Close(),
                    _ => NavigationResult.Fail($"Unknown request '{request.Kind}'."),
                };
                if (!result.Ok)
                    _logger?.LogWarning("[UiExecutor] {Kind} failed: {Error}", request.Kind, result.Error);
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[UiExecutor] {Kind} threw", request.Kind);
                return NavigationResult.Fail(ex.Message);
            }
        }

        // In Blazor, Application.MainWindow.View is always null: the views live in nested frames,
        // tracked by ActiveViewTrackingController. WinForms tracks the same thing.
        private View ActiveView => _viewContext?.ActiveFrame?.View ?? _application.MainWindow?.View;

        private NavigationResult NavigateToList(string entityName)
        {
            var entityInfo = _schema.Schema.FindEntity(entityName ?? "");
            if (entityInfo == null) return NavigationResult.Fail($"Entity '{entityName}' not found.");

            var listViewId = _application.FindListViewId(entityInfo.ClrType);
            if (listViewId == null) return NavigationResult.Fail($"No list view exists for {entityInfo.Name}.");

            var os = _application.CreateObjectSpace(entityInfo.ClrType);
            try
            {
                var listView = _application.CreateListView(listViewId, _application.CreateCollectionSource(os, entityInfo.ClrType, listViewId), true);
                return Show(listView, ref os);
            }
            finally
            {
                os?.Dispose();   // AI-009: nothing else will
            }
        }

        /// <summary>
        /// Shows the view; from here the view owns the ObjectSpace. Known gap: the Blazor tabbed
        /// MDI strategy refuses silently (a warning, no exception, no ViewShown) once its tab limit
        /// is reached, and nothing observable here tells that apart from success, since in Blazor
        /// ViewShown fires after this call returns. That refusal still reads as ok.
        /// </summary>
        private NavigationResult Show(View view, ref IObjectSpace os)
        {
            _application.ShowViewStrategy.ShowViewFromCommonView(view);
            os = null;
            return NavigationResult.Success;
        }

        private NavigationResult NavigateToDetail(string entityName, string keyValue)
        {
            var entityInfo = _schema.Schema.FindEntity(entityName ?? "");
            if (entityInfo == null) return NavigationResult.Fail($"Entity '{entityName}' not found.");

            var os = _application.CreateObjectSpace(entityInfo.ClrType);
            try
            {
                object obj = null;
                if (Guid.TryParse(keyValue, out var guidKey))
                    obj = os.GetObjectByKey(entityInfo.ClrType, guidKey);

                if (obj == null)
                {
                    var (match, candidates) = DisplayText.Resolve(os.GetObjects(entityInfo.ClrType).Cast<object>(), keyValue);
                    if (match == null && candidates.Count > 1)
                        return NavigationResult.Fail($"'{keyValue}' matches {candidates.Count} {entityInfo.Name} records.");
                    obj = match;
                }

                if (obj == null) return NavigationResult.Fail($"No {entityInfo.Name} record found matching '{keyValue}'.");

                var detailView = _application.CreateDetailView(os, obj);
                return Show(detailView, ref os);
            }
            finally
            {
                os?.Dispose();
            }
        }

        private NavigationResult Filter(string criteriaString)
        {
            if (ActiveView is not ListView listView)
                return NavigationResult.Fail("No list view is active.");

            if (string.IsNullOrEmpty(criteriaString))
                listView.CollectionSource.Criteria.Remove(AiFilterKey);
            else
                listView.CollectionSource.Criteria[AiFilterKey] = CriteriaOperator.Parse(criteriaString);

            listView.CollectionSource.ResetCollection();   // so the UI reflects the change
            return NavigationResult.Success;
        }

        private NavigationResult Refresh()
        {
            var view = ActiveView;
            if (view == null) return NavigationResult.Fail("No view is active.");
            // Refresh() resets unsaved changes (dxdocs: BaseObjectSpace.Refresh); never on the user's behalf (AI-003).
            if (view.ObjectSpace.IsModified) return NavigationResult.Fail("The active view has unsaved changes.");

            view.ObjectSpace.Refresh();
            if (view is ListView listView) listView.CollectionSource.ResetCollection();
            return NavigationResult.Success;
        }

        private NavigationResult Save()
        {
            var view = ActiveView;
            if (view == null) return NavigationResult.Fail("No view is active.");
            var os = view.ObjectSpace;
            if (!os.IsModified) return NavigationResult.Fail("There are no unsaved changes.");

            // The view's PersistenceValidationController validates on ObjectSpace.Committing exactly
            // as for the Save action and throws ValidationException on a broken rule; Execute turns
            // that, like a database rejection, into a failed result.
            os.CommitChanges();
            return NavigationResult.Success;
        }

        private NavigationResult Close()
        {
            var view = ActiveView;
            if (view == null) return NavigationResult.Fail("No view is active.");
            return view.Close()
                ? NavigationResult.Success
                : NavigationResult.Fail("The view refused to close (unsaved changes?).");
        }
    }
}
