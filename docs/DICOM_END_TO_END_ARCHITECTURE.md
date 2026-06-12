# 1Rad Cloud PACS — End-to-End DICOM Architecture

Every stage from "scanner produces images" to "radiologist reads them", the
exact technologies, and an honest scorecard against the four targets:
(1) ~1 s load for a <500 MB study, (2) low-bandwidth tolerant, (3) fast +
reliable UX, (4) a complete toolset.

---

## 0. Master flow

```
  ┌─────────────┐   DICOM    ┌──────────────────────────────────────────────┐
  │  Modality   │ ─C-STORE─► │  INGESTION (3 paths)                          │
  │ (CT/MR/US…) │            │                                                │
  └─────────────┘            │  A. Web technician   B. Web Cloud-PACS         │
        │ or files           │     (appointment)       (study, no visit)      │
        ▼                    │  C. Bridge (Orthanc → per-instance)            │
  ┌─────────────┐            └───────────────┬──────────────────────────────┘
  │ Browser /   │                            │  3-step SAS handshake
  │ Bridge      │   ① upload-token ──────────┤  (no DB row until blob exists)
  └─────────────┘   ② PUT bytes ─────────────┤
        │           ③ upload-complete ───────┘
        ▼  direct to blob (browser never goes through the API for bytes)
  ┌──────────────────────────────────────────────────────────────────────────┐
  │  AZURE BLOB  «dicom-files»     (original upload retained)                  │
  └───────────────────────────────┬──────────────────────────────────────────┘
                                   │  enqueue assetId
                                   ▼
  ┌──────────────────────────────────────────────────────────────────────────┐
  │  EXTRACTION WORKER (N parallel consumers, Channel-backed queue)            │
  │  download → parse (fo-dicom) → group by series                             │
  │  → PER SLICE (6-wide parallel, retry):                                     │
  │       transcode → HTJ2K Lossless RPCL  ──┐                                 │
  │       upload  …/series/{s}/{i}.dcm       │  resilient: 1 bad slice         │
  │       (frame .jhc — OFF by default)      │  is skipped, not fatal          │
  │       thumbnail (series[0], 256px JPEG)  ┘                                 │
  │  → write StudySliceIndex rows (EF)  → study = Ready                        │
  └───────────────────────────────┬──────────────────────────────────────────┘
                                   │  per-slice blobs + immutable cache headers
                                   ▼
  ┌──────────────────────────────────────────────────────────────────────────┐
  │  AZURE FRONT DOOR (CDN)   edge-caches /dicom-files/*  (HTTP/2, global)     │
  └───────────────────────────────┬──────────────────────────────────────────┘
                                   │  ToCdn() rewrites blob host → Front Door
                                   ▼
  ┌──────────────────────────────────────────────────────────────────────────┐
  │  VIEWER (React + Cornerstone3D)                                            │
  │  manifest → per-slice CDN URLs                                             │
  │  IndexedDB cache ◄─► fetch (sniff+retry) ─► worker decode ─► WebGL render  │
  │  middle-slice-first · bounded prefetch · 25+ tools · MPR/3D               │
  └──────────────────────────────────────────────────────────────────────────┘
```

---

## 1. Ingestion (upload)

**Core model — 3-step SAS, browser → blob direct (no API byte-hop):**
1. `POST /Study/upload-token` — validates (PACS module, storage quota, 1 GB cap,
   tenant), mints a blob path, returns a **Write SAS URL** whose lifetime scales
   with file size (30 min → 6 h). **No DB row yet** (so a failed PUT can't orphan
   a row).
2. `PUT <sasUrl>` — browser uploads straight to Azure Blob. >8 MB → **parallel
   4-MB block upload** (4 concurrent + block-list commit); else single PUT with
   XHR progress.
3. `POST /Study/upload-complete` — verifies the blob exists, derives the read URL
   server-side, creates the `StudyAsset`, marks `ExtractionStatus=Queued`,
   **enqueues** it.

**Three paths, one model:**
- **A — Web technician** (appointment-bound): `uploadStudyAssetDirect`.
- **B — Web Cloud PACS** (appointment-free): `registerStudy` → `uploadStudyAssetToStudy`.
- **C — Bridge** (Orthanc → blob): per-instance SAS PUT, 6-wide, then `registerInstanceUpload`.

**Tech:** axios + XHR (progress) + `fetch` (blocks); Azure Blob SAS (account-key
signed); fo-dicom on the bridge side via Orthanc.

---

## 2. Extraction / processing

**Queue:** `Channel<Guid>` (unbounded, multi-reader) → `DicomExtractionWorker`
runs **N parallel consumers** (`Dicom:ExtractionConcurrency`, default 3); each
asset gets its own DI scope (DbContext not shared across threads). Crash-recovery
re-queues stuck `Queued`/`Running` assets on boot.

**Per asset (`DicomExtractionService.ExtractAsync`):**
1. **Download** source (ZIP / staged instances 8-wide / single DCM).
2. **Parse** each instance — `fo-dicom DicomFile.Open`; a bad entry is skipped.
3. **Group** by SeriesInstanceUID, order by InstanceNumber.
4. **Per slice, 6-wide parallel, retry-once, failure-tolerant:**
   - **Transcode → HTJ2K Lossless RPCL** (`1.2.840.10008.1.2.4.202`) via
     `fo-dicom.Codecs` native codec. Lossless = bit-exact pixels; ~2–3× smaller.
     Idempotent (already-HTJ2K passes through); falls back to original bytes on
     any codec error → compression never fails an upload.
   - **Upload** `…/series/{s}/{i}.dcm`, `Cache-Control: max-age=31536000, immutable`.
   - **(`.jhc` raw frame — OFF by default**, `Dicom:WriteProgressiveFrames`; it's
     for the future byte-range path, unused today, so it isn't produced.)
   - **Thumbnail** — series[0] only, 256px JPEG (ImageSharp).
   - One transient failure on a slice → skipped, not fatal; study delivers the
     slices that succeeded.
5. **Write** `StudySliceIndex` rows; recompute `StorageBytes`; study → `Ready`.

**Tech:** .NET 8 BackgroundService, System.Threading.Channels, fo-dicom 5.2 +
fo-dicom.Codecs 5.16 (native HTJ2K/JPEG-LS/J2K) + ImageSharp.

---

## 3. Storage & delivery

- **Container `dicom-files`** (Azure Blob), public-ACL **behind Front Door**
  (Posture A: storage firewall locks origin to Front Door).
- **Layout:** `{hospital}/{appt|study}/extracted/{asset}/series/{s}/{i}.dcm`,
  `/thumbs/{s}.jpg`. Deterministic → easy delete-by-prefix + orphan sweep.
- **Immutable cache headers** (`max-age=1yr, immutable`) → repeat views are
  CDN-edge + browser-cache hits.
- **Front Door (CDN):** global edge cache, **HTTP/2** (one multiplexed
  connection — vs blob's HTTP/1.1 ~6-connection cap). `ToCdn()` rewrites the blob
  host → the Front Door host (`AzureBlobStorage:CdnBaseUrl`).
- **Lifecycle:** orphan-sweep job (staging blobs always; full reconcile opt-in
  dry-run); re-extraction self-cleans prior blobs; PACS-downgrade auto-delete.

---

## 4. Retrieval & rendering

**Manifest** (`GET …/manifest`, or `/shared/{token}/manifest` for share links) →
assets → series → per-slice `url` (CDN-rewritten), `thumbnailUrl`, pixel
`metadata`. A lightweight `extraction-status` is polled (6 s) while processing.

**Viewer (`AdvancedDicomViewer.jsx`, Cornerstone3D 4.x):**
- Slice load path: **IndexedDB cache HIT** → local bytes (zero network); **MISS**
  → `fetch` (30 s timeout, 1 retry, **DICM-magic sniff** to fast-fail HTML/JSON
  from a CDN misconfig), persist to IndexedDB, hand Cornerstone a local `blob:`
  URL → **web-worker decode** (OpenJPH WASM) → **WebGL** texture.
- **Middle-slice-first** load (radiologist sees a diagnostic image immediately),
  then **bounded outward prefetch** (24 desktop / 10 mobile).
- In-memory decoded cache (2 GB desktop / 800 MB mobile).
- Cross-study **connection-aware background prefetcher** (skips on Save-Data/2G).

**Tech:** React/Vite, Cornerstone3D core/tools/dicom-image-loader, OpenJPH WASM,
IndexedDB, WebGL2.

---

## 5. Toolset (expectation 4)

25+ tools wired: WindowLevel, Zoom, Pan, StackScroll, Length, Height,
Bidirectional, Angle, CobbAngle, Elliptical/Rectangle/Circle/Freehand/**Spline**
ROI, **Livewire** smart contour, Probe, **DragProbe**, **WindowLevelRegion**,
**PlanarRotate**, **UltrasoundDirectional**, Arrow, **Label**, **Eraser**,
Magnify, AdvancedMagnify, always-on **ScaleOverlay**. Plus **MPR/3D** (axial/
coronal/sagittal + VR, thick-slab MIP/MinIP/Average, crosshairs), windowing
presets, invert/flip/rotate, cine, key-image, screenshot, 1×2/2×2 layouts,
touch (1-finger tool / 2-finger pan+pinch-zoom).

---

## 6. Data model (core)

```
ImagingStudy (Id, HospitalId, PatientId?, AppointmentId?, StudyInstanceUID?,
              PatientName, Modality, StudyDate, Status[Received|Processing|
              Ready|Failed], MatchStatus, AccessionNumber, ReadyAt)
   └─< StudyAsset (Id, ImagingStudyId?, AppointmentId?, BlobUrl, FileType,
                   StorageBytes, ExtractionStatus, ExtractionError)
          └─< StudySliceIndex (SliceId, SeriesUID, SopInstanceUID,
                   InstanceNumber, BlobUrl, ThumbnailUrl, MetadataJson)
DiagnosticReport (… ImagingStudyId? | AppointmentId?)   ← one-of
HospitalSubscription (Modules CSV, IncludedStorageGb, …)  ← gates PACS + quota
```

---

## 7. Scorecard vs. the four targets

### ① "≈1 s load for a <500 MB study"
**Reframe first:** 500 MB cannot fully transfer in 1 s over any normal link
(500 MB ÷ 50 Mbps ≈ 80 s for *all* bytes). The achievable and correct goal is
**~1 s to the first diagnostic image**, with the rest streaming behind it. The
architecture is built for exactly that (middle-slice-first + per-slice CDN +
worker decode).
- **Met when:** Front Door is actually in the path (CdnBaseUrl resolves) and the
  cache is warm — second open of a study is ~instant (IndexedDB/edge hits).
- **GAPS to hit ~1 s cold, first-image:**
  - 🔴 **CdnBaseUrl must resolve.** Confirm via the startup log
    `[CDN] CdnBaseUrl resolved to …`. If it doesn't resolve, slices use the raw
    blob host → firewall hang / HTTP-1.1 → seconds, not 1 s. (The resolver reads
    every key form, so this is about the value being *set*, not its spelling —
    see §8.1 for the host-OS key-naming nuance.)
  - 🟠 **Byte-range progressive not wired** — first paint waits for the *whole*
    middle slice (~0.3–0.5 MB) instead of a low-res prefix. Wiring the wadors
    path (and re-enabling `.jhc`) gives sub-second low-res-first.

### ② Low bandwidth
- **Strong foundations:** HTJ2K (2–3× smaller, lossless), CDN edge + immutable
  cache, IndexedDB persistent cache (re-open = zero network), middle-first,
  connection-aware background prefetch, fetch timeout/retry.
- **GAP:** the single biggest low-bandwidth lever — **per-slice byte-range
  progressive decode** (low-res from ~15 % of bytes, then sharpen) — exists in
  the encoding (RPCL) but **isn't wired into the viewer** (wadors path deferred,
  `.jhc` currently off). Until then, each slice is a whole-file fetch.

### ③ Fast & reliable UX
- **Met/strong:** middle-first first paint, skeleton/loading states, fast-fail
  sniff (no 45 s hangs on a misconfig), **resilient extraction** (1 bad slice
  doesn't fail the study; retry; partial delivery), surfaced failure reason +
  Retry, parallel extraction, 2D-default land (no forced 3D), touch.
- **Watch:** the **linked-scroll sync bug** (2×2 comparison has no synced
  scroll — `getSynchronizer` API mismatch); 45 s+30 s decoder budget is long on
  a true hang (mitigated by the sniff once the frontend is fresh).

### ④ Complete toolset
- **Met:** broad 2D + measurement + ROI + MPR/3D set (Section 5).
- **Gaps (clinical depth):** **measurement persistence** (annotations are
  memory-only today), **multiframe** (US/XA clips render one frame), **priors
  comparison**, real cine controls, **standard corner overlays + orientation
  markers**, segmentation/volumetrics.

---

## 8. Critical path to fully hit ①+②

1. **Make `CdnBaseUrl` resolve** — confirm via the startup log
   `[CDN] CdnBaseUrl resolved to …`. The runtime is .NET 8 (`net8.0`), so the
   App Service can be a Windows **or** Linux plan; the *only* thing that changes
   is the App-Setting key spelling:
   - **Windows** App Service → `AzureBlobStorage:CdnBaseUrl` (colon) is valid.
   - **Linux** App Service / container → use `AzureBlobStorage__CdnBaseUrl`
     (double underscore), because a colon is illegal in a Linux env-var name.
   Check which you're on: `az webapp show -n <app> -g <rg> --query kind -o tsv`
   (`app` = Windows, `app,linux` = Linux). The resolver already reads both forms
   plus the env var, so the fix is ensuring the value is *present and non-empty*,
   not renaming. (Unblocks ①+②.)
2. **Wire the wadors byte-range progressive path** in the viewer (metadata
   provider from the manifest + `ProgressiveRetrieveImages` range stages) and
   **re-enable `.jhc`** (`Dicom:WriteProgressiveFrames=true`). (Delivers
   sub-second low-res-first + true low-bandwidth ②.)
3. **Confirm Front Door**: origin path = root (no doubled `/dicom-files/`), Range
   passthrough, and **blob CORS** for the SPA origin (+ `Range`/`Content-Range`).
4. **Then** drop the `.dcm` (store only `.jhc`) for the full storage saving while
   keeping progressive.
5. Toolset depth: measurement persistence → multiframe → priors → corner
   overlays → fix linked-scroll sync.

Done in this order, "≈1 s to first image, even on low bandwidth" becomes real
rather than aspirational — the encoding and storage are already right; the gap
is the delivery config (#1, #3) and the viewer's progressive consumption (#2).
```
