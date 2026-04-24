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
            if (string.IsNullOrEmpty(connectionString))
            {
                // Tactical: If connection string is missing, we must fail fast with a clear diagnostic
                throw new InvalidOperationException("AZURE_STORAGE_CONFIG_MISSING: The 'AzureBlobStorage' connection string is not configured in appsettings.json or Azure environment variables.");
            }
            _blobServiceClient = new BlobServiceClient(connectionString);
            _containerName = configuration["AzureBlobStorage:ContainerName"] ?? "prescriptions";
        }

        public async Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, string? containerName = null)
        {
            var targetContainer = containerName ?? _containerName;
            var containerClient = _blobServiceClient.GetBlobContainerClient(targetContainer);
            await containerClient.CreateIfNotExistsAsync(PublicAccessType.Blob);

            var sanitizedFileName = fileName.Replace(" ", "_");
            var blobClient = containerClient.GetBlobClient($"{Guid.NewGuid()}_{sanitizedFileName}");
            
            var blobHttpHeader = new BlobHttpHeaders { ContentType = contentType };
            
            await blobClient.UploadAsync(fileStream, new BlobUploadOptions { HttpHeaders = blobHttpHeader });

            return blobClient.Uri.ToString();
        }

        public async Task DeleteFileAsync(string fileUrl, string? containerName = null)
        {
            if (string.IsNullOrEmpty(fileUrl)) return;

            var uri = new Uri(fileUrl);
            var blobName = Path.GetFileName(uri.LocalPath);
            
            var targetContainer = containerName ?? _containerName;
            var containerClient = _blobServiceClient.GetBlobContainerClient(targetContainer);
            var blobClient = containerClient.GetBlobClient(blobName);

            await blobClient.DeleteIfExistsAsync();
        }
    }
}
