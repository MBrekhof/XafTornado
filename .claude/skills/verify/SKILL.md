---
name: verify
description: Build the solution, run the tool-level tests, and run the LLM evals if a Blazor Server instance is up
---

Layers are described in `DOCS/TESTING.md`.

## Steps

1. **Build the solution:**
   ```bash
   dotnet build XafTornado.slnx
   ```
   If the build fails, report errors and stop.

2. **Tool-level tests (layer 1, always).** Needs the `xaf-postgres` Docker container:
   ```bash
   docker start xaf-postgres
   dotnet test XafTornado/XafTornado.ToolTests --no-build
   ```
   If Docker is not running, start Docker Desktop first; do not skip this layer.

3. **LLM evals (layer 3, only if the app is up)** at http://localhost:5000:
   ```bash
   curl -s -o /dev/null -w "%{http_code}" http://localhost:5000/ || echo "not running"
   dotnet run --project XafTornado/XafTornado.Tests --no-build -- tests/llm-evals.yaml
   ```
   If the server is NOT running, report:
   > "LLM evals skipped — Blazor Server not running at http://localhost:5000."
   On a failed eval, quote the `Calls:` line — it shows what the model did instead.

4. **Smoke test (layer 2)** only when the change touched startup, config, or packages:
   ```bash
   powershell -File scripts/smoke.ps1
   ```
   It hosts the app itself; port 5000 must be free.

5. **Summarize**: build status, tool-test count, eval pass/fail (or skipped), smoke result (or not run).
