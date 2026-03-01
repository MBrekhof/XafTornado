using System.Collections.Concurrent;
using XafTornado.Module.Services;

namespace XafTornado.Win.Services
{
    /// <summary>
    /// WinForms implementation of <see cref="INavigationService"/>.
    /// AI tools enqueue requests; <see cref="Controllers.WinNavigationExecutorController"/>
    /// dequeues and executes them on the WinForms UI thread.
    /// </summary>
    public sealed class WinNavigationService : INavigationService
    {
        private readonly ConcurrentQueue<NavigationRequest> _navQueue = new();
        private readonly ConcurrentQueue<FilterRequest> _filterQueue = new();
        private volatile bool _refreshRequested;
        private volatile bool _saveRequested;
        private volatile bool _closeRequested;

        public event Action OnNavigationRequested;
        public event Action OnFilterRequested;
        public event Action OnRefreshRequested;
        public event Action OnSidePanelToggleRequested;
        public event Action OnSaveRequested;
        public event Action OnCloseRequested;

        public void NavigateToListView(string entityName)
        {
            _navQueue.Enqueue(new NavigationRequest(entityName, null));
            OnNavigationRequested?.Invoke();
        }

        public void NavigateToDetailView(string entityName, string keyValue)
        {
            _navQueue.Enqueue(new NavigationRequest(entityName, keyValue));
            OnNavigationRequested?.Invoke();
        }

        public void ToggleSidePanel() => OnSidePanelToggleRequested?.Invoke();

        public void FilterActiveList(string criteriaString)
        {
            _filterQueue.Enqueue(new FilterRequest(criteriaString));
            OnFilterRequested?.Invoke();
        }

        public void ClearActiveListFilter()
        {
            _filterQueue.Enqueue(new FilterRequest(null));
            OnFilterRequested?.Invoke();
        }

        public void RefreshActiveView()
        {
            _refreshRequested = true;
            OnRefreshRequested?.Invoke();
        }

        public void SaveActiveView()
        {
            _saveRequested = true;
            OnSaveRequested?.Invoke();
        }

        public void CloseActiveView()
        {
            _closeRequested = true;
            OnCloseRequested?.Invoke();
        }

        public bool TryDequeueNavigation(out NavigationRequest request) =>
            _navQueue.TryDequeue(out request);

        public bool TryDequeueFilter(out FilterRequest request) =>
            _filterQueue.TryDequeue(out request);

        public bool ConsumeRefresh()
        {
            if (!_refreshRequested) return false;
            _refreshRequested = false;
            return true;
        }

        public bool ConsumeSave()
        {
            if (!_saveRequested) return false;
            _saveRequested = false;
            return true;
        }

        public bool ConsumeClose()
        {
            if (!_closeRequested) return false;
            _closeRequested = false;
            return true;
        }
    }

    public sealed record NavigationRequest(string EntityName, string KeyValue);
    public sealed record FilterRequest(string CriteriaString);
}
