using System.Threading;

namespace XafTornado.Module.Services
{
    /// <summary>
    /// Platform-agnostic service for driving the XAF UI from AI tools. Every method reports
    /// whether the UI actually did it (AI-007): the tool relays <see cref="NavigationResult"/>
    /// to the model instead of guessing.
    /// </summary>
    public interface INavigationService
    {
        /// <summary>Navigate the user to the ListView for the given entity.</summary>
        NavigationResult NavigateToListView(string entityName);

        /// <summary>Navigate the user to a specific record's DetailView.</summary>
        NavigationResult NavigateToDetailView(string entityName, string keyValue);

        /// <summary>Apply a filter to the active ListView using XAF criteria syntax.</summary>
        NavigationResult FilterActiveList(string criteriaString);

        /// <summary>Clear the AI-applied filter from the active ListView.</summary>
        NavigationResult ClearActiveListFilter();

        /// <summary>Refresh the active view's data from the database (after AI creates/updates records).</summary>
        NavigationResult RefreshActiveView();

        /// <summary>Save (commit) changes in the active detail view; XAF validation runs as for the Save action.</summary>
        NavigationResult SaveActiveView();

        /// <summary>Close the active view and return to the previous one.</summary>
        NavigationResult CloseActiveView();

        /// <summary>Toggle the AI assistant side panel open/closed.</summary>
        void ToggleSidePanel();
    }

    /// <summary>What the UI did with a request; <c>Error</c> is for the model, in plain words.</summary>
    public sealed record NavigationResult(bool Ok, string Error = null)
    {
        public static readonly NavigationResult Success = new(true);
        public static NavigationResult Fail(string error) => new(false, error);
    }

    public enum UiRequestKind { NavigateToList, NavigateToDetail, Filter, ClearFilter, Refresh, Save, Close }

    /// <summary>
    /// One request from a tool to the UI. Exactly one of two things happens to it: an executor
    /// claims and runs it, or the submitter abandons it because nobody answered inline. The
    /// state is an atomic hand-off so an executor on another thread (WinForms BeginInvoke) can
    /// neither run an abandoned request nor be lost after claiming one.
    /// </summary>
    public sealed class UiRequest
    {
        private const int Pending = 0, Claimed = 1, AbandonedState = 2;
        private int _state;
        private readonly ManualResetEventSlim _done = new(false);

        public UiRequestKind Kind { get; init; }
        public string EntityName { get; init; }
        public string KeyValue { get; init; }
        public string Criteria { get; init; }

        /// <summary>Set by the executor before <see cref="MarkDone"/>; read by the submitter after <see cref="WaitDone"/>.</summary>
        public NavigationResult Outcome { get; set; }

        /// <summary>Executor side: true once, false if the submitter already abandoned it.</summary>
        public bool TryClaim() => Interlocked.CompareExchange(ref _state, Claimed, Pending) == Pending;

        /// <summary>Submitter side: true if no executor claimed it; the request must then never run.</summary>
        public bool TryAbandon() => Interlocked.CompareExchange(ref _state, AbandonedState, Pending) == Pending;

        public void MarkDone() => _done.Set();

        /// <summary>Waits for a claimed request to finish (the executor is on the UI thread, the waiter is not).</summary>
        public bool WaitDone(int millisecondsTimeout) => _done.Wait(millisecondsTimeout);
    }
}
