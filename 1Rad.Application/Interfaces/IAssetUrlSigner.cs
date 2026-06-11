using System;

namespace _1Rad.Application.Interfaces
{
    /// <summary>
    /// Mints and validates short-lived "capability" signatures for blob read
    /// URLs served through <c>Study/proxy-asset</c>. The signature lets the
    /// proxy authorize an unauthenticated request (e.g. the DICOM viewer's bare
    /// <c>fetch(sliceUrl)</c>, which carries no Bearer) WITHOUT making the PHI
    /// container publicly readable: only the backend — which already enforced
    /// module + tenant access when it built the manifest — can produce a valid
    /// signature, and that signature is bound to the exact blob and an expiry.
    /// </summary>
    public interface IAssetUrlSigner
    {
        /// <summary>
        /// Signs the blob URL for <paramref name="ttl"/>. Returns the expiry
        /// (unix seconds) and signature to append as <c>&amp;exp=&amp;sig=</c>.
        /// The signature binds the blob's path (container + blob), so it can't
        /// be replayed against a different asset.
        /// </summary>
        (long Exp, string Sig) Sign(string blobUrl, TimeSpan ttl);

        /// <summary>
        /// True if <paramref name="sig"/> is a valid, unexpired signature for
        /// <paramref name="blobUrl"/>. Host-agnostic: validates against the
        /// blob's path, so a CDN-fronted vs storage-host URL still matches.
        /// </summary>
        bool Validate(string blobUrl, long exp, string sig);
    }
}
