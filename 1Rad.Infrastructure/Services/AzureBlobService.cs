using _1Rad.Application.Interfaces;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
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
                throw new InvalidOperationException("AZURE_STORAGE_CONFIG_MISSING: The 'AzureBlobStorage' connection string is not configured.");
            }
            _blobServiceClient = new BlobServiceClient(connectionString);
            _containerName = configuration["AzureBlobStorage:ContainerName"] ?? "prescriptions";
        }

        public async Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, string? containerName = null)
        {
            var targetContainer = containerName ?? _containerName;
            var containerClient = _blobServiceClient.GetBlobContainerClient(targetContainer);
            await EnsureContainerAsync(containerClient);

            var sanitizedFileName = SanitiseFileName(fileName);
            var blobClient = containerClient.GetBlobClient($"{Guid.NewGuid()}_{sanitizedFileName}");

            var blobHttpHeader = new BlobHttpHeaders { ContentType = contentType };
            await blobClient.UploadAsync(fileStream, new BlobUploadOptions { HttpHeaders = blobHttpHeader });

            return blobClient.Uri.ToString();
        }

        public async Task<string> UploadFileAtPathAsync(Stream fileStream, string blobPath, string contentType, string containerName, string? cacheControl = null)
        {
            if (string.IsNullOrWhiteSpace(blobPath))
                throw new ArgumentException("blobPath is required", nameof(blobPath));
            if (string.IsNullOrWhiteSpace(containerName))
                throw new ArgumentException("containerName is required", nameof(containerName));

            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
            await EnsureContainerAsync(containerClient);

            // Sanitise each path segment but preserve the / separators (Azure treats / as virtual folders).
            var sanitisedPath = string.Join('/',
                blobPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .Select(SanitiseFileName));

            var blobClient = containerClient.GetBlobClient(sanitisedPath);

            var blobHttpHeader = new BlobHttpHeaders { ContentType = contentType };
            // Optional Cache-Control — set for immutable assets (e.g. extracted
            // DICOM slices) so browsers/CDNs serve repeat views from cache
            // instead of re-downloading every slice.
            if (!string.IsNullOrWhiteSpace(cacheControl))
                blobHttpHeader.CacheControl = cacheControl;
            await blobClient.UploadAsync(fileStream, new BlobUploadOptions { HttpHeaders = blobHttpHeader, });

            return blobClient.Uri.ToString();
        }

        public async IAsyncEnumerable<_1Rad.Application.Interfaces.BlobItem> ListBlobsAsync(
            string containerName, string? prefix = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
            await foreach (var b in containerClient.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
            {
                yield return new _1Rad.Application.Interfaces.BlobItem(
                    b.Name,
                    b.Properties.LastModified,
                    b.Properties.ContentLength ?? 0);
            }
        }

        public async Task DeleteBlobByNameAsync(string blobName, string containerName, CancellationToken ct = default)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
            await containerClient.GetBlobClient(blobName).DeleteIfExistsAsync(cancellationToken: ct);
        }

        public async Task DeleteFileAsync(string fileUrl, string? containerName = null)
        {
            if (string.IsNullOrEmpty(fileUrl)) return;

            var uri = new Uri(fileUrl);
            var targetContainer = containerName ?? _containerName;
            var containerClient = _blobServiceClient.GetBlobContainerClient(targetContainer);

            // Extract the full blob path (including virtual folders), not just the file name.
            // URL shape: https://{account}.blob.core.windows.net/{container}/{folder/.../file}
            // The segment after "/{container}/" is the blob's name, possibly with slashes.
            var pathSegments = uri.AbsolutePath.TrimStart('/').Split('/', 2);
            string blobName;
            if (pathSegments.Length == 2 && string.Equals(pathSegments[0], targetContainer, StringComparison.OrdinalIgnoreCase))
            {
                blobName = Uri.UnescapeDataString(pathSegments[1]);
            }
            else
            {
                // Fallback for URLs that don't include the container in their path (e.g. CDN-fronted).
                blobName = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            }

            var blobClient = containerClient.GetBlobClient(blobName);
            await blobClient.DeleteIfExistsAsync();
        }

        /// <summary>
        /// Creates the container if missing. Many Azure storage accounts now ship
        /// with "Allow Blob public access" DISABLED at the account level — in that
        /// configuration requesting <see cref="PublicAccessType.Blob"/> throws
        /// (PublicAccessNotPermitted), which surfaced as a hard 500 on every
        /// upload to a not-yet-created container. We try public-read first (so
        /// existing public-read viewers keep working where allowed) and fall back
        /// to a private container otherwise. Assets are also reachable via SAS, so
        /// a private container does not break the DICOM viewer flow.
        /// </summary>
        private static async Task EnsureContainerAsync(BlobContainerClient containerClient)
        {
            try
            {
                await containerClient.CreateIfNotExistsAsync(PublicAccessType.Blob);
            }
            catch (Azure.RequestFailedException ex) when (
                ex.ErrorCode == "PublicAccessNotPermitted" ||
                ex.Status == 409 /* container exists with different access */ ||
                (ex.Message?.Contains("public access", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                // Account forbids public containers — create/keep it private.
                await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
            }
        }

        private static string SanitiseFileName(string fileName)
        {
            // Strip Windows path components, replace whitespace, and remove characters that Azure dislikes.
            var name = Path.GetFileName(fileName);
            name = name.Replace(' ', '_');
            // Azure blob names disallow none of these but keep things URL-safe.
            foreach (var invalid in new[] { '\\', ':', '*', '?', '"', '<', '>', '|' })
                name = name.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(name) ? Guid.NewGuid().ToString("N") : name;
        }

        public async Task<SasUploadTarget> GenerateSasUploadUrlAsync(string blobPath, string containerName, TimeSpan validFor, string? contentType = null)
        {
            if (string.IsNullOrWhiteSpace(blobPath))
                throw new ArgumentException("blobPath is required", nameof(blobPath));
            if (string.IsNullOrWhiteSpace(containerName))
                throw new ArgumentException("containerName is required", nameof(containerName));

            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
            await EnsureContainerAsync(containerClient);

            // Sanitise each path segment but preserve / separators (Azure treats / as virtual folders).
            var sanitisedPath = string.Join('/',
                blobPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .Select(SanitiseFileName));

            var blobClient = containerClient.GetBlobClient(sanitisedPath);

            if (!blobClient.CanGenerateSasUri)
            {
                throw new InvalidOperationException(
                    "AZURE_SAS_UNAVAILABLE: The storage client cannot generate SAS URIs. " +
                    "This requires authentication via account key (storage connection string with AccountKey=...). " +
                    "Managed-identity-only deployments must use user-delegation SAS instead.");
            }

            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = containerName,
                BlobName = sanitisedPath,
                Resource = "b",  // blob-level (not container)
                StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5), // clock-skew tolerance
                ExpiresOn = DateTimeOffset.UtcNow.Add(validFor),
                Protocol = SasProtocol.Https,
            };
            // Write + Create lets the browser PUT a fresh blob OR overwrite (for retries).
            sasBuilder.SetPermissions(BlobSasPermissions.Write | BlobSasPermissions.Create);

            if (!string.IsNullOrEmpty(contentType))
            {
                sasBuilder.ContentType = contentType;
            }

            var sasUri = blobClient.GenerateSasUri(sasBuilder);

            return new SasUploadTarget
            {
                SasUrl = sasUri.ToString(),
                PublicReadUrl = blobClient.Uri.ToString(),
                BlobPath = sanitisedPath,
                ContainerName = containerName,
                ExpiresAt = sasBuilder.ExpiresOn,
            };
        }

        // ── Multipart (parallel) upload ───────────────────────────────────────────
        // Not implemented for Azure: the browser already parallelises large Azure
        // uploads with the native staged-block protocol (PUT ...&comp=block then
        // &comp=blocklist) against the single write SAS this service mints — it
        // needs no server-minted per-part URLs. The client detects the Azure
        // backend and routes large uploads down that block path, so it never calls
        // these. They throw (rather than silently no-op) to surface a mis-route.
        public Task<MultipartUploadInit> InitiateMultipartUploadAsync(string blobPath, string containerName, int partCount, TimeSpan validFor, string? contentType = null)
            => throw new NotSupportedException("Multipart upload is S3/MinIO-only; Azure uploads use the client-side staged-block protocol against the write SAS.");

        public Task CompleteMultipartUploadAsync(string blobPath, string containerName, string uploadId, IEnumerable<MultipartCompletedPart> parts)
            => throw new NotSupportedException("Multipart upload is S3/MinIO-only; Azure uploads use the client-side staged-block protocol against the write SAS.");

        public Task AbortMultipartUploadAsync(string blobPath, string containerName, string uploadId)
            => Task.CompletedTask; // nothing staged server-side on Azure

        // Short-lived read SAS so the browser can download the blob directly.
        // Returns the URL unchanged if SAS can't be generated (e.g. MSI-only auth).
        public string GeneratePresignedReadUrl(string fileUrl, TimeSpan validFor)
        {
            if (string.IsNullOrEmpty(fileUrl)) return fileUrl;
            try
            {
                var uri = new Uri(fileUrl);
                var segs = uri.AbsolutePath.TrimStart('/').Split('/', 2);
                if (segs.Length < 2) return fileUrl;
                var container = segs[0];
                var blobName = Uri.UnescapeDataString(segs[1]);

                var blobClient = _blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blobName);
                if (!blobClient.CanGenerateSasUri) return fileUrl;

                var sas = new BlobSasBuilder
                {
                    BlobContainerName = container,
                    BlobName = blobName,
                    Resource = "b",
                    StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5),
                    ExpiresOn = DateTimeOffset.UtcNow.Add(validFor),
                    Protocol = SasProtocol.Https,
                };
                sas.SetPermissions(BlobSasPermissions.Read);
                return blobClient.GenerateSasUri(sas).ToString();
            }
            catch
            {
                return fileUrl;
            }
        }

        public async Task<bool> BlobExistsAsync(string blobPath, string containerName)
        {
            if (string.IsNullOrWhiteSpace(blobPath) || string.IsNullOrWhiteSpace(containerName))
                return false;

            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
            var blobClient = containerClient.GetBlobClient(blobPath);
            return await blobClient.ExistsAsync();
        }

        public string GetBlobReadUrl(string blobPath, string containerName)
        {
            if (string.IsNullOrWhiteSpace(blobPath) || string.IsNullOrWhiteSpace(containerName))
                return string.Empty;
            // Resolve against our own account client so the returned URL always
            // points at the verified blob, regardless of any host the caller sent.
            return _blobServiceClient.GetBlobContainerClient(containerName).GetBlobClient(blobPath).Uri.ToString();
        }

        public async Task<long> GetBlobSizeAsync(string blobPath, string containerName)
        {
            if (string.IsNullOrWhiteSpace(blobPath) || string.IsNullOrWhiteSpace(containerName))
                return 0;
            try
            {
                var blobClient = _blobServiceClient.GetBlobContainerClient(containerName).GetBlobClient(blobPath);
                var props = await blobClient.GetPropertiesAsync();
                return props.Value.ContentLength;
            }
            catch
            {
                return 0; // missing blob / transient failure — metering treats it as unknown
            }
        }

        public async Task<long> GetBlobSizeByUrlAsync(string fileUrl)
        {
            if (string.IsNullOrEmpty(fileUrl)) return 0;
            try
            {
                // Same URL shape DownloadFileAsync parses: /{container}/{blob}
                var uri = new Uri(fileUrl);
                var segments = uri.Segments;
                if (segments.Length < 3) return 0;
                var containerName = segments[1].TrimEnd('/');
                var blobName = Uri.UnescapeDataString(string.Join("", segments.Skip(2)));
                return await GetBlobSizeAsync(blobName, containerName);
            }
            catch
            {
                return 0;
            }
        }

        public async Task<Stream> DownloadFileAsync(string fileUrl)
        {
            if (string.IsNullOrEmpty(fileUrl)) throw new ArgumentException("URL is required");

            var uri = new Uri(fileUrl);
            // Extract container name and blob name from the URI
            // URI format: https://[account].blob.core.windows.net/[container]/[blob]
            var segments = uri.Segments;
            if (segments.Length < 3) throw new ArgumentException("Invalid blob URL format");

            var containerName = segments[1].TrimEnd('/');
            var blobName = Uri.UnescapeDataString(string.Join("", segments.Skip(2))); 

            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
            var blobClient = containerClient.GetBlobClient(blobName);

            if (!await blobClient.ExistsAsync())
                throw new FileNotFoundException("The specified clinical asset was not found in storage.");

            var downloadInfo = await blobClient.DownloadStreamingAsync();
            return downloadInfo.Value.Content;
        }
    }
}
