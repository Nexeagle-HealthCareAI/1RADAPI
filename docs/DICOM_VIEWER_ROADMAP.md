# DICOM Viewer — Deep Capability Review & Roadmap to Best-in-Class

Review date: 2026-06-11. Baseline: easyrad `AdvancedDicomViewer.jsx` (~4,000
lines), `DicomViewerPage.jsx`, `MprViewport.jsx`, compared against OHIF,
RadiAnt, Visage and Sectra feature sets.

## What we already have (genuinely strong)

- HTJ2K-lossless pipeline + IndexedDB re-open cache + middle-slice-first load
- 25+ tools incl. Livewire smart contour, Spline ROI, W/L Region, Drag Probe,
  Eraser, Label, scale overlay (after this week's additions)
- MPR with **thick-slab MIP / MinIP / Average + slab thickness control**,
  crosshairs, 3D volume — this is ahead of most mid-market viewers
- 1×2 / 2×2 multi-viewport layouts with per-cell series
- 10 windowing presets, invert/flip/rotate, cine loop, key-image starring,
  screenshot capture, series rail with backend thumbnails, keyboard map

## Bug found during review (fix before anything else)

**Linked scroll/zoom between viewports is silently dead.** The build warns
`Import getSynchronizer will always be undefined` — `synchronizers.
getSynchronizer` does not exist in the installed @cornerstonejs/tools v4
(`SynchronizerManager.getSynchronizer` is the correct API). Every sync setup
call (AdvancedDicomViewer.jsx:2442–2509) no-ops. The 2×2 comparison layout
therefore has no linked scrolling — the single feature comparison layouts
exist for. ~1 hour fix.

---

## Tier 1 — clinical must-haves (the gap between "demo" and "daily driver")

### 1.1 Measurement persistence + measurement panel
Annotations live only in memory — close the study, lose every measurement.
- Serialize `annotation.state.getAllAnnotations()` (already used for
  clear-all) → `POST /Study/studies/{id}/measurements`; restore on open.
  New table keyed by ImagingStudyId/SOPInstanceUID so measurements follow the
  slice.
- Side panel listing measurements (tool, value, slice) → click = jump to
  slice; rename label; delete; **"insert into report"** pushes values into the
  NarrativeEditor. ReportingPage already has a `.measurements-panel` stub.
- This single feature is the difference between a viewer and a reporting tool.

### 1.2 Multiframe support (US clips, XA runs) — likely silent data loss today
Extraction indexes one slice per SOP instance; a 120-frame ultrasound clip is
ONE instance and renders as a single static frame. Cornerstone supports
`wadouri:...?frame=N` imageIds.
- Backend: extraction reads `NumberOfFrames`; slice index gains a frame count.
- Frontend: expand multiframe instances into frame-level imageIds; cine plays
  frames at `RecommendedDisplayFrameRate`/`FrameTime` from DICOM.

### 1.3 Real cine controls
Current cine = fixed 10 FPS slice loop. Add play/pause/step buttons, FPS
slider, reverse + bounce mode, and the DICOM-declared frame rate as default.

### 1.4 Standard viewport overlays
Radiologists expect 4-corner text + orientation labels:
- Corners: patient name/ID · study date/desc · series/instance + slice
  position · zoom %, WW/WL (live, not the static metadata panel)
- **L/R/A/P/H/F orientation markers** from ImageOrientationPatient (cornerstone
  `OrientationMarkerTool` or computed labels)
- Laterality + "PORTABLE"/view code where present.

### 1.5 Prior comparison
Load a prior study of the same patient beside the current one: a "Priors"
dropdown in the series rail (query `/Study/studies?q=<patientId>`), opens into
the 1×2 layout with (fixed) linked scroll where frame-of-reference matches.
This is the most-requested radiologist feature in every PACS RFP.

### 1.6 Reference lines / localizer cross-reference
With multi-viewport layouts working, enable `ReferenceLines` so the scout
shows where the current axial slice cuts. Cheap (tool exists) once 1.5 lands.

---

## Tier 2 — differentiators

| # | Feature | Notes |
|---|---|---|
| 2.1 | **Undo/redo for annotations** | snapshot annotation state per mutation; Ctrl+Z/Y |
| 2.2 | **Export: JPEG/PNG with burned-in overlays, MP4/GIF cine, true-size print** | canvas capture exists; add overlay burn-in + (optional) anonymize; MediaRecorder for cine |
| 2.3 | **PET/CT fusion + colormaps** | second volume overlaid with colormap + opacity slider; CS3D supports multi-volume blending natively |
| 2.4 | **VOI LUT function + modality auto-presets** | honour SIGMOID VOILUTFunction + embedded VOI LUTs; auto-pick preset by modality/body part |
| 2.5 | **DICOM overlays (60xx) + shutters + GSPS** | render modality-burned annotations and saved presentation states |
| 2.6 | **Hanging protocols** | per-modality default layout + series placement (CR: PA/LAT side-by-side; MR: T1/T2/FLAIR 2×2), JSON-configurable per clinic |
| 2.7 | **DICOM SR (TID 1500) measurement export** | interop gold standard; pairs with 1.1 |
| 2.8 | **PET SUV readout** | SUV-bw computed from RadiopharmaceuticalInfo in Probe/ROI stats |
| 2.9 | **Annotation style settings** | colour/line width/font; per-user persistence |

---

## Tier 3 — advanced / flagship

| # | Feature | Notes |
|---|---|---|
| 3.1 | **Segmentation suite + volumetrics** | brush/threshold/scissors tools exist in CS3D; needs labelmap UI; tumor volume in cm³; export DICOM SEG |
| 3.2 | **Volume-rendering presets** | bone/angio/soft-tissue transfer functions in the existing 3D pane |
| 3.3 | **Curved MPR** | vessel centerline reformat — dental/vascular differentiator |
| 3.4 | **RadAI overlay hooks** | render AI findings (boxes/contours from the API) as a toggleable layer; one-click "insert finding into report" |
| 3.5 | **Dual-monitor pop-out** | window.open + BroadcastChannel state sync |
| 3.6 | **Worklist flow** | next/prev unreported study buttons in the viewer header; mark-read |
| 3.7 | **Touch gestures** | pinch-zoom / two-finger pan bindings for tablet reading |
| 3.8 | **Progressive HTJ2K streaming** | slices are already RPCL-encoded — byte-range partial decode gives instant low-res first paint (`ProgressiveRetrieveImages` is already imported in MprViewport, unused in stack) |

---

## Recommended build order

1. **Fix linked-scroll sync bug** (hours) — restores 2×2 comparison.
2. **1.1 Measurement persistence + panel + report insertion** (2–3 days incl.
   API + table) — biggest clinical value per effort.
3. **1.4 Corner overlays + orientation markers** (1 day) — biggest perceived-
   professionalism jump.
4. **1.2 Multiframe + 1.3 cine controls** (2 days backend+frontend) — unblocks
   ultrasound/angio clinics; currently silent data loss.
5. **1.5 Priors + 1.6 reference lines** (2–3 days) — completes the comparison
   workflow.
6. Then Tier 2 in order 2.2 → 2.6 → 2.1 → 2.7 → 2.3.

Items 1–4 together make the viewer competitive with RadiAnt for everyday 2D
reading; Tier 2 closes on OHIF; Tier 3 items are flagship differentiators in
the cloud-PACS market.
