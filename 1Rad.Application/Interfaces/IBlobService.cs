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
        Task<string> UploadFileAtPathAsync(Stream fileStream, string blobPath, string contentType, string containerName);

        Task DeleteFileAsync(string fileUrl, string? containerName = null);
        Task<Stream> DownloadFileAsync(string fileUrl);
    }
}
