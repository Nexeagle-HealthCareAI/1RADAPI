# RadAI Retrain — Capture + Helper (developer notes)

RadAI is **grounded, not trained** — it answers only from `app_knowledge.json`. So
"retraining from popular questions" = capturing what users ask, finding the gaps,
and appending to `app_knowledge.json`. This change adds the **capture** and a
**retrain helper** that tells you exactly what to add next.

## What was added

| Layer | File | Change |
|-------|------|--------|
| Domain | `1Rad.Domain/Entities/RadAiQuestionLog.cs` | **New** entity (hospital-scoped via `IHospitalContext`). |
| Application | `Interfaces/IApplicationDbContext.cs` | Added `DbSet<RadAiQuestionLog> RadAiQuestionLogs`. |
| Infrastructure | `Persistence/ApplicationDbContext.cs` | Added the `DbSet` + model config (table `dbo.RadAiQuestionLogs`, indexes on `(HospitalId, CreatedAt)` and `(HospitalId, Covered)`). |
| Application | `Features/Assist/RadAi/RadAiCommand.cs` | Injected `IApplicationDbContext`; `LogQuestionAsync` writes one row per answered question, recording the **`Covered`** flag. Best-effort (wrapped in try/catch — never breaks or fails the user's answer). |
| Application | `Features/Assist/Retrain/GetRadAiRetrainCandidatesQuery.cs` | **New** query: clusters + ranks logged questions, returns paste-ready draft entries. |
| API | `Controllers/AssistController.cs` | **New** `GET /api/v1/assist/retrain-candidates`. |

## 1) Create the migration

The model change is the source of truth — let EF generate the migration (it writes
the `.cs`, the `.Designer.cs`, and updates `ApplicationDbContextModelSnapshot.cs`;
don't hand-write those three):

```bash
dotnet ef migrations add AddRadAiQuestionLog -p 1Rad.Infrastructure -s 1RadAPI
dotnet ef database update                    -p 1Rad.Infrastructure -s 1RadAPI
```

(Adjust `-p`/`-s` if your project names differ.)

### Manual SQL fallback (only if you can't run EF tooling)

```sql
CREATE TABLE dbo.RadAiQuestionLogs (
    RadAiQuestionLogId uniqueidentifier NOT NULL CONSTRAINT PK_RadAiQuestionLogs PRIMARY KEY,
    HospitalId         uniqueidentifier NOT NULL,
    AskedByUserId      uniqueidentifier NULL,
    SessionId          uniqueidentifier NULL,
    Question           nvarchar(2000)   NULL,
    WasVoice           bit              NOT NULL,
    Page               nvarchar(200)    NULL,
    ReplyLanguage      nvarchar(8)      NULL,
    Covered            bit              NOT NULL,
    AnswerSnippet      nvarchar(500)    NULL,
    CreatedAt          datetime2        NOT NULL
);
CREATE INDEX IX_RadAiQuestionLogs_HospitalId_CreatedAt ON dbo.RadAiQuestionLogs (HospitalId, CreatedAt);
CREATE INDEX IX_RadAiQuestionLogs_HospitalId_Covered   ON dbo.RadAiQuestionLogs (HospitalId, Covered);
```

If you apply the SQL directly, prefer the `dotnet ef` route afterwards anyway so the
model snapshot stays consistent for future migrations.

## 2) Use the retrain helper

```
GET /api/v1/assist/retrain-candidates?days=30&maxCandidates=25&minCount=1&uncoveredOnly=false
```

Returns window stats plus a ranked list. Each candidate has: the representative
question, how many times it was asked (`count`), how many of those were **uncovered**
(`uncoveredCount`, `uncoveredRate`), sample phrasings, a suggested `id`, and a
ready-to-paste `draftFaq` = `{ id, q, a }`.

**Workflow:** sort attention by high `count` + high `uncoveredRate` → write the real
steps into each `draftFaq.a` from the live app → append it to `app_knowledge.json`
`faqs[]` (or a richer `modules[]` entry for a whole workflow) — append-only, merge by
`id` → redeploy. RadAI answers it on the next request.

## Notes / good to know

- **Hospital scoping** is automatic (the global query filter), so each centre's admin
  sees only their centre's questions. For a group-wide view, query with
  `IgnoreQueryFilters()` and filter by `UserContext.AuthorizedHospitalIds`.
- **Restrict the endpoint to admins.** The controller is `[Authorize]` but not yet
  admin-only — add your standard admin policy/attribute before shipping.
- **Voice questions:** the spoken transcript isn't currently returned by the model, so
  voice rows store `Covered` + page but no question text (`WasVoice = true`). To make
  voice questions show up as retrain candidates, add a `transcribed_question` field to
  the RadAI response schema (`assistant_system_prompt.md` + `RadAiAnswer`) and store it.
- **Clustering** is a first pass (normalised exact match). Swap `Normalize()` for
  embeddings later for semantic grouping — the response shape doesn't change.
- **Draft answers are placeholders by design** (a human writes the real steps). If you
  want them pre-drafted, wire `IAnthropicService` into the query handler to draft each
  answer grounded ONLY on the existing `app_knowledge.json` (same rule RadAI follows).
- **Latency:** logging awaits one INSERT before returning the answer (negligible). If
  you want zero added latency, offload the write to a background channel.
