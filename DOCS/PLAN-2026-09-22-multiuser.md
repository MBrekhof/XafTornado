# Plan: per-user isolation and the security boundary

Date: 2026-09-22. Source: Codex review of `master` at `4a8828c` (findings 1-4, 11), cross-checked
against `DOCS/REVIEW-2026-03-04.md` (#1, #3) and `DOCS/PHASE3.md` (Track B).
Cards: SEC-001, SEC-002, SEC-003, SEC-004, AI-007 on the XafTornado board.

The nine smaller findings (AI-002..AI-006, AI-008..AI-010, SEC-005) are quick fixes and are not
covered here; they ship in `fix/codex-review-quick-fixes`.

## 1. What is wrong, in one picture

```
Blazor host (one process)
  Singletons:  AIChatService (history, model, last tool calls)
               AIToolsProvider (bound to the two below)
               BlazorNavigationService (queues + flags, events)
               ActiveViewContext (ActiveFrame, current record)
               AILogStore
  Circuit A ── DxAIChat ── IChatClient ──┐
  Circuit B ── DxAIChat ── IChatClient ──┴─► the same AIChatService, the same queues,
                                              whichever circuit's controller dequeues first
```

Every piece of per-user state is process-wide. Tools also open a **non-secured** ObjectSpace from
a fresh DI scope, so no user identity reaches the data layer at all.

## 2. Target shape

```
Singleton:  TornadoApiProvider  the TornadoApi + lazy key init, nothing else
Scoped:     AIChatService      history, current model, last tool calls, per-conversation turn lock,
                               Tool + AIFunction list (bound to THIS scope's tools provider)  (SEC-002)
            AIToolsProvider    tools bound to the scope's nav service + view context
            BlazorNavigationService / WinNavigationService                       (SEC-003)
            ActiveViewContext                                                    (SEC-003)
            AILogScope         this scope's tool trace, feeds AILogViewer        (SEC-004)
            IChatClient        AIChatClient(AIChatService)   [AddChatClient(..., ServiceLifetime.Scoped)]
Data:       IObjectSpaceFactory (secured, scoped) in Blazor; XafApplication.CreateObjectSpace
            in WinForms (already secured)                                        (SEC-001)
```

Revised after the Codex plan review (see § 6): the first draft had a singleton "engine" holding the
`AIFunction` list. Those functions are bound instance-method delegates on `AIToolsProvider`
(AIToolsProvider.cs:62), so a singleton holding them would pin one circuit's services for everyone.
Only the `TornadoApi` is shared; everything that executes stays scoped.

In XAF Blazor each circuit owns one `BlazorApplication`, and `Application.ServiceProvider` is that
circuit's scoped provider (this is why `IObjectSpaceFactory` per scope knows the user). WinForms
has one application and one scope, so "scoped" degenerates to "one instance" there without any
platform branch.

## 3. Steps, in order

### Step 1: scoped lifetimes (SEC-002 + SEC-003), one PR

1. Make `AIChatService` **scoped** as it is (history, model, last tool calls, tools). Extract only the
   `TornadoApi` + `EnsureInitialized` into a singleton `TornadoApiProvider`. Add a per-instance
   `SemaphoreSlim(1,1)` around `AskAsync` so two chat surfaces in one circuit (`AISidePanel.razor:39`
   and `AIChat.razor:9`) cannot interleave turns; model switch and `ClearHistory` take the same lock.
   `SelectAIModelController` sets the scoped service's `CurrentModel`, no longer `AIOptions.Model`.
2. Register `AIToolsProvider`, `ActiveViewContext`, `BlazorNavigationService`/`WinNavigationService`
   and `INavigationService` as **scoped**. The executors and the tracker already resolve them from
   `Application.ServiceProvider`, so they pick up the circuit's instances with no code change.
3. `AddChatClient(sp => new AIChatClient(sp.GetRequiredService<AIChatService>()),
   ServiceLifetime.Scoped)` (overload confirmed in Microsoft.Extensions.AI 10.x; pin the package,
   `Module.csproj` currently floats `*`). DxAIChat resolves its response provider from the circuit
   scope (confirmed by Codex against the installed DevExpress.AIIntegration), so tool calls now run
   with the circuit's services.
4. **Tool dispatch.** Tools run on the LLM client's continuation. AsyncLocal keeps the scope, but XAF
   wants its work on the circuit's synchronization context. Give `AIToolsProvider` a
   `Func<Func<string>, Task<string>> Dispatch` set by the executor controller on activation
   (`blazorApp.InvokeAsync` in Blazor, the existing `UiContext.Send` in WinForms) and run the **whole**
   tool body (ObjectSpace create + query/mutate + projection + dispose) inside it, catching exceptions
   inside the delegate so `InvokeAsync` cannot swallow them. Network calls stay outside.
5. WinForms: register the chat client after `Setup()` from `winApplication.ServiceProvider`
   (Program.cs already has the post-Setup block; keep the `Application`/`UiContext` wiring there) and
   drop the pre-Setup resolution in `Win/Startup.cs`. **Identity change**: the WinForms application
   and its scope survive logoff/relogon (`WinApplication.cs:894`), so subscribe to
   `Application.LoggedOff`/`LoggedOn` and reset history, model, trace, `ActiveViewContext` and pending
   navigation requests, cancelling any in-flight turn.
6. `TestApiController` (Debug): a scoped MVC request has no circuit. Keep a Debug-only
   `ConcurrentDictionary<string, List<ChatMessageEntry>>` of **history data only**, keyed by an
   `X-Test-Session` header the YAML runner generates per run; each request builds its executor from
   its own request scope. `/api/test/clear` removes the entry. (The runner already relies on
   multi-request continuity: `TestRunner.cs:32` clears once per file, then runs all steps.)
7. `AppFixture` (ToolTests) resolves tools from an explicit, retained scope
   (`_factory.Services.CreateScope()`), disposed with the fixture.

Verification: ToolTests green; a new test that opens two scopes, asks in one, and asserts the
other's `LastToolCalls`/history are empty; smoke test; manual two-browser check (user A filters,
user B's grid unchanged).

Implemented 2026-09-22 (`feat/scoped-ai-services`), two deviations from the text above:

- 1.1 turn lock: `ClearHistory` (model switch, WinForms logoff) **cancels** the in-flight turn
  instead of taking the lock. It runs on the UI thread and a turn can last `TimeoutSeconds`; a
  blocking wait would freeze the UI. A reset bumps a generation counter; a turn that straddles it
  (waiting for the lock, in flight, or answered but not yet appended) answers "The conversation
  was reset." and appends nothing (Codex implementation review, findings 3 and 4).
- 1.4 dispatch: no tool body changed. Every `AIFunction` is wrapped in a `DelegatingAIFunction`
  whose `InvokeCoreAsync` routes through `AIToolsProvider.Dispatch`
  (`Func<Func<Task<object>>, Task<object>>`). Blazor sets `blazorApp.InvokeAsync`, WinForms
  `SynchronizationContext.Send`; tests and the test API leave it null and run inline. The old
  `UiContext` property is gone: the body already runs on the UI thread when it creates the ObjectSpace.

### Step 2: secured ObjectSpace (SEC-001), one PR, after Step 1

1. In `AIToolsProvider.GetObjectSpace` (Blazor branch) replace the new-scope +
   `INonSecuredObjectSpaceFactory` pair with the scope's `IObjectSpaceFactory.CreateObjectSpace(type)`.
   No scope creation: the provider is now scoped and the factory is the circuit's.
2. Catch `SecurityAccessDeniedException` (and the EF Core security "object not found" case for
   reads) in every tool and return `{ "error": "permission denied", "entity": ..., "operation": ... }`
   so the model says so instead of "no records".
3. `query_entity` on a type the user cannot read returns the same error, not an empty list. Detect via
   `ISecurityStrategyBase`/`IsGranted` before querying, per the dxdocs "Determine if the Current User
   Has Specific Permissions" page.
4. Optional (PHASE3 Track B): `get_current_user_permissions` tool.
5. `TestApiController` and `AppFixture` must log a user on **in the scope that runs the tools**.
   Dxdocs: `IObjectSpaceFactory` throws when nobody is authenticated, and
   `IStandardAuthenticationService.Authenticate` only returns a principal; the secured factory then
   calls `EnsureLogon` on the scope's security. So: authenticate, set the principal on the scope's
   security strategy, assert `ISecurityStrategyBase.User` is the expected user, and only then resolve
   tools. Two retained fixture scopes: `Admin` and `User`. For the MVC test API, establish the request
   identity the same way before the tool runs.
6. Narrow the existing catch-alls first: `AIToolsProvider.cs:620` swallows record-load failures,
   `:761` treats every keyed-lookup exception as "not a key", and the property-set catches label every
   failure a conversion error. A permission failure must reach the normalised error, not those.

Verification: ToolTests `User` cannot `query_entity Customer` (permission error) and cannot
`update_entity`; readable-but-unwritable, row-restricted and member-restricted cases each get a test;
`Admin` unchanged; evals unchanged (they run as Admin).

Implemented 2026-09-22 (`fix/secured-object-space`, on top of Step 4). `GetObjectSpace` takes the
scope's `IObjectSpaceFactory` (Blazor) or `Application.CreateObjectSpace` (WinForms); no scope is
created. Because a secured EF Core space filters reads and **drops unauthorised writes silently**
(dxdocs "2-Tier Security, Integrated Mode"), every data tool asks first through
`IsGrantedExtensions` (`CanRead` type, `CanCreate`, `CanWrite` object and member) and answers
`{ "error": "permission denied", entity, operation, member? }`; a record projection leaves out a
member the user may not read instead of showing the type's default. Row-restricted rows are simply
absent, so a keyed lookup of one answers "not found". The optional permissions tool (2.4) is not
built. Headless logon: `TestApiController.SignIn(scope, user)` = `UserManager.FindUserByName` on a
non-secured space + `SignInManager.SignIn` (dxdocs "User Logon and Authentication"); the test API
signs Admin in per request, `AppFixture` keeps one signed-in scope per user and creates the
`reader`, `germany` (row-restricted) and `nophone` (member-restricted) users with their roles for
`SecurityTests`. Catch-alls narrowed to conversion exceptions (2.6). Codex review: a reference
target the user is explicitly denied is "permission denied" rather than "not found" (a target
with no permission at all gets XAF's implicit read for referenced objects, so `CanRead` is true
and the hidden rows answer "not found"); `get_active_view`
re-reads the current record through the secured space and never echoes the cached id/display
of a row the user may no longer see; `SignIn` checks `AuthenticationResult.Succeeded`.

### Step 3: per-scope log (SEC-004), rides on Step 1

`AILoggerProvider` currently pushes every log line into the singleton `AILogStore`. Replace with:
the tool loop in `AIConversation` records its own trace (`ToolCall` list already exists) into a
scoped `AILogScope`; `AILogViewer` injects that. Keep `AILoggerProvider` for the console only.
Interim until then (in the quick-fix PR): render `AILogViewer` only for administrative users and
make the on-disk `ai-debug.log` opt-in.

Implemented 2026-09-22 (`fix/per-scope-ai-log`, on top of Step 1): `AILogStore` + `AILoggerProvider`
+ `AI:LogToFile` are gone. The scoped `AILogScope` is written by `AIToolsProvider` (every call:
name, arguments, result or error) and `AIChatService` (turn start, response with token usage,
timeout, retries, tool failures); `AILogViewer` injects it. The admin-only gate on the panel is
removed: the trace is the viewer's own. Executor and view-tracker lines go to the console (Blazor)
or, in WinForms, to XAF's trace log through `XafTracingLoggerProvider` (WinForms registered no
other provider, so they would otherwise vanish). WinForms logoff clears the scope; a cancelled tool
call logs nothing; an `{ "error": ... }` tool result is a Warning entry (Codex review).

### Step 4: truthful UI tools (AI-007), after Step 1

`INavigationService` becomes:

```csharp
Task<NavigationResult> NavigateToListView(string entityName, CancellationToken ct);
... same for detail, filter, clear filter, refresh, save, close
record NavigationResult(bool Ok, string Error = null);
```

Each Blazor/Win request carries a `TaskCompletionSource<NavigationResult>`, a `CancellationToken`
and the **target view** captured at enqueue time (`ActiveViewContext.ActiveFrame?.View`). The
executor skips a request whose token is cancelled or whose target view is no longer the frame's
current view (completing it with an error), so a timed-out "save" can never land on the record the
user switched to meanwhile. The tool awaits with a 10 s timeout, cancels on timeout, and returns
`{ ok, error }` from the result. On controller deactivation every pending request is completed with
"view closed". `save_active_view` reports success only when the captured view's ObjectSpace committed.

Headless harnesses have no executor: `ToolTests.cs:264` and the navigation evals
(`llm-evals.yaml:27`) currently pass because the tool answers `ok` without a UI. Give the test
fixture a fake `INavigationService` that records requests and completes them, and assert on the
recorded request instead of the tool's `ok`.

Verification: tool test with the fake executor set to fail: `save_active_view` returns
`{ ok: false, error }`. Playwright: validation error on a detail view + "save this" -> assistant
reports the failure.

Implemented 2026-09-22 (`fix/truthful-ui-tools`, on top of Step 3), simpler than the text above
because Step 1 changed the ground: tool bodies already run on the circuit's synchronization
context (`AIToolsProvider.Dispatch`), and both `BlazorApplication.InvokeAsync` (Blazor's
`Dispatcher` runs inline when already on its context) and the WinForms `Control.InvokeRequired`
check execute the executor **inline** from there. So no `TaskCompletionSource`, timeout or
captured target view: `INavigationService` methods return `NavigationResult` synchronously, the
`UiRequest` carries the outcome, and if no executor answered inline the request is abandoned and
the tool answers `{ ok: false, error: "The application window did not execute the request." }`.
Both platform services collapsed into `NavigationRequestQueue` (Module) and both executors into
`UiRequestExecutor` (Module); the controllers only dispatch. `Save` commits the view's ObjectSpace;
its `PersistenceValidationController` validates on `Committing` exactly as for the Save action
(dxdocs: PersistenceValidationController), and the `ValidationException`, like a database
rejection or the optimistic lock, becomes a failed result. A vetoed `Close` is a failure too.
Known gap (Codex, pre-existing): the Blazor tabbed MDI strategy refuses a view silently once its
tab limit is reached, and since `ViewShown` fires after `ShowViewFromCommonView` returns in
Blazor, nothing synchronous tells that refusal from success; it still reads as ok. The hand-off is atomic (`UiRequest.TryClaim`
/ `TryAbandon`): an executor on another thread (WinForms `BeginInvoke`) can neither run an
abandoned request nor be lost after claiming one, in which case the submitter waits for it
(Codex implementation review). Headless harnesses: `AppFixture` attaches
a fake executor that records requests and answers a configurable outcome; the Debug
`TestApiController` acknowledges requests so the evals keep asserting on the tool trace.

## 4. Risks and open questions

- **Model switch per user** means `AIOptions.Model` becomes the default only. The picker card
  (AI-001) should land after Step 1 or set the scoped service's `CurrentModel`.
- **WinForms `AIChatControl`** registers the chat client through `AIExtensionsContainerDesktop.Default`
  (static). One conversation per process is acceptable only with the logoff reset in Step 1.5.
- Step 2 depends on the seeded `Default` role: it grants nothing on business objects, so `User` is a
  true negative test. If the demo wants `User` to read data, add explicit read permissions in
  `Updater.cs` rather than weakening the test.
- `AILoggerProvider` has no console fallback today; when the store-backed provider goes (Step 3),
  keep ordinary console logging for the same categories.

## 6. Codex plan review, 2026-09-22

Verdict was "needs changes"; all eight points are folded in above:

| # | Finding | Where it landed |
|---|---------|-----------------|
| 1 | Singleton engine would pin one circuit's `AIFunction` delegates for everyone | § 2, Step 1.1 |
| 2 | Tool work must be dispatched through the circuit's sync context, whole operation, exceptions caught inside | Step 1.4 |
| 3 | WinForms scope survives logoff/relogon; `Win/Startup.cs` already resolves from the app scope | Step 1.5 |
| 4 | Header-keyed test conversations must cache data only, per-run key; runner is already multi-request | Step 1.6 |
| 5 | Test logon must land in the executing scope; headless navigation tests need a fake executor | Step 2.5, Step 4 |
| 6 | Queued navigation needs cancellation and target-view identity | Step 4 |
| 7 | Existing inner catch-alls would hide permission errors | Step 2.6 |
| 8 | Two chat surfaces per circuit can interleave turns | Step 1.1 (turn lock) |

Also from the review: pin `Microsoft.Extensions.AI` (currently `*` in `Module.csproj`, resolves to
10.10.0 locally).

## 5. Out of scope here

Conversation persistence (PHASE3 Track C) and mutation confirmation (Track A) are unchanged by this
plan; both become simpler once `AIConversation` is a scoped object with a clear owner.
