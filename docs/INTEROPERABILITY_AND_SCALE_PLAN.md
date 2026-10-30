# 1Rad — interoperability and multi-server plan (gap analysis P3-6, P3-9)

Written 30 Oct 2026 with the P3 fixes. P3-1 to P3-5, P3-7 and P3-8 are implemented;
this file covers the two items that are architecture work, with what is already done
and the order to do the rest.

## P3-6 — standard imaging and HIS interfaces

Today 1Rad speaks only its own manifest API (`/Study/...`) plus the on-prem Orthanc
bridge (repo `nexegale-dicom-bridge`, not in this folder). A hospital HIS, a third-party
viewer or a modern modality cannot connect directly.

| Phase | What | Why first | Size |
| --- | --- | --- | --- |
| 1 | **DICOMweb read**: QIDO-RS (`/dicomweb/studies`, `/series`, `/instances` search) and WADO-RS (instances, metadata, frames) over `ImagingStudies` + `StudySliceIndexes` + the stored `.dcm`. Bearer auth, centre-scoped, `[RequiresModule(PACS)]`. | Lets OHIF/Weasis/any DICOMweb viewer read 1Rad studies, and unlocks Cornerstone's `wadors` loader — the progressive/HTJ2K partial-decode path `DICOM_PIPELINE_ARCHITECTURE.md` says is blocked (`.jhc` files are written but unused). | 2–3 weeks |
| 2 | **STOW-RS ingest** (`POST /dicomweb/studies`) feeding the existing extraction worker. | Modalities/PACS that push DICOMweb no longer need the bridge's per-instance SAS dance. | 1–2 weeks |
| 3 | **Modality worklist**: `GET /api/v1/worklist/mwl` (today's BOOKED/CONFIRMED services as MWL JSON); the bridge converts to Orthanc worklist files (DIMSE C-FIND stays on-prem). | Removes re-typing patient demographics at the modality — a frequent source of the "unmatched study" queue. | 1 week + bridge change |
| 4 | **FHIR R4** facade: `Patient`, `ServiceRequest`, `ImagingStudy`, `DiagnosticReport` (signed reports only, PDF attachment — see P2-7). Follow the ABDM / NRCeS FHIR profiles. | Integration with NexEagle's own 1HMS (which already does ABDM) and outside HIS; HL7 v2 ORM/ORU only if a customer insists. | 3–4 weeks |

Notes: keep the original transfer syntax available for WADO-RS (`Accept` negotiation)
because not every viewer decodes HTJ2K; reuse `AssetUrlSigner` for short-lived
bulk-data URLs instead of proxying bytes through the API.

## P3-9 — running more than one API instance

Done on 30 Oct 2026:

- **Daily jobs run once per slot** — `DailyFinancialReportJob`, `DailyReferralExcelReportJob`
  and `ReferralLinkRenewalJob` claim `(JobName, RunKey)` in `dbo.JobRuns` (DB script 99)
  before doing anything; only one instance wins. They also now run on **India time**
  (`JobSchedule`) — before, the container's UTC clock made "21:00" fire at 02:30 IST and
  report the UTC day.
- **Session revocation reaches every instance within 2 minutes** — `ActiveSessionCache`
  positive entries are capped at 2 minutes, so a sign-out on one instance is honoured
  by the others at the next DB re-check (was: until the refresh token expired).
- Subscription lock check (`SubscriptionValidationMiddleware`) caches 60 s per instance — acceptable.

Also done (30 Oct 2026, second pass):

- **Shared rate limits** — `[DistributedRateLimit]` counts in `dbo.RateLimitCounters`
  (DB script 100), so the limit is the same however many instances run. The old OTP
  limit was ONE bucket for the whole platform (5 OTPs per 10 min for everyone); it is
  now per mobile number plus a platform ceiling. Login, forgot-password and
  verify-reset-code are now limited per identifier too. Fails open (logs a warning) if
  the table is missing.
- **Every background job runs once per slot** — `SubscriptionLifecycleJob` (6-hour block),
  `BlobOrphanSweepJob` (its interval) and `DicomExtractionBackfillJob` (per hour) claim
  `JobRuns` like the daily jobs.
- **Heavy analytics are computed once per data change** — the finance matrix, finance
  stats, strategic outlook, referral intelligence/summary/visits, referral matrix and
  acquisition sources implement `ICachedAnalyticsQuery`; `AnalyticsCacheBehavior` reuses
  a result while a one-query "data version" (row counts + latest UpdatedAt of the tables
  they read) is unchanged, for up to 2 minutes.

Remaining:

| Component | Effect with 2+ instances | Fix |
| --- | --- | --- |
| `ModuleEntitlementService` / `StorageMeteringService` caches (60 s) | A module change takes up to 60 s on other instances | Accept, or invalidate via Redis pub/sub |
| `RadAiResponseCache` (6 h) | Lower hit rate only | Accept |
| `DicomExtractionQueue` (in-memory channel) | Fine — the DB lease is the real queue | none |
| First load of an all-time analytics view | Still reads the centre's full history once per data change | Pre-aggregated daily rows (`DailyFinanceFacts`) refreshed by a job, if a large centre's first load becomes slow |

## Live push instead of polling (30 Oct 2026)

- Hub `/api/v1/live` (SignalR, WebSockets only, token in `?access_token=`). A browser
  joins its centre's group; `LiveChangeWatcher` checks, every 4 s and only for centres
  with someone connected, one version query per centre (row counts + latest UpdatedAt
  per topic) and pushes `changed(["appointments" | "approvals" | "finance" | "reports" |
  "patients" | "studies"])`. No message bus needed: each instance watches the centres
  connected to it. Only topic names are sent.
- Web: `src/live/liveUpdates.js` maps topics to the window events the screens already
  use. While connected, the worklist (30 s), overdue (30 s), approvals badge (12 s),
  sync pulls (30 s), Referrals (90 s) and Studies extraction (4 s) polls become a
  5-minute (Studies: 30 s) safety net; without the connection everything polls as before.
- nginx (Dev VM) needs the WebSocket `Upgrade`/`Connection` headers on `/api/` (see the
  comment in 1RADWeb's deploy.yml); Caddy (Prod) passes WebSockets already.
