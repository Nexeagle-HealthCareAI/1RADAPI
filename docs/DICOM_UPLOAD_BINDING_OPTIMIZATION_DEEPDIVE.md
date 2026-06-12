# DICOM Upload + Binding — Deep Dive: Compression, Low-Bandwidth, Optimization

Focused on three asks: (1) lossless compression / smaller files, (2) fast binding
to the viewer + low-bandwidth tolerance, (3) the optimal, error-free, smooth
pipeline. Grounded in the current code after this session's work (leased queue,
streaming extraction, HTJ2K transcode, MPR metadata provider).

---

## 0. The key mental model — there are THREE "sizes", optimise each separately

```
   [Modality/PC]  --(A) UPLOAD bytes-->  [Blob]
                                           |
                                   (B) STORED bytes
                                           |
        [Viewer]  <--(C) DELIVERED bytes-- [Front Door CDN]
```

| | What it is | Today | Lever |
|---|---|---|---|
| **A. Upload** | bytes crossing the wire on upload | original ZIP (often *uncompressed* DICOM inside) | parallel chunked PUT ✅; client-side compression ⛔ (future) |
| **B. Stored** | durable blob footprint | HTJ2K slices only (ZIP dropped) ✅ | already optimal (lossless) |
| **C. Delivered** | bytes the viewer pulls per view | whole HTJ2K `.dcm` per slice | HTJ2K ✅; **byte-range progressive ⛔ (the big gap)** |

Most "make it smaller / faster" wins are at **C** (delivery) and the upload-speed
win is at **A**. **B** is essentially solved.

---

## 1. Lossless compression & reducing size

### What's already right
- **HTJ2K Lossless RPCL** server-side transcode — the correct choice for medical
  imaging (bit-exact, ~2–3× vs uncompressed CT, and *progressive* unlike JPEG-LS).
- **ZIP-drop on success** — stored footprint collapses to the slices.
- Streaming extraction — no OOM, so big studies actually finish.

### The honest gaps
1. **Upload bytes are NOT reduced.** The browser uploads the original archive. If
   the modality exported *uncompressed* DICOM, a 250 MB study uploads 250 MB even
   though it will be ~90 MB once transcoded. On a slow clinic uplink this is the
   single slowest step. Options:
   - **Client-side transcode before upload** (browser compresses each slice to
     HTJ2K, then uploads ~3× less). Biggest upload-speed win, but CPU-heavy in the
     browser and a non-trivial build (encode in a worker, re-pack). *Future.*
   - **Parallel block upload** (already done for >8 MB) — maximises throughput of
     whatever bytes there are. This is the pragmatic lever in place now.
2. **Delivered bytes per slice are whole-file.** HTJ2K shrinks them ~3×, but the
   viewer still fetches the *entire* slice before showing anything. On low
   bandwidth that's the latency you feel (see §2).

### Is HTJ2K the best we can do losslessly?
Yes. vs alternatives for CT/MR lossless: JPEG-LS ≈ similar ratio but *not*
progressive; JPEG-2000 (J2K) ≈ similar but slower decode; HTJ2K is the modern,
fast, progressive choice. Lossy is off the table for primary diagnosis. So the
*ratio* is near-optimal — the remaining wins are about **how** bytes are delivered,
not squeezing the ratio further.

---

## 2. Binding speed + low bandwidth

### The physics (why you must NOT load the whole study)
A 500 MB study, fully transferred:

| Link | 500 MB whole study | One HTJ2K slice (~180 KB) | Low-res prefix (~25 KB) |
|---|---|---|---|
| 100 Mbps | ~40 s | ~14 ms | ~2 ms |
| 25 Mbps | ~2.7 min | ~58 ms | ~8 ms |
| 5 Mbps (poor clinic) | ~13 min | ~290 ms | ~40 ms |

So "load the study in 1 s" is impossible and *unnecessary*. The goal is **first
diagnostic image in ~1 s, rest streaming** — and on 5 Mbps even one whole slice is
~0.3 s, but a low-res prefix is ~0.04 s. **That gap is the low-bandwidth feature.**

### What's built (good)
- **Middle-slice-first** load → radiologist sees a usable image immediately.
- **IndexedDB whole-slice cache** → re-open = zero network.
- **Front Door CDN + immutable cache** → edge hits, HTTP/2.
- **Bounded outward prefetch** (24 desktop / 10 mobile) → neighbours stream behind.
- **Decimated/interleaved volume** for MPR (`ProgressiveRetrieveImages`) → coarse
  volume in ~1 s then sharpens.
- **Connection-aware** background prefetch (skips on Save-Data/2G).
- **Manifest metadata provider** (just added) → MPR/3D bind without waiting for
  pixel decode.

### THE missing lever — byte-range progressive within a slice (wadors)
HTJ2K RPCL is ordered low-res-first, so a **Range request for the first ~15 % of a
slice decodes a low-res preview**, then the rest sharpens it. The backend already
produces the raw HTJ2K codestream (`.jhc`, currently flag-disabled) and ships the
full pixel module in the manifest (so a partial decode knows its dimensions). The
**frontend path that consumes it is NOT built** — the viewer fetches whole files
via the custom cached `wadouri` loader.

**Impact:** on 5 Mbps this turns ~290 ms/slice into ~40 ms to first paint, ~7×
faster perceived load, and scrolling stays responsive because each slice shows
blurry-then-sharp instead of blank-then-whole.

**What building it needs (and the honest complexity):**
1. **Range support end-to-end** — blob supports it; **Front Door must pass
   `Range`/`Accept-Ranges` through** (cache config) + **CORS must expose
   `Content-Range`/`Accept-Ranges`**.
2. **Re-enable `.jhc`** (`Dicom:WriteProgressiveFrames=true`) — the raw codestream
   the range loader decodes.
3. **A progressive retrieve strategy** in the viewer (Cornerstone's
   `imageRetrieveMetadataProvider` + retrieve stages: a `lossy/fast` low-res stage
   then a `final` full stage) that issues the Range requests and renders twice.
4. **Reconcile with the IndexedDB cache** — progressive for first view, then
   persist the full slice so re-scroll is a cache hit (don't double-fetch).

This is the highest-value low-bandwidth build, and it's a real piece of work
(worker decode of partial codestreams + cache reconciliation + FD/CORS config),
so it deserves its own verified pass.

---

## 3. Optimal, error-free, smooth — where we are + what remains

### Already hardened this session
- **Durable leased queue** (multi-instance, no double-processing, crash-reclaim).
- **Auto-retry ×N with backoff** (transient failures self-heal).
- **Streaming extraction** (no OOM — the actual cause of "every file fails").
- **DICM content-sniff + fast-fail** (no 75 s hangs on a CDN/HTML misconfig).
- **Live DB progress** (real progress bar, any instance).
- **MPR/3D metadata provider** (MPR was bailing "metadata unavailable").

### Remaining error/UX sources → fixes
| Risk | Fix |
|---|---|
| CORS `ACAO: localhost` blocks slices on deployed origin | storage CORS = app origins (or `*`) + **purge Front Door** |
| FD serves stale *uncompressed* cached bytes | purge after the transcode deploy |
| Whole-file fetch feels slow on poor links | byte-range progressive (§2) |
| Decode worker hiccups on some browsers | the no-worker fallback exists; add a one-shot worker health check |
| Very large studies still pressure RAM at high concurrency | tune `Dicom:ExtractionConcurrency`; the streaming fix already cut peak ~3× |

---

## The prioritised solution (do in this order)

1. **Finish delivery config** (no code): storage CORS for the deployed origin,
   **purge Front Door**, confirm `Range`/`Accept-Ranges` pass-through. *Unblocks
   correctness + lets the HTJ2K size win actually reach the viewer.*
2. **Build byte-range progressive (wadors)** + re-enable `.jhc`. *The headline
   low-bandwidth + fast-binding feature — first paint ~7× faster on poor links.*
3. **Drop the `.dcm`, keep only `.jhc`** once progressive is validated. *Final
   storage saving while keeping progressive.*
4. **(Optional) client-side compression before upload** — cuts upload bytes ~3×
   for slow clinic uplinks. Heavier lift; do after 1–3.
5. **Polish** — connection-aware quality tiers, prefetch window tuning, worker
   health check.

**Bottom line:** the *compression ratio* and *storage* are already near-optimal;
the *reliability* is now solid; the one transformational lever left for
"fast + low-bandwidth + smooth" is **byte-range progressive delivery (#2)** —
everything needed on the backend (HTJ2K RPCL, `.jhc`, manifest pixel module) is
already in place, so it's a frontend + delivery-config build, not a redesign.
