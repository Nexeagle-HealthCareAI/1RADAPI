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
