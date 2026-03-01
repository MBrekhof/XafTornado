# XafTornado

Integrating [LLMTornado](https://github.com/lofcz/LlmTornado) into a DevExpress XAF application to create an in-app AI assistant that queries live business data, navigates views, creates and updates records conversationally, and works on both Blazor Server and WinForms.

## Features

- **Dynamic Schema Discovery** — The AI assistant automatically discovers all entities, properties, relationships, and enum values at runtime via XAF `ITypesInfo` reflection. Add or modify business objects and the AI immediately knows about them.
- **12 AI Tools** — Generic data tools (`list_entities`, `describe_entity`, `query_entity`, `create_entity`, `update_entity`), navigation tools (`navigate_to_list`, `navigate_to_detail`), view management (`filter_active_list`, `clear_active_list_filter`, `save_active_view`, `close_active_view`), and context awareness (`get_active_view`).
- **Active View Awareness** — The AI knows what the user is currently viewing (entity, list vs. detail, current record) and can act on it contextually ("filter this list", "save this record", "close this view").
- **Navigation & Filtering** — The AI can navigate the application to any list or detail view and apply DevExpress criteria filters on the active list — all from natural language.
- **Conversation History** — Full conversation continuity across messages (up to 50 message pairs), so the AI remembers previous questions and answers within a session.
- **Date & Time Awareness** — The system prompt includes the current date and time, enabling time-relative queries ("orders from last month", "invoices due this week").
- **Multi-Provider AI** — Supports OpenAI, Anthropic, Google, Mistral, Cohere, and more via LLMTornado. Switch providers and models at runtime.
- **Attribute-Based Schema Control** — `[AIVisible]` and `[AIDescription]` attributes control which entities and properties the AI can see, with human-readable context.
- **Two-Tier Discovery** — Lightweight system prompt (entity names only) with on-demand `describe_entity` tool for detail loading, minimizing per-message token cost.
- **Dual Platform** — Full support for both Blazor Server and WinForms using DevExpress AI chat controls (`DxAIChat` and `AIChatControl`), backed by the same shared module.
- **Markdown Rendering** — AI responses rendered as formatted HTML with table, code block, and list support via Markdig + HtmlSanitizer.
- **Runtime Model Switching** — Switch between AI models (Claude Sonnet 4, GPT-4o, Gemini 2.5 Pro, etc.) at runtime via a toolbar action.

## Architecture

```
XafTornado.Module/          Platform-agnostic core (business objects, services, controllers)
  BusinessObjects/                EF Core entities (Northwind-style domain)
  Attributes/                     AIVisible, AIDescription attributes
  Services/                       LLMTornado integration layer
    SchemaDiscoveryService        Discovers entities via ITypesInfo, generates system prompt
    AIChatService                 Manages TornadoApi lifecycle, conversation history, tool loop
    AIChatClient                  IChatClient adapter for DevExpress AI controls
    AIToolsProvider               12 AI tools (data, navigation, view management, context)
    ActiveViewContext             Tracks what the user is currently viewing
    AIChatDefaults                Shared UI config, prompt suggestions, Markdown rendering
    AIOptions                     Configuration model bound from appsettings.json
    ServiceCollectionExtensions   AddAIServices() — registers all services with DI
  Controllers/                    XAF controllers
    SelectAIModelController       Runtime model switching
    ActiveViewTrackingController  Updates ActiveViewContext on view changes
    NavigationExecutorController  Executes navigation/filter on XAF UI thread

XafTornado.Blazor.Server/   Blazor Server UI
  Services/
    BlazorNavigationService       INavigationService impl (queue-based for UI thread safety)
  Controllers/
    NavigationExecutorController  Dequeues nav/filter/save/close on Blazor UI thread
  Editors/AIChatViewItem/         AIChat.razor (DxAIChat component)
  Components/
    AISidePanel.razor             Collapsible AI chat side panel

XafTornado.Win/             WinForms UI
  Services/
    WinNavigationService          INavigationService impl for WinForms (queue-based)
  Controllers/
    AISidePanelController         Docked AIChatControl panel (right side, resizable)
    WinNavigationExecutorController  Dequeues nav/filter/save/close on WinForms UI thread
  Editors/                        AIChatControl integration (ViewItem wrapper)
```

### How It Works

1. **Startup** — `ServiceCollectionExtensions.AddAIServices()` registers all services. `SchemaDiscoveryService` reflects over `ITypesInfo` to discover the data model. The system prompt and AI tools are generated dynamically from this metadata. In WinForms, schema discovery is re-run after `Application.Setup()` to ensure all XAF types are registered.

2. **Multi-Provider Init** — `AIChatService` lazy-initializes a `TornadoApi` client from API keys configured in `appsettings.json` (or `appsettings.Development.json` for local keys). Multiple providers can be configured simultaneously; the correct one is auto-selected based on the model name prefix.

3. **AI Tools** — `AIToolsProvider` exposes 12 tools to the LLM:
   - `list_entities` — Returns all entity names with descriptions
   - `describe_entity` — Returns full schema details for a single entity (properties, types, relationships, enums)
   - `query_entity` — Queries any entity with optional `PropertyName=value` filters
   - `create_entity` — Creates a record for any entity
   - `update_entity` — Updates an existing record
   - `navigate_to_list` / `navigate_to_detail` — Navigate the app to list or detail views
   - `filter_active_list` / `clear_active_list_filter` — Apply or clear DevExpress criteria filters
   - `save_active_view` / `close_active_view` — Save or close the current view
   - `get_active_view` — Returns what the user is currently viewing

4. **Native Tool Loop** — `AIChatService.AskAsync()` sends the user's message with full conversation history, receives the response via `GetResponseRich()`, and executes any tool calls in a loop (up to `MaxToolIterations`). Tool results are fed back to the LLM until it produces a final text response.

5. **Chat Flow** — User messages flow through `DxAIChat` (Blazor) or `AIChatControl` (WinForms) → `AIChatClient` (IChatClient adapter) → `AIChatService` → LLMTornado → AI model. The response streams back through the same chain and is rendered as formatted HTML.

6. **Platform-Specific ObjectSpace Handling** — In Blazor, AI tools create ObjectSpaces via `INonSecuredObjectSpaceFactory` from DI scopes (AsyncLocal carries the XAF context). In WinForms, this factory doesn't work from manual DI scopes, so tools use `XafApplication.CreateObjectSpace()` directly, dispatched to the UI thread via `SynchronizationContext.Send()`.

For a detailed step-by-step walkthrough of how a user question becomes a data-driven answer — including what the AI model sees, how it decides which tool to call, and how the query executes against the database — see **[Behind the Scenes](BEHIND_THE_SCENES.md)**.

For a step-by-step guide on adding the AI assistant to your own XAF application — see **[How to Implement](HOW_TO_IMPLEMENT.md)**.

## Data Model

A Northwind-style order management domain with 13 business entities:

| Entity | Key Properties | Relationships |
|--------|---------------|---------------|
| **Customer** | CompanyName, ContactName, Phone, Email, City, Country | has many Orders |
| **Order** | OrderDate, Status (New/Processing/Shipped/Delivered/Cancelled), Freight, ShipCity | belongs to Customer, Employee, Shipper, Invoice; has many OrderItems |
| **OrderItem** | UnitPrice, Quantity, Discount | belongs to Order, Product |
| **Product** | Name, UnitPrice, UnitsInStock, Discontinued | belongs to Category, Supplier |
| **Category** | Name, Description | has many Products |
| **Supplier** | CompanyName, ContactName, Phone, Email | has many Products |
| **Employee** | FirstName, LastName, Title, HireDate | belongs to Department; has many Orders, Territories, DirectReports |
| **Department** | Name, Code, Location, Budget, IsActive | has many Employees |
| **EmployeeTerritory** | (join table) | belongs to Employee, Territory |
| **Territory** | Name | belongs to Region |
| **Region** | Name | has many Territories |
| **Shipper** | CompanyName, Phone | has many Orders |
| **Invoice** | InvoiceNumber, InvoiceDate, DueDate, Status (Draft/Sent/Paid/Overdue/Cancelled) | has many Orders |

Seed data is generated automatically on first run: 20 customers, 5 employees across 5 departments, 3 shippers, 30 products across 8 categories, 50 orders, and 20 invoices.

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- [DevExpress Universal Subscription](https://www.devexpress.com/) (v25.2+) with a valid NuGet feed configured
- An API key for at least one supported AI provider (OpenAI, Anthropic, Google, Mistral, etc.)

## Getting Started

### 1. Clone and build

```bash
git clone https://github.com/MBrekhof/XafTornado.git
cd XafTornado
dotnet build XafTornado.slnx
```

### 2. Configure API keys

Create `appsettings.Development.json` (gitignored) alongside `appsettings.json` in both UI projects with your provider API keys:

```json
{
  "AI": {
    "ApiKeys": {
      "anthropic": "sk-ant-...",
      "openai": "sk-..."
    }
  }
}
```

You only need a key for the provider(s) you want to use. The Development file overrides the base `appsettings.json` and is excluded from source control. See the [Configuration](#configuration) section for all available settings.

### 3. Run

```bash
# Blazor Server (web)
dotnet run --project XafTornado/XafTornado.Blazor.Server

# WinForms (desktop, Windows only)
dotnet run --project XafTornado/XafTornado.Win
```

Log in with user **Admin** (empty password) or **User** (empty password).

On Blazor Server, navigate to the **AI Chat** item in the sidebar or expand the AI side panel. On WinForms, the AI chat panel is docked on the right side of the main window and can be resized via the splitter.

## Configuration

All AI settings are in the `"AI"` section of `appsettings.json`:

| Setting | Default | Description |
|---------|---------|-------------|
| `Model` | `"claude-sonnet-4-6"` | Default AI model ID. Can be switched at runtime via the toolbar. |
| `DefaultProvider` | `"anthropic"` | Fallback provider if not derivable from the model name. |
| `ApiKeys` | `{}` | Per-provider API keys. Key = provider ID (`anthropic`, `openai`, `google`, `mistral`, `cohere`, `voyage`, `upstage`), value = API key. |
| `MaxOutputTokens` | `16384` | Maximum output tokens per LLM response. |
| `MaxToolIterations` | `10` | Maximum tool-calling loop iterations per message. |
| `TimeoutSeconds` | `120` | Request timeout in seconds. |

### Available AI Models

Selectable at runtime via the model switcher toolbar action:

| Provider | Models |
|----------|--------|
| Anthropic | Claude Sonnet 4, Claude Sonnet 4.5, Claude Opus 4 |
| OpenAI | GPT-4o, GPT-4o Mini, GPT-4.1, GPT-4.1 Mini, o3-mini, o4-mini |
| Google | Gemini 2.5 Pro, Gemini 2.5 Flash |
| Mistral | Mistral Large |

## Example Prompts

| Use Case | Example Prompt |
|----------|---------------|
| Data Query | "Show me all orders for Around the Horn that are still processing" |
| Schema Discovery | "What entities are available in the database?" |
| Entity Details | "Describe the Product entity" |
| Create Record | "Create a new order for Alfreds Futterkiste: 10 units of Chai, ship via Speedy Express" |
| Update Record | "Change the status of this order to Shipped" |
| Navigation | "Open the products list" |
| Navigation (detail) | "Open the record for customer Alfreds Futterkiste" |
| Filtering | "Filter the orders list to show only orders with status Processing" |
| Clear Filter | "Remove the filter" |
| Save & Close | "Save this record and close it" |
| Active View | "What am I looking at right now?" |
| Date-Relative | "Show me invoices due this month" |
| Follow-Up | "Now show me just the overdue ones" (remembers previous context) |

## Dynamic Schema Discovery in Action

The AI assistant does not use hardcoded entity definitions. Instead, `SchemaDiscoveryService` reflects over the XAF `ITypesInfo` type system at startup to discover every persistent entity, its properties, relationships, and enum values. This means you can add, rename, or remove business objects and the AI immediately knows about the changes — no service code modifications required.

### Example: Adding the Department Entity

The `Department` entity was added to demonstrate this. Here is everything that was needed:

**1. Create the business object** (`BusinessObjects/Department.cs`):

```csharp
[DefaultClassOptions]
[NavigationItem("HR")]
[DefaultProperty(nameof(Name))]
public class Department : BaseObject
{
    [StringLength(128)]
    public virtual string Name { get; set; }

    [StringLength(64)]
    public virtual string Code { get; set; }

    [StringLength(256)]
    public virtual string Location { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public virtual decimal Budget { get; set; }

    public virtual bool IsActive { get; set; } = true;

    public virtual IList<Employee> Employees { get; set; } = new ObservableCollection<Employee>();
}
```

**2. Add a `DbSet` to the `DbContext`:**

```csharp
public DbSet<Department> Departments { get; set; }
```

**3. Configure relationships in `OnModelCreating`** (standard EF Core):

```csharp
modelBuilder.Entity<Department>()
    .HasMany(d => d.Employees)
    .WithOne(e => e.Department)
    .HasForeignKey(e => e.DepartmentId)
    .OnDelete(DeleteBehavior.SetNull);
```

**4. Run the app and ask the AI:**

| Prompt | What happens |
|--------|-------------|
| "What entities are available?" | The AI calls `list_entities` and Department appears in the list with its description — automatically. |
| "What departments do we have?" | The AI calls `describe_entity` then `query_entity` and returns all department records. |
| "Which employees are in the Sales department?" | The AI calls `query_entity` with `entityName=Employee` and `filter=Department=Sales`, navigating the relationship. |
| "Create a new department called R&D with code RND, budget 800000, located in Building D" | The AI calls `create_entity` and creates the record. |

No changes were made to `SchemaDiscoveryService`, `AIToolsProvider`, `AIChatService`, or any other service file. The AI discovered Department entirely through runtime reflection.

## Roadmap

Phase 1 (attribute-based schema filtering) and Phase 2 (two-tier discovery with `describe_entity`) are complete. Phase 3 (validation testing with large data models, token reduction measurement) is next.

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Framework | DevExpress XAF 25.2.* |
| UI (Web) | Blazor Server, DevExpress `DxAIChat` |
| UI (Desktop) | WinForms, DevExpress `AIChatControl` |
| AI | LLMTornado, Microsoft.Extensions.AI |
| Database | EF Core 8.0.18 + PostgreSQL |
| Resilience | Polly (retry with exponential backoff) |
| Rendering | Markdig (Markdown), HtmlSanitizer (XSS protection) |
| Runtime | .NET 10.0 |

## Articles

This project started from the ideas in this two-part series. The current implementation has evolved significantly beyond what the articles describe.

- [The Day I Integrated LLMTornado Inside My XAF App — Part 1](https://www.jocheojeda.com/2026/02/16/the-day-i-integrated-github-copilot-sdk-inside-my-xaf-app-part-1/)
- [The Day I Integrated LLMTornado Inside My XAF App — Part 2](https://www.jocheojeda.com/2026/02/16/the-day-i-integrated-github-copilot-sdk-inside-my-xaf-app-part-2/)

## License

This project is provided as a reference implementation for educational purposes.
