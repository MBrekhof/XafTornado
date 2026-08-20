# Testing Strategy

Decided 2026-08-20 after the DX 26.1 / EF Core 10 upgrade: the build was green and the app was
broken twice at runtime (connection string, DB creation). What needs testing here is **our AI tool
layer** — schema discovery, filter parsing, create/update mapping, navigation queue — not XAF CRUD
(DevExpress tests that).

## Layers

| # | Layer | Tool | Needs | Runs when |
|---|-------|------|-------|-----------|
| 1 | **Tool-level tests** — call `AIToolsProvider` tools directly, assert on JSON fields | xUnit, `WebApplicationFactory<Startup>`, real PostgreSQL (`xaftornado_test`) seeded via `IDBUpdater` | Docker `xaf-postgres` | every `dotnet test` |
| 2 | **Smoke test** — update DB → login → list view → AI panel → one tool call | C# Playwright (`Microsoft.Playwright.NUnit`) | running app + Postgres | before a PR that touches startup/config/packages |
| 3 | **LLM evals** — natural-language prompt → assert on the **tool-call trace**, not the prose | existing YAML runner (`XafTornado.Tests`) via `/api/test/*` | running app + provider API key | opt-in / manual |

## Ground rules

- **Tools return JSON, never prose.** Every tool result is a single JSON object; errors are
  `{"error": "...", ...hints}`. Records carry `"id"` (the XAF key) so follow-up tools use real keys.
  Tests assert on fields; string-matching on wording is forbidden.
- **Layer 3 asserts on behaviour, not text.** "`query_entity` was called with `entityName=Customer`"
  is a valid assertion; `contains: "Germany"` on the model's answer is not (model wording drifts,
  assertions rot).
- **`TestApiController` is Development-only.** It is an unauthenticated, non-secured write endpoint.
- **Real Postgres over in-memory** for layer 1: Npgsql filter translation is the thing most likely to
  break, and the container already exists.
- **No CI until there is a suite worth running.** Exit codes are already 0/1; a workflow is one file
  when the time comes.

## Order of work

1. JSON tool results (`AIToolsProvider`) — prerequisite for 1 and 3; also simplifies Phase 3
   (mutation confirmation, security boundary), which rewrites the tool contract anyway.
2. Layer 1 xUnit project.
3. Layer 2 smoke test.
4. Demote the YAML runner to layer 3: trace-based assertions, gate `TestApiController`.

Each step is its own branch + PR.

## Running

```bash
# Layer 1 (after step 2 lands)
docker start xaf-postgres
dotnet test XafTornado/XafTornado.Module.Tests

# Layer 3
dotnet run --project XafTornado/XafTornado.Blazor.Server -- --updateDatabase --forceUpdate --silent
dotnet run --project XafTornado/XafTornado.Blazor.Server --urls http://localhost:5000
dotnet run --project XafTornado/XafTornado.Tests -- tests/sample-orders.yaml
```
