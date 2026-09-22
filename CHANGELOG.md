# Changelog

All notable changes to XafTornado. Card ids (`SEC-001`, `AI-007`, ...) refer to the ContextBoard project; PR numbers to this repository.

## 2026-09-22 — Multi-user hardening

Starting point: a Codex review of `master` found the AI integration unsafe for more than one user. Every finding was reproduced, carded, fixed and re-reviewed by Codex before merging. The plan is in [DOCS/PLAN-2026-09-22-multiuser.md](DOCS/PLAN-2026-09-22-multiuser.md).

### Security

- **SEC-002, SEC-003 (PR #20, was #13)** — Per-user AI services. `AIChatService` (history, model, tool trace), `AIToolsProvider`, `ActiveViewContext`, the navigation queue and the log are scoped: one per Blazor circuit, one per WinForms application. Only the `TornadoApi` is shared (`TornadoApiProvider`). Tool bodies run on the circuit's synchronization context through `AIToolsProvider.Dispatch`; a model switch or a WinForms logoff cancels the in-flight turn and a turn that straddles a reset yields nothing.
- **SEC-001 (PR #19)** — Tools use the calling user's **secured ObjectSpace** (scope `IObjectSpaceFactory` in Blazor, `Application.CreateObjectSpace` in WinForms). Every data tool checks `CanRead` / `CanCreate` / `CanWrite` (object and member) first and answers `{ "error": "permission denied", entity, operation, member? }`; a record projection leaves out members the user may not read; `get_active_view` re-reads the current record through the secured space. Headless harnesses log a user into the scope (`TestApiController.SignIn`); the test fixture creates row-, member- and type-restricted users for the permission tests.
- **SEC-004 (PR #17)** — Per-user AI log panel. The process-wide log store, the logger provider that fed it and the `AI:LogToFile` option are gone; a scoped `AILogScope` is written by the tools provider and the chat service, and late writes after a reset are rejected. WinForms forwards the AI log categories to XAF's trace log (`XafTracingLoggerProvider`).
- **SEC-005 (PR #12)** — The Debug-only test API answers loopback callers only.

### Correctness

- **AI-007 (PR #18)** — UI tools report what the window did. `INavigationService` methods return `NavigationResult`; requests execute inline on the UI thread (both platforms) and a request nobody executed is reported as such, never as ok. `BlazorNavigationService` / `WinNavigationService` collapsed into `NavigationRequestQueue`, both executors into `UiRequestExecutor`. Validation, database and optimistic-lock failures on save, and a vetoed close, come back as failures. Known gap: a Blazor tabbed-MDI tab-limit refusal is silent and still reads as ok.
- **AI-002 .. AI-006, AI-008 .. AI-010 (PR #12)** — Retry never replays a turn whose tools already committed and never retries a user cancel; refresh after create/update skips a dirty view; Next/Previous record navigation updates the view context; `InvalidateCache` keeps the opt-in flag; ambiguous name matches return candidates instead of the first substring hit; cancellation reaches tool execution and `TimeoutSeconds` bounds the whole turn; ObjectSpaces and scopes are disposed on exceptions; the system prompt's date and time are computed per turn.

### Models and dependencies

- **AI-001 (PR #16)** — Model picker: Claude Fable 5.1, Claude Opus 5, Claude Sonnet 5, Claude Sonnet 4.6 (default), Claude Opus 4.8, GPT-6 Astra, GPT-5.5, GPT-5 Mini, Gemini 2.5 Pro / Flash, Mistral Large; retired entries removed. LlmTornado 3.8.67 → 3.8.68. A picker id that is a canonical LlmTornado catalog name gets the catalog model so endpoint capabilities apply (GPT-6 tool calls need the Responses endpoint); aliases such as `mistral-large-latest` keep following the provider.
- **DX-001 (PR #15)** — DevExpress 26.1.4 → 26.1.5 in all three projects.
- **DEP-001 (PR #14)** — Every floating package version pinned: Microsoft.Extensions.AI 10.10.0, Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3, Microsoft.Extensions.Configuration.Json 10.0.12, YamlDotNet 16.3.0 (and the out-of-solution ConsoleTest).

### Tests

- Tool-level xUnit suite grew from 24 to 59: scope isolation, bound-tool isolation, dispatch exception capture and cancellation, atomic UI request hand-off, UI failure relay, per-scope log trace, and six permission tests (Default role denied; read-only, row-restricted, member-restricted and explicitly denied reference targets; Admin unchanged).
- Verified live with Playwright: two circuits (A filters, B unchanged, B's history empty), navigate and close, a failed save reported as "changed by another user", a restricted user's "list all customers" refused with a permission error, and the WinForms client on every step.

### Docs

- README, CLAUDE.md, HOW_TO_IMPLEMENT.md, BEHIND_THE_SCENES.md and DOCS/TESTING.md describe the scoped, secured design; the architecture picture in the README is generated from `DOCS/architecture.archify.json`.
