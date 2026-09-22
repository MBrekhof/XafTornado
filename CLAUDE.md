# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Git Rules

- Push to `origin` (MBrekhof/XafTornado).
- Always create feature branches off `master` — do not commit directly to `master`.
- Use conventional commits: `feat:`, `fix:`, `chore:`, `docs:`, `refactor:`, `test:`.

## Code Style

- Format with `dotnet format` (targets the solution). No `.editorconfig` yet — uses SDK defaults.

## Project Overview

DevExpress XAF application integrating LLMTornado to provide an in-app AI assistant that queries live business data, navigates views, creates and updates records conversationally, and works on both Blazor Server and WinForms. Uses a Northwind-style domain (orders, customers, products, employees, invoices) seeded automatically on first run.

## Build & Run Commands

```bash
# Restore and build the entire solution
dotnet build XafTornado.slnx

# Run the Blazor Server app (primary development target)
dotnet run --project XafTornado/XafTornado.Blazor.Server

# Run the WinForms app (Windows only)
dotnet run --project XafTornado/XafTornado.Win

# Build a specific project
dotnet build XafTornado/XafTornado.Module/XafTornado.Module.csproj
```

### Testing

Strategy and layers: `DOCS/TESTING.md`. AI tools return JSON — tests assert on fields, never on wording.

- Tool-level tests (no LLM, real Postgres, ~10 s): `docker start xaf-postgres && dotnet test XafTornado/XafTornado.ToolTests`
- Smoke test (Playwright, ~30 s, hosts the app itself): `powershell -File scripts/smoke.ps1`
- LLM evals (opt-in, needs API key) — YAML runner asserting on the tool-call trace: `dotnet run --project XafTornado/XafTornado.Tests -- tests/llm-evals.yaml`
- Requires the Blazor Server app running at `http://localhost:5000` (Debug build — `TestApiController` is compiled out of Release)
- `ConsoleTest` project is a minimal console app for ad-hoc testing (not in solution).

## Architecture

### Solution Structure (3-tier XAF pattern)

- **`XafTornado.Module/`** — Platform-agnostic core: business objects (EF Core entities), attributes (`AIVisible`, `AIDescription`), XAF controllers, and all LLMTornado integration services. Both UI projects reference this.
- **`XafTornado.Blazor.Server/`** — Blazor Server UI. Entry point: `Program.cs` → `Startup.cs`. Uses DevExpress `DxAIChat` Blazor component for the chat UI (`Editors/AIChatViewItem/AIChat.razor`). Contains `NavigationExecutorController`, which runs the circuit's `NavigationRequestQueue` on the circuit's synchronization context.
- **`XafTornado.Win/`** — WinForms UI (net10.0-windows). Uses DevExpress `AIChatControl` for the chat UI.

### LLMTornado Integration (Module/Services/)

The integration chain flows:

1. **`ServiceCollectionExtensions.AddAIServices()`** — Registers all services. Called from both Blazor `Startup.cs` and WinForms `Startup.cs`.
2. **`AIChatService`** — Singleton managing the `TornadoApi` lifecycle. Lazy-initializes from API keys on first request. Maintains conversation history (50 message pairs). Runs a native tool-calling loop with `GetResponseRich()` (up to `MaxToolIterations`). Polly retry pipeline (3 attempts, exponential backoff) for resilience.
3. **`AIChatClient`** — `IChatClient` adapter bridging DevExpress AI chat controls to `AIChatService`. This is what DxAIChat/AIChatControl resolves via DI.
4. **`AIToolsProvider`** — Creates 12 `AIFunction` tools for function calling. Tools use the calling user's secured ObjectSpace (scope `IObjectSpaceFactory` in Blazor, `Application.CreateObjectSpace` in WinForms) for database access. Core tools: `list_entities`, `describe_entity`, `query_entity`, `create_entity`, `update_entity`. Navigation tools (Blazor only): `navigate_to_list`, `navigate_to_detail`, `filter_active_list`, `clear_active_list_filter`, `save_active_view`, `close_active_view`. Context tool: `get_active_view`.
5. **`SchemaDiscoveryService`** — Discovers entities via `ITypesInfo` reflection. Respects `[AIVisible]` and `[AIDescription]` attributes. Generates a lightweight system prompt (entity names + descriptions) with date/time context.
6. **`ActiveViewContext`** — Singleton tracking current view state (entity, list vs. detail, current record). Updated by `ActiveViewTrackingController`.
7. **`AIChatDefaults`** — Shared UI config (prompt suggestions, Markdown→HTML rendering via Markdig + HtmlSanitizer).
8. **`AIOptions`** — Bound from `appsettings.json` section `"AI"`. Keys: `Model` (default `"claude-sonnet-4-6"`), `DefaultProvider` (default `"anthropic"`), `ApiKeys` (per-provider API keys), `MaxOutputTokens` (default 16384), `MaxToolIterations` (default 10), `TimeoutSeconds` (default 120).

### Key Patterns

- **Non-Persistent Business Objects**: `AIChat` is a `DomainComponent` (not stored in DB) — it exists only to host the chat ViewItem in XAF's navigation.
- **Secured ObjectSpace per tool call**: `AIToolsProvider.GetObjectSpace` takes the scope's `IObjectSpaceFactory` (Blazor) or `Application.CreateObjectSpace` (WinForms), disposed after the call. A secured space filters reads and drops unauthorised writes silently, so tools check `CanRead`/`CanCreate`/`CanWrite` first and answer `{ "error": "permission denied", entity, operation }`. Headless harnesses log a user into the scope with `TestApiController.SignIn`.
- **Model switching at runtime**: `SelectAIModelController` lets users switch AI models (claude-fable-5-1, claude-sonnet-4-6, gpt-6-astra, gemini-2.5-pro, etc.) via a `SingleChoiceAction` that sets `AIChatService.CurrentModel` and clears conversation history. A picker id that is a canonical LlmTornado catalog name gets the catalog `ChatModel` (endpoint capabilities, e.g. gpt-6 tool calls on the Responses endpoint).
- **UI request pattern**: tools submit a `UiRequest` to the scoped `NavigationRequestQueue` (Module); the platform executor controller (`NavigationExecutorController` / `WinNavigationExecutorController`) runs it through `UiRequestExecutor` on the UI thread. Tool bodies already run there (`AIToolsProvider.Dispatch`), so the request executes inline and the tool returns the real `NavigationResult`; a request nobody executed is abandoned and reported as such, never as ok.
- **Two-tier schema discovery**: System prompt contains only entity names + descriptions. Full property/relationship details loaded on-demand via `describe_entity` tool.
- **XAF Model Differences**: `Model.DesignedDiffs.xafml` (embedded in Module) and `Model.xafml` (copied to output in UI projects) configure XAF views, navigation, and layout.

### Database

- EF Core 10.0.11 with PostgreSQL (`xaftornado`) for development
- DbContext: `XafTornadoEFCoreDbContext`
- Auto-migration via XAF's `ModuleUpdater` pattern (`DatabaseUpdate/Updater.cs`)
- 14 entities: Order, OrderItem, Customer, Product, Category, Supplier, Employee, Department, EmployeeTerritory, Territory, Region, Shipper, Invoice, ApplicationUser, ApplicationUserLoginInfo
- Seed data: 20 customers, 5 employees, 30 products, 50 orders, 20 invoices, test users "User"/"Admin" (empty passwords in debug)

## Tech Stack

- .NET 10.0 (net10.0 / net10.0-windows)
- DevExpress XAF 26.1.*, DevExpress AI Integration 26.1.*
- LLMTornado, Microsoft.Extensions.AI, Polly
- EF Core 10.0.11 + PostgreSQL
- Markdig + HtmlSanitizer for Markdown rendering

## Configuration

Add an `"AI"` section to `appsettings.json` (or `appsettings.Development.json`) with provider API keys:
```json
{
  "AI": {
    "Model": "claude-sonnet-4-6",
    "DefaultProvider": "anthropic",
    "ApiKeys": {
      "anthropic": "sk-ant-...",
      "openai": "sk-..."
    }
  }
}
```

Authentication is API-key based. Add keys only for the providers you want to use.
