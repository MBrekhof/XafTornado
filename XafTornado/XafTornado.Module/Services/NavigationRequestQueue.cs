using System;
using System.Collections.Concurrent;

namespace XafTornado.Module.Services
{
    /// <summary>
    /// The one <see cref="INavigationService"/> for both platforms. A tool submits a
    /// <see cref="UiRequest"/> and raises <see cref="OnRequest"/>; the platform's executor
    /// controller dequeues and runs it on the UI thread. Tool bodies already run there
    /// (<see cref="AIToolsProvider.Dispatch"/>), so the executor answers inline and the tool
    /// returns the real outcome. If nothing answered, the request is abandoned and the tool
    /// says so rather than claiming success (AI-007). Scoped: one per circuit (SEC-003).
    /// </summary>
    public sealed class NavigationRequestQueue : INavigationService
    {
        public const string NotConfirmed = "The application window did not execute the request.";

        private readonly ConcurrentQueue<UiRequest> _queue = new();

        /// <summary>A request was enqueued; the executor should drain the queue now.</summary>
        public event Action OnRequest;

        /// <summary>Fired when the side panel toggle is requested.</summary>
        public event Action OnSidePanelToggleRequested;

        public NavigationResult NavigateToListView(string entityName) =>
            Submit(new UiRequest { Kind = UiRequestKind.NavigateToList, EntityName = entityName });

        public NavigationResult NavigateToDetailView(string entityName, string keyValue) =>
            Submit(new UiRequest { Kind = UiRequestKind.NavigateToDetail, EntityName = entityName, KeyValue = keyValue });

        public NavigationResult FilterActiveList(string criteriaString) =>
            Submit(new UiRequest { Kind = UiRequestKind.Filter, Criteria = criteriaString });

        public NavigationResult ClearActiveListFilter() => Submit(new UiRequest { Kind = UiRequestKind.ClearFilter });

        public NavigationResult RefreshActiveView() => Submit(new UiRequest { Kind = UiRequestKind.Refresh });

        public NavigationResult SaveActiveView() => Submit(new UiRequest { Kind = UiRequestKind.Save });

        public NavigationResult CloseActiveView() => Submit(new UiRequest { Kind = UiRequestKind.Close });

        public void ToggleSidePanel() => OnSidePanelToggleRequested?.Invoke();

        private NavigationResult Submit(UiRequest request)
        {
            _queue.Enqueue(request);
            OnRequest?.Invoke();
            // Nobody claimed it inline (no executor, or one on another thread that has not got to
            // it): abandon it so it can never run later against whatever view is active by then.
            if (request.TryAbandon())
                return NavigationResult.Fail(NotConfirmed);
            // An executor claimed it. Inline that means it is already done; off-thread (WinForms
            // BeginInvoke) wait for it: the executor is on the UI thread, this thread is not.
            return request.WaitDone(10_000) && request.Outcome != null
                ? request.Outcome
                : NavigationResult.Fail(NotConfirmed);
        }

        /// <summary>Next request nobody has abandoned; the caller owns it (claimed) and must MarkDone it.</summary>
        public bool TryDequeue(out UiRequest request)
        {
            while (_queue.TryDequeue(out request))
            {
                if (request.TryClaim()) return true;
            }
            request = null;
            return false;
        }

        /// <summary>Drop everything pending: WinForms logoff, where the scope outlives the user.</summary>
        public void Clear()
        {
            while (_queue.TryDequeue(out _)) { }
        }
    }
}
