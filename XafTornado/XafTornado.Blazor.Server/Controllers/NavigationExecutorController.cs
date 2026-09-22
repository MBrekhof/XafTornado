using System.Threading.Tasks;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Blazor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XafTornado.Module.Services;

namespace XafTornado.Blazor.Server.Controllers
{
    /// <summary>
    /// Blazor-only WindowController: runs the circuit's <see cref="NavigationRequestQueue"/> on the
    /// circuit's synchronization context and gives <see cref="AIToolsProvider"/> the same dispatcher
    /// for tool bodies. Because tool bodies run on that context, the queue is drained inline
    /// and the tool gets the real outcome (AI-007).
    /// </summary>
    public class NavigationExecutorController : WindowController
    {
        private NavigationRequestQueue _queue;
        private UiRequestExecutor _executor;
        private AIToolsProvider _toolsProvider;
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

            _queue = Application.ServiceProvider.GetService<INavigationService>() as NavigationRequestQueue;
            if (_queue != null)
            {
                _executor = new UiRequestExecutor(Application,
                    Application.ServiceProvider.GetService<ActiveViewContext>(),
                    Application.ServiceProvider.GetRequiredService<SchemaDiscoveryService>(),
                    _logger);
                _queue.OnRequest += OnRequest;
            }

            // Tool bodies run on the circuit's synchronization context, like the executor's own work.
            if (Application is BlazorApplication blazorApp)
            {
                _toolsProvider = Application.ServiceProvider.GetService<AIToolsProvider>();
                if (_toolsProvider != null)
                    _toolsProvider.Dispatch = body => blazorApp.InvokeAsync(body);
            }
        }

        protected override void OnDeactivated()
        {
            if (_toolsProvider != null)
            {
                _toolsProvider.Dispatch = null;
                _toolsProvider = null;
            }
            if (_queue != null)
            {
                _queue.OnRequest -= OnRequest;
                _queue = null;
                _executor = null;
            }
            base.OnDeactivated();
        }

        /// <summary>
        /// BlazorApplication.InvokeAsync restores the XAF ExecutionContext (ValueManagerContext);
        /// SynchronizationContext.Post alone does not. On the circuit's context already (tool bodies
        /// are), it runs synchronously, which is what lets the tool read the outcome.
        /// </summary>
        private void OnRequest()
        {
            if (Application is BlazorApplication blazorApp)
            {
                _ = blazorApp.InvokeAsync(() =>
                {
                    Drain();
                    return Task.CompletedTask;
                });
            }
            else
            {
                Drain();
            }
        }

        private void Drain()
        {
            while (_queue != null && _queue.TryDequeue(out var request))
            {
                _logger?.LogInformation("[NavExecutor] {Kind} {Entity} {Key} {Criteria}", request.Kind, request.EntityName, request.KeyValue, request.Criteria);
                request.Outcome = _executor.Execute(request);
            }
        }
    }
}
