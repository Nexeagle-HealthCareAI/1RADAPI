# Securing PHI blob reads (`dicom-files`) — Front Door approach

This covers the finding 1/2 security fix (anonymous account-wide blob read +
publicly-readable PHI). **Chosen approach (decided with the team): keep Azure
Front Door in front of the blob for DICOM speed, lock the storage account so the
blob is only reachable *via* Front Door, and enforce access at Front Door — do
NOT route slice reads through the API.** This preserves the existing Front Door
edge-cache that DICOM relies on.

## What shipped in code

- **`Study/proxy-asset` hardened (closes finding 1).** It is no longer an
  anonymous account-wide reader. A request is authorized only if it carries a
  valid signature, a Bearer entitled to the blob's hospital, or targets an
  intentionally-public branding container (`prescriptions`, used by the public
  patient-tracking letterhead). The proxy is now used only for letterhead and
  the apiClient DICOM-download fallback — **not** the viewer's hot path.
- **Read seams emit Front Door URLs.** The manifest (`{appt}/manifest`,
  `by-study/{id}/manifest`), viewer-config, and study endpoints hand the browser
  `ToCdn(blobUrl)` URLs (rewritten to `AzureBlobStorage:CdnBaseUrl`), so the
  viewer's `fetch(sliceUrl)` hits the Front Door edge cache, not the API. This
  is the original speed path, restored.
- **`AssetUrlSigner` + the proxy's signature branch remain in place** but are
  *unused by the read seams* under this approach. They're kept so an
  app-minted-token path is available if Front Door token auth is adopted later
  (see "Adding Front Door token auth" below) — `AssetUrlSigner` signs by blob
  *path*, so the same signature validates a Front Door URL.

`NOT SSRF`: `DownloadFileAsync` resolves container/path against our own account
client and ignores the URL host.

## Infra steps (Azure — the actual finding-2 fix)

The blob must not be reachable directly from the internet; Front Door must be the
only ingress.

1. **Restrict the storage account to Front Door.** Storage account → Networking →
   set public network access to "Disabled" / "Selected networks", and allow only
   Front Door — either via **Private Link** (Front Door Premium) or the storage
   **resource-instance / `AzureFrontDoor.Backend` service-tag** rule. Direct blob
   URLs (the old anonymous-read exposure, and any leaked URL) then fail; only
   Front Door can read.
2. **Front Door origin auth.** If you also tighten the container ACL, Front Door
   reads the origin via **managed identity**. (If the ACL stays "Blob", the
   firewall in step 1 is what provides isolation.)
3. **Decide Front Door access control** — see the two postures below.

### Posture A — storage firewall only (smallest change, what's coded today)

Front Door serves any URL it's asked for; security rests on (a) the blob being
unreachable except via Front Door, and (b) slice paths being high-entropy GUIDs
(`{hospitalId:N}/{studyId|apptId:N}/extracted/...`). **Residual:** anyone who
obtains a Front Door URL (shared link, logs) can read that one blob until it's
deleted — capability-by-obscurity, no per-request auth. Acceptable as an interim
posture; it removes account-wide enumeration and the public-container exposure.

### Posture B — Front Door token auth (full access control)

Add a Front Door **rules-engine / WAF token (signed-URL) rule** so only
backend-minted URLs are served, validated at the edge on every request (cache key
stays the path, so edge-cache is fully preserved). This needs **Front Door
Premium** and a small app change: swap the read seams from `ToCdn(blobUrl)` to a
`SignedCdnUrl(blobUrl)` that appends the token Front Door expects (reuse
`AssetUrlSigner` — it already signs by path; align its HMAC with Front Door's
token format). This is the proper end-state; budget a short spike to match the
token formats.

## Verify

1. **Viewer speed** — open a study; slices load from the Front Door host (check
   Network tab shows the CDN host, not `…/api/v1/Study/proxy-asset`).
2. **Direct blob blocked** — `GET` a raw `…blob.core.windows.net/dicom-files/…`
   URL from outside Azure → blocked by the storage firewall (403/timeout).
3. **Proxy denies anonymous PHI** — `GET /api/v1/Study/proxy-asset?url=<a
   dicom-files blob url>` with no token and no `sig` → **403**.
4. **Letterhead still renders** — public patient-tracking page (`prescriptions`
   stays open).
5. (Posture B only) a Front Door URL **without** a valid token → rejected at the
   edge.

## Config

| Key | Default | Notes |
| --- | --- | --- |
| `AzureBlobStorage:CdnBaseUrl` | empty | Front Door base URL. Empty = `ToCdn` returns the raw blob URL (dev / no Front Door). |
| `AssetProxy:SigningKey` | falls back to `Jwt:Secret` | Only used by the proxy signature branch / future Front Door token auth. |
| `AssetProxy:SignedUrlTtlHours` | `8` | Ditto. |

## Notes / follow-ups

- `GetStudyAssets` returns raw blob URLs; consumers (StudyPrefetcher,
  PatientTimeline) fetch via `apiClient` (Bearer), covered by the proxy's Bearer
  branch, or via Front Door once `ToCdn` is applied there if desired.
- **Offline parity for PACS-only (study) reports — intentionally online-first.**
  Saving offline already works (the autosave hook queues `addToOutbox('REPORT', payload)`
  with `imagingStudyId`, replayed by `SyncEngine` to `POST /reporting/save`).
  Crash-recovery on reload works (`fetchStudyReportingContext` restores the
  `study_<id>` draft). Not supported: opening a study report while fully offline
  (no IndexedDB cache / `pullReports` for studies) — add later by extending
  `reportsRepo` + `SyncEngine.pullReports` and a cache fallback in
  `fetchStudyReportingContext`.
