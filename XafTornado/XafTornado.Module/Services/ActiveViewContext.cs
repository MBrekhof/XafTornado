using DevExpress.ExpressApp;

namespace XafTornado.Module.Services
{
    /// <summary>
    /// Tracks the currently active XAF view so AI tools can be context-aware.
    /// Singleton service — updated by <see cref="ActiveViewTrackingController"/>.
    /// </summary>
    public sealed class ActiveViewContext
    {
        /// <summary>Entity name shown in the active view (e.g. "Product", "Customer").</summary>
        public string EntityName { get; set; }

        /// <summary>Whether the active view is a ListView (true) or DetailView (false).</summary>
        public bool IsListView { get; set; }

        /// <summary>XAF View ID of the active view.</summary>
        public string ViewId { get; set; }

        /// <summary>CLR type of the entity in the active view.</summary>
        public Type EntityType { get; set; }

        /// <summary>
        /// The Frame hosting the active view. Used by NavigationExecutorController
        /// to apply filters/criteria. In Blazor XAF, Application.MainWindow.View is
        /// always null — the actual views live in nested frames.
        /// </summary>
        public Frame ActiveFrame { get; set; }

        /// <summary>When on a DetailView, the string key of the current object.</summary>
        public string CurrentObjectKey { get; set; }

        /// <summary>When on a DetailView, a human-readable label for the current object.</summary>
        public string CurrentObjectDisplay { get; set; }

        /// <summary>Fires when the active view changes.</summary>
        public event Action OnViewChanged;

        public void Update(string entityName, bool isListView, string viewId, Type entityType, Frame frame,
            string objectKey = null, string objectDisplay = null)
        {
            EntityName = entityName;
            IsListView = isListView;
            ViewId = viewId;
            EntityType = entityType;
            ActiveFrame = frame;
            CurrentObjectKey = objectKey;
            CurrentObjectDisplay = objectDisplay;
            OnViewChanged?.Invoke();
        }

        public void ClearFrame(Frame frame)
        {
            // Only clear if it's the same frame (avoid clearing a newer view's frame)
            if (ActiveFrame == frame)
                ActiveFrame = null;
        }

        public override string ToString() =>
            EntityName != null
                ? $"{EntityName} ({(IsListView ? "List" : "Detail")} View)"
                : "No active view";
    }
}
