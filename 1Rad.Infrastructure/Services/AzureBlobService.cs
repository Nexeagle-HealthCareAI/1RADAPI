using _1Rad.Application.Interfaces;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Threading.Tasks;

namespace _1Rad.Infrastructure.Services
{
    public class AzureBlobService : IBlobService
    {
        private readonly BlobServiceClient _blobServiceClient;
        private readonly string _containerName;

        public AzureBlobService(IConfiguration configuration)
        {
            var connectionString = configuration.GetConnectionString("AzureBlobStorage");
            _blobServiceClient = new BlobServiceClient(connectionString);
            _containerName = configuration["AzureBlobStorage:ContainerName"] ?? "prescriptions";
        }

        public async Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
            await containerClient.CreateIfNotExistsAsync(PublicAccessType.Blob);

            var blobClient = containerClient.GetBlobClient($"{Guid.NewGuid()}_{fileName}");
            
            var blobHttpHeader = new BlobHttpHeaders { ContentType = contentType };
            
            await blobClient.UploadAsync(fileStream, new BlobUploadOptions { HttpHeaders = blobHttpHeader });

            return blobClient.Uri.ToString();
        }

        public async Task DeleteFileAsync(string fileUrl)
        {
            if (string.IsNullOrEmpty(fileUrl)) return;

            var uri = new Uri(fileUrl);
            var blobName = Path.GetFileName(uri.LocalPath);
            var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
            var blobClient = containerClient.GetBlobClient(blobName);

            await blobClient.DeleteIfExistsAsync();
        }
    }
}
