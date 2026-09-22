using System;
using System.Windows.Forms;
using DevExpress.ExpressApp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XafTornado.Module.Services;

namespace XafTornado.Win.Controllers
{
    /// <summary>
    /// WinForms WindowController: drains the <see cref="NavigationRequestQueue"/> on the UI thread.
    /// Tool bodies already run there (Program.cs sets <see cref="AIToolsProvider.Dispatch"/>), so
    /// requests execute inline and the tool gets the real outcome (AI-007).
    /// </summary>
    public class WinNavigationExecutorController : WindowController
    {
        private NavigationRequestQueue _queue;
        private UiRequestExecutor _executor;
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
            _queue = Application.ServiceProvider.GetService<INavigationService>() as NavigationRequestQueue;

            _logger?.LogInformation("[WinNavExecutor] Activated. Queue={HasQueue}, Template={TemplateType}",
                _queue != null, Window.Template?.GetType().Name);

            if (_queue == null) return;

            if (Window.Template is Control ctl)
                _uiControl = ctl;
            else
                Window.TemplateChanged += OnTemplateChanged;

            _executor = new UiRequestExecutor(Application,
                Application.ServiceProvider.GetService<ActiveViewContext>(),
                Application.ServiceProvider.GetRequiredService<SchemaDiscoveryService>(),
                _logger);
            _queue.OnRequest += OnRequest;
        }

        private void OnTemplateChanged(object sender, EventArgs e)
        {
            if (Window.Template is Control ctl)
            {
                _uiControl = ctl;
                Window.TemplateChanged -= OnTemplateChanged;
            }
        }

        protected override void OnDeactivated()
        {
            Window.TemplateChanged -= OnTemplateChanged;
            if (_queue != null)
            {
                _queue.OnRequest -= OnRequest;
                _queue = null;
                _executor = null;
            }
            _uiControl = null;
            base.OnDeactivated();
        }

        private void OnRequest()
        {
            if (_uiControl != null && _uiControl.IsHandleCreated && _uiControl.InvokeRequired)
                _uiControl.BeginInvoke(Drain);   // off the UI thread: Submit waits for the claimed request, or abandons it first
            else
                Drain();
        }

        private void Drain()
        {
            while (_queue != null && _queue.TryDequeue(out var request))
            {
                _logger?.LogInformation("[WinNavExecutor] {Kind} {Entity} {Key} {Criteria}", request.Kind, request.EntityName, request.KeyValue, request.Criteria);
                _executor.Run(request);
            }
        }
    }
}
