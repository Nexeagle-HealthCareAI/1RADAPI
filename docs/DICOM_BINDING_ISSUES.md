# DICOM Binding — Potential Issues (deep research)

Context: `AzureBlobStorage:CdnBaseUrl` is confirmed set in Azure App Settings
with an `https://1rad…` value, yet DICOM binding/viewing still shows symptoms.
"Binding" spans two distinct stages — **upload→DB binding** and **read→viewer
binding** — and each has its own failure modes.

---

## 🥇 #1 (most likely): the App-Setting key `:` doesn't bind on LINUX App Service

The code reads `_configuration["AzureBlobStorage:CdnBaseUrl"]`. Azure App
Service injects Application Settings as **environment variables**:

- **Windows** App Service: a `AzureBlobStorage:CdnBaseUrl` colon key binds fine.
- **Linux** App Service / containers: env-var names can't use `:`. The nested
  key MUST be written with a **double underscore**: `AzureBlobStorage__CdnBaseUrl`.
  A literal `:` setting **silently does not bind** → the lookup returns null →
  `ToCdn()` returns the raw `*.blob.core.windows.net` URL → every slice bypasses
  Front Door (HTML/JSON via SPA fallback, 403 on a firewalled account, or just
  no CDN). This perfectly matches the "set but ignored" symptom.

**This is the prime suspect because the value is present yet behaves as absent.**

Fixes shipped:
- `ToCdn` now reads via `ResolveCdnBaseUrl()` which tries `:`, `__`, flat
  `CdnBaseUrl`, and direct env vars — robust to platform/keying.
- Startup now **logs the resolved value** (or a loud "did NOT resolve" with the
  `__` hint).

**Action / definitive check:**
1. App Service → **Log stream**. On boot you'll now see either
   `[CDN] CdnBaseUrl resolved to https://…` (good) or `[CDN] … did NOT resolve`.
2. If it didn't resolve: rename the App Setting key from
   `AzureBlobStorage:CdnBaseUrl` → **`AzureBlobStorage__CdnBaseUrl`** and restart.
3. Confirm the App Service OS (Overview → "Operating System"). If **Linux**, use
   `__` always.

---

## #2: Front Door origin-path "double container" → 404

`ToCdn` emits `{cdnBase}{absolutePath}` = `https://<fd>/dicom-files/<path>`.
That's correct **only if** the Front Door origin points at the storage account
root. If the FD origin/route was configured with an **origin path of
`/dicom-files`** (i.e. pointing straight at the container), the emitted URL
becomes `…/dicom-files/dicom-files/<path>` → 404 on every slice.

**Check:** open one slice URL from the viewer's Network tab directly in a new
tab. A 404 with a doubled `/dicom-files/dicom-files/` path confirms it → set the
FD origin path to empty (root) so the container stays in the request path.

---

## #3: Blob CORS for the browser `fetch()` of slices

The viewer pulls each slice with `fetch()` from the Front Door host — a
**cross-origin** request from the SPA. The response needs
`Access-Control-Allow-Origin` for the SPA origin (and `Access-Control-Allow-
Methods: GET, HEAD`, plus `Range`/`Content-Range` exposed for the progressive
path). There is **no CORS configured in code** — it must be set on the storage
account (Blob service → Resource sharing/CORS) and passed through by Front Door.
The public **share page** is especially exposed (different origin, no session).

**Check:** a slice request failing with a CORS error / status 0 in the console
(vs. a clean 200) points here. Add a CORS rule: allowed origins = your SPA
origin(s), methods GET/HEAD/OPTIONS, allowed+exposed headers `*` (or at least
`Range`, `Content-Range`, `Accept-Ranges`).

---

## #4: Storage firewall (Posture A) vs. Front Door

If the storage account is locked to Front Door (Posture A) but the FD instance
isn't on the allow-list (or Private Link isn't wired), slices 403 even with a
correct CdnBaseUrl. Direct-blob fallback reads also break.

**Check:** the failing slice returns **403 AuthorizationFailure** from storage.

---

## UPLOAD-SIDE binding (a different 403 class)

## #5: DICOM upload is 403'd when the centre lacks the PACS module

`RequestSasUploadToken` → `RequireDicomCapabilityAsync` returns **403
MODULE_NOT_ENABLED** for any `.dcm`/`.zip`/`.dicom` upload if the active centre's
subscription `Modules` CSV doesn't include `PACS`. So a centre on a RIS-only or
a **trial whose modules weren't set** can't upload/bind DICOM at all. (This is
the same family as the earlier ApprovePayment-didn't-set-Modules bug.)

**Check:** the upload (not the viewer) fails with `403 MODULE_NOT_ENABLED`.
Verify the centre's active `HospitalSubscriptions.Modules` contains `PACS`.

## #6: Tenant binding in a multi-centre group

Upload endpoints load the appointment with `IgnoreQueryFilters()` and gate via
`EnsureHospitalAccess` (caller's centre OR a group centre in the token's hubs
claim). If the token's authorized-hospital set is stale, a legitimate
cross-centre upload 403s `HOSPITAL_FORBIDDEN`.

## #7: Single-DCM now extracts (recent change)

Single `.dcm` uploads now go through extraction (to normalise preamble-less
files into valid HTJ2K P10). Until extraction completes the study is
`Processing`, not instantly `Ready` — the viewer shows "processing" briefly.
Legacy single-DCMs (`NotApplicable`) lazily re-extract on first manifest hit.

---

## Fastest path to root-cause

1. **App Service Log stream on boot** → does `[CDN] CdnBaseUrl resolved…` print
   the URL, or "did NOT resolve"? (Settles #1 immediately.)
2. **Network tab on a failing slice** → open its URL directly:
   - HTML / SPA page → #1 (CDN not resolving) or wrong host.
   - 404 with doubled `/dicom-files/dicom-files/` → #2.
   - CORS error / status 0 → #3.
   - 403 AuthorizationFailure → #4.
3. **If the UPLOAD fails** (not the view): `403 MODULE_NOT_ENABLED` → #5;
   `HOSPITAL_FORBIDDEN` → #6.
