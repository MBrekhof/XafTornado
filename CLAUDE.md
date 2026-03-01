# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Git Rules

- Push to `origin` (MBrekhof/XafTornado).
- Always create feature branches off `master` — do not commit directly to `master`.

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

There is no formal test suite configured. The `ConsoleTest` project is a minimal console app for ad-hoc testing.

## Architecture

### Solution Structure (3-tier XAF pattern)

- **`XafTornado.Module/`** — Platform-agnostic core: business objects (EF Core entities), attributes (`AIVisible`, `AIDescription`), XAF controllers, and all LLMTornado integration services. Both UI projects reference this.
- **`XafTornado.Blazor.Server/`** — Blazor Server UI. Entry point: `Program.cs` → `Startup.cs`. Uses DevExpress `DxAIChat` Blazor component for the chat UI (`Editors/AIChatViewItem/AIChat.razor`). Contains `BlazorNavigationService` (queue-based `INavigationService` implementation).
- **`XafTornado.Win/`** — WinForms UI (net10.0-windows). Uses DevExpress `AIChatControl` for the chat UI.

### LLMTornado Integration (Module/Services/)

The integration chain flows:

1. **`ServiceCollectionExtensions.AddAIServices()`** — Registers all services. Called from both Blazor `Startup.cs` and WinForms `Startup.cs`.
2. **`AIChatService`** — Singleton managing the `TornadoApi` lifecycle. Lazy-initializes from API keys on first request. Maintains conversation history (50 message pairs). Runs a native tool-calling loop with `GetResponseRich()` (up to `MaxToolIterations`). Polly retry pipeline (3 attempts, exponential backoff) for resilience.
3. **`AIChatClient`** — `IChatClient` adapter bridging DevExpress AI chat controls to `AIChatService`. This is what DxAIChat/AIChatControl resolves via DI.
4. **`AIToolsProvider`** — Creates 12 `AIFunction` tools for function calling. Tools use `ScopedObjectSpace` (DI scope + `INonSecuredObjectSpaceFactory`) for database access. Core tools: `list_entities`, `describe_entity`, `query_entity`, `create_entity`, `update_entity`. Navigation tools (Blazor only): `navigate_to_list`, `navigate_to_detail`, `filter_active_list`, `clear_active_list_filter`, `save_active_view`, `close_active_view`. Context tool: `get_active_view`.
5. **`SchemaDiscoveryService`** — Discovers entities via `ITypesInfo` reflection. Respects `[AIVisible]` and `[AIDescription]` attributes. Generates a lightweight system prompt (entity names + descriptions) with date/time context.
6. **`ActiveViewContext`** — Singleton tracking current view state (entity, list vs. detail, current record). Updated by `ActiveViewTrackingController`.
7. **`AIChatDefaults`** — Shared UI config (prompt suggestions, Markdown→HTML rendering via Markdig + HtmlSanitizer).
8. **`AIOptions`** — Bound from `appsettings.json` section `"AI"`. Keys: `Model` (default `"claude-sonnet-4-6"`), `DefaultProvider` (default `"anthropic"`), `ApiKeys` (per-provider API keys), `MaxOutputTokens` (default 16384), `MaxToolIterations` (default 10), `TimeoutSeconds` (default 120).

### Key Patterns

- **Non-Persistent Business Objects**: `AIChat` is a `DomainComponent` (not stored in DB) — it exists only to host the chat ViewItem in XAF's navigation.
- **ScopedObjectSpace pattern**: Tool methods in `AIToolsProvider` create a DI scope + non-secured ObjectSpace per call, disposed after use. This is required because tools run outside the normal XAF request lifecycle.
- **Model switching at runtime**: `SelectAIModelController` lets users switch AI models (claude-sonnet-4-6, gpt-4o, gemini-2.5-pro, etc.) via a `SingleChoiceAction` that sets `AIChatService.CurrentModel` and clears conversation history.
- **Navigation queue pattern**: `BlazorNavigationService` enqueues navigation/filter/save/close requests; `NavigationExecutorController` dequeues and executes them on the XAF UI thread.
- **Two-tier schema discovery**: System prompt contains only entity names + descriptions. Full property/relationship details loaded on-demand via `describe_entity` tool.
- **XAF Model Differences**: `Model.DesignedDiffs.xafml` (embedded in Module) and `Model.xafml` (copied to output in UI projects) configure XAF views, navigation, and layout.

### Database

- EF Core 8.0.18 with PostgreSQL (`xaftornado`) for development
- DbContext: `XafTornadoEFCoreDbContext`
- Auto-migration via XAF's `ModuleUpdater` pattern (`DatabaseUpdate/Updater.cs`)
- 14 entities: Order, OrderItem, Customer, Product, Category, Supplier, Employee, Department, EmployeeTerritory, Territory, Region, Shipper, Invoice, ApplicationUser, ApplicationUserLoginInfo
- Seed data: 20 customers, 5 employees, 30 products, 50 orders, 20 invoices, test users "User"/"Admin" (empty passwords in debug)

## Tech Stack

- .NET 10.0 (net10.0 / net10.0-windows)
- DevExpress XAF 25.2.*, DevExpress AI Integration 25.2.*
- LLMTornado, Microsoft.Extensions.AI, Polly
- EF Core 8.0.18 + PostgreSQL
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
