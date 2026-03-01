using DevExpress.ExpressApp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XafTornado.Module.Services;

namespace XafTornado.Module.Controllers
{
    /// <summary>
    /// WindowController that updates <see cref="ActiveViewContext"/> whenever
    /// the user navigates to or switches to a different view.
    /// Uses <c>Application.ViewShown</c> which fires reliably on tab switches
    /// in Blazor's tabbed MDI — unlike ViewController.OnActivated which only
    /// fires once when a view is first opened.
    /// </summary>
    public class ActiveViewTrackingController : WindowController
    {
        private ILogger _logger;

        public ActiveViewTrackingController()
        {
            TargetWindowType = WindowType.Main;
        }

        protected override void OnActivated()
        {
            base.OnActivated();
            _logger = Application.ServiceProvider.GetService<ILogger<ActiveViewTrackingController>>();
            Application.ViewShown += OnViewShown;
            _logger?.LogInformation("[ViewTracker] Activated, listening to Application.ViewShown");
        }

        protected override void OnDeactivated()
        {
            Application.ViewShown -= OnViewShown;
            base.OnDeactivated();
        }

        private void OnViewShown(object sender, ViewShownEventArgs e)
        {
            var view = e.TargetFrame?.View;
            if (view == null) return;

            // Only track top-level views (Window frames). Skip nested ListViews
            // (e.g. Order Items inside a Product DetailView) and lookup ListViews
            // (e.g. Category_LookupListView) — these are embedded in plain Frame
            // instances and would overwrite the actual active view context.
            if (!(e.TargetFrame is Window))
            {
                _logger?.LogInformation("[ViewTracker] Skipped nested/lookup view: {ViewId}", view.Id);
                return;
            }

            // Skip non-persistent views like AIChat that don't represent real data
            var entityName = view.ObjectTypeInfo?.Name;
            if (string.IsNullOrEmpty(entityName)) return;

            var context = Application.ServiceProvider.GetService<ActiveViewContext>();
            if (context == null) return;

            var objectType = view.ObjectTypeInfo?.Type;
            var isListView = view is ListView;

            // For detail views, capture the current object's key and display text
            string objectKey = null;
            string objectDisplay = null;
            if (view is DetailView detailView && detailView.CurrentObject != null)
            {
                try
                {
                    objectKey = detailView.ObjectSpace.GetKeyValueAsString(detailView.CurrentObject);
                    objectDisplay = GetObjectDisplayText(detailView.CurrentObject);
                }
                catch
                {
                    // Best effort — don't crash if key extraction fails
                }
            }

            context.Update(entityName, isListView, view.Id, objectType, e.TargetFrame, objectKey, objectDisplay);
            _logger?.LogInformation("[ViewTracker] Active view updated: {Entity} ({ViewType}) ViewId={ViewId} Object={Display}",
                entityName, isListView ? "List" : "Detail", view.Id, objectDisplay ?? "(none)");
        }
        private static string GetObjectDisplayText(object obj)
        {
            if (obj == null) return null;
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
