# Cloud PACS — Low-Bandwidth Support Assessment

Assessed: 2026-06-12. Question: does the Cloud PACS support low-bandwidth use?
Verdict: **Partially. The foundations are genuinely strong, but the single
biggest low-bandwidth lever — progressive resolution decode — is supported by
the format and present in the imports yet NOT wired into the diagnostic
viewer.** On a slow link today, every slice is an all-or-nothing full download
before it paints.

## What's already in place (the good news — verified in code)

| Capability | Where | Low-bandwidth value |
|---|---|---|
| **HTJ2K Lossless RPCL transcode** of every slice | `DicomExtractionService.cs:101` | RPCL = **R**esolution-**P**rogressive; ~2–3× lossless compression. The format is the right one. |
| **Front Door CDN + `public, max-age=31536000, immutable`** headers | `StudyController.ToCdn` + `ImmutableCacheControl` | Edge caching + HTTP/2 → slices served near the user, multiplexed (when `CdnBaseUrl` is correctly set — currently the weak link). |
| **Persistent IndexedDB slice cache** | `DicomCache.js`, `loadManifestSliceCached` | Re-opening a study = **zero network**. Biggest practical win on a slow link. |
| **Middle-slice-first load + bounded prefetch** | `AdvancedDicomViewer.jsx` setupEngine | First diagnostic image paints before the whole series arrives. |
| **Backend JPEG thumbnails (256 px, ~30 KB)** | `RenderThumbnail`, `ThumbnailMaxDim=256` | Series rail loads cheaply without pulling full slices. |
| **Connection-aware background prefetcher** | `StudyPrefetcher.js:180` | Skips on `Save-Data`/`2g`; caps to a few studies on `3g`/cellular; uncapped on wifi. Genuinely bandwidth-aware. |
| **Slice fetch timeout + retry** | `fetchSliceWithRetry` | A stalled slice on a flaky link retries instead of hanging. |
| **Per-instance parallel SAS upload (bridge)** | bridge + `instance-upload` endpoints | Upload parallelism; a dropped instance retries just that instance, not the whole study. |

This is a better low-bandwidth baseline than most self-hosted PACS.

## The gaps (what stops it being a true low-bandwidth viewer)

### 1. No progressive / reduced-resolution decode in the live viewer — THE big one
HTJ2K RPCL's defining feature is that a **low-resolution image decodes from a
byte-range PREFIX** of each slice. You can show a usable 1/4-res image from
~10–20 % of the bytes, then refine to full res as the rest streams. Cornerstone
supports this via `ProgressiveRetrieveImages` with a retrieve configuration
that sets `decodeLevel` / HTTP range requests.

- It is **imported** in `MprViewport.jsx` but only as
  `interleavedRetrieveStages` — that's slice-ORDER interleaving (coarse volume
  → fill in), NOT per-image resolution-progressive decode.
- The diagnostic stack viewer (`AdvancedDicomViewer.jsx`) does **not** use it at
  all — `loadManifestSliceCached` fetches the **whole** slice blob, then decodes.
- Net effect on a 2 Mbps link: a 500 KB slice = ~2 s of blank before it paints,
  per slice. With progressive decode the user would see a low-res in ~0.2 s.

### 2. Everything is lossless — no "preview/draft quality" mode
Lossless is correct for primary diagnosis, but there's no low-bandwidth toggle
that serves a lossy HTJ2K rendition (e.g. ~10:1) for triage / remote review /
prior comparison. That would cut bytes 3–5× for non-primary viewing.

### 3. Main-image resolution is never reduced server-side
Only the 256 px series thumbnail is downscaled. The viewport always streams
full-resolution slices — there is no "fit-to-viewport resolution" delivery
(a 512² viewport doesn't need a 2048² slice until zoom).

### 4. In-viewer prefetch aggression is fixed, not adaptive
The background cross-study prefetcher reads `navigator.connection`, but the
in-viewer slice prefetch pool (24-wide) is a constant — it doesn't shrink on a
measured-slow link or grow on fibre.

### 5. Cine has no adaptive frame-skip on slow links
Fixed-rate loop; on a slow link it stalls waiting for undecoded frames rather
than skipping to keep temporal continuity.

## Recommended additions (ranked by low-bandwidth impact per effort)

1. **Wire progressive HTJ2K decode into the stack viewer** (highest impact).
   Register a `ProgressiveRetrieveImages` retrieve configuration with
   `decodeLevel`/range stages and route `loadManifestSliceCached` through it so
   each slice paints low-res-first. The bytes are already RPCL-encoded — this is
   "turn on a capability we already paid for," not new infrastructure.
2. **Confirm/repair `CdnBaseUrl`** (already hardened in `ToCdn` to add the
   scheme) so the CDN path is actually used — without it, none of the caching
   helps and it's blob-origin HTTP/1.1.
3. **Adaptive prefetch**: feed `navigator.connection.downlink` /
   `effectiveType` into the in-viewer prefetch pool size + cine frame-skip.
4. **Optional lossy "preview quality" rendition** for triage/prior compare —
   a second transcode at extraction time + a viewer toggle.
5. **Fit-to-viewport resolution delivery** using the same `decodeLevel` knob —
   don't decode beyond what the current zoom needs.

## Implementation investigation (2026-06-12) — native byte-range path

Verified against the installed `@cornerstonejs/*` **v4.22.2**:

- **Per-image byte-range + reduced-resolution decode is implemented ONLY in the
  `wadors` (DICOMweb) loader**, not the `wadouri` (single-file URL) loader we
  use. `dicom-image-loader/.../wadors/getPixelData.js` calls
  `internal/rangeRequest.js` / `streamRequest.js`; the `wadouri/` folder has no
  range/stream/decodeLevel references at all — it fetches the whole file.
- The HTJ2K decoder DOES support reduced levels (`decodeHTJ2K.js` →
  `decoder.decodeSubResolution(imageInfo.decodeLevel)`), so the codec capability
  exists; it's the **delivery + loader wiring** that's wadors-only.
- `singleRetrieveStages` is a single `retrieveType:'single'` stage (no
  progression); the real progression lives in the wadors range path +
  `interleavedRetrieveStages` (volume slice-order, not per-image resolution).

**Consequence:** on our current single-file blob (`wadouri`) delivery, native
per-image byte-range progressive is **not available as a config toggle**. Two
ways to actually get it:

1. **Range-served frame delivery the wadors loader understands (recommended).**
   Serve each slice's raw HTJ2K frame at a DICOMweb-style endpoint with
   `Accept-Ranges: bytes` and point cornerstone at a `wadors`/streaming imageId.
   Azure Blob already supports range requests, but wadors expects a raw *frame*
   (not a full P10 `.dcm`) and DICOMweb response semantics — so this needs a
   thin frame-serving endpoint (or a real DICOMweb facade). Then progressive +
   decodeLevel work natively, including IndexedDB as the cache-hit fast path.
2. **Hand-rolled truncated-HTJ2K decode** against our blob URLs (range-fetch a
   prefix → locate PixelData → call the wasm decoder at a sub-resolution).
   Technically possible but **high-risk and untestable here**: the codebase
   already carries scars from hand-rolled decode ("missing pixel-format fields →
   blank canvas", the Path C shim). Failure mode risks *wrong pixels*, not just
   *no benefit* — unacceptable for diagnosis. Not recommended.

**Safe, native-aligned wins available now (frontend-only, low risk):**
- **Adaptive in-viewer prefetch** — scale the slice prefetch pool + outward
  window off `navigator.connection` (downlink/effectiveType/saveData). Reduces
  contention so the *visible* slice arrives first on slow links. (The
  cross-study background prefetcher is already connection-aware; the in-viewer
  pool is a fixed 24.)
- **Thumbnail-first viewport paint** — show the existing 256 px series JPEG in
  the viewport as a placeholder while slice 0 streams (coarse-then-sharp without
  any codec-truncation risk).

## Implementation status — option 1 (native byte-range progressive)

Decision: **range-served raw HTJ2K frames, added alongside the .dcm (safe
rollout)**. Frame transfer syntax must be streamable (RPCL `.202` / `.203`).

### ✅ DONE — backend (build-verified, additive, zero risk to current viewer)
- `DicomExtractionService.ExtractStreamableFrame()` pulls the raw HTJ2K
  codestream (frame 0) from each transcoded slice; only RPCL/.203 qualify.
- Extraction now writes a `…/{i:D4}.jhc` raw-frame blob beside each `.dcm`,
  `Content-Type: application/octet-stream; transfer-syntax=<uid>`, immutable
  cache. Non-streamable slices simply get no frame (→ .dcm fallback).
- `ExtractMetadataJson` expanded to the FULL pixel module (samplesPerPixel,
  bitsAllocated, bitsStored, highBit, pixelRepresentation, planarConfiguration,
  photometric) + plane geometry (IOP/IPP/pixelSpacing) + VOI/modality LUT +
  the `frameUrl`. The wadors path has no .dcm header to parse, so the frontend
  builds a metadata provider from exactly these fields.
- Manifest exposes per-slice **`frameUrl`** (CDN-rewritten via `ToCdn`, lifted
  out of metadata by `ExtractFrameUrl`). Null for legacy slices → .dcm.

### ⏭ NEXT — frontend (flagged off by default; needs live validation)
1. Cornerstone **metadata provider** fed from manifest slice metadata
   (imagePixelModule / imagePlaneModule / voiLutModule / modalityLutModule /
   generalSeriesModule / sopCommonModule) keyed by the wadors imageId.
2. Build `wadors:<frameUrl>` imageIds; register the **progressive retrieve
   configuration** (range stage at a reduced `decodeLevel`, then full).
3. Route the slice loader: flag ON **and** slice has `frameUrl` → wadors range
   path (IndexedDB still the cache-hit fast path); else current `.dcm` wadouri.
   Per-slice fallback on any load error.
4. Flag (e.g. `localStorage['1rad_progressive_dicom']`) so it can be validated
   on a throttled connection without affecting anyone else.

### ⚠ INFRA PREREQUISITES (yours, before the frontend can be tested)
- **Azure Blob CORS** on the `dicom-files` account must allow the `Range`
  request header and expose `Content-Range`/`Accept-Ranges` to the SPA origin —
  without it the browser can't issue cross-origin range requests.
- **Front Door** passes `Range` through by default; just don't strip it. Keep
  query-string caching "Ignore" (frame URLs are query-less).
- **Existing studies have no `.jhc` frames** → they auto-fall back to `.dcm`.
  Re-extract a study (or upload a new one) to exercise the progressive path.

## One-line answer
Format, CDN, persistent cache and connection-aware prefetch make it
**low-bandwidth-capable**; but because the diagnostic viewer downloads each
slice in full instead of using HTJ2K progressive decode, it is **not yet
low-bandwidth-optimised**. Item 1 above closes most of that gap using a
capability the encoding already provides.
