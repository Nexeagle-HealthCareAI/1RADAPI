# DICOM Lossless Compression & Extraction — Deep Dive

Covers two things you asked: (1) why extraction can fail and how we've reduced
that risk, and (2) exactly how the lossless compression works, part by part.

---

## PART A — Extraction failure: causes, blast radius, and the hardening shipped

### The pipeline (where failures live)
Upload and processing are **two decoupled stages**:

1. **Upload** (browser → blob via SAS). Reaching 100% = bytes are in storage.
   This is why a "failed" study is still downloadable — the original file is fine.
2. **Extraction** (the async worker), with these sub-steps, each a failure point:
   - **Download** the source from blob (`GatherSlicesFrom{Zip|StagedInstances|SingleDcm}Async`).
   - **Parse** each instance (`ParseSlice` → `DicomFile.Open`).
   - **Transcode** each slice to HTJ2K (`TranscodeSliceBytes`).
   - **Upload** each per-slice `.dcm` + `.jhc` frame + thumbnail to blob.
   - **Write** the slice index (EF).

### Failure causes, ranked
1. **Not valid DICOM** → `ParseSlice` returns null for every entry →
   `"No readable DICOM instances found in the upload."` (ZIP with no DICOM, a
   corrupt/renamed file). Most common.
2. **Backend can't read the blob** — with the Posture-A storage firewall, the
   browser path (SAS/CDN) works but the **worker's `DownloadFileAsync`** (App
   Service → storage account) is blocked → a storage `403`/connection error.
   The single most likely *infrastructure* cause for "uploads fine, processing
   fails." Fix: allow the App Service on the storage firewall.
3. **Transient blob hiccup on one slice upload** — previously **fatal to the
   whole study** (see below). Now mitigated.
4. **OOM** — the whole study is parsed into memory at once (every `DicomFile` +
   original bytes held). A multi-GB study on a small App Service can OOM →
   process killed → study stuck `Queued`/`Running` (the crash-requeue then
   re-runs it). Mitigation idea below.
5. **Exotic source codec** the native transcoder can't decode — **not fatal**:
   `TranscodeSliceBytes` catches and falls back to the original bytes, so the
   slice still uploads (it just isn't re-compressed).

### Blast-radius fix shipped (the big one)
The parallel slice phase was **all-or-nothing**: one slice's upload throwing
made `Task.WhenAll` throw → the entire study `Failed`. For a 300-slice study,
one flaky upload killed all 300. Now:
- Each slice upload **retries once** (`UploadWithRetryAsync`).
- A slice that still fails is **caught and skipped** (returns `null`), not
  thrown — counted in `failedSliceCount`.
- After the phase: if **zero** slices landed → genuine failure (surface it); if
  **some** failed but most succeeded → **deliver the partial study** + log a
  warning. So a study no longer dies because of one bad slice.

### Other risk-reduction already in place
- **Per-slice parse resilience** — a single unreadable entry in a ZIP is skipped,
  not fatal.
- **Crash-requeue** — assets stuck `Queued`/`Running` after a worker crash are
  re-enqueued on startup (now covering zip/instances/dcm).
- **Failure reason surfaced** — `ExtractionError` is now returned on the studies
  list + shown in the worklist + a **Retry** button re-queues it.
- **Parallel consumers** — N studies extract concurrently (configurable).

### Recommended next risk reducers (not yet done)
- **Stream large ZIPs** instead of loading every `DicomFile` into memory at once
  (cap concurrent in-flight slices) → removes the OOM class (#4).
- **Retry the blob *download*** (`GatherSlices…`) like we retry uploads.
- **Per-study failure threshold** — if >X% of slices fail, mark Failed rather
  than delivering a heavily-partial study.

---

## PART B — How the lossless compression works (part by part)

### B0. One-line summary
Every slice is **transcoded to HTJ2K Lossless RPCL** (High-Throughput JPEG 2000,
DICOM transfer syntax **`1.2.840.10008.1.2.4.202`**) during extraction, before
it's stored. "Lossless" = the decoded pixels are **bit-for-bit identical** to the
source — diagnosis-safe — while the bytes shrink ~2–3×.

### B1. Where it runs — `DicomExtractionService.TranscodeSliceBytes`
Per slice, in the parallel extraction phase:
```
src = dicom.Dataset.InternalTransferSyntax
if src is already HTJ2K (.202/.201/.203) → return original (don't re-encode)
else: transcoder = new DicomTranscoder(src, HTJ2KLosslessRPCL)
      transcodedFile = transcoder.Transcode(dicom)
      return transcodedFile bytes;  didTranscode = true
on ANY exception → return original bytes (never fail the slice over compression)
```
Key properties:
- **Idempotent** — already-HTJ2K slices are passed through untouched.
- **Lossless from ANY source** — uncompressed, JPEG-LS, JPEG-2000, JPEG-baseline
  all decode then re-encode to HTJ2K *losslessly*. Re-encoding an already-lossy
  source adds **no further** loss (the pixels are already what they are).
- **Fail-safe** — a codec it can't handle falls back to the original bytes, so
  compression never causes an extraction failure.

### B2. The codec engine — fo-dicom + native codecs
Wired once in the constructor:
```
new DicomSetupBuilder()
  .RegisterServices(AddImageManager<ImageSharpImageManager>())   // rendering (thumbnails)
  .RegisterServices(AddTranscoderManager<NativeTranscoderManager>()) // the codecs
  .SkipValidation().Build();
```
Packages (`1Rad.Infrastructure.csproj`):
- **`fo-dicom` 5.2.1** — DICOM parsing/dataset model.
- **`fo-dicom.Codecs` 5.16.0** — the **native** JPEG-LS / JPEG-2000 / HTJ2K /
  JPEG-baseline encoders/decoders (the actual compression math, in native libs).
- **`fo-dicom.Imaging.ImageSharp` + SixLabors.ImageSharp** — software rendering
  for the 256px JPEG **thumbnails** (quality 70 — the only *lossy* artefact, and
  it's a preview, never the diagnostic image).
> Deployment note: the native codec libs must ship for the target OS (Linux
> `.so`). If they fail to load, transcode throws → we fall back to original
> bytes — uploads still succeed, just uncompressed. So a missing codec degrades
> compression, it doesn't break extraction.

### B3. Why **HTJ2K RPCL** specifically (not JPEG-LS)
- **Lossless** — primary-diagnosis safe (regulatory + clinical requirement).
- **High-Throughput** J2K decodes far faster than classic JPEG-2000 (the "HT"
  block coder), which matters for browser-side WASM decode.
- **RPCL = Resolution-Position-Component-Layer** progression order. This is the
  payoff: the codestream is ordered **lowest-resolution-first**, so:
  - a **byte-range prefix** decodes a coarse preview → the basis of the
    progressive 2D loading and decimated MPR (low-res first, then sharpen);
  - the `.jhc` raw-frame we store alongside each `.dcm` is exactly this
    resolution-progressive codestream, served with `Accept-Ranges`.
- `.201` (HTJ2K Lossless, non-RPCL) is **deliberately avoided** — it isn't in
  Cornerstone's streamable set, so it can't do byte-range progressive. RPCL
  (`.202`) is the one that unlocks it.

### B4. What gets stored (the per-slice footprint)
For each slice, extraction writes to `dicom-files`:
1. `…/series/{s}/{i}.dcm` — the **HTJ2K-transcoded P10 slice** (what the viewer
   loads today), `Cache-Control: public, max-age=31536000, immutable`.
2. `…/series/{s}/{i}.jhc` — the **raw HTJ2K codestream** (frame 0) for byte-range
   progressive, content-type carrying the transfer syntax.
3. `…/thumbs/{s}.jpg` — one 256px JPEG per series (lossy preview).
Plus the original uploaded asset is retained (ZIPs) for export/re-extraction.
`StudyAsset.StorageBytes` = original (ZIP) + Σ transcoded slice bytes + frames —
this is the number the storage meter + the new per-study Size column report.

### B5. The compression ratio (measured, logged)
At the end of every extraction we log:
```
Bytes uploaded: {Uploaded:N0} (was {Original:N0}, ratio {Ratio:P1})
```
- Lossless HTJ2K on CT/MR typically lands ~**2–3×** smaller than uncompressed
  (40–55% of original). The exact ratio depends on modality/noise.
- **Caveat — the `.jhc` doubles per-slice storage right now.** We store BOTH the
  `.dcm` and the raw `.jhc` (the safe progressive rollout you chose). Net effect:
  current footprint ≈ (transcoded `.dcm`) + (≈same-size `.jhc`) ≈ original-ish.
  Once progressive is validated, dropping the `.dcm` realises the full ~2–3×
  saving AND keeps progressive. **That is the single biggest storage lever left.**

### B6. Centre-vs-platform savings
- **Lossless = same pixels, fewer bytes** → lower Azure storage cost (yours) and
  faster transfers (the centre's experience), with zero diagnostic compromise.
- It's done **once, server-side** at extraction — the centre uploads whatever
  their modality produces; normalisation to HTJ2K is transparent.
- The meter (`SUM(StorageBytes)`) bills the compressed footprint, so a centre on
  a storage-metered plan directly benefits from the compression.

### One-line answer to "how are we doing lossless compression?"
Server-side, during extraction, fo-dicom's native codec re-encodes every slice
to **HTJ2K Lossless RPCL** (bit-exact pixels), which both shrinks the bytes
~2–3× and makes each slice resolution-progressive for byte-range streaming —
with a fail-safe fallback to the original bytes so compression can never fail an
upload.
