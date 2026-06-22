using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace _1Rad.Application.Interfaces
{
    /// <summary>One blob's identity + age/size, for the orphan-sweep job.</summary>
    public record BlobItem(string Name, DateTimeOffset? LastModified, long Length);

    public interface IBlobService
    {
        /// <summary>
        /// Streams blob listings under an optional prefix. Used by the orphan
        /// sweep to reconcile storage against live DB references.
        /// </summary>
        IAsyncEnumerable<BlobItem> ListBlobsAsync(string containerName, string? prefix = null, CancellationToken ct = default);

        /// <summary>
        /// Deletes a blob by its container-relative name/path (not a full URL).
        /// </summary>
        Task DeleteBlobByNameAsync(string blobName, string containerName, CancellationToken ct = default);

        /// <summary>
        /// Uploads a file. The blob name is auto-generated as "{Guid}_{sanitised-fileName}".
        /// </summary>
        Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, string? containerName = null);

        /// <summary>
        /// Uploads a file using a caller-supplied blob path (supports virtual folders, e.g. "hospitalId/staffId/file.pdf").
        /// Returns the full HTTPS URL of the uploaded blob.
        /// </summary>
        Task<string> UploadFileAtPathAsync(Stream fileStream, string blobPath, string contentType, string containerName, string? cacheControl = null);

        Task DeleteFileAsync(string fileUrl, string? containerName = null);
        Task<Stream> DownloadFileAsync(string fileUrl);

        /// <summary>
        /// Generates a short-lived SAS write URL for the given blob path. Lets the browser upload
        /// directly to Azure, bypassing the backend. The returned object has both the SAS URL
        /// (use for the PUT) and the public read URL (store on the StudyAsset row).
        /// </summary>
        Task<SasUploadTarget> GenerateSasUploadUrlAsync(string blobPath, string containerName, TimeSpan validFor, string? contentType = null);

        /// <summary>
        /// Short-lived presigned GET URL for DIRECT download of the blob at the given
        /// stored URL — lets the browser/bridge read straight from object storage,
        /// bypassing the API. Important for remote stores (e.g. E2E) so DICOM bytes
        /// don't round-trip through the VM. Implementations that can't presign return
        /// the input URL unchanged.
        /// </summary>
        string GeneratePresignedReadUrl(string fileUrl, TimeSpan validFor);

        /// <summary>
        /// Returns true if a blob exists at the given path. Used by `upload-complete` to verify
        /// the browser actually uploaded what it claimed.
        /// </summary>
        Task<bool> BlobExistsAsync(string blobPath, string containerName);

        /// <summary>
        /// Canonical read URL for a blob, derived from container + path against OUR
        /// storage account. Use this instead of trusting a client-echoed "public
        /// read URL", so a caller can't have us store a URL pointing at someone
        /// else's blob.
        /// </summary>
        string GetBlobReadUrl(string blobPath, string containerName);

        /// <summary>
        /// Size in bytes of the blob at the given container-relative path, or 0 if it
        /// doesn't exist. Used by storage metering (Phase 3 of the RIS/PACS split).
        /// </summary>
        Task<long> GetBlobSizeAsync(string blobPath, string containerName);

        /// <summary>
        /// Size in bytes of the blob at the given full HTTPS URL, or 0 if it doesn't
        /// exist / the URL can't be parsed. Used by storage metering.
        /// </summary>
        Task<long> GetBlobSizeByUrlAsync(string fileUrl);

        // ── Multipart (parallel) upload ──────────────────────────────────────
        // S3/MinIO multipart upload: split one large object into parts the
        // browser PUTs in parallel, then commit. A single PUT is throughput-
        // capped by one TCP stream's bandwidth-delay product on a high-RTT
        // link; parallel parts saturate the pipe. (Azure has an equivalent
        // client-side block protocol against a single SAS URL, so the Azure
        // implementation of these is a no-op / NotSupported — the client
        // routes Azure large uploads down its own block path.)

        /// <summary>
        /// Begins a multipart upload at <paramref name="blobPath"/> and mints a
        /// presigned PUT URL for each of <paramref name="partCount"/> parts. The
        /// client uploads parts concurrently, collects each part's ETag, then
        /// calls <see cref="CompleteMultipartUploadAsync"/>.
        /// </summary>
        Task<MultipartUploadInit> InitiateMultipartUploadAsync(string blobPath, string containerName, int partCount, TimeSpan validFor, string? contentType = null);

        /// <summary>
        /// Commits a multipart upload from the per-part ETags the client gathered.
        /// After this the blob is readable as a single object.
        /// </summary>
        Task CompleteMultipartUploadAsync(string blobPath, string containerName, string uploadId, IEnumerable<MultipartCompletedPart> parts);

        /// <summary>
        /// Cancels an in-flight multipart upload and discards any staged parts
        /// (storage doesn't bill for them once aborted). Best-effort cleanup for
        /// a client that failed mid-upload.
        /// </summary>
        Task AbortMultipartUploadAsync(string blobPath, string containerName, string uploadId);
    }

    /// <summary>Result of initiating a multipart upload — the upload id plus a
    /// presigned PUT URL per part for the browser to fan out across.</summary>
    public class MultipartUploadInit
    {
        public string UploadId { get; set; } = string.Empty;
        public string BlobPath { get; set; } = string.Empty;       // container-relative
        public string ContainerName { get; set; } = string.Empty;
        public string PublicReadUrl { get; set; } = string.Empty;  // canonical read URL
        public List<MultipartUploadPart> Parts { get; set; } = new();
        public DateTimeOffset ExpiresAt { get; set; }
    }

    public class MultipartUploadPart
    {
        public int PartNumber { get; set; }   // 1-based, as S3 requires
        public string Url { get; set; } = string.Empty;
    }

    /// <summary>A part the client finished uploading: its number + the ETag the
    /// storage returned on the part PUT (needed to commit the upload).</summary>
    public class MultipartCompletedPart
    {
        public int PartNumber { get; set; }
        public string ETag { get; set; } = string.Empty;
    }

    public class SasUploadTarget
    {
        public string SasUrl { get; set; } = string.Empty;     // include in PUT request
        public string PublicReadUrl { get; set; } = string.Empty; // store on StudyAsset
        public string BlobPath { get; set; } = string.Empty;
        public string ContainerName { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
