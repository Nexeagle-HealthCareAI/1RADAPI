# Locking down the PHI blob container (`dicom-files`)

This is the **final** step of the finding 1/2 security fix (anonymous account-wide
blob read + public containers). Do it **only after** the signed-capability read
path has shipped and been verified, per the agreed "read-access first, then
private" sequencing.

## What already shipped (code)

- `IAssetUrlSigner` / `AssetUrlSigner` — HMAC-SHA256 capability signatures bound
  to a blob's path + expiry.
- `Study/proxy-asset` no longer serves the PHI container anonymously. A request
  is authorized only if it carries **a valid signature**, **a Bearer entitled to
  the blob's hospital**, or targets an **intentionally-public branding container**
  (`prescriptions`, used by the public patient-tracking letterhead).
- The manifest (`{appt}/manifest`, `by-study/{id}/manifest`) and viewer-config
  (`{appt}/viewer`) endpoints now hand the browser **signed** proxy URLs for
  every `dicom-files` read (slices, thumbnails, blobUrl). The DICOM viewer's
  bare `fetch(sliceUrl)` works with no Bearer because the signature is the
  capability.

No frontend change was required: the viewer fetches whatever URL the manifest
returns, and the legacy-ZIP CORS fallback uses `apiClient` (Bearer), which the
proxy's Bearer branch authorizes.

## Optional config

| Key | Default | Notes |
| --- | --- | --- |
| `AssetProxy:SigningKey` | falls back to `Jwt:Secret` | Set a dedicated key to rotate signing independently of JWT. |
| `AssetProxy:SignedUrlTtlHours` | `8` | How long a signed read URL stays valid. Must outlast a reporting/viewing session; the manifest re-mints on each load. |

No new config is **required** — it works out of the box off `Jwt:Secret`.

## Verify BEFORE flipping the container private

With `dicom-files` still public-read, confirm every read surface works through
the new signed path (i.e. it's the signature, not public-read, doing the work):

1. Open a study in the DICOM viewer → slices + thumbnails load.
2. Open a legacy (un-extracted ZIP) study → loads via the Bearer fallback.
3. Patient tracking page (unauthenticated) → letterhead still renders
   (`prescriptions` container stays open — unchanged).
4. Report preview / Word export → letterhead renders.
5. Sanity-check the proxy denies an anonymous PHI read **without** a signature:
   `GET /api/v1/Study/proxy-asset?url=<a dicom-files blob url>` with no token and
   no `sig` should return **403**.

## Flip the container private (Azure)

Once the above passes, set the `dicom-files` container access level to
**Private (no anonymous access)**. Either:

```bash
az storage container set-permission \
  --name dicom-files \
  --account-name <storageAccount> \
  --public-access off
```

or in the Portal: Storage account → Containers → `dicom-files` → Change access
level → Private.

Leave `prescriptions` (and any other branding container) as-is — it is
intentionally public.

## Re-verify after the flip

Repeat steps 1–4 above. They must still pass (now genuinely depending on the
signed/Bearer path). Step 5's 403 is now also enforced at the storage layer.

## Notes / follow-ups

- **CDN edge-cache tradeoff:** signed reads go through the API (`proxy-asset`),
  not Front Door, so per-slice CDN edge caching is no longer in the read path.
  The blobs still carry `Cache-Control: immutable`, so the browser caches them
  locally. If edge caching becomes a perf need, configure Front Door token auth
  against a private origin and sign CDN URLs instead — the URL shape can stay the
  same.
- `GetStudyAssets` (`{appt}/assets`) still returns raw blob URLs; its only
  consumers (StudyPrefetcher, PatientTimeline) fetch through `apiClient` (Bearer)
  and so are covered by the proxy's Bearer branch. Sign them too if a future
  consumer fetches those URLs without a Bearer.
