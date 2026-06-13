# Cloud PACS — DICOM Pipeline Architecture (current state)

**Scope:** the end-to-end DICOM path — upload → extract → deliver → view — across
three repos, plus the design decisions, config, deploy requirements, and known
limitations. This is the authoritative current-state reference; the older
`DICOM_*_DEEPDIVE.md` / `*_ASSESSMENT.md` docs are point-in-time investigations
that fed into this.

**The four product goals this architecture is judged against:**
1. **Low-bandwidth loading** — usable on 2G/3G/mobile.
2. **Fast binding** — study opens to first diagnostic image quickly.
3. **Smooth scrolling** + adequate 2D viewing.
4. **Proper reformats** — coronal/sagittal (MPR), including on low bandwidth/mobile.

---

## 1. Components

| Repo | Role | Stack |
|---|---|---|
| `EasyHMS/1RadAPI` | Backend: extraction, delivery, manifest | .NET 8 (Clean Arch: Domain/Application/Infrastructure/API) |
| `1Rad/easyrad` | Viewer SPA | React + Vite, Cornerstone3D 4.22.2 |
| `1Rad/1RadDb` | SQL schema | `oneRadDB/schema/NN_*.sql` (numbered, idempotent) |

**Storage/CDN:** Azure Blob (`dicom-files` container) behind Azure Front Door.
Slices carry `public, max-age=31536000, immutable`; the viewer fetches via the
Front Door host (`AzureBlobStorage:CdnBaseUrl`) for HTTP/2 + edge caching.

---

## 2. End-to-end flow

```
Upload (ZIP / per-instance SAS / single .dcm)
   → StudyAsset row, ExtractionStatus=Queued
        → [DicomExtractionWorker] claims via SQL lease
             → GatherSlices (stream ZIP→temp file, parse metadata on demand)
             → PARALLEL per-slice (bounded 6): transcode→HTJ2K, upload .dcm,
               render preview JPEG (+thumbnail on slice 0), extract metadata JSON
               [+ accumulate downsampled volume plane if reformat enabled]
             → SEQUENTIAL: write StudySliceIndex rows, mark Extracted (study Ready)
             → [if reformat] reslice volume → coronal/sagittal series → upload
             → [if clean] reclaim source ZIP
   → Viewer GET /manifest → per-series slice URLs (CDN) + metadata + previews
        → Cornerstone wadouri loader (IndexedDB cache in front) → render
```

---

## 3. Extraction (`DicomExtractionService` + `DicomExtractionWorker`)

**Durable leased queue.** The `StudyAssets` table *is* the queue.
`ClaimNextExtractionJobAsync` claims a job with a lease (`READPAST, UPDLOCK,
ROWLOCK`) so multiple API instances never double-process; a crashed instance's
lease expires and is reclaimed. The worker is poll → claim → heartbeat (renew
lease) → process → retry. Retry is DB-durable (`ExtractionAttempts` /
`ExtractionNextAttemptAt`, default 3).

**Streaming / bounded memory (the OOM fix).** The ZIP streams to a temp file
(never fully in RAM); each slice's `DicomFile` is re-opened on demand in the
parallel task and not retained; raw bytes are freed right after upload. Peak is
bounded to ~6 in-flight slices (`uploadGate`), not the whole study.

**Compression.** Every slice is transcoded to **HTJ2K Lossless RPCL** (~2–3×;
a 512² CT slice ≈ 515 KB → ~131 KB). Lossless = primary-diagnosis safe, and RPCL
is resolution-progressive (basis for byte-range/`.jhc` if ever wired).

**Two-tier preview.** One decode per slice yields a 128 px preview JPEG (~2–4 KB,
`previewUrl`) and, on slice 0, a 256 px series thumbnail. The viewer shows the
preview instantly (blurry→sharp). Rendered DIRECTLY from `DicomPixelData` +
the DICOM linear VOI window (`RenderGrayscaleJpeg`) — NOT via fo-dicom's
ImageSharp bridge (binary-incompatible with the ImageSharp 3.x we ship).

**Server-side MPR reformatting** — see §6.

**Per-environment caveat:** the native HTJ2K codec (`fo-dicom.Codecs`, win-x64)
needs a **64-bit App Service**. A 32-bit worker can't load it → transcode throws,
is caught, and uploads the ORIGINAL (uncompressed ~515 KB) bytes. Log symptom:
`HTJ2K transcode failed — uploading original bytes`.

---

## 4. Delivery + manifest (`StudyController`)

- **Manifest** groups `StudySliceIndex` rows by `SeriesUID` → per-series
  `{ seriesDescription, modality, thumbnailUrl, slices[] }`. Each slice carries
  `url` (.dcm, CDN), `previewUrl`, optional `frameUrl` (.jhc), and a compact
  `metadata` JSON (pixel module + plane geometry + VOI/rescale).
- **CDN rewrite (`ToCdn`)** rewrites blob URLs to the Front Door host. The raw
  (non-CDN) `frameUrl` is stripped from the metadata copy (Front-Door-bypass
  footgun).
- **Response compression** (Brotli+Gzip, `application/json`) shrinks the
  ~300 KB manifest to ~30–50 KB → faster open.

---

## 5. Viewer (`easyrad`)

**2D stack** (`AdvancedDicomViewer`) — default path:
- **Custom wadouri loader** with an **IndexedDB compressed cache** in front:
  re-opening a study is zero-network.
- **Binding (cold-start):** `warmupCornerstone()` runs on page mount so codec/WASM
  init OVERLAPS the manifest fetch; `preParsedMetadata` (built from the manifest)
  flips `isReady` with no redundant slice-0 fetch; the middle slice loads first;
  the placeholder prefers the middle slice's (pre-warmed) preview. Net cold-start
  ≈ halved (manifest → middle slice → paint).
- **Scroll:** instant preview overlay per slice; a scroll-direction prefetch
  decodes ahead of travel; the per-slice React commit is rAF + time-throttled
  (~20/sec, trailing flush) so it frees the main thread for Cornerstone's canvas
  render (canvas scrolls at full native rate). 8 decode workers; 16-bit textures.
- **Connection-aware (`getConnectionTier`):** on slow links, previews are warmed
  for the WHOLE study (<1 MB makes it blurry-browsable), full-slice prefetch drops
  to ±1, request-pool prefetch concurrency is 24/10/4 (fast/med/slow), and the
  full-res whole-study skeleton is skipped (previews cover reach).

**MPR / 3D** (`MprViewport`) — opt-in overlay, desktop + capable GPU:
- One streaming volume from the same cached imageIds → axial/coronal/sagittal +
  3D, crosshair-synced. Geometry served by the manifest metadata provider
  (`manifestMetadata.js`) and validated (`dicomVolume.js`: orientation
  consistency, uniform spacing, gantry-tilt rejection) — bad geometry falls back
  to 2D cleanly. Interleaved progressive load (coarse→sharp); VTK clipping-plane
  crop box; slab MIP/MinIP; VR presets. GPU-gated off on iOS/phones.

---

## 6. Server-side MPR reformatting (NEW — opt-in)

**Why:** client-side MPR needs the whole volume + a GPU, so it can't serve
low-bandwidth or mobile users. Server-side reslicing delivers coronal/sagittal as
ordinary 2D series → they get all the low-bandwidth treatment and work on phones.

**Design (memory-safe, best-effort):**
- The **largest eligible** axial series is chosen; a **downsampled** (`ReformatMaxDim`,
  default 256²) `short[]` volume is accumulated DURING the existing per-slice
  decode — each slice writes its own z-plane (lock-free), no second decode/download.
- A study whose downsampled volume exceeds `ReformatMaxVoxels` (40 M ≈ 80 MB) is
  **skipped** → cannot reintroduce the OOM. Only one accumulator at a time.
- Reslicing runs **after axial is durably committed**, so any failure leaves the
  axial study untouched. Each plane → 16-bit MONOCHROME2 DICOM (correct IOP/IPP/
  PixelSpacing/FrameOfReference) → HTJ2K → upload + preview + thumbnail → a new
  `CORONAL (MPR)` / `SAGITTAL (MPR)` series row.
- Pure reslice + geometry math is isolated in **`VolumeReformatter.cs`** and
  **unit-tested** (`VolumeReformatterTests`, 6/6) against a synthetic volume.
- Reformatted series ride the EXISTING manifest (grouped by SeriesUID) and cleanup
  (prior-wipe / orphan sweep / DeleteStudy all key off `StudySliceIndex` rows) —
  **no new plumbing, no new CORS/Front Door**.

**Rollout:** deploy → set `Dicom:WriteReformattedPlanes=true` → **re-extract** a
study (existing studies have no reformats until re-extracted).

---

## 7. Configuration (all `Dicom:` keys)

| Key | Default | Meaning |
|---|---|---|
| `DeleteSourceAfterExtraction` | `true` | Reclaim source ZIP after a clean run |
| `WriteSlicePreviews` | `true` | Per-slice 128 px preview JPEG |
| `WriteProgressiveFrames` | `false` | Write `.jhc` raw frames (consumer NOT wired) |
| `ExtractionConcurrency` | `3` | Parallel extraction jobs per instance |
| `ExtractionMaxAttempts` | `3` | DB-durable retry cap |
| `ExtractionLeaseSeconds` | `90` | Lease duration (heartbeat renews) |
| `ExtractionPollSeconds` | `5` | Worker poll interval |
| `WriteReformattedPlanes` | `false` | **Server-side coronal/sagittal** |
| `ReformatMaxDim` | `256` | In-plane resolution cap of the reformat volume |
| `ReformatMaxVoxels` | `40000000` | Skip reformat above this (memory guard) |
| `ReformatMinSlices` | `16` | Below this a series isn't resliced |
| `AzureBlobStorage:CdnBaseUrl` | — | Front Door host (`:` Windows / `__` Linux) |

---

## 8. Data model (key tables)

- **`StudyAsset`** — one upload. Doubles as the extraction queue row:
  `ExtractionStatus/Phase/Attempts/NextAttemptAt/LeaseOwner/LeaseUntil/
  ProcessedSlices/TotalSlices`, `StorageBytes`.
- **`StudySliceIndex`** — one per delivered slice (axial AND reformatted):
  `SeriesUID, SopInstanceUID, InstanceNumber, SeriesDescription, Modality,
  BlobUrl, BlobPath, ThumbnailUrl, MetadataJson`. Reformatted slices are just
  rows with `SeriesDescription = "CORONAL (MPR)" / "SAGITTAL (MPR)"`.

---

## 9. Deploy / config gaps (NOT in code — per environment)

1. Run the schema files in `1RadDb/oneRadDB/schema/` (incl. `77_extraction_leased_queue.sql`).
2. **App Service = 64-bit** (B1+ tier) — required for the HTJ2K codec.
3. **Storage blob CORS** must allow the deployed SWA origin (`*` is cache-safe;
   explicit origins need Front Door to vary cache by Origin).
4. **Purge Front Door** after any transcode/CORS change (slices are 1yr-immutable).
5. `AzureBlobStorage:CdnBaseUrl` set to the Front Door endpoint (confirm the
   `[CDN] CdnBaseUrl resolved to …` line at startup, else slices bypass the CDN →
   HTTP/1.1 6-connection cap → ~4× worse scroll).
6. For reformats: `Dicom:WriteReformattedPlanes=true` + re-extract.

---

## 10. Goal scorecard

| Goal | Verdict | Notes |
|---|---|---|
| 1. Low bandwidth | ✅ 2D met | Previews-on-slow + adaptive prefetch; reformats extend it to MPR |
| 2. Fast binding | ✅ met | Warm codec + preParsedMetadata + middle-first; warm re-opens ~instant |
| 3. Smooth scroll | ✅ met (typical) | Preview bridge + prefetch + commit throttle; volume viewport is the remaining lever for large-slice/weak-GPU jank |
| 4. Coronal/sagittal | ✅ desktop; ⏳ low-bw via reformat | Client MPR correct on desktop; server-side reformat brings it to low-bw/mobile (needs real-study validation) |

---

## 11. Known limitations / follow-ups

- **Reformat needs real-DICOM validation.** The reslice MATH is unit-tested, but
  the decode→build-DICOM→HTJ2K→upload path is not yet validated on a real study —
  verify coronal/sagittal orientation + that a measurement reads correctly.
- **Reformat is synchronous within extraction** → holds a worker slot for the
  duration (256+256 HTJ2K encodes). Fine opt-in; for scale, move to a separate
  queued job.
- **Extra decode CPU** when reformatting (the reslice re-decodes pixels rather
  than reusing the preview decode) — a known sharing optimization.
- **Reformat assumes an axial source.** A non-axial-acquired largest series would
  mislabel the planes (geometry is still IOP-correct, labels aren't).
- **Coverage gate is 60%** — a series with many dimension-mismatched/failed slices
  could show black bands; consider raising or gap-interpolating.
- **Multi-frame DICOM** (one file = many frames) isn't modelled (one file = one
  slice) — a pre-existing pipeline assumption.
- **Byte-range progressive (`.jhc`)** is built server-side but the wadors consumer
  is NOT wired (Cornerstone's progressive is wadors-only; our path is wadouri).
- **Volume viewport disabled** (`VOLUME_VIEWPORT_THRESHOLD=Infinity`) — re-enabling
  with the `MprViewport` wait-for-center pattern is the remaining scroll lever.

---

## 12. What this review checked (this pass)

- ✅ Storage metering: fixed a clobber where ZIP reclaim dropped reformat bytes.
- ✅ FrameOfReferenceUID now populated on reformatted slices.
- ✅ Cleanup: prior-wipe, orphan sweep, and DeleteStudy all cover reformat blobs
  (they key off `StudySliceIndex` rows + the derived `_prev.jpg`).
- ✅ Lease: reformat runs after the lease is released + the asset is `Extracted`,
  so it can't be double-claimed; the worker slot is simply held longer.
- ✅ Manifest: reformatted series surface automatically (grouped by SeriesUID).
- ✅ Builds: backend `0 Error(s)`; `VolumeReformatterTests` 6/6 green.
