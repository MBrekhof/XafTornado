# Integrating the AI Assistant into an Existing XAF Application

This guide walks you through adding the LLMTornado-powered AI chat assistant to your own DevExpress XAF application. The integration is modular — you copy a set of service files, register them at startup, and the AI automatically discovers your data model at runtime.

## Prerequisites

- An existing DevExpress XAF application (EF Core, v25.2+) with Blazor Server and/or WinForms
- An API key for at least one supported AI provider (OpenAI, Anthropic, Google, Mistral, etc.)
- .NET 10.0 SDK

## Step 1: Add NuGet Packages

Add the following packages to your **Module** project (`.csproj`):

```xml
<PackageReference Include="LlmTornado" Version="*" />
<PackageReference Include="Polly" Version="*" />
<PackageReference Include="Microsoft.Extensions.AI" Version="*" />
<PackageReference Include="Markdig" Version="*" />
<PackageReference Include="HtmlSanitizer" Version="*" />
```

Add the AI chat control package to your **UI project(s)**:

```xml
<!-- Blazor Server project -->
<PackageReference Include="DevExpress.AIIntegration.Blazor.Chat" Version="25.2.*" />

<!-- WinForms project -->
<PackageReference Include="DevExpress.AIIntegration.WinForms.Chat" Version="25.2.*" />
```

## Step 2: Copy the Service Files

Copy the entire `Services/` folder from `XafTornado.Module` into your module project. These are the files you need:

| File | Purpose |
|------|---------|
| `SchemaDiscoveryService.cs` | Discovers all entities, properties, relationships, and enums via `ITypesInfo` at runtime. Generates the AI system prompt dynamically. Supports `[AIVisible]` and `[AIDescription]` attributes. |
| `AIChatService.cs` | Manages the `TornadoApi` client lifecycle, conversation history (50 message pairs), native tool-calling loop with Polly retry, and multi-provider support. |
| `AIChatClient.cs` | `IChatClient` adapter that bridges DevExpress AI chat controls to `AIChatService`. |
| `AIToolsProvider.cs` | Provides 12 AI tools: data access (`list_entities`, `describe_entity`, `query_entity`, `create_entity`, `update_entity`), navigation (`navigate_to_list`, `navigate_to_detail`), view management (`filter_active_list`, `clear_active_list_filter`, `save_active_view`, `close_active_view`), and context (`get_active_view`). |
| `ActiveViewContext.cs` | Singleton tracking what the user is currently viewing (entity, list vs. detail, current record). Updated by `ActiveViewTrackingController`. |
| `AIChatDefaults.cs` | Shared UI defaults: header text, empty-state text, prompt suggestions, and Markdown-to-HTML rendering. |
| `AIOptions.cs` | Configuration model bound from the `"AI"` section in `appsettings.json`. |
| `ServiceCollectionExtensions.cs` | `AddAIServices()` extension method that registers all services with DI. |

Also copy the platform-agnostic interface and logging:

| File | Purpose |
|------|---------|
| `INavigationService.cs` | Platform-agnostic interface for navigation, filtering, save, and close. |
| `AILogStore.cs` | In-memory log store for AI debug logging. |
| `AILoggerProvider.cs` | Custom logger provider that filters to AI-related categories. |

**For Blazor Server**, also copy from `XafTornado.Blazor.Server/Services/`:

| File | Purpose |
|------|---------|
| `BlazorNavigationService.cs` | `INavigationService` implementation using a producer/consumer queue for UI-thread-safe navigation, filtering, save, and close operations. |

**For WinForms**, also copy from `XafTornado.Win/Services/`:

| File | Purpose |
|------|---------|
| `WinNavigationService.cs` | `INavigationService` implementation for WinForms with queue-based navigation, filtering, save, and close. |

**Important:** Update the namespace in `SchemaDiscoveryService.cs` to match your project:

```csharp
// Change this to your own BusinessObjects namespace
var businessObjectNamespace = "YourApp.Module.BusinessObjects";
```

Also review the entity exclusion logic in `SchemaDiscoveryService.cs` and add any custom framework types from your application that should not be exposed to the AI (e.g., audit log entities, internal configuration objects). The preferred approach is to use `[AIVisible]` attributes — see [Customizing for Your Domain](#customizing-for-your-domain).

## Step 3: Copy the Non-Persistent AIChat Object

Copy `BusinessObjects/AIChat.cs` — this is the XAF non-persistent `DomainComponent` that provides the navigation item for the chat view.

## Step 4: Copy the Controllers

Copy these **shared controllers** from `XafTornado.Module/Controllers/`:

| File | Purpose |
|------|---------|
| `SelectAIModelController.cs` | Adds a toolbar action to switch AI models at runtime. Clears conversation history on model change. |
| `ActiveViewTrackingController.cs` | Updates `ActiveViewContext` whenever the user navigates to a new view. Required for `get_active_view` and `update_entity` tools. |

**For Blazor Server**, also copy from `XafTornado.Blazor.Server/Controllers/`:

| File | Purpose |
|------|---------|
| `NavigationExecutorController.cs` | Dequeues navigation/filter/save/close requests from `BlazorNavigationService` and executes them on the XAF UI thread. |

**For WinForms**, also copy from `XafTornado.Win/Controllers/`:

| File | Purpose |
|------|---------|
| `AISidePanelController.cs` | WindowController that adds a docked `AIChatControl` panel (right side, resizable) to the main window. |
| `WinNavigationExecutorController.cs` | Dequeues navigation/filter/save/close requests from `WinNavigationService` and executes them on the WinForms UI thread. Handles `Window.TemplateChanged` for deferred UI control capture. |

## Step 5: Copy the UI Components

**For Blazor Server**, copy:
- `Editors/AIChatViewItem/AIChat.razor` — The Blazor component wrapping `DxAIChat`
- `Editors/AIChatViewItem/AIChatViewItem.cs` — The XAF ViewItem that hosts the Razor component
- `Components/AISidePanel.razor` — Collapsible AI chat side panel

**For WinForms**, copy:
- `Editors/AIChatViewItemWin.cs` — The XAF ViewItem wrapping the DevExpress `AIChatControl`

**Note:** The WinForms side panel is handled by `AISidePanelController` (Step 4), not a separate editor.

## Step 6: Copy the Attribute Files

Copy from `Attributes/`:

| File | Purpose |
|------|---------|
| `AIVisibleAttribute.cs` | Controls which entities and properties the AI can discover. `[AIVisible]` = include, `[AIVisible(false)]` = exclude. |
| `AIDescriptionAttribute.cs` | Provides human-readable descriptions the AI sees for entities and properties. |

## Step 7: Register Services at Startup

**Blazor Server** — in `Startup.cs` or `Program.cs`:

```csharp
services.AddAIServices(builder.Configuration);
```

**WinForms** — in your `Startup.cs` (ApplicationBuilder):

```csharp
// Build configuration from appsettings.json + Development overlay
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
    .Build();

// Register AI services
builder.Services.AddAIServices(configuration);
builder.Services.AddDevExpressAI();

// Register WinForms navigation service
builder.Services.AddSingleton<WinNavigationService>();
builder.Services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<WinNavigationService>());
```

**WinForms** — in your `Program.cs`, after `winApplication.Setup()`:

```csharp
winApplication.Setup();

// Re-discover schema now that XAF types are fully registered.
var schemaService = winApplication.ServiceProvider.GetRequiredService<SchemaDiscoveryService>();
schemaService.InvalidateCache();
var aiService = winApplication.ServiceProvider.GetRequiredService<AIChatService>();
aiService.SystemMessage = schemaService.GenerateSystemPrompt();

// Give AI tools a reference to the application + UI sync context so they
// can create ObjectSpaces on the UI thread (required in WinForms).
var toolsProvider = winApplication.ServiceProvider.GetRequiredService<AIToolsProvider>();
toolsProvider.Application = winApplication;
toolsProvider.UiContext = SynchronizationContext.Current;

winApplication.Start();
```

**Important WinForms note:** In WinForms, `INonSecuredObjectSpaceFactory` does not work from manually-created DI scopes because XAF's `SimpleValueManager` doesn't propagate application context. The `Application` and `UiContext` properties on `AIToolsProvider` enable ObjectSpace creation via `XafApplication.CreateObjectSpace()` dispatched to the UI thread.

## Step 8: Configure appsettings.json

Add the `"AI"` section to your base `appsettings.json` (without secrets):

```json
{
  "AI": {
    "Model": "claude-sonnet-4-6",
    "DefaultProvider": "anthropic",
    "MaxOutputTokens": 16384,
    "MaxToolIterations": 10,
    "TimeoutSeconds": 120,
    "ApiKeys": {
      "anthropic": "",
      "openai": ""
    }
  }
}
```

Create `appsettings.Development.json` (add to `.gitignore`) with your actual API keys:

```json
{
  "AI": {
    "ApiKeys": {
      "anthropic": "sk-ant-your-key-here",
      "openai": "sk-your-key-here"
    }
  }
}
```

You only need keys for the providers you want to use. The Development file overrides the base settings and is excluded from source control.

### Full Configuration Reference

| Setting | Default | Description |
|---------|---------|-------------|
| `Model` | `"claude-sonnet-4-6"` | Default AI model ID. Can be switched at runtime. |
| `DefaultProvider` | `"anthropic"` | Fallback provider if not derivable from model name. |
| `ApiKeys` | `{}` | Per-provider API keys. Supported providers: `anthropic`, `openai`, `google`, `mistral`, `cohere`, `voyage`, `upstage`. |
| `MaxOutputTokens` | `16384` | Maximum output tokens per LLM response. |
| `MaxToolIterations` | `10` | Maximum tool-calling loop iterations per message. |
| `TimeoutSeconds` | `120` | Request timeout in seconds. |

## Step 9: Run and Verify

1. Build your application
2. Run it and log in
3. **Blazor**: Navigate to the **AI Chat** item in the sidebar or expand the AI side panel. **WinForms**: The AI chat panel is docked on the right side of the main window.
4. Ask: *"What entities are available in the database?"*
5. The AI should list all your business objects with their descriptions
6. Try: *"Open the customers list"* — the AI should navigate the app
7. Try: *"Filter by country USA"* — the AI should apply a criteria filter

## What Gets Discovered Automatically

`SchemaDiscoveryService` picks up the following from your business objects without any additional configuration:

- All persistent entity types in your `BusinessObjects` namespace (or only those with `[AIVisible]` if any entity uses the attribute)
- Scalar properties with their CLR types (string, int, decimal, DateTime, bool, etc.)
- Enum properties with all possible values
- To-one navigation properties (e.g., `Order.Customer`)
- To-many collection properties (e.g., `Customer.Orders`)
- `[AIDescription]` text for entities and properties
- The `[DefaultProperty]` attribute is used by the tools to display human-readable names when resolving relationships

## Customizing for Your Domain

### Controlling AI Visibility with Attributes

Use `[AIVisible]` and `[AIDescription]` to control what the AI sees:

```csharp
[AIVisible]
[AIDescription("Customer orders with status tracking and shipping details")]
public class Order : BaseObject
{
    [AIDescription("Current processing status")]
    public virtual OrderStatus Status { get; set; }

    [AIVisible(false)]  // Hide internal field from AI
    public virtual string InternalNotes { get; set; }
}
```

When any entity in your model has `[AIVisible]`, the discovery switches to **opt-in mode** — only entities with `[AIVisible]` are exposed to the AI. If no entity uses the attribute, all entities are discovered (backward-compatible).

### Other Customizations

- **System prompt tone**: Edit `SchemaDiscoveryService.GenerateSystemPrompt()` to change the opening line from "order management application" to whatever fits your domain.
- **Additional tools**: Add new methods to `AIToolsProvider` with `[Description]` attributes and register them in `CreateTools()`. For example, you could add domain-specific tools like `generate_report` or `send_notification`.
- **Prompt suggestions**: Edit `AIChatDefaults.PromptSuggestions` to provide domain-specific example prompts for your users.
- **Model list**: Edit the `AvailableModels` array in `SelectAIModelController` to include only the models relevant to your subscription.
