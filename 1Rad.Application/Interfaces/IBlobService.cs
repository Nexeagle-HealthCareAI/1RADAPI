using System.IO;
using System.Threading.Tasks;

namespace _1Rad.Application.Interfaces
{
    public interface IBlobService
    {
        Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType);
        Task DeleteFileAsync(string fileUrl);
    }
}
