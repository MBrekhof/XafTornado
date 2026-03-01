namespace XafTornado.Module.Services
{
    /// <summary>
    /// Platform-agnostic service for navigating the XAF application from AI tools.
    /// Blazor provides an implementation; WinForms returns null (navigation not supported).
    /// </summary>
    public interface INavigationService
    {
        /// <summary>Navigate the user to the ListView for the given entity.</summary>
        void NavigateToListView(string entityName);

        /// <summary>Navigate the user to a specific record's DetailView.</summary>
        void NavigateToDetailView(string entityName, string keyValue);

        /// <summary>Toggle the AI assistant side panel open/closed.</summary>
        void ToggleSidePanel();

        /// <summary>Apply a filter to the active ListView using XAF criteria syntax.</summary>
        void FilterActiveList(string criteriaString);

        /// <summary>Clear the AI-applied filter from the active ListView.</summary>
        void ClearActiveListFilter();

        /// <summary>Refresh the active view's data from the database (after AI creates/updates records).</summary>
        void RefreshActiveView();

        /// <summary>Save (commit) changes in the active detail view.</summary>
        void SaveActiveView();

        /// <summary>Close the active view and return to the previous one.</summary>
        void CloseActiveView();
    }
}
