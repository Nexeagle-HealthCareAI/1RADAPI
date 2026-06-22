using _1Rad.Application.Interfaces;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace _1Rad.Infrastructure.Services
{
    /// <summary>
    /// Self-hosted, S3-compatible (MinIO) implementation of <see cref="IBlobService"/>.
    /// Drop-in replacement for <see cref="AzureBlobService"/> so DICOM/assets live on
    /// our own VM instead of Azure Blob Storage.
    ///
    /// Two layouts, chosen by config:
    ///  • SEPARATE buckets (Storage:Minio:Bucket empty) — each logical container
    ///    (dicom-files, prescriptions, staff-documents) is its OWN bucket. Mirrors the
    ///    Azure container model 1:1, so the proxy-asset open-container logic and
    ///    URL shape ("{host}/{container}/{path}") match exactly. Buckets are
    ///    auto-created on first use if the key has permission.
    ///  • SINGLE bucket (Storage:Minio:Bucket set, e.g. "nexeagle-dev") — the
    ///    container becomes a key PREFIX inside that one bucket. Use this when the
    ///    access key is scoped to a single pre-created bucket.
    ///
    /// Either way blobPath/containerName round-trips through the SAS→register flow
    /// unchanged (BlobPath stays container-relative, like the Azure impl), and
    /// URL-based reads/deletes resolve to the same object. Buckets are private;
    /// reads are served by the API's proxy-asset endpoint (DownloadFileAsync).
    /// </summary>
    public class MinioBlobService : IBlobService
    {
        private readonly IAmazonS3 _s3;
        private readonly string _publicBaseUrl;    // e.g. http://151.185.45.77:9000
        private readonly string? _singleBucket;    // set => single-bucket mode; null => bucket-per-container
        private readonly string _defaultContainer; // default container when none supplied
        private readonly HashSet<string> _ensured = new(StringComparer.Ordinal);

        public MinioBlobService(IConfiguration configuration)
        {
            var endpoint = configuration["Storage:PublicBaseUrl"]
                           ?? configuration["Storage:Minio:PublicBaseUrl"];
            var accessKey = configuration["Storage:Minio:AccessKey"];
            var secretKey = configuration["Storage:Minio:SecretKey"];

            if (string.IsNullOrWhiteSpace(endpoint))
                throw new InvalidOperationException("MINIO_CONFIG_MISSING: 'Storage:PublicBaseUrl' is not configured.");
            if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
                throw new InvalidOperationException("MINIO_CONFIG_MISSING: 'Storage:Minio:AccessKey'/'SecretKey' are not configured.");

            _publicBaseUrl = endpoint.TrimEnd('/');
            var bucket = configuration["Storage:Minio:Bucket"];
            _singleBucket = string.IsNullOrWhiteSpace(bucket) ? null : bucket;
            _defaultContainer = configuration["Storage:Minio:DefaultContainer"]
                                ?? configuration["AzureBlobStorage:ContainerName"]
                                ?? "prescriptions";

            var s3Config = new AmazonS3Config
            {
                ServiceURL = _publicBaseUrl,        // scheme decides HTTP vs HTTPS
                ForcePathStyle = true,              // MinIO needs path-style, not vhost-style
                AuthenticationRegion = configuration["Storage:Minio:Region"] ?? "us-east-1",
            };
            _s3 = new AmazonS3Client(accessKey, secretKey, s3Config);
        }

        // ── Uploads ────────────────────────────────────────────────────────────

        public async Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, string? containerName = null)
        {
            var container = containerName ?? _defaultContainer;
            var (bucket, key) = Resolve(container, $"{Guid.NewGuid()}_{SanitiseFileName(fileName)}");
            await EnsureBucketAsync(bucket);
            await PutAsync(bucket, key, fileStream, contentType, null);
            return UrlFor(bucket, key);
        }

        public async Task<string> UploadFileAtPathAsync(Stream fileStream, string blobPath, string contentType, string containerName, string? cacheControl = null)
        {
            if (string.IsNullOrWhiteSpace(blobPath))
                throw new ArgumentException("blobPath is required", nameof(blobPath));
            if (string.IsNullOrWhiteSpace(containerName))
                throw new ArgumentException("containerName is required", nameof(containerName));

            var (bucket, key) = Resolve(containerName, blobPath);
            await EnsureBucketAsync(bucket);
            await PutAsync(bucket, key, fileStream, contentType, cacheControl);
            return UrlFor(bucket, key);
        }

        private async Task PutAsync(string bucket, string key, Stream fileStream, string contentType, string? cacheControl)
        {
            // S3 PutObject needs a seekable stream to compute Content-Length; buffer if not.
            var stream = fileStream;
            var bufferedHere = false;
            if (!fileStream.CanSeek)
            {
                var ms = new MemoryStream();
                await fileStream.CopyToAsync(ms);
                ms.Position = 0;
                stream = ms;
                bufferedHere = true;
            }

            try
            {
                var req = new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    InputStream = stream,
                    ContentType = contentType,
                    AutoCloseStream = false, // caller owns the original stream (matches Azure impl)
                };
                if (!string.IsNullOrWhiteSpace(cacheControl))
                    req.Headers.CacheControl = cacheControl;

                await _s3.PutObjectAsync(req);
            }
            finally
            {
                if (bufferedHere) stream.Dispose();
            }
        }

        // ── Listing / deletion ───────────────────────────────────────────────────

        public async IAsyncEnumerable<BlobItem> ListBlobsAsync(
            string containerName, string? prefix = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            // In single-bucket mode keys are prefixed with "{container}/"; strip it
            // so returned names stay container-relative (matches the Azure impl).
            var bucket = _singleBucket ?? containerName;
            var keyPrefix = _singleBucket != null ? $"{containerName.Trim('/')}/" : string.Empty;
            var listPrefix = keyPrefix + (prefix ?? string.Empty);

            string? continuationToken = null;
            do
            {
                var resp = await _s3.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = bucket,
                    Prefix = listPrefix,
                    ContinuationToken = continuationToken,
                }, ct);

                foreach (var o in resp.S3Objects)
                {
                    var name = keyPrefix.Length > 0 && o.Key.StartsWith(keyPrefix, StringComparison.Ordinal)
                        ? o.Key.Substring(keyPrefix.Length)
                        : o.Key;
                    yield return new BlobItem(name, o.LastModified, o.Size);
                }

                continuationToken = resp.IsTruncated ? resp.NextContinuationToken : null;
            }
            while (continuationToken != null && !ct.IsCancellationRequested);
        }

        public async Task DeleteBlobByNameAsync(string blobName, string containerName, CancellationToken ct = default)
        {
            var (bucket, key) = Resolve(containerName, blobName);
            await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, ct);
        }

        public async Task DeleteFileAsync(string fileUrl, string? containerName = null)
        {
            if (string.IsNullOrEmpty(fileUrl)) return;
            var (bucket, key) = FromUrl(fileUrl);
            if (string.IsNullOrEmpty(key)) return;
            await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key });
        }

        // ── Presigned upload (replaces Azure SAS) ─────────────────────────────────

        public async Task<SasUploadTarget> GenerateSasUploadUrlAsync(string blobPath, string containerName, TimeSpan validFor, string? contentType = null)
        {
            if (string.IsNullOrWhiteSpace(blobPath))
                throw new ArgumentException("blobPath is required", nameof(blobPath));
            if (string.IsNullOrWhiteSpace(containerName))
                throw new ArgumentException("containerName is required", nameof(containerName));

            var relPath = SanitisePath(blobPath);          // container-relative (round-trips back to us)
            var (bucket, key) = Resolve(containerName, blobPath);
            await EnsureBucketAsync(bucket);

            // Content-Type is deliberately NOT bound into the signature: S3 presigned
            // PUTs that pin Content-Type require the client to send a byte-exact match,
            // which is brittle across the browser and the bridge. The client's sent
            // Content-Type is still stored on the object.
            var url = _s3.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = bucket,
                Key = key,
                Verb = HttpVerb.PUT,
                Expires = DateTime.UtcNow.Add(validFor),
            });

            return new SasUploadTarget
            {
                SasUrl = url,
                PublicReadUrl = UrlFor(bucket, key),
                BlobPath = relPath,                         // container-relative, like the Azure impl
                ContainerName = containerName,
                ExpiresAt = DateTimeOffset.UtcNow.Add(validFor),
            };
        }

        // ── Multipart (parallel) upload ───────────────────────────────────────────

        public async Task<MultipartUploadInit> InitiateMultipartUploadAsync(
            string blobPath, string containerName, int partCount, TimeSpan validFor, string? contentType = null)
        {
            if (string.IsNullOrWhiteSpace(blobPath))
                throw new ArgumentException("blobPath is required", nameof(blobPath));
            if (string.IsNullOrWhiteSpace(containerName))
                throw new ArgumentException("containerName is required", nameof(containerName));
            if (partCount < 1)
                throw new ArgumentOutOfRangeException(nameof(partCount), "partCount must be >= 1.");

            var relPath = SanitisePath(blobPath);
            var (bucket, key) = Resolve(containerName, blobPath);
            await EnsureBucketAsync(bucket);

            var init = await _s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                // Content-Type is set on the object at initiate time (not pinned
                // into each part's signature — same rationale as the single-PUT
                // path: a byte-exact match is brittle across browser/bridge).
                ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            });

            var expires = DateTime.UtcNow.Add(validFor);
            var parts = new List<MultipartUploadPart>(partCount);
            for (int part = 1; part <= partCount; part++)
            {
                // Presigned UploadPart URL: UploadId + PartNumber make the SDK
                // sign the part-PUT operation rather than a whole-object PUT.
                var url = _s3.GetPreSignedURL(new GetPreSignedUrlRequest
                {
                    BucketName = bucket,
                    Key = key,
                    Verb = HttpVerb.PUT,
                    UploadId = init.UploadId,
                    PartNumber = part,
                    Expires = expires,
                });
                parts.Add(new MultipartUploadPart { PartNumber = part, Url = url });
            }

            return new MultipartUploadInit
            {
                UploadId = init.UploadId,
                BlobPath = relPath,
                ContainerName = containerName,
                PublicReadUrl = UrlFor(bucket, key),
                Parts = parts,
                ExpiresAt = DateTimeOffset.UtcNow.Add(validFor),
            };
        }

        public async Task CompleteMultipartUploadAsync(
            string blobPath, string containerName, string uploadId, IEnumerable<MultipartCompletedPart> parts)
        {
            if (string.IsNullOrWhiteSpace(uploadId))
                throw new ArgumentException("uploadId is required", nameof(uploadId));

            var (bucket, key) = Resolve(containerName, blobPath);
            // S3 requires parts in ascending PartNumber order on commit.
            var partETags = parts
                .OrderBy(p => p.PartNumber)
                .Select(p => new PartETag(p.PartNumber, p.ETag))
                .ToList();
            if (partETags.Count == 0)
                throw new ArgumentException("At least one completed part is required.", nameof(parts));

            await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = uploadId,
                PartETags = partETags,
            });
        }

        public async Task AbortMultipartUploadAsync(string blobPath, string containerName, string uploadId)
        {
            if (string.IsNullOrWhiteSpace(uploadId)) return;
            var (bucket, key) = Resolve(containerName, blobPath);
            await _s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = uploadId,
            });
        }

        // Presigned GET so the browser/bridge downloads straight from object storage
        // (no API hop). Pure local signing — no network call. Returns the input URL
        // unchanged if it can't be parsed into a (bucket, key).
        public string GeneratePresignedReadUrl(string fileUrl, TimeSpan validFor)
        {
            if (string.IsNullOrEmpty(fileUrl)) return fileUrl;
            try
            {
                var (bucket, key) = FromUrl(fileUrl);
                if (string.IsNullOrEmpty(key)) return fileUrl;
                return _s3.GetPreSignedURL(new GetPreSignedUrlRequest
                {
                    BucketName = bucket,
                    Key = key,
                    Verb = HttpVerb.GET,
                    Expires = DateTime.UtcNow.Add(validFor),
                });
            }
            catch
            {
                return fileUrl;
            }
        }

        // ── Existence / size / read URL ───────────────────────────────────────────

        public async Task<bool> BlobExistsAsync(string blobPath, string containerName)
        {
            if (string.IsNullOrWhiteSpace(blobPath) || string.IsNullOrWhiteSpace(containerName))
                return false;
            var (bucket, key) = Resolve(containerName, blobPath);
            try
            {
                await _s3.GetObjectMetadataAsync(bucket, key);
                return true;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        public string GetBlobReadUrl(string blobPath, string containerName)
        {
            if (string.IsNullOrWhiteSpace(blobPath) || string.IsNullOrWhiteSpace(containerName))
                return string.Empty;
            var (bucket, key) = Resolve(containerName, blobPath);
            return UrlFor(bucket, key);
        }

        public async Task<long> GetBlobSizeAsync(string blobPath, string containerName)
        {
            if (string.IsNullOrWhiteSpace(blobPath) || string.IsNullOrWhiteSpace(containerName))
                return 0;
            var (bucket, key) = Resolve(containerName, blobPath);
            try
            {
                var meta = await _s3.GetObjectMetadataAsync(bucket, key);
                return meta.ContentLength;
            }
            catch
            {
                return 0;
            }
        }

        public async Task<long> GetBlobSizeByUrlAsync(string fileUrl)
        {
            if (string.IsNullOrEmpty(fileUrl)) return 0;
            try
            {
                var (bucket, key) = FromUrl(fileUrl);
                if (string.IsNullOrEmpty(key)) return 0;
                var meta = await _s3.GetObjectMetadataAsync(bucket, key);
                return meta.ContentLength;
            }
            catch
            {
                return 0;
            }
        }

        public async Task<Stream> DownloadFileAsync(string fileUrl)
        {
            if (string.IsNullOrEmpty(fileUrl)) throw new ArgumentException("URL is required");
            var (bucket, key) = FromUrl(fileUrl);
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("Invalid blob URL format");

            try
            {
                var resp = await _s3.GetObjectAsync(bucket, key);
                return resp.ResponseStream;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                throw new FileNotFoundException("The specified clinical asset was not found in storage.");
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        // Map a logical (container, relative path) to a physical (bucket, key).
        private (string Bucket, string Key) Resolve(string container, string relPath)
        {
            var clean = SanitisePath(relPath);
            return _singleBucket != null
                ? (_singleBucket, $"{container.Trim('/')}/{clean}")   // single bucket: container is a prefix
                : (container, clean);                                  // bucket-per-container
        }

        private string UrlFor(string bucket, string key) => $"{_publicBaseUrl}/{bucket}/{key}";

        // Parse a stored URL "{host}/{bucket}/{key...}" back into (bucket, key).
        // Works for both layouts: the first path segment is always the physical
        // bucket and the remainder is the key. The host is ignored.
        private (string Bucket, string Key) FromUrl(string fileUrl)
        {
            try
            {
                var path = new Uri(fileUrl).AbsolutePath.TrimStart('/');
                var segments = path.Split('/', 2);
                if (segments.Length == 2)
                    return (segments[0], Uri.UnescapeDataString(segments[1]));
                return (_singleBucket ?? _defaultContainer, Uri.UnescapeDataString(path));
            }
            catch
            {
                return (_singleBucket ?? _defaultContainer, string.Empty);
            }
        }

        // Best-effort: auto-create the bucket on first use. Swallows failures — the
        // bucket may already exist, or a scoped key may lack create permission, in
        // which case a real PutObject error surfaces the actual problem.
        private async Task EnsureBucketAsync(string bucket)
        {
            if (_ensured.Contains(bucket)) return;
            try
            {
                if (!await AmazonS3Util.DoesS3BucketExistV2Async(_s3, bucket))
                    await _s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
            }
            catch { /* best-effort */ }
            _ensured.Add(bucket);
        }

        private static string SanitisePath(string blobPath) =>
            string.Join('/', blobPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SanitiseFileName));

        private static string SanitiseFileName(string fileName)
        {
            var name = Path.GetFileName(fileName);
            name = name.Replace(' ', '_');
            foreach (var invalid in new[] { '\\', ':', '*', '?', '"', '<', '>', '|' })
                name = name.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(name) ? Guid.NewGuid().ToString("N") : name;
        }
    }
}
