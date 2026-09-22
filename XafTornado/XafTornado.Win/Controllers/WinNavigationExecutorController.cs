using System;
using System.Linq;
using System.Windows.Forms;
using DevExpress.Data.Filtering;
using DevExpress.ExpressApp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XafTornado.Module.Services;
using XafTornado.Win.Services;

namespace XafTornado.Win.Controllers
{
    /// <summary>
    /// WinForms WindowController that subscribes to <see cref="WinNavigationService"/>
    /// events and executes navigation/filter/save/close requests on the UI thread.
    /// </summary>
    public class WinNavigationExecutorController : WindowController
    {
        private const string AiFilterKey = "AIFilter";
        private WinNavigationService _navService;
        private ILogger _logger;
        private Control _uiControl;

        public WinNavigationExecutorController()
        {
            TargetWindowType = WindowType.Main;
        }

        protected override void OnActivated()
        {
            base.OnActivated();
            _logger = Application.ServiceProvider.GetService<ILogger<WinNavigationExecutorController>>();
            _navService = Application.ServiceProvider.GetService<INavigationService>() as WinNavigationService;

            _logger?.LogInformation("[WinNavExecutor] Activated. NavService={HasNav}, Template={TemplateType}",
                _navService != null, Window.Template?.GetType().Name);

            if (_navService == null) return;

            if (Window.Template is Control ctl)
            {
                _uiControl = ctl;
            }
            else
            {
                Window.TemplateChanged += OnTemplateChanged;
            }

            _logger?.LogInformation("[WinNavExecutor] UIControl={HasControl}", _uiControl != null);

            _navService.OnNavigationRequested += OnNavigationRequested;
            _navService.OnFilterRequested += OnFilterRequested;
            _navService.OnRefreshRequested += OnRefreshRequested;
            _navService.OnSaveRequested += OnSaveRequested;
            _navService.OnCloseRequested += OnCloseRequested;
        }

        private void OnTemplateChanged(object sender, EventArgs e)
        {
            if (Window.Template is Control ctl)
            {
                _uiControl = ctl;
                Window.TemplateChanged -= OnTemplateChanged;
                _logger?.LogInformation("[WinNavExecutor] UIControl set via TemplateChanged: {Type}", ctl.GetType().Name);
            }
        }

        protected override void OnDeactivated()
        {
            Window.TemplateChanged -= OnTemplateChanged;
            if (_navService != null)
            {
                _navService.OnNavigationRequested -= OnNavigationRequested;
                _navService.OnFilterRequested -= OnFilterRequested;
                _navService.OnRefreshRequested -= OnRefreshRequested;
                _navService.OnSaveRequested -= OnSaveRequested;
                _navService.OnCloseRequested -= OnCloseRequested;
                _navService = null;
            }
            _uiControl = null;
            base.OnDeactivated();
        }

        private void DispatchToUI(Action action, [System.Runtime.CompilerServices.CallerMemberName] string caller = null)
        {
            _logger?.LogInformation("[WinNavExecutor] DispatchToUI from {Caller}, UIControl={HasControl}, InvokeRequired={InvokeReq}",
                caller, _uiControl != null, _uiControl?.IsHandleCreated == true && _uiControl.InvokeRequired);

            if (_uiControl != null && _uiControl.InvokeRequired)
            {
                _uiControl.BeginInvoke(action);
            }
            else
            {
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
                    _logger?.LogInformation("[WinNavExecutor] Navigation: {Entity} / {Key}", request.EntityName, request.KeyValue);
                    ExecuteNavigation(request);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[WinNavExecutor] Navigation failed");
                }
            }
        }

        private void ExecuteNavigation(NavigationRequest request)
        {
            var schemaService = Application.ServiceProvider.GetRequiredService<SchemaDiscoveryService>();
            var entityInfo = schemaService.Schema.FindEntity(request.EntityName);
            if (entityInfo == null)
            {
                _logger?.LogWarning("[WinNavExecutor] Entity '{Entity}' not found", request.EntityName);
                return;
            }

            var entityType = entityInfo.ClrType;
            var os = Application.CreateObjectSpace(entityType);
            var shown = false;
            try
            {
                if (string.IsNullOrEmpty(request.KeyValue))
                {
                    var listViewId = Application.FindListViewId(entityType);
                    if (listViewId == null)
                    {
                        _logger?.LogWarning("[WinNavExecutor] No ListView ID for {Entity}", request.EntityName);
                        return;
                    }
                    var listView = Application.CreateListView(
                        listViewId,
                        Application.CreateCollectionSource(os, entityType, listViewId),
                        true);
                    Application.ShowViewStrategy.ShowViewFromCommonView(listView);
                    shown = true;
                }
                else
                {
                    object obj = null;

                    if (Guid.TryParse(request.KeyValue, out var guidKey))
                        obj = os.GetObjectByKey(entityType, guidKey);

                    if (obj == null)
                    {
                        var (match, candidates) = DisplayText.Resolve(os.GetObjects(entityType).Cast<object>(), request.KeyValue);
                        if (match == null && candidates.Count > 1)
                        {
                            _logger?.LogWarning("[WinNavExecutor] '{Key}' is ambiguous for {Entity}; not navigating", request.KeyValue, request.EntityName);
                            return;
                        }
                        obj = match;
                    }

                    if (obj == null)
                    {
                        _logger?.LogWarning("[WinNavExecutor] No {Entity} matching '{Key}'", request.EntityName, request.KeyValue);
                        return;
                    }

                    var detailView = Application.CreateDetailView(os, obj);
                    Application.ShowViewStrategy.ShowViewFromCommonView(detailView);
                    shown = true;
                }
            }
            finally
            {
                if (!shown) os.Dispose(); // AI-009: the view owns the ObjectSpace only once it is shown
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
                    ExecuteFilter(request);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[WinNavExecutor] Filter failed");
                }
            }
        }

        private void ExecuteFilter(FilterRequest request)
        {
            _logger?.LogInformation("[WinNavExecutor] ExecuteFilter: {Criteria}", request.CriteriaString ?? "(clear)");
            var view = Application.MainWindow?.View as ListView;
            if (view == null)
            {
                var activeViewContext = Application.ServiceProvider.GetService<ActiveViewContext>();
                view = activeViewContext?.ActiveFrame?.View as ListView;
            }
            if (view == null)
            {
                _logger?.LogWarning("[WinNavExecutor] ExecuteFilter: No active ListView found");
                return;
            }
            _logger?.LogInformation("[WinNavExecutor] ExecuteFilter: Applying to {ViewId}", view.Id);

            if (string.IsNullOrEmpty(request.CriteriaString))
            {
                view.CollectionSource.Criteria.Remove(AiFilterKey);
            }
            else
            {
                view.CollectionSource.Criteria[AiFilterKey] = CriteriaOperator.Parse(request.CriteriaString);
            }
            view.CollectionSource.ResetCollection();
        }

        // -- Refresh / Save / Close ----------------------------------------------------

        private void OnRefreshRequested() => DispatchToUI(() =>
        {
            if (_navService == null || !_navService.ConsumeRefresh()) return;
            var view = GetActiveView();
            if (view == null) return;
            if (view.ObjectSpace.IsModified)
            {
                // Refresh() resets unsaved changes (dxdocs: BaseObjectSpace.Refresh); never on the user's behalf (AI-003).
                _logger?.LogWarning("[WinNavExecutor] Refresh skipped: {ViewId} has unsaved changes", view.Id);
                return;
            }

            view.ObjectSpace.Refresh();
            if (view is ListView lv)
                lv.CollectionSource.ResetCollection();
        });

        private void OnSaveRequested() => DispatchToUI(() =>
        {
            if (_navService == null || !_navService.ConsumeSave()) return;
            GetActiveView()?.ObjectSpace.CommitChanges();
        });

        private void OnCloseRequested() => DispatchToUI(() =>
        {
            if (_navService == null || !_navService.ConsumeClose()) return;
            GetActiveView()?.Close();
        });

        private DevExpress.ExpressApp.View GetActiveView()
        {
            var view = Application.MainWindow?.View;
            if (view != null) return view;
            return Application.ServiceProvider.GetService<ActiveViewContext>()?.ActiveFrame?.View;
        }
    }
}
