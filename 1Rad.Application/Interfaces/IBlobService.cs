using System;
using System.IO;
using System.Threading.Tasks;

namespace _1Rad.Application.Interfaces
{
    public interface IBlobService
    {
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
