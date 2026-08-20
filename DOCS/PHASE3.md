# XafTornado — Phase 3 Development Instructions

## Project Overview

XafTornado is a DevExpress XAF application (Blazor Server + WinForms) that integrates a conversational AI assistant capable of querying, creating, updating, and navigating XAF business objects via natural language. It uses LLMTornado for multi-provider LLM support and dynamic schema discovery via XAF `ITypesInfo`.

**Repository:** https://github.com/MBrekhof/XafTornado  
**Tech stack:** .NET 10, XAF 25.2.*, EF Core 8, PostgreSQL, LLMTornado, Blazor Server, WinForms  
**Existing phases:** Phase 1 (attribute-based schema filtering) and Phase 2 (two-tier discovery with `describe_entity`) are complete.

---

## Phase 3 Goals

Phase 3 has three parallel tracks. Implement them in order — each track depends on the previous.

---

## Track A: Mutation Confirmation Flow

### Problem
`create_entity` and `update_entity` currently fire-and-forget. There is no user confirmation before a write commits. This is the primary blocker for production use.

### Goal
Introduce a **speculative execution / preview** pattern. Before any write commits, the AI presents what it is about to do and waits for explicit user confirmation.

### Design

Add a new tool: `preview_mutation`

```
Tool name: preview_mutation
Input:
  - operation: "create" | "update" | "delete"
  - entityName: string
  - recordId: string? (null for create)
  - proposedValues: Dictionary<string, object>
Output:
  - previewId: Guid (short-lived, in-memory)
  - humanReadableSummary: string (e.g. "Update Order #4471: Status Pending → Approved, ShipCity Berlin → Amsterdam")
  - fieldChanges: list of { field, oldValue, newValue }
```

The AI **must** call `preview_mutation` before calling `create_entity`, `update_entity`, or `delete_entity`. The system prompt must enforce this with explicit instruction.

Add a corresponding tool: `commit_mutation`

```
Tool name: commit_mutation
Input:
  - previewId: Guid
Output:
  - success: bool
  - message: string
```

And a cancellation tool: `cancel_mutation`

```
Tool name: cancel_mutation
Input:
  - previewId: Guid
```

### Implementation notes

- Store pending previews in a scoped `IMutationPreviewStore` (in-memory dictionary keyed by Guid, with a 5-minute expiry).
- `commit_mutation` retrieves the preview by ID and executes the actual ObjectSpace write.
- The chat UI must detect when the AI presents a preview and optionally render a structured confirmation card (not just raw text). This is a UI enhancement — start with the text summary, add the card as a follow-on.
- Update the system prompt in `SchemaDiscoveryService` or `AIChatService` to include: *"Always call preview_mutation before any create, update, or delete operation. Never call create_entity, update_entity, or delete_entity directly. Only call commit_mutation after the user explicitly says yes, confirms, or approves."*

---

## Track B: Security Boundary

### Problem
`AIToolsProvider` uses `INonSecuredObjectSpaceFactory`, bypassing XAF's role-based permission system. The AI can read and write any data regardless of the current user's permissions — a privilege escalation vector.

### Goal
All AI data operations must be gated by the current user's XAF security permissions, identical to what the UI enforces.

### Design

Replace `INonSecuredObjectSpaceFactory` with `IObjectSpaceFactory` in `AIToolsProvider`. XAF will then enforce `SecurityStrategy` rules on every query and mutation.

In Blazor, the current user context flows via `ISecurityStrategyBase` which is scoped. This should work without changes once the factory is swapped.

In WinForms, `XafApplication.CreateObjectSpace()` is already security-aware — verify this path is correctly used.

**Handle permission failures gracefully:** when a query returns empty due to access denial (not because no records exist), the AI should say "you don't have permission to view X" rather than "no records found." Catch `SecurityAccessDeniedException` in tool execution and return a structured error the AI can reason about.

Add a `get_current_user_permissions` tool (optional but useful):
```
Output:
  - userName: string
  - roles: string[]
  - canCreate: string[] (entity names)
  - canRead: string[] (entity names)
  - canWrite: string[] (entity names)
  - canDelete: string[] (entity names)
```

This lets the AI answer "what can I do?" and avoid calling tools it knows will fail.

### Implementation notes

- Review every `CreateObjectSpace()` call in `AIToolsProvider` and replace with the secured factory.
- Add integration tests (or at minimum manual test cases) verifying that a user in a read-only role cannot create or update records via the AI chat.
- Document the security model in `HOW_TO_IMPLEMENT.md`.

---

## Track C: Conversation Persistence

### Problem
Conversation history lives in memory (up to 50 message pairs). It is lost on page refresh or app restart. Power users lose context; there is no way to review or resume previous sessions.

### Goal
Persist conversation history as XAF business objects. This gives you conversation management UI for free.

### Design

Add two new EF Core entities to `XafTornado.Module`:

```csharp
[DefaultClassOptions]
[NavigationItem("AI")]
public class AiConversation : BaseObject
{
    public virtual string Title { get; set; }           // auto-generated from first user message (truncated)
    public virtual DateTime StartedAt { get; set; }
    public virtual DateTime LastMessageAt { get; set; }
    public virtual string ModelName { get; set; }       // model used in this session
    public virtual IList<AiMessage> Messages { get; set; } = new ObservableCollection<AiMessage>();
}

[DefaultClassOptions]
[NavigationItem("AI")]
public class AiMessage : BaseObject
{
    public virtual AiConversation Conversation { get; set; }
    public virtual string Role { get; set; }            // "user" | "assistant" | "tool"
    public virtual string Content { get; set; }         // full text content
    public virtual string ToolName { get; set; }        // populated for tool calls/results
    public virtual DateTime SentAt { get; set; }
    public virtual int SequenceNumber { get; set; }
}
```

Update `AIChatService`:
- On session start, create an `AiConversation` record.
- After each exchange, persist the new `AiMessage` records.
- On session resume (new browser tab, app restart), offer to load a recent conversation.

Add a "New Conversation" action and a "Load Conversation" list view to the AI navigation section.

### Implementation notes

- Keep the in-memory history as-is for the active session. Persistence is write-through, not the primary read path.
- `Content` for large assistant responses can be long — use `[MaxLength(-1)]` or `nvarchar(max)` / PostgreSQL `text`.
- The `AiConversation` ListView makes a natural admin surface: see all conversations, who asked what, which model was used.
- Apply `[AIVisible(false)]` to both entities so the AI doesn't try to query its own conversation log (circular and confusing).

---

## Track D: Richer Query Capability (stretch goal)

### Problem
`query_entity` supports only `PropertyName=value` equality filters. Complex domain queries fail or produce wrong results.

### Goal
Support DevExpress criteria syntax in `query_entity` and `filter_active_list`, with LLM guidance on how to construct it.

### Design

Update the system prompt to include a section on criteria syntax with examples:

```
DevExpress criteria syntax examples:
  - [Status] = 'Processing'
  - [OrderDate] >= #2025-01-01# And [OrderDate] < #2026-01-01#
  - [Freight] > 50 And Not ([Status] = 'Cancelled')
  - [Customer.CompanyName] = 'Alfreds Futterkiste'
  - IsNullOrEmpty([ShipCity])
  - [OrderItems][UnitPrice > 100]   (collection filter)
```

Update `query_entity` to accept an optional `criteriaExpression: string` parameter alongside the existing simple filters. When present, use `CriteriaOperator.Parse()` to build the filter and apply it via `IObjectSpace.GetObjects(type, criteria)`.

Validate and catch `CriteriaParserException` — return a structured error so the AI can retry with a corrected expression.

---

## Cross-Cutting Concerns

### Error handling standard
All tool methods must return a consistent result shape:

```csharp
public class ToolResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }      // human-readable, LLM-visible
    public object? Data { get; set; }
}
```

The LLM must be able to reason about failures and communicate them to the user in plain language rather than surfacing raw exception messages.

### Token budget awareness
- The system prompt currently includes all entity names. As the domain grows, monitor prompt token cost.
- `describe_entity` lazy-loads detail — preserve this pattern for all new tools.
- New tools (`preview_mutation`, `commit_mutation`, `cancel_mutation`, `get_current_user_permissions`) must have compact descriptions in the tool manifest.

### Testing checkpoints
After each track, verify these scenarios manually before moving to the next:

**Track A (Mutation Confirmation):**
- [ ] "Create a new order for Alfreds Futterkiste" → AI calls `preview_mutation`, shows summary, waits
- [ ] User says "yes" → AI calls `commit_mutation` → record created
- [ ] User says "cancel" → AI calls `cancel_mutation` → no record created
- [ ] AI never calls `create_entity` directly without a prior `preview_mutation`

**Track B (Security):**
- [ ] Read-only user cannot create records via chat
- [ ] Permission denied returns a clear message, not "no records found"
- [ ] Admin user can still perform all operations

**Track C (Persistence):**
- [ ] Conversation saved after each exchange
- [ ] Page refresh → option to resume last conversation
- [ ] AiConversation ListView visible and queryable by admin
- [ ] AI cannot see or query AiConversation / AiMessage entities

**Track D (Criteria):**
- [ ] "Show orders from last month where freight exceeds 50" → correct criteria generated and executed
- [ ] Invalid criteria expression → AI retries with correction, not a crash

---

## Files Most Likely to Change

| File | Track |
|------|-------|
| `AIToolsProvider.cs` | A, B, D |
| `AIChatService.cs` | A, C |
| `SchemaDiscoveryService.cs` | A (system prompt update), D |
| `ServiceCollectionExtensions.cs` | A (IMutationPreviewStore registration), C |
| New: `MutationPreviewStore.cs` | A |
| New: `AiConversation.cs`, `AiMessage.cs` | C |
| `DbContext` + migrations | C |
| `HOW_TO_IMPLEMENT.md` | B (security documentation) |
| Blazor `AIChat.razor` | A (confirmation card UI) |
| WinForms `AISidePanelController.cs` | A (confirmation card UI) |

---

## What NOT to Change

- The two-tier schema discovery pattern (entity list in system prompt + `describe_entity` on demand) — it works well, do not collapse it.
- The `ActiveViewContext` / `ActiveViewTrackingController` pattern — active view awareness is a core differentiator.
- The `INavigationService` abstraction with platform-specific implementations — keep Blazor and WinForms paths separate.
- LLMTornado integration — do not switch to a different LLM client library.
- The `[AIVisible]` / `[AIDescription]` attribute system — extend it, don't replace it.

---

## Definition of Done for Phase 3

Phase 3 is complete when:

1. No write operation (create/update/delete) can execute without an explicit user confirmation step.
2. All AI data operations are bounded by XAF security permissions of the logged-in user.
3. Conversation history is persisted and resumable across sessions.
4. All four testing checklists above pass.
5. `HOW_TO_IMPLEMENT.md` is updated to reflect the security model and the confirmation flow.
6. `BEHIND_THE_SCENES.md` is updated with a walkthrough of the new preview → confirm → commit lifecycle.
