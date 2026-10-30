# 1Rad — interoperability and multi-server plan (gap analysis P3-6, P3-9)

Written 30 Oct 2026 with the P3 fixes. P3-1 to P3-5, P3-7 and P3-8 are implemented;
this file covers the two items that are architecture work, with what is already done
and the order to do the rest.

## P3-6 — standard imaging and HIS interfaces (implemented 30 Oct 2026)

All four phases are in the API. Outside systems authenticate with a **centre integration
key** (Settings → Integrations, admins only; DB script 101 `IntegrationClients`). A key looks
like `1rk_<id>_<secret>`, is shown once, is stored only as a SHA-256 hash, belongs to one
centre, and opens **only** `/api/v1/dicomweb`, `/api/v1/worklist` and `/api/v1/fhir` for the
scopes it was given (`dicomweb.read`, `dicomweb.write`, `worklist.read`, `fhir.read`). Send it
as `Authorization: Bearer 1rk_…`, `X-Api-Key: 1rk_…`, or as the Basic-auth password (any
user name). Revoking takes effect within a minute on every instance. Staff access tokens work
on the same endpoints with their normal roles. The subscription lock applies to keys too.

| Phase | Endpoints | Notes |
| --- | --- | --- |
| 1 DICOMweb read | `GET /api/v1/dicomweb/studies`, `/series`, `/studies/{s}/series`, `/studies/{s}/instances`, `/studies/{s}/series/{se}/instances` (QIDO-RS); `GET /studies/{s}[/series/{se}[/instances/{i}]]` (WADO-RS, multipart `application/dicom`), `…/metadata` at each level, `…/instances/{i}/frames/{n,…}` | Served from `ImagingStudies` + `StudySliceIndexes` + the extracted `.dcm` blobs. Only Ready studies with a UID and at least one extracted instance are listed; server MPR planes are hidden; duplicate SOP uploads are served once. Instances are stored as HTJ2K lossless: `transfer-syntax=*` returns them as stored; no transfer syntax (PS3.18 default) decodes to Explicit VR Little Endian on the fly. Frames requested as `application/octet-stream; transfer-syntax=*` (what OHIF/Cornerstone send) come back compressed as stored — the `wadors` path `DICOM_PIPELINE_ARCHITECTURE.md` was waiting for. Full metadata is built once per series from the files and cached (15 min in memory, plus a `…/dicomweb-meta/…json` blob keyed by the exact slice set). QIDO search: StudyInstanceUID, PatientName (wildcards, `fuzzymatching=true`), PatientID, AccessionNumber, StudyDate (ranges), ModalitiesInStudy, StudyDescription, SeriesInstanceUID, SeriesDescription, limit/offset. No matches → 204. |
| 2 STOW-RS | `POST /api/v1/dicomweb/studies[/{s}]` (`multipart/related; type="application/dicom"`, ≤1 GB per request) | Instances are staged and handed to the existing extraction queue. Requests for the same study within `DicomWeb:StowBatchSeconds` (20 s) join one batch, so a CT sent as 50 small requests is extracted once. Studies are created and matched (worklist UID → accession → MRN → unique name). Response: PS3.18 DICOM JSON with Referenced/Failed SOP sequences (200 / 202 / 409). Storage quota applies. Instances become retrievable when extraction finishes (seconds to a few minutes). |
| 3 Modality worklist | `GET /api/v1/worklist/mwl?date=YYYYMMDD&modality=CT&days=1` | DICOM JSON, one item per booked service not yet scanned (IST day). Each item's Study Instance UID is `2.25.<service id>`; when images come back with it, `StudyMatchingService` links them to that exact service, appointment and patient. Accession = the appointment's display id (≤16 chars). The DIMSE C-FIND side stays on-prem: **bridge change still needed** — poll this endpoint every 1–2 min with a `worklist.read` key and write the items as Orthanc worklist files (`.wl`, e.g. with `json2dcm`/pydicom) into Orthanc's Worklists folder. |
| 4 FHIR R4 (read-only) | `GET /api/v1/fhir/metadata` (no auth), `Patient`, `ServiceRequest` (one per booked service), `ImagingStudy` (with a contained DICOMweb Endpoint), `DiagnosticReport` (signed reports only: Preliminary / Final / Addended, PDF via a time-limited signed link) — read by id and search, `_count` ≤200 + `_offset` paging | `application/fhir+json`. Codes follow what ABDM/NRCeS use (SNOMED imaging category, LOINC 18748-4, v2-0074 RAD, DICOM modality codes) but resources do not claim NRCeS-profile conformance: 1Rad holds age as text and no ABHA id. HL7 v2 not built. |

Connecting a viewer (OHIF example): data source `dicomweb`, `qidoRoot` and `wadoRoot` =
`https://<api>/api/v1/dicomweb`, `imageRendering: "wadors"`, `thumbnailRendering: "wadors"`,
request header `Authorization: Bearer 1rk_…`. Browsers on another origin also need that origin
in `Cors:AllowedOrigins`. Weasis / Orthanc DICOMweb client: URL as above, Basic auth with the
key as password.

Set `Interop:PublicApiBaseUrl` (e.g. `https://1rad-api.nexeagle.com`) so retrieve URLs and FHIR
links use the public host; otherwise they come from the request / X-Forwarded headers.

Second pass (30 Oct 2026):

- **Rendered and thumbnail** — `…/instances/{i}/rendered`, `…/frames/{n}/rendered` and
  `…/thumbnail` at study, series and instance level. JPEG (default) or PNG by `Accept`;
  `quality`, `viewport=w,h` and `window=center,width` follow PS3.18. Grayscale goes through
  rescale + linear VOI (MONOCHROME1 inverted); RGB / YBR_FULL colour is supported; palette
  colour is not. Thumbnails reuse the JPEG preview extraction already wrote when there is one.
- **STOW JSON + bulk data form** — `type="application/dicom+json"`: datasets are rebuilt into
  Part 10 files with each `BulkDataURI` filled from the part with that `Content-Location`.
  Pixel data in a part labelled with a compressed transfer syntax is stored encapsulated
  (one part = one frame). Bulk values inside sequences are not resolved.
- **FHIR create** (new scope `fhir.write`) — `POST Patient` (returns the existing patient
  when the MRN matches; otherwise the front desk's de-duplication on mobile + name applies)
  and `POST ServiceRequest` (books through the normal booking command: display id,
  duplicate-booking guard → 409, auto-billing; price and modality from the centre's service
  list when `code.text` matches a service name). 201 + `Location`.
- **Worklist sync for Orthanc** — `tools/worklist-sync/worklist_sync.py` (Python, pydicom)
  turns the worklist feed into Orthanc `.wl` files and removes them once scanned/cancelled.
  Runs beside Orthanc as a service or cron job, so the bridge repo does not need a change.

Still not built: DIMSE inside the API (by design), FHIR update/delete, an ABHA-linked
Patient. The DICOMweb metadata cache blobs are not referenced by any row, so the orphan
sweep may delete them after `OrphanMinAgeHours` when deletion is on; they are rebuilt on
the next request.

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
