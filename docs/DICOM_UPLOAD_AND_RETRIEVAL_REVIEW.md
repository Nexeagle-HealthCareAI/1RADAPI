# DICOM Upload & Retrieval — Architecture Review

Reviewed: 2026-06-12, against live code (not docs). Container: `dicom-files`.

---

## PART 1 — UPLOAD (how a DICOM gets IN)

There are **three ingestion paths**, all converging on the same SAS-direct-to-blob
model + the same extraction worker.

### The core upload model: 3-step SAS handshake (no backend byte hop)
Used by the web technician page and the web Cloud-PACS uploader
([azureUpload.js](../../1Rad/easyrad/src/utils/azureUpload.js)):

1. **`POST /Study/upload-token`** → backend validates (module entitlement,
   storage quota, 1 GB cap, tenant), mints a blob path
   `{hospitalId:N}/{appointmentId:N}/{stamp}_{assetId:N}_{file}`, and returns a
   **30-minute Write+Create SAS URL** ([StudyController.cs:862], SAS built in
   [AzureBlobService.cs:136]). **No DB row is created here** — deliberately, so a
   failed PUT can't leave a 404-causing orphan.
2. **`PUT <sasUrl>`** — the browser uploads **straight to Azure Blob**, bypassing
   the API. Files >8 MB use a **parallel 4-MB block upload** (4 concurrent
   workers, then a block-list commit); smaller files use a single PUT with XHR
   progress.
3. **`POST /Study/upload-complete`** → backend verifies the blob actually exists
   (`BlobExistsAsync`), derives the read URL **server-side** (ignores the
   client-echoed URL), creates the `StudyAsset` row, flips the appointment to
   `IN_PROGRESS`, marks `ExtractionStatus="Queued"` for zip/dcm, and **enqueues**
   the asset id.

Fallback: if the SAS flow throws, both web callers fall back to the legacy
**`POST /Study/upload`** multipart (browser→API→blob 2-hop).

### Path A — Web technician (appointment-bound)
`uploadStudyAssetDirect(file, appointmentId)` → the 3-step flow above, stamped
with `AppointmentServiceId` for multi-service visits.

### Path B — Web Cloud PACS (appointment-free)
`registerStudy()` creates an `ImagingStudy`, then
`uploadStudyAssetToStudy(file, studyId)` runs the **study-scoped** twin
(`/studies/{id}/upload-token` + `/upload-complete`).

### Path C — Modality bridge (Orthanc → blob)
[nexegale-dicom-bridge/src/index.js]: pulls each instance from Orthanc and PUTs
it **per-instance** straight to blob via SAS (`requestInstanceSas` returns N
targets), 6-wide concurrency, then `registerInstanceUpload` stages a small JSON
manifest of instance URLs. Appointment-matched and standalone (PACS inbox)
variants both exist.

### Extraction (the common tail) — [DicomExtractionService.cs]
A single `DicomExtractionWorker` (BackgroundService) drains the queue and, per
asset, downloads the source (ZIP / staged instances / single DCM), and for every
slice: **transcodes to HTJ2K-Lossless-RPCL → uploads the per-slice `.dcm` →
(new) uploads a raw `.jhc` frame → renders a series thumbnail → writes a
StudySliceIndex row** with metadata JSON. Study flips `Received→Processing→Ready`.
Slice transcode/upload now runs **6-wide in parallel** (recent fix); staged
instance downloads run **8-wide**.

---

## PART 2 — RETRIEVAL (how a DICOM gets OUT to the viewer)

### Manifest → per-slice URLs
The viewer page ([DicomViewerPage.jsx]) calls one of:
- `GET /Study/{appointmentId}/manifest` (RIS) or
- `GET /Study/by-study/{imagingStudyId}/manifest` (PACS)

The manifest returns assets → series → **slices**, each with `url` (the per-slice
`.dcm`), `frameUrl` (the new raw HTJ2K frame), `thumbnailUrl`, and pixel
`metadata`. **Every URL is rewritten by `ToCdn()`** to the Front Door host
(`AzureBlobStorage:CdnBaseUrl`) so reads go through the CDN, not blob origin.
A lightweight `GET …/extraction-status` is polled (every 6 s) while processing,
so the heavy manifest is only re-fetched once something is `Extracted`.

### Decode + cache (the viewer loader) — [AdvancedDicomViewer.jsx]
Per-slice HTTPS URLs are routed through `loadManifestSliceCached`:
- **IndexedDB cache HIT** → serve compressed bytes locally (zero network).
- **MISS** → `fetchSliceWithRetry` (30 s timeout, 1 retry, **DICM-magic sniff**
  to fast-fail HTML/JSON), persist to IndexedDB, then hand Cornerstone a local
  `blob:` URL so its worker pool decodes with no second network trip.
- Middle-slice-first load; bounded outward prefetch (24 desktop / 10 mobile).
- Decoded images + parsed datasets cached in memory (2 GB desktop / 800 MB
  mobile).

### Delivery posture
Blob is **public-ACL behind Front Door** (Posture A); immutable cache headers
(`max-age=31536000, immutable`) make repeat views CDN/IndexedDB hits.

---

## PART 3 — CRITICAL ASSESSMENT

### Strengths (genuinely good)
- **Direct-to-blob SAS** removes the API as an upload bottleneck; parallel block
  upload saturates the link on big studies.
- **Row-after-blob** ordering (no orphan rows on failed PUT) is correct and
  often gotten wrong elsewhere.
- **upload-complete verifies blob existence + derives the read URL server-side**
  — doesn't trust the client.
- Per-instance bridge path avoids whole-study ZIPs.
- Retrieval: HTJ2K + immutable caching + IndexedDB + middle-first + fast-fail
  sniff is a strong, modern read path.

### Risks / gaps (ranked)

1. **Delivery depends on `CdnBaseUrl` being a valid `https://` URL.** If unset or
   scheme-less, every slice resolves to the SPA origin → HTML → the viewer fails.
   This has bitten you repeatedly. `ToCdn` is now hardened to prepend the scheme,
   and a startup warning logs when unset — **but verify the App Service value.**
   This is the single highest-impact retrieval risk.

2. **Blob CORS is a hard dependency for BOTH upload and (future) range reads.**
   The browser PUT and block-commit are cross-origin; without CORS allowing
   PUT/GET/HEAD/POST/OPTIONS, uploads fail with a status-0 network error (the
   code flags `AZURE_CORS_OR_NETWORK`). The same CORS must expose `Range` /
   `Content-Range` for the progressive path.

3. **Extraction worker is single-threaded across the whole server.** The
   `await foreach` processes ONE asset at a time. Within an asset, slices now
   parallelize, but two studies uploaded together extract sequentially. Under
   load (a busy centre, or the bridge dumping many studies), the queue backs up
   and "Processing" lingers. Consider N parallel consumers.

4. **Crash-recovery requeue only covers ZIPs.** `RequeueStaleAsync` filters
   `FileType == "zip"` — a crash mid-extraction of a **bridge per-instance
   (`instances`) or single `dcm`** asset leaves it stuck in `Queued`/`Running`
   **forever** (never requeued, never surfaced). Real correctness bug; widen the
   filter to all extractable types.

5. **SAS requires an account key.** `GenerateSasUploadUrlAsync` throws
   `AZURE_SAS_UNAVAILABLE` if the storage client lacks an account key — a
   managed-identity-only deployment would break all SAS uploads (needs
   user-delegation SAS). Fine today (connection string has the key), but a
   landmine if you move to MSI.

6. **Storage doubling from the new raw frames.** Newly extracted studies now
   store `.dcm` + `.jhc` per slice (~2× per-slice bytes). Intentional for the
   progressive rollout, reversible by dropping the `.dcm` once validated, but
   it's live cost on a metered product — track it.

7. **No resumable upload.** A dropped connection mid-block-upload restarts the
   whole file (blocks aren't checkpointed across sessions). Fine for ≤1 GB on
   decent links; rough on flaky rural links — relevant to the low-bandwidth goal.

8. **1 GB hard cap** per asset (`MaxBytes`). A large multi-phase CT ZIP can
   exceed this. Per-instance (bridge) sidesteps it; web ZIP upload does not.

### Quick wins
- Verify `CdnBaseUrl` (config) — biggest retrieval risk.
- Widen `RequeueStaleAsync` to `zip|instances|dcm` (bug #4) — small, correctness.
- Add 2–4 parallel extraction consumers (#3) — throughput under load.
