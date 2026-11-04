# LlmTornado 3.8.68 → 3.8.82: what changed, what it means for XafTornado

Date: 2026-11-04. Pin in `XafTornado.Module.csproj` before this review: **3.8.68** (2026-09-19). NuGet latest: **3.8.82**
(2026-11-04). Outcome: bumped to 3.8.82 on the same day (AI-013), together with DevExpress 26.1.6 (DX-261-6).
Releases in between: 3.8.69, .71, .73, .74, .75, .76, .77, .78, .79, .80, .81, .82 (twelve, near-weekly since mid-October).

## Verdict

Bump to 3.8.82. The trial bump builds clean, the 71 tool-level tests pass, and the 7 LLM evals pass end to end
(Mistral `ministral-8b-latest`, through the hosted Blazor app). No API we call was renamed or removed. Nothing in
the release notes forces a code change; two small follow-ups are worth taking with the bump (below).

## Trial evidence (trial bump in the working tree, before the AI-013 branch)

| Check | Result |
| --- | --- |
| `dotnet build XafTornado.slnx` with LlmTornado 3.8.82 | Build succeeded, 0 errors |
| `dotnet test XafTornado.ToolTests` | 71 passed, 0 failed |
| `tests/llm-evals.yaml` against the hosted app | 7 passed, 0 failed (Mistral) |
| Same evals on OpenAI `gpt-5.5` (new key; the old one answered 401 `expired_secret_key`) | 7 passed, 0 failed |
| Same evals on Anthropic `claude-sonnet-5-5` (new key, catalog model via `ResolveModel`) | 7 passed, 0 failed |
| Anthropic with the Win dev-config key | that key answers 401 `invalid x-api-key`, also from a plain curl to `/v1/models`: dead key, not the library |

The trial ran on a tree that already carried the DevExpress 26.1.5 → 26.1.6 bump (DX-261-6); both bumps ship in the
AI-013 branch.

## What we use, and what changed underneath it

Our surface is small: `TornadoApi(List<ProviderAuthentication>)`, `api.Chat.CreateConversation(ChatRequest)`,
`AppendSystemMessage` / `AppendUserInput` / `AppendExampleChatbotOutput`, `GetResponseRich(fnHandler)`,
`ChatRichResponse.Blocks` / `.Usage`, `Tool(ToolFunction(name, description, JsonElement schema))`,
`ChatModel.AllModelsMap`, `new ChatModel(id, provider)`, and the typed Claude 5 / GPT-6 constants in the picker.

Relevant upstream changes, by effect on us:

**Affects the path we run (bug fixes, no code change)**
- 3.8.77 "Route a model of unknown provider to the authenticated provider". We already pass the provider explicitly
  (`new ChatModel(modelId, provider)`), so this only matters for the catalog fallback; harmless.
- 3.8.77 "Resolve provider finish reasons regardless of case", "Map Mistral, Zai and DeepSeek finish reasons" —
  Mistral finish reasons were previously unmapped; our loop keys on `Blocks`, not finish reason, so no behaviour change.
- 3.8.75 "Fix parallel tool calls with Anthropic" — the fix is in the Anthropic *streaming* accumulator (tool calls were
  re-yielded after every `tool_use` block) and in the Microsoft.Extensions.AI adapter. We use the non-streaming
  `GetResponseRich`, so we were not hit, but it closes the door for `AskStreamingAsync` if that ever streams for real.
- 3.8.80 "Report cached prompt tokens as ChatUsage.CacheReadTokens" / "OpenAI cache write tokens as CacheCreationTokens",
  3.8.77 "Take the final input and cache token counts from Anthropic message_delta". Our usage log line
  (`[AskAsync] Tokens — input/output`) can now also show cache reads. The first eval turn was 10,425 input tokens
  (system prompt + 13 tool schemas), so the cache figure is worth seeing.
- 3.8.82 Anthropic: `anthropic-version` header is now a property (`AnthropicEndpointProvider.ApiRevision`), default
  unchanged at `2023-06-01`. The new `2026-10-21` revision is opt-in and drops sampling parameters; we send
  `Temperature = 1.0`, which the legacy revision still accepts. Nothing to do unless we opt in.

**Catalog (picker)**
- Claude 5.5 constants exist since the 3.8.8x line: `ChatModelAnthropicClaude5.ModelOpus55`, `.ModelSonnet55`,
  `.ModelHaiku55`. Our picker stops at Fable 5.1 / Opus 5 / Sonnet 5. `ChatModel.Anthropic.Claude5.Sonnet` (5.0) is
  now the library's default when a request has no model.
- Opus 4.8 is re-described as previous generation; its fast mode is retired 2026-11-30. We list `claude-opus-4-8` but
  never request fast mode. Keep or drop, cosmetic.
- 3.8.81 marks retired Mistral constants `[Obsolete]`. We use string ids for Mistral, so no warnings.
- 3.8.79 library default for OpenAI moved to `gpt-5.6-terra`; our picker has `gpt-6-astra`, `gpt-5.5`, `gpt-5-mini`.
  No change needed.

**New features we do not need**
- `TornadoApiRouter` (3.8.80): routes models to named `TornadoApi` instances. We have one api with several keys.
- Anthropic message threads (`Chat.CreateAnthropicThread`, 3.8.82): server-held history with replay/rollback.
  Anthropic-only; our history replay is provider-neutral and 50 pairs deep. Skip.
- Anthropic user profiles, org webhooks, analytics, Claude Tag, Google Cloud routing (3.8.82): admin surface. Skip.
- Vector databases, Codex OAuth threads, LiteLLM gateway, OpenRouter Decisions: not our domain.

**Nothing removed or renamed** in `LlmTornado`, `LlmTornado.Chat`, `.Chat.Models`, `.ChatFunctions`, `.Code`,
`.Common` that we reference (the build is the proof).

## Follow-ups

1. **Bump to 3.8.82** and add the Claude 5.5 picker entries (`ModelOpus55`, `ModelSonnet55`, `ModelHaiku55`), log
   `Usage.CacheReadTokens` next to input/output. One card.
2. **Dev OpenAI key expired.** The Blazor `appsettings.Development.json` key answers 401 `expired_secret_key`. Until a
   new key is pasted in, the LLM evals only run on Mistral. Martin's action; no card.
3. **Side finding, not LlmTornado:** with DevExpress 26.1.6 the restore warns NU1903 (high) on
   `System.Security.Cryptography.Pkcs 9.0.11`, pulled by `DevExpress.ExpressApp` → `DevExpress.Pdf.Core`. Whether
   26.1.5 pulled the same is unverified (no 26.1.5 package in the local cache). A direct `PackageReference` to a
   patched 10.0.x in Module/Blazor/Win would silence it. One card, marked unverified for 26.1.5.
