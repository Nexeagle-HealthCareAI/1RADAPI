# DICOM Viewer Latency — Code Audit Findings

Audit date: 2026-06-11. Scope: easyrad viewer (`AdvancedDicomViewer.jsx`,
`DicomViewerPage.jsx`, `DicomCache.js`), API (`StudyController` manifest +
`DicomExtractionService`), delivery chain (blob → Front Door → browser).

The storage layer is well designed (HTJ2K-lossless slices, `public,
max-age=31536000, immutable` cache headers, per-slice URLs, backend JPEG
thumbnails, IndexedDB re-open cache, middle-slice-first + outward prefetch).
The latency lives in the seams below, ordered by expected impact.

---

## Tier 1 — dominant suspects

### 1. `CdnBaseUrl` may not be configured → viewer bypasses Front Door
`ToCdn()` (StudyController.cs:249) rewrites slice URLs to the Front Door host
**only if** `AzureBlobStorage:CdnBaseUrl` is set. It is **absent from every
appsettings*.json** — if it is also absent from the App Service configuration,
every slice fetch goes straight to `*.blob.core.windows.net`:

- **No edge caching** — every GET pays the full origin round-trip.
- **Azure Blob serves HTTP/1.1** to browsers → the browser caps at ~6
  concurrent connections per host. The viewer queues up to 120 prefetches;
  they drip through 6 at a time. Front Door serves HTTP/2 (one multiplexed
  connection, no cap).
- A 300-slice CT ≈ 60–90 MB of HTJ2K. At 6-way HTTP/1.1 with origin RTT this
  is the difference between ~5 s and ~60 s for a warm scroll-through.

**Verify first** (5 minutes): open DevTools → Network on a slow study. Check
the slice request **host** (blob vs Front Door) and **protocol** (h2 vs
http/1.1). If host is blob: set `AzureBlobStorage:CdnBaseUrl` in App Service
config and the problem class disappears.

### 2. The worker-disable fallback permanently cripples decoding
`AdvancedDicomViewer.jsx:1775-1809` — if the FIRST image doesn't arrive within
45 s, the code re-initialises the loader with `maxWebWorkers: 0` and **never
restores workers**. Two bugs compound:

- The 45 s timeout covers **network fetch + decode**. On a slow connection (or
  the blob-origin path from finding #1) a big slice can blow the budget with a
  perfectly healthy decoder.
- After the fallback, **every subsequent slice decodes on the main thread**
  for the rest of the session — scrolling becomes seconds-per-slice and the UI
  janks. The user experiences this as "the viewer is always slow", when it is
  one bad first load poisoning the session.

Fix: separate fetch from decode before timing anything (fetch the bytes, then
time only the decode), and if the no-worker retry is kept, re-init WITH
workers after the first successful decode.

### 3. Extraction is fully sequential → long "Processing", blocking first view
`DicomExtractionService` (GatherSlices + main loop):

- Staged-instances path downloads each instance blob **one at a time**
  (`foreach url … await Download`), then the main loop transcodes + uploads
  each slice **one at a time** (`await UploadFileAtPathAsync` per slice).
- 300 slices × (download RTT + HTJ2K transcode + upload RTT) ≈ **2–5 minutes**
  wall-clock before the study turns Ready. The user reads this as viewer
  latency ("I uploaded it, why can't I see it").
- Worse: for legacy ZIPs without an ExtractionStatus, the **manifest endpoint
  runs this extraction synchronously inside the request**
  (StudyController.cs:317-330) — the first viewer open can hang for minutes.

Fix: parallelise download/transcode/upload with `SemaphoreSlim` (4–8 wide —
transcode is CPU-bound, upload is IO-bound, they pipeline well); change the
lazy-extract path to enqueue + return "Queued" instead of blocking the
request (the frontend already polls every 6 s and renders a processing state).

---

## Tier 2 — meaningful, cheap to fix

### 4. First paint decodes a wasted slice
`viewport.setStack(imageIds)` (line 1841) defaults to index 0, which kicks off
a fetch+decode of **slice 0** — immediately followed by
`setImageIdIndex(middleIdx)`. The middle slice is already cached, but slice 0's
fetch+decode competes for a worker + connection at the worst moment.
Fix: `viewport.setStack(imageIds, middleIdx)`.

### 5. Prefetch concurrency is set far too high
`requestPoolManager.maxRequestsPerOrigin = { interaction: 200, prefetch: 120 }`
(line 275-279). 120 parallel prefetches saturate bandwidth and CPU and compete
with the slice the radiologist is actually looking at (the code itself notes
this race pushed first paint past 3 s). With HTTP/2 via Front Door, ~16–24
prefetch / ~6 interaction is the sweet spot; today's setting mostly creates
queueing chaos.

### 6. Slow-network double fetch in the IndexedDB-cached path
`loadManifestSliceCached` fetches the **whole blob** with no timeout and no
retry; a stalled connection waits forever (no AbortController), and a failure
bubbles straight to a decode error. Add a fetch timeout + one retry — cheap
insurance against transient AFD/origin hiccups being read as "viewer broken".

### 7. Manifest endpoint does redundant work on every poll
While a study is Processing the page re-fetches the manifest every 6 s; each
call loads **all StudyAssets with all Slices** (including `MetadataJson`) and
re-runs the lazy-extraction scan. For a large study this is a heavy query
polled repeatedly. Cheap fix: a lightweight `status`-only endpoint for the
poll loop (the full manifest only once Ready); or cache slice DTOs.

---

## Tier 3 — infra checklist (no code change, verify in Azure)

1. **Front Door caching enabled** on the route serving `dicom-files`, with
   query-string caching behaviour "Ignore" (URLs carry no query strings — any
   accidental SAS would bust the cache).
2. **Origin region vs user geography** — if the storage account is
   `centralindia` and users are elsewhere, only edge caching saves them; cold
   first reads still pay origin RTT per slice.
3. **Blob access tier Hot** for `dicom-files` (Cool/Cold adds per-read latency
   + cost).
4. **Front Door compression OFF** for `application/dicom` (HTJ2K is already
   entropy-coded; compression burns CPU for ~0 gain).
5. SPA host **cross-origin isolation** (COOP/COEP) is not set —
   `SharedArrayBuffer` unavailable (console warns). The HTJ2K WASM codec works
   without it but cannot use threads. Optional; measure before bothering.

---

## Suggested measurement before/after

In DevTools on a representative 200+ slice CT:
- **TTFI (time to first image)** — manifest request start → first canvas paint.
- **Scroll latency** — enable cache-disabled run, scroll 50 slices, note
  stalls.
- **Protocol + host** per slice request (h2 via Front Door expected).
- `x-cache` response header (Front Door: `TCP_HIT` / `TCP_MISS`) — second run
  through the same study should be near-100 % HIT.

## Suggested fix order

| # | Fix | Effort | Expected gain |
|---|---|---|---|
| 1 | Verify/set `CdnBaseUrl` in App Service config | config-only | dominant if currently unset |
| 2 | `setStack(imageIds, middleIdx)` | 1 line | ~0.5–1 s off first paint |
| 3 | Worker fallback: time decode only + restore workers | small | removes "permanently slow session" |
| 4 | Prefetch pool 120 → ~24 | 1 line | smoother first paint + scroll |
| 5 | Parallelise extraction (Semaphore 4–8) | medium | minutes → tens of seconds to Ready |
| 6 | Lazy-extract → enqueue instead of blocking manifest | small | removes minutes-long first open on legacy ZIPs |
| 7 | Fetch timeout/retry in slice loader | small | resilience |
| 8 | Status-only poll endpoint | small | API load |
