using System.IO;
using System.Threading.Tasks;

namespace _1Rad.Application.Interfaces
{
    public interface IBlobService
    {
        Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, string? containerName = null);
        Task DeleteFileAsync(string fileUrl, string? containerName = null);
        Task<Stream> DownloadFileAsync(string fileUrl);
    }
}
