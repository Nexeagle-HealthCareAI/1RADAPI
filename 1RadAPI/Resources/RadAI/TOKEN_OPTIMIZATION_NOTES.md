# RadAI Token-Optimization Layer (developer notes)

Internal version of the "stop burning tokens" idea, built on RadAI. Three pieces:
**(1) prompt caching**, **(2) response (semantic) caching**, **(3) usage + savings
attribution** with a before/after dashboard endpoint.

## What was added / changed

| Layer | File | Change |
|-------|------|--------|
| Application | `Interfaces/AiJsonResult.cs` | **New** `AiUsage` + `AiJsonResult` records. |
| Application | `Interfaces/IAnthropicService.cs` | Added `GenerateJsonWithUsageAsync` (returns text + real token usage). |
| Application | `Interfaces/IRadAiResponseCache.cs` | **New** cache contract + `RadAiCachedAnswer`. |
| Infrastructure | `Services/AnthropicService.cs` | **Prompt caching**: system prompt sent as a cached content block (`cache_control: ephemeral`). Now parses the API `usage` block (input/output + `cache_read_input_tokens`). |
| Infrastructure | `Services/RadAiResponseCache.cs` | **New** `IMemoryCache`-backed response cache. |
| Infrastructure | `DependencyInjection.cs` | Registered `IRadAiResponseCache` (singleton). |
| Domain | `Entities/RadAiQuestionLog.cs` | Added `Model, InputTokens, OutputTokens, CacheReadInputTokens, CacheHit, SavedInputTokens, SavedOutputTokens`. |
| Infrastructure | `Persistence/ApplicationDbContext.cs` | `Model` length config. |
| Application | `Features/Assist/RadAi/RadAiCommand.cs` | Response-cache check (hit → skip the model), real-usage capture, `LogUsageAsync` writing token/cache columns. |
| Application | `Features/Assist/Usage/GetRadAiUsageQuery.cs` | **New** savings dashboard query. |
| API | `Controllers/AssistController.cs` | **New** `GET /api/v1/assist/usage`. |

## How it works

1. **Response cache** — typed questions are keyed on `normalised-question + page + language + hash(system prompt)`. A hit returns the cached answer with **zero** model tokens, and logs the tokens it *avoided* (so savings are measured). The key hashes the system prompt, so editing `app_knowledge.json` auto-invalidates stale answers. The cache is shared across centres (RadAI answers are generic, non-PHI help) to maximise hit rate. TTL 6h, `IMemoryCache` (swap for Redis when multi-instance).
2. **Prompt caching** — the big static system prompt (~5k tokens of `app_knowledge.json`, identical every call) is sent as an Anthropic cached block; recurring calls bill those tokens at ~10%. Benefits every Claude caller (RadAI + voice reporting).
3. **Attribution** — every answered request logs real token usage (`input_tokens`, `output_tokens`, `cache_read_input_tokens`) and, on a cache hit, the avoided tokens.

## The before/after dashboard

```
GET /api/v1/assist/usage?days=30
```

Returns: total requests, cache hits, hit-rate, billed input/output tokens, prompt-cache
read tokens, response-cache saved tokens, and cost as **baseline (no caching) vs actual
vs saved (USD + %)**, split by response-cache vs prompt-cache.

## To finish before shipping

- **Restrict `/usage` (and `/retrain-candidates`) to admins** — class is `[Authorize]`, not yet admin-only.
- **Set the Haiku price** in `GetRadAiUsageQuery` (`InputRatePerMTok`, `OutputRatePerMTok`) to your actual plan rate. Token counts are measured/real; only the cost multiplier is an assumption.
- **Schema:** the `RadAiQuestionLog` table now has the new token columns. Since you keep schema in code, regenerate the table (e.g. `dotnet ef migrations add` or recreate). If the table was already created without these columns, ALTER it to add: `Model NVARCHAR(20) NULL`, `InputTokens INT`, `OutputTokens INT`, `CacheReadInputTokens INT`, `CacheHit BIT`, `SavedInputTokens INT`, `SavedOutputTokens INT` (all `NOT NULL DEFAULT 0` except `Model`).
- **Build & test** — I can't compile .NET here; run `dotnet build`. The Gemini (audio) path estimates tokens (~4 chars/token); Claude uses real counts. To get exact voice numbers later, surface the transcript + usage from the Gemini service.

## Why this is the right "internal first" wedge

It dogfoods the token-cost product on your own stack: measurable savings on your own bill (prove ROI), reuses the `RadAiQuestionLog` you already had, and the `/usage` numbers become the case study if you later productize a healthcare-vertical token-optimization layer.
