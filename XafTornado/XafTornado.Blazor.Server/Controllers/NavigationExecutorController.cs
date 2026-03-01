using System.Threading;
using System.Threading.Tasks;
using DevExpress.Data.Filtering;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Blazor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XafTornado.Blazor.Server.Services;
using XafTornado.Module.Services;

namespace XafTornado.Blazor.Server.Controllers
{
    /// <summary>
    /// Blazor-only WindowController that subscribes to <see cref="BlazorNavigationService"/>
    /// events and executes navigation/filter requests within the XAF UI context.
    /// AI tool calls run on background threads (AI SDK), so we capture the
    /// Blazor circuit's <see cref="SynchronizationContext"/> during activation and
    /// dispatch all UI work through it.
    /// </summary>
    public class NavigationExecutorController : WindowController
    {
        private const string AiFilterKey = "AIFilter";
        private BlazorNavigationService _navService;
        private ILogger _logger;

        public NavigationExecutorController()
        {
            TargetWindowType = WindowType.Main;
        }

        protected override void OnActivated()
        {
            base.OnActivated();
            _logger = Application.ServiceProvider.GetService<ILogger<NavigationExecutorController>>();
            _logger?.LogInformation("[NavExecutor] Activated. BlazorApplication: {IsBlazor}", Application is BlazorApplication);
            _navService = Application.ServiceProvider.GetService<INavigationService>() as BlazorNavigationService;
            if (_navService != null)
            {
                _navService.OnNavigationRequested += OnNavigationRequested;
                _navService.OnFilterRequested += OnFilterRequested;
                _navService.OnRefreshRequested += OnRefreshRequested;
                _navService.OnSaveRequested += OnSaveRequested;
                _navService.OnCloseRequested += OnCloseRequested;
            }
        }

        protected override void OnDeactivated()
        {
            if (_navService != null)
            {
                _navService.OnNavigationRequested -= OnNavigationRequested;
                _navService.OnFilterRequested -= OnFilterRequested;
                _navService.OnRefreshRequested -= OnRefreshRequested;
                _navService.OnSaveRequested -= OnSaveRequested;
                _navService.OnCloseRequested -= OnCloseRequested;
                _navService = null;
            }
            base.OnDeactivated();
        }

        /// <summary>
        /// Dispatches an action to the Blazor circuit thread via BlazorApplication.InvokeAsync,
        /// which restores the XAF ExecutionContext (including ValueManagerContext).
        /// SynchronizationContext.Post alone doesn't restore ValueManagerContext, causing
        /// failures in Application.CreateObjectSpace/ProcessShortcut.
        /// </summary>
        private void DispatchToUI(Action action)
        {
            if (Application is BlazorApplication blazorApp)
            {
                _logger?.LogInformation("[NavExecutor] Dispatching via BlazorApplication.InvokeAsync");
                _ = blazorApp.InvokeAsync(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "[NavExecutor] Error in dispatched action");
                    }
                    return Task.CompletedTask;
                });
            }
            else
            {
                _logger?.LogWarning("[NavExecutor] Not a BlazorApplication — executing directly");
                action();
            }
        }

        // -- Navigation ----------------------------------------------------------------

        private void OnNavigationRequested() => DispatchToUI(ProcessPendingNavigations);

        private void ProcessPendingNavigations()
        {
            while (_navService != null && _navService.TryDequeueNavigation(out var request))
            {
                try
                {
                    _logger?.LogInformation("[NavExecutor] Processing navigation: {Entity} / {Key}", request.EntityName, request.KeyValue);
                    ExecuteNavigation(request);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[NavExecutor] Navigation failed");
                }
            }
        }

        private void ExecuteNavigation(NavigationRequest request)
        {
            var schemaService = Application.ServiceProvider.GetRequiredService<SchemaDiscoveryService>();
            var entityInfo = schemaService.Schema.FindEntity(request.EntityName);
            if (entityInfo == null)
            {
                _logger?.LogWarning("[NavExecutor] Entity '{Entity}' not found in schema", request.EntityName);
                return;
            }

            var entityType = entityInfo.ClrType;

            if (string.IsNullOrEmpty(request.KeyValue))
            {
                // Show a ListView using ShowViewFromCommonView (per DevExpress docs:
                // "Ways to Show a View" > "Show a View from a Custom Context").
                var os = Application.CreateObjectSpace(entityType);
                var listViewId = Application.FindListViewId(entityType);
                if (listViewId == null)
                {
                    _logger?.LogWarning("[NavExecutor] No ListView ID found for {Entity}", request.EntityName);
                    os.Dispose();
                    return;
                }
                _logger?.LogInformation("[NavExecutor] Navigating to ListView {ViewId}", listViewId);
                var listView = Application.CreateListView(
                    listViewId,
                    Application.CreateCollectionSource(os, entityType, listViewId),
                    true);
                Application.ShowViewStrategy.ShowViewFromCommonView(listView);
                _logger?.LogInformation("[NavExecutor] ShowViewFromCommonView completed for ListView");
            }
            else
            {
                var os = Application.CreateObjectSpace(entityType);
                object obj = null;

                if (Guid.TryParse(request.KeyValue, out var guidKey))
                {
                    obj = os.GetObjectByKey(entityType, guidKey);
                    _logger?.LogInformation("[NavExecutor] GUID lookup: {Found}", obj != null);
                }

                if (obj == null)
                {
                    foreach (var item in os.GetObjects(entityType))
                    {
                        if (GetObjectDisplayText(item).IndexOf(request.KeyValue, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            obj = item;
                            break;
                        }
                    }
                    _logger?.LogInformation("[NavExecutor] Text search for '{Key}': {Found}", request.KeyValue, obj != null);
                }

                if (obj == null)
                {
                    _logger?.LogWarning("[NavExecutor] No {Entity} record found matching '{Key}'", request.EntityName, request.KeyValue);
                    os.Dispose();
                    return;
                }

                _logger?.LogInformation("[NavExecutor] Creating DetailView for {Entity}, object={Display}",
                    request.EntityName, GetObjectDisplayText(obj));
                var detailView = Application.CreateDetailView(os, obj);
                Application.ShowViewStrategy.ShowViewFromCommonView(detailView);
                _logger?.LogInformation("[NavExecutor] ShowViewFromCommonView completed for DetailView");
            }
        }

        // -- Filtering -----------------------------------------------------------------

        private void OnFilterRequested() => DispatchToUI(ProcessPendingFilters);

        private void ProcessPendingFilters()
        {
            while (_navService != null && _navService.TryDequeueFilter(out var request))
            {
                try
                {
                    _logger?.LogInformation("[NavExecutor] Processing filter: {Criteria}", request.CriteriaString ?? "(clear)");
                    ExecuteFilter(request);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[NavExecutor] Filter failed");
                }
            }
        }

        private void ExecuteFilter(FilterRequest request)
        {
            // In Blazor XAF, Application.MainWindow.View is always null — the actual
            // views live in nested frames. Use ActiveViewContext.ActiveFrame instead,
            // which is set by ActiveViewTrackingController whenever the user navigates.
            var activeViewContext = Application.ServiceProvider.GetService<ActiveViewContext>();
            var view = activeViewContext?.ActiveFrame?.View as ListView;
            if (view == null)
            {
                _logger?.LogWarning("[NavExecutor] No active ListView found. ActiveFrame={HasFrame}, FrameView={ViewType}",
                    activeViewContext?.ActiveFrame != null, activeViewContext?.ActiveFrame?.View?.GetType().Name);
                return;
            }

            _logger?.LogInformation("[NavExecutor] Applying filter to ListView {ViewId}", view.Id);

            if (string.IsNullOrEmpty(request.CriteriaString))
            {
                // Clear AI filter
                view.CollectionSource.Criteria.Remove(AiFilterKey);
                _logger?.LogInformation("[NavExecutor] Filter cleared");
            }
            else
            {
                // Apply AI filter
                var criteria = CriteriaOperator.Parse(request.CriteriaString);
                view.CollectionSource.Criteria[AiFilterKey] = criteria;
                _logger?.LogInformation("[NavExecutor] Filter applied: {Criteria}", criteria);
            }

            // Force the collection to reload so the Blazor UI reflects the change.
            view.CollectionSource.ResetCollection();
            _logger?.LogInformation("[NavExecutor] Collection reset after filter change");
        }

        // -- Refresh -------------------------------------------------------------------

        private void OnRefreshRequested() => DispatchToUI(ExecuteRefresh);

        private void ExecuteRefresh()
        {
            if (_navService == null || !_navService.ConsumeRefresh()) return;

            var activeViewContext = Application.ServiceProvider.GetService<ActiveViewContext>();
            var view = activeViewContext?.ActiveFrame?.View;
            if (view == null)
            {
                _logger?.LogWarning("[NavExecutor] Refresh: No active view found");
                return;
            }

            _logger?.LogInformation("[NavExecutor] Refreshing active view {ViewId}", view.Id);

            view.ObjectSpace.Refresh();

            if (view is ListView listView)
            {
                listView.CollectionSource.ResetCollection();
                _logger?.LogInformation("[NavExecutor] ListView collection reset after refresh");
            }
            else
            {
                _logger?.LogInformation("[NavExecutor] DetailView ObjectSpace refreshed");
            }
        }

        // -- Save ----------------------------------------------------------------------

        private void OnSaveRequested() => DispatchToUI(ExecuteSave);

        private void ExecuteSave()
        {
            if (_navService == null || !_navService.ConsumeSave()) return;

            var activeViewContext = Application.ServiceProvider.GetService<ActiveViewContext>();
            var view = activeViewContext?.ActiveFrame?.View;
            if (view == null)
            {
                _logger?.LogWarning("[NavExecutor] Save: No active view found");
                return;
            }

            _logger?.LogInformation("[NavExecutor] Saving active view {ViewId}", view.Id);
            view.ObjectSpace.CommitChanges();
            _logger?.LogInformation("[NavExecutor] Save committed for {ViewId}", view.Id);
        }

        // -- Close ---------------------------------------------------------------------

        private void OnCloseRequested() => DispatchToUI(ExecuteClose);

        private void ExecuteClose()
        {
            if (_navService == null || !_navService.ConsumeClose()) return;

            var activeViewContext = Application.ServiceProvider.GetService<ActiveViewContext>();
            var view = activeViewContext?.ActiveFrame?.View;
            if (view == null)
            {
                _logger?.LogWarning("[NavExecutor] Close: No active view found");
                return;
            }

            _logger?.LogInformation("[NavExecutor] Closing active view {ViewId}", view.Id);
            view.Close();
            _logger?.LogInformation("[NavExecutor] View closed: {ViewId}", view.Id);
        }

        // -- Helpers -------------------------------------------------------------------

        private static string GetObjectDisplayText(object obj)
        {
            if (obj == null) return string.Empty;
            var type = obj.GetType();
            foreach (var propName in new[] { "Name", "CompanyName", "Title", "FullName", "FirstName", "Description", "InvoiceNumber" })
            {
                var prop = type.GetProperty(propName);
                if (prop != null)
                {
                    var val = prop.GetValue(obj);
                    if (val != null) return val.ToString();
                }
            }
            return obj.ToString();
        }
    }
}
