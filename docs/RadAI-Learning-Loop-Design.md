# RadAI Learning Loop — Design Document

**Status:** Draft for review · **Owner:** (TBD) · **Last updated:** 2026-06-06
**Decision baseline:** House spelling **en-GB** · Learning gate **human-approved** · Build order **design-doc-first**

---

## 1. Goal & non-goals

**Goal.** Make the RadAI report formatter measurably better over time by learning
from the clinic's *own* finalized reports, **classified by modality / test_code**,
with a **human-approval gate** before any learned knowledge influences a future
report.

**How it "learns" (important reframe).** We do **not** retrain the Gemini model per
report. The model is fixed; what grows is the **context we feed it every call** —
the two ingredients `RadiologyPack.BuildContext` already injects:

1. the **lexicon** (`corrections`, abbreviations, style rules), and
2. the **few-shot gold examples** (per `test_code`).

More finalized reports → a richer, house-specific lexicon + better examples →
better drafts. This is the pack README's "become a master over time." True model
fine-tuning is kept as a **future option** (§11), not the mechanism.

**Non-goals (for now).** Real-time online learning; auto-applying learned content
without review; cross-hospital knowledge sharing; replacing the radiologist's
sign-off. Output stays **assistive only** — a radiologist signs every report.

---

## 2. Current state (what we build on)

| Piece | Where | Note |
|---|---|---|
| Formatter command | `FormatReportCommandHandler` | Produces `aiDraft`, `corrections`, `flags`; de-ids via `PhiRedactor`; modality/test_code already known |
| Report storage | `DiagnosticReport` (`Findings`/`Impression`/`Advice`, `IsFinalized`, `FinalizedAt`, `AppointmentServiceId`, `DoctorId`, `HospitalId`) | One report per `AppointmentServiceId` |
| Finalize hook | `SaveReportCommandHandler`, when `request.IsFinalized == true` | Both insert & update branches set `FinalizedAt` |
| Knowledge pack | `1RadAPI/Resources/Radiology/*` + `RadiologyPack` | Static, en-GB; `BuildContext(modality, testCode)` |
| De-identification | `PhiRedactor.Redact/Restore` | Masks name / PTID / phone |
| Schema changes | `oneRadDB/schema/NN_*.sql` numbered scripts | **Not** EF migrations |
| Background jobs | `IHostedService` (e.g. `DailyFinancialReportJob`) | Pattern for the nightly miner |

---

## 3. Architecture — the flywheel

```
   ┌─────────── Capture ───────────┐      ┌──── Curate (human-gated) ────┐
   │  format()  → draft + corr     │      │  nightly miner → suggestions │
   │  finalize()→ final + DIFF     │ ───▶ │  admin console → APPROVE     │
   └───────────────────────────────┘      └──────────────┬───────────────┘
                  │ store (de-identified, by modality)    │ approved lexicon
                  ▼                                        ▼ + gold examples
            RadAiSample (corpus)  ◀── metrics ──  BuildContext OVERLAY (Apply)
                                                          │
                                              better drafts next time ───┐
                                                                         ▼
                                                            (loop repeats)
```

Four stages: **Capture → Store → Curate → Apply**, with an **edit-distance KPI**
measuring whether drafts are getting closer to what the radiologist signs.

---

## 4. Data model (oneRadDB scripts + EF entities)

All tables carry `HospitalId` and are **always queried hospital-scoped**
(multi-tenant isolation). New tables ship as a numbered script, e.g.
`oneRadDB/schema/67_radai_learning.sql`, with matching `1Rad.Domain` entities and
`IApplicationDbContext` `DbSet`s + EF configuration.

### 4.1 `RadAiSample` — the corpus (Phase 1)

One row per RadAI-formatted (or finalized) report. **De-identified.**

| Column | Type | Notes |
|---|---|---|
| `Id` | uniqueidentifier PK | |
| `HospitalId` | uniqueidentifier | tenant scope (indexed) |
| `ReportId` | uniqueidentifier NULL | FK `DiagnosticReport` |
| `AppointmentServiceId` | uniqueidentifier NULL | join key between format & finalize |
| `DoctorId` | uniqueidentifier NULL | per-radiologist signal |
| `Modality` | nvarchar(32) | classification axis |
| `TestCode` | nvarchar(64) | e.g. `USG_ABDOMEN` |
| `UsedRadAi` | bit | false = final-only (no AI draft) |
| `RawInput` | nvarchar(max) | de-identified editor text at format time |
| `AiDraft` | nvarchar(max) NULL | de-identified `formatted_report` |
| `FinalText` | nvarchar(max) NULL | de-identified signed text (set at finalize) |
| `CorrectionsJson` | nvarchar(max) NULL | AI self-reported corrections |
| `FlagsJson` | nvarchar(max) NULL | AI flags |
| `DiffJson` | nvarchar(max) NULL | computed draft→final ops (§6) |
| `EditDistance` | int NULL | word-level Levenshtein |
| `EditRatio` | float NULL | `editDistance / max(len)` ∈ [0,1] |
| `HouseSpelling` | nvarchar(4) | `UK` |
| `ModelVersion` | nvarchar(64) | e.g. `gemini-2.5-flash` |
| `PromptVersion` | nvarchar(32) | bumps when `system_prompt.md` changes |
| `LexiconVersion` | nvarchar(32) | hash of merged lexicon used |
| `Status` | nvarchar(16) | `draft` → `finalized` / `discarded` |
| `IsTeaching` | bit | radiologist "star as teaching example" |
| `CreatedAt`/`UpdatedAt` | datetime2 | |

Indexes: `(HospitalId, Modality, TestCode)`, `(HospitalId, Status)`,
`(AppointmentServiceId)`, `(HospitalId, CreatedAt)`.

### 4.2 `RadAiLexiconSuggestion` — mined, pending review (Phase 2)

| Column | Type | Notes |
|---|---|---|
| `Id` | uniqueidentifier PK | |
| `HospitalId` | uniqueidentifier | |
| `Modality` | nvarchar(32) | NULL = all modalities |
| `TestCode` | nvarchar(64) NULL | |
| `FromText` / `ToText` | nvarchar(256) | the recurring edit |
| `Type` | nvarchar(16) | spelling \| grammar \| style \| abbreviation |
| `Frequency` | int | times observed |
| `ExampleSampleIds` | nvarchar(max) | provenance (JSON array) |
| `Status` | nvarchar(16) | `pending` \| `approved` \| `rejected` |
| `ReviewedBy`/`ReviewedAt` | … | audit |

Dedup key: `(HospitalId, Modality, lower(FromText), lower(ToText))`.

### 4.3 `RadAiLexiconEntry` — approved overlay corrections (Phase 3)

`Id, HospitalId, Modality(NULL=all), FromText, ToText, Type, Source('seed'|'learned'),
Active bit, CreatedBy, CreatedAt`. Merged on top of the static lexicon at call time.

### 4.4 `RadAiGoldExample` — approved few-shot examples (Phase 3)

`Id, HospitalId, Modality, TestCode, RawInput, FormattedReport, Source('seed'|'promoted'),
Priority int, Active bit, ApprovedBy, ApprovedAt, CreatedAt`. Promoted from a sample's
final text (optionally edited). Used in `BuildContext` ahead of the static seed example.

---

## 5. Capture (Phase 1)

A thin **`IRadAiLearningStore`** (Application interface, Infrastructure impl)
isolates persistence so handlers stay clean and capture **never blocks** the user
path (wrap in try/catch; log-and-continue).

```csharp
public interface IRadAiLearningStore
{
    Task CaptureDraftAsync(RadAiDraftCapture c, CancellationToken ct);   // at format
    Task CaptureFinalAsync(RadAiFinalCapture c, CancellationToken ct);   // at finalize
}
```

**At format** — in `FormatReportCommandHandler.Handle`, after a successful draft:
upsert a `RadAiSample` for `(HospitalId, AppointmentServiceId)` with
`Status='draft'`, `UsedRadAi=true`, `RawInput` (already de-id'd `safeText`),
`AiDraft`, `CorrectionsJson`, `FlagsJson`, modality/test_code, versions.
*Re-formatting overwrites the draft row.*

**At finalize** — in `SaveReportCommandHandler`, when `request.IsFinalized == true`:
de-identify the signed text (`Findings`+`Impression`+`Advice`) via `PhiRedactor`
(appointment PHI), find the latest `draft` sample for that `AppointmentServiceId`:
- found → set `FinalText`, compute `DiffJson`/`EditDistance`/`EditRatio`, `Status='finalized'`.
- none (report not RadAI-formatted) → insert a **final-only** sample
  (`UsedRadAi=false`, `AiDraft=null`) — still a **gold-example candidate**.

**Privacy:** nothing is persisted before `PhiRedactor`. Findings text rarely
contains identifiers, but redaction is the safety net. The corpus is PHI-free.

**Failure isolation:** capture is best-effort. A learning-store exception is logged
and swallowed — it must never fail a save or a format.

---

## 6. Diff & edit-distance (Phase 1 util)

`ReportDiff.Compute(aiDraft, finalText)` (Application/Common):
- Tokenise to words (keep measurements/laterality intact).
- Word-level Levenshtein → `EditDistance`; `EditRatio = distance / max(tokens)`.
- `DiffJson` = ordered ops `{op: replace|insert|delete, before, after, context}`.

`EditRatio` is the per-report quality signal (0 = AI draft signed unchanged).
Short `replace` ops (1–3 tokens) are the raw material for lexicon suggestions.

---

## 7. Curate (Phase 2) — human-approved

### 7.1 Nightly miner (`IHostedService`, like `DailyFinancialReportJob`)

Per hospital × modality, over `finalized` samples since the last run:
- From `DiffJson` `replace` ops, keep **short, term-like** pairs (drop full-sentence
  rewrites and anything touching protected patterns — measurements, laterality,
  negations, levels). Classify `type` heuristically (spelling vs abbreviation vs style).
- Normalise (lowercase, trim) and **count frequency** → upsert `RadAiLexiconSuggestion`.
- Flag `gold-example candidates`: `finalized` samples with low `EditRatio`
  (AI near-perfect) **or** `IsTeaching=true`.
- Compute rolling **EditRatio** aggregates per modality for the dashboard.

A suggestion becomes "ready" at `Frequency ≥ threshold` (config, default **3**).

### 7.2 Admin review console (frontend, admin-only)

New page **"RadAI Learning"** (under Admin), modality-filtered, three tabs:
- **Suggestions** — `from → to`, type, frequency, sample provenance → **Approve / Reject** (1-click). Approve writes a `RadAiLexiconEntry`.
- **Gold examples** — candidate finalized reports → **Promote** (with optional inline edit) to `RadAiGoldExample`; toggle Active; set Priority.
- **Metrics** — EditRatio trend per modality, RadAI adoption %, pending counts.

### 7.3 API (admin-scoped; `[Authorize]` admin)

```
GET  /api/v1/radai/learning/suggestions?modality=        list pending
POST /api/v1/radai/learning/suggestions/{id}/approve
POST /api/v1/radai/learning/suggestions/{id}/reject
GET  /api/v1/radai/learning/examples/candidates?modality=&testCode=
POST /api/v1/radai/learning/examples/{sampleId}/promote  body: { formattedReport?, priority? }
GET  /api/v1/radai/learning/metrics?modality=
GET  /api/v1/radai/learning/samples?modality=&status=    browse corpus
```

Plus a small radiologist affordance: **"★ Use as teaching example"** on the RadAI
review modal → sets `IsTeaching=true` (cheap, high-value signal).

---

## 8. Apply (Phase 3) — DB overlay on the static pack

Extend the formatter context with the hospital's approved learning:

- `RadiologyPack.BuildContext(modality, testCode)` gains a **hospital overlay**
  (via `IRadAiLearningStore.GetOverlayAsync(hospitalId, modality, testCode)`):
  - **Lexicon:** append active `RadAiLexiconEntry` rows to the lexicon `corrections`
    block (learned entries listed last so they win).
  - **Examples:** prepend up to **2** active `RadAiGoldExample` (by Priority) ahead
    of the static seed example for that `test_code`.
- Stamp the resulting `LexiconVersion` onto new samples (traceability).
- **Gemini context caching:** the per-(hospital, modality, testCode, lexiconVersion)
  prefix is identical across calls → cache it to cut cost/latency at volume.

No change to the safety contract: output is still a reviewed draft; PHI still masked.

---

## 9. Metrics / KPI

- **EditRatio per modality over time** (weekly avg) — the headline "is it learning?"
  number. Down-and-to-the-right = success.
- **RadAI adoption** — % of finalized reports that were RadAI-formatted.
- **Suggestion funnel** — pending / approved / rejected per modality.
- **Flag rate** — flags per report (should fall as the lexicon absorbs known issues).

All derived from `RadAiSample` aggregates; surfaced on the Metrics tab.

---

## 10. Privacy, security, compliance

- **De-identify before persistence** (`PhiRedactor`) — corpus is PHI-free.
- **Multi-tenant isolation** — every row + query scoped by `HospitalId`; learned
  content never crosses hospitals (a future curated "global seed" would be an
  explicit, separate, manually-reviewed path).
- **Human-approval gate** — nothing learned reaches a patient's report unreviewed.
- **Admin-only** learning console + endpoints.
- **Traceability** — `ModelVersion` / `PromptVersion` / `LexiconVersion` on every
  sample; lets you compare quality across versions and invalidate a bad batch.
- **Retention** — corpus is de-identified; still define a retention window (config).
- **Compliance** — secondary use of de-identified clinical text for QA/quality
  improvement; confirm against the centre's local regulatory expectations before
  go-live.

---

## 11. Future option — true fine-tuning

The corpus you accumulate (de-identified `RawInput → FinalText` pairs, labelled by
modality) **is** a supervised fine-tuning dataset. Once it's large and clean
(order thousands per modality), Vertex AI supervised tuning of a per-modality model
is feasible. It's **not** needed now — the lexicon + few-shot overlay captures most
of the gain at a fraction of the cost and with full human control. Revisit when the
KPI plateaus.

---

## 12. Delivery plan

**Phase 1 — Capture (foundation; start collecting day one).**
1. `67_radai_learning.sql` → `RadAiSample`.
2. Domain entity + EF config + `IApplicationDbContext` DbSet.
3. `IRadAiLearningStore` + Infrastructure impl (best-effort, try/catch).
4. `ReportDiff` util (word Levenshtein + ops).
5. Hook `FormatReportCommandHandler` (draft capture).
6. Hook `SaveReportCommandHandler` on `IsFinalized` (final + diff capture).
7. Verify: format → finalize a USG report → one `finalized` sample with a diff.

**Phase 2 — Curate + console.**
8. Suggestion / GoldExample / LexiconEntry tables.
9. Nightly miner `IHostedService`.
10. Admin endpoints + "RadAI Learning" page (Suggestions / Examples / Metrics).
11. "★ Teaching example" toggle on the review modal.

**Phase 3 — Apply overlay + dashboard.**
12. Overlay merge in `BuildContext` (lexicon + examples per hospital/modality).
13. Gemini context caching.
14. EditRatio dashboard.

---

## 13. Open questions / risks

- **Final-only samples** (reports not formatted by RadAI): capture as gold
  candidates? *Recommended yes, flagged `UsedRadAi=false`.*
- **Diff granularity** — word-level proposed; revisit char-level if needed.
- **Suggestion threshold** — default 3; tune per volume.
- **Storage growth** — `nvarchar(max)` × reports/day is modest; retention covers it.
- **Multi-service reports** — one sample per `AppointmentServiceId` (already the key).
- **Prompt/lexicon drift** — version stamping lets us segment metrics by version.
```
