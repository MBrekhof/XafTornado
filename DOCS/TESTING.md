# Testing Strategy

Decided 2026-08-20 after the DX 26.1 / EF Core 10 upgrade: the build was green and the app was
broken twice at runtime (connection string, DB creation). What needs testing here is **our AI tool
layer** — schema discovery, filter parsing, create/update mapping, navigation queue — not XAF CRUD
(DevExpress tests that).

## Layers

| # | Layer | Tool | Needs | Runs when |
|---|-------|------|-------|-----------|
| 1 | **Tool-level tests** (`XafTornado.ToolTests`, 24 tests) — invoke tools through `AIFunction.InvokeAsync` exactly like the model does, assert on JSON fields | xUnit, `WebApplicationFactory<Program>`, real PostgreSQL `xaftornado_test` dropped/recreated/seeded via `IDBUpdater` once per run | Docker `xaf-postgres` | every `dotnet test` |
| 2 | **Smoke test** (`XafTornado.Smoke`, 1 test) — login → list view has rows → AI panel → one tool call via `/api/test/tool` | C# Playwright (`Microsoft.Playwright.NUnit`); `scripts/smoke.ps1` updates the DB, starts the app, runs it, stops the app | Docker `xaf-postgres`; Debug build | before a PR that touches startup/config/packages |
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

1. ~~JSON tool results (`AIToolsProvider`)~~ done 2026-08-20 — prerequisite for 1 and 3; also simplifies Phase 3
   (mutation confirmation, security boundary), which rewrites the tool contract anyway.
2. ~~Layer 1 xUnit project.~~ done 2026-08-20 (`XafTornado.ToolTests`).
3. ~~Layer 2 smoke test.~~ done 2026-08-20 (`XafTornado.Smoke`, `scripts/smoke.ps1`).
4. Demote the YAML runner to layer 3: trace-based assertions, gate `TestApiController`.

Each step is its own branch + PR.

## Running

```bash
# Layer 1 — ~10 s incl. host boot + reseed; XAFTORNADO_TEST_PG overrides the server part of the connection string
docker start xaf-postgres
dotnet test XafTornado/XafTornado.ToolTests

# Layer 2 — ~30 s; screenshot next to the test DLL on failure
powershell -File scripts/smoke.ps1

# Layer 3
dotnet run --project XafTornado/XafTornado.Blazor.Server -- --updateDatabase --forceUpdate --silent
dotnet run --project XafTornado/XafTornado.Blazor.Server --urls http://localhost:5000
dotnet run --project XafTornado/XafTornado.Tests -- tests/sample-orders.yaml
```
