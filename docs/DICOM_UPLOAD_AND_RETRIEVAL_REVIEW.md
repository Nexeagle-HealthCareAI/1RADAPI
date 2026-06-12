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
- ~~Widen `RequeueStaleAsync` to `zip|instances|dcm` (bug #4)~~ — DONE.
- ~~Add 2–4 parallel extraction consumers (#3)~~ — DONE.

---

## PART 4 — DEEPER REVIEW: storage lifecycle & cleanup gaps (2026-06-12)

These are the gaps the first pass didn't reach. The **storage-lifecycle ones
are the most material** — they leak real, uncapped Azure cost.

### A. 🐛 Raw `.jhc` frames are ORPHANED on every delete (regression I introduced)
`DeleteStudy` ([StudyController]) and the auto-delete job
([SubscriptionLifecycleJob]) both build the blob-deletion list from
`asset.BlobUrl` + `slice.BlobUrl` (.dcm) + `slice.ThumbnailUrl` — but **not the
new `.jhc` frame blobs**. So every deleted / auto-deleted study now leaks all
its frame blobs forever. Fix: derive the frame path from the slice URL
(`.dcm`→`.jhc`) and add it to the deletion list in BOTH places.

### B. 🐛 NO orphan/staging sweep job exists — despite the code claiming one
Multiple comments say staging blobs / prior-run slices are "reclaimed by a
periodic sweep" / "a periodic cleanup script can sweep them." **That job does
not exist** (`BackgroundJobs/` has no sweep). Three permanent-orphan sources:
- **Bridge per-instance staging blobs** (`staging/{assetId}/…`) — written by the
  bridge, never deleted.
- **Re-extraction orphans** — re-running extraction deletes the prior
  `StudySliceIndex` ROWS but leaves the old slice/frame/thumbnail BLOBS.
- All accumulate **unbounded**. Fix: a real sweep job (by prefix age, or
  reconcile blobs vs. live slice rows).

### C. Storage metering UNDER-reports actual Azure usage
Metering = `SUM(StudyAsset.StorageBytes)`, recomputed per extraction to the
*current* footprint. The orphans from (A)+(B) are real bytes in your account
that metering never counts — so customers aren't overcharged, but **your Azure
bill grows without bound and without visibility**. (A)+(B) must be fixed for
metering to mean anything.

### D. Re-extraction is destructive-rebuild with no rollback
`ExtractAsync` sets `Running`, deletes prior slice rows, then rebuilds. A crash
mid-way leaves partial slices + orphaned old blobs; the crash-requeue re-runs
and wipes again. Acceptable, but means a poison asset can thrash. Consider a
"build to a new prefix, swap, then delete old" pattern.

### E. Frame-URL footgun in metadata JSON
The raw (non-CDN) `frameUrl` stays embedded in each slice's `MetadataJson`
(the manifest lifts a CDN copy out as a sibling, but the raw one remains in the
metadata the client receives). Anything reading `metadata.frameUrl` directly
would bypass Front Door / hit a firewalled blob. Strip it from the metadata copy.

### F. SAS 30-min expiry vs. 1 GB on a slow link
A near-1 GB upload on a slow (low-bandwidth-target) link can exceed the 30-min
SAS lifetime → block PUTs start 403-ing mid-upload, no renewal. Either lengthen
the SAS for large files or renew on expiry.

### G. Orphan BLOB on upload-complete failure
Row-after-blob prevents orphan *rows*, but if the PUT succeeds and
`upload-complete` then fails (network), the blob exists with no DB row — an
orphan blob counted nowhere. The sweep (B) would reclaim it; today nothing does.

### H. Manifest list does a per-row `assetCount` subquery
`ListStudies` projects `assetCount = StudyAssets.Count(...)` per study — an N+1
on large worklists. Pre-aggregate or join.

### Resolution (all fixed 2026-06-12)
- **A** ✅ Both delete paths (DeleteStudy + auto-delete job) now reclaim the
  `.jhc` frame via `DicomExtractionService.FrameUrlFromSlice` (canonical helper).
- **B** ✅ New `BlobOrphanSweepJob`. Phase 1 (staging sweep) runs by default and
  deletes transient `…/staging/…` blobs past retention. Phase 2 (full reconcile
  vs. live StudyAsset/StudySliceIndex references incl. derived frames) is opt-in
  (`Dicom:OrphanSweep:ReconcileEnabled`) and **dry-run unless**
  `Dicom:OrphanSweep:DeleteOrphans=true` — so a reference-set miss can't delete
  live data until you've reviewed its candidate logs. Added `IBlobService.
  ListBlobsAsync` + `DeleteBlobByNameAsync`.
- **C** ✅ Resolved structurally by A+B — no new uncounted orphans, and existing
  ones are reclaimable, so actual usage converges to metered `SUM(StorageBytes)`.
- **D** ✅ Re-extraction now deletes the prior run's slice/frame/thumbnail blobs
  before rebuilding (self-cleaning; the sweep is the backstop).
- **E** ✅ Manifest strips the raw `frameUrl` from the metadata copy
  (`StripFrameUrl`); only the CDN-rewritten sibling is exposed.
- **F** ✅ SAS lifetime scales with file size (`SasValidityFor`: 30 min → 6 h
  cap) on both whole-file upload-token endpoints.
- **G** ✅ Covered by B's reconcile (a blob with no live reference is reclaimed).
- **H** ✅ Studies-list replaced the per-row correlated `assetCount` subquery
  with a single grouped-count query merged in memory.

### Config for the sweep (defaults are safe)
```
Dicom:OrphanSweep:Enabled (true)              # run the job
Dicom:OrphanSweep:IntervalHours (24)
Dicom:OrphanSweep:StagingRetentionHours (24)  # phase 1 deletes staging older than this
Dicom:OrphanSweep:ReconcileEnabled (false)    # turn on phase 2
Dicom:OrphanSweep:DeleteOrphans (false)        # phase 2 DELETES vs. dry-run-log
Dicom:OrphanSweep:OrphanMinAgeHours (48)
```
