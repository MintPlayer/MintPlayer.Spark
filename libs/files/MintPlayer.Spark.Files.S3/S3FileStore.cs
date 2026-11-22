using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using MintPlayer.Spark.Files.Storage;

namespace MintPlayer.Spark.Files.S3;

/// <summary>Where <see cref="S3FileStore"/> keeps its files.</summary>
public sealed class S3FileStoreOptions
{
    /// <summary>The bucket; it must exist and be private. Required.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>The region (<c>eu-west-1</c>; Garage: its <c>s3_region</c>). Required.</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>The access key id. Required: the POST policy is signed with its secret.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>The secret access key. Required.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>The endpoint of an S3-compatible server (<c>http://garage:3900</c>); null for AWS.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>
    /// The endpoint as a browser reaches it, when that differs from <see cref="ServiceUrl"/> (a container name inside a
    /// compose network, a public host name outside). Direct uploads post here.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>Path-style addressing (<c>{endpoint}/{bucket}/{key}</c>). Most self-hosted servers need it. Default true when <see cref="ServiceUrl"/> is set.</summary>
    public bool? ForcePathStyle { get; set; }

    /// <summary>A key prefix inside the bucket (<c>myapp/</c>), for a bucket several apps share. Optional.</summary>
    public string? Prefix { get; set; }

    /// <summary>
    /// Browsers upload straight to the bucket through a presigned POST policy capped at the slot's size. Needs CORS on the
    /// bucket for the app's origin. Default true.
    /// </summary>
    public bool DirectUploads { get; set; } = true;

    /// <summary>
    /// Browsers read straight from the bucket through a presigned GET on one key, short-lived, issued only after Spark checked
    /// the read; it overrides the response's type and disposition. Signed for <see cref="PublicUrl"/> when set. Default true;
    /// false proxies the bytes through Spark.
    /// </summary>
    public bool DirectDownloads { get; set; } = true;
}

/// <summary>
/// File bytes in an S3-compatible bucket (#700 D12, spike SP9). The bucket must be private: files are served only through
/// Spark's download endpoint. A direct upload is a POST policy: one key, a <c>content-length-range</c> up to the slot's cap,
/// the upload window as its expiry. A presigned PUT is never issued, because it cannot limit size.
/// </summary>
public sealed class S3FileStore : ObjectFileStore, IDisposable
{
    private readonly AmazonS3Client client;
    private readonly S3FileStoreOptions options;
    private readonly string prefix;

    public S3FileStore(S3FileStoreOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Bucket) || string.IsNullOrWhiteSpace(options.Region)
            || string.IsNullOrWhiteSpace(options.AccessKey) || string.IsNullOrWhiteSpace(options.SecretKey))
            throw new ArgumentException("The S3 store needs a Bucket, Region, AccessKey and SecretKey.", nameof(options));
        this.options = options;
        prefix = NormalizePrefix(options.Prefix);

        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle ?? options.ServiceUrl is not null,
            // Only what the operation requires: S3-compatible servers differ in which checksum headers they accept (SP9).
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (options.ServiceUrl is { } serviceUrl)
        {
            config.ServiceURL = serviceUrl;
            config.AuthenticationRegion = options.Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }
        client = new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);

        // A presigned URL signs its host: one meant for browsers is signed by a client that addresses the public endpoint.
        // Signing is local, so this client never opens a connection.
        if (options.PublicUrl is { } publicUrl && options.ServiceUrl is not null && publicUrl != options.ServiceUrl)
        {
            var publicConfig = new AmazonS3Config
            {
                ForcePathStyle = config.ForcePathStyle,
                ServiceURL = publicUrl,
                AuthenticationRegion = options.Region,
            };
            presigner = new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), publicConfig);
        }
        else
        {
            presigner = client;
        }
        presignProtocol = (options.PublicUrl ?? options.ServiceUrl) is { } endpoint && endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? Protocol.HTTP
            : Protocol.HTTPS;
    }

    private readonly AmazonS3Client presigner;
    private readonly Protocol presignProtocol;

    public override Uri? CreateReadUrl(string key, SparkFileReadRequest request)
    {
        if (!options.DirectDownloads)
            return null;
        var url = presigner.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = options.Bucket,
            Key = ObjectKey(key),
            Verb = HttpVerb.GET,
            Expires = request.Expires.UtcDateTime,
            Protocol = presignProtocol,
            ResponseHeaderOverrides = new ResponseHeaderOverrides
            {
                ContentType = request.ContentType,
                ContentDisposition = request.ContentDisposition,
                CacheControl = SparkFileServing.CacheControl,
            },
        });
        return new Uri(url);
    }

    public override Task<SparkFileUploadTarget?> CreateUploadTargetAsync(string key, long maxBytes, DateTimeOffset expires, CancellationToken cancellationToken)
    {
        if (!options.DirectUploads)
            return Task.FromResult<SparkFileUploadTarget?>(null);
        var objectKey = ObjectKey(key);
        var fields = PostPolicy.Sign(options.Bucket, objectKey, maxBytes, expires, DateTimeOffset.UtcNow, options.Region, options.AccessKey, options.SecretKey);
        return Task.FromResult<SparkFileUploadTarget?>(new SparkFileUploadTarget(PostUrl(), SparkFileUploadTarget.Post, fields, new Dictionary<string, string>()));
    }

    public override async Task WriteAsync(string key, Stream content, string? contentType, CancellationToken cancellationToken)
    {
        // The SDK needs the length up front once chunked (streaming) signing is off, which several S3-compatible servers refuse.
        await using var seekable = await SeekableAsync(content, cancellationToken);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Bucket,
            Key = ObjectKey(key),
            InputStream = seekable,
            AutoCloseStream = false,
            ContentType = contentType ?? "application/octet-stream",
            UseChunkEncoding = false,
        }, cancellationToken);
    }

    public override async Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetObjectAsync(options.Bucket, ObjectKey(key), cancellationToken);
            return new OwningStream(response.ResponseStream, response);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public override async Task DeleteAsync(string key, CancellationToken cancellationToken)
        => await client.DeleteObjectAsync(options.Bucket, ObjectKey(key), cancellationToken);

    public override async Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken)
        => await client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = options.Bucket,
            SourceKey = ObjectKey(sourceKey),
            DestinationBucket = options.Bucket,
            DestinationKey = ObjectKey(destinationKey),
        }, cancellationToken);

    public override async IAsyncEnumerable<SparkStoredObject> ListAsync(string keyPrefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new ListObjectsV2Request { BucketName = options.Bucket, Prefix = prefix + EnsurePrefix(keyPrefix) };
        while (true)
        {
            var response = await client.ListObjectsV2Async(request, cancellationToken);
            foreach (var item in response.S3Objects ?? [])
            {
                var modified = item.LastModified is DateTime lastModified
                    ? new DateTimeOffset(DateTime.SpecifyKind(lastModified.ToUniversalTime(), DateTimeKind.Utc))
                    : DateTimeOffset.MinValue;
                yield return new SparkStoredObject(item.Key[prefix.Length..], modified);
            }
            if (response.IsTruncated != true || string.IsNullOrEmpty(response.NextContinuationToken))
                yield break;
            request.ContinuationToken = response.NextContinuationToken;
        }
    }

    public void Dispose()
    {
        if (!ReferenceEquals(presigner, client))
            presigner.Dispose();
        client.Dispose();
    }

    private string ObjectKey(string key) => prefix + EnsureKey(key);

    private string PostUrl()
    {
        var endpoint = options.PublicUrl ?? options.ServiceUrl;
        if (endpoint is null)
            return $"https://{options.Bucket}.s3.{options.Region}.amazonaws.com/";
        return (options.ForcePathStyle ?? true)
            ? $"{endpoint.TrimEnd('/')}/{options.Bucket}"
            : new UriBuilder(endpoint) { Host = options.Bucket + "." + new Uri(endpoint).Host }.Uri.ToString();
    }

    internal static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return string.Empty;
        var trimmed = prefix.Trim('/');
        if (trimmed.Length == 0 || trimmed.Split('/').Any(s => s.Length == 0 || s is "." or ".." || !s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("A store prefix is segments of letters, digits, '-', '_' and '.'.", nameof(prefix));
        return trimmed + "/";
    }

    /// <summary>The object stream, disposing the response that owns its connection.</summary>
    private sealed class OwningStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// A browser-based upload's POST policy, signed with Signature Version 4 (AWS "Browser-Based Uploads Using POST"). Spike SP9:
/// Garage needs the <c>bucket</c> condition and field; <c>content-length-range</c> refuses a larger body with 400.
/// </summary>
internal static class PostPolicy
{
    public static IReadOnlyDictionary<string, string> Sign(string bucket, string key, long maxBytes, DateTimeOffset expires, DateTimeOffset now, string region, string accessKey, string secretKey)
    {
        var date = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var credential = $"{accessKey}/{date}/{region}/s3/aws4_request";

        // Built with a JSON writer, never by splicing: a key or bucket can't break out of its condition.
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("expiration", expires.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            json.WriteStartArray("conditions");
            Condition(json, "bucket", bucket);
            json.WriteStartArray(); json.WriteStringValue("eq"); json.WriteStringValue("$key"); json.WriteStringValue(key); json.WriteEndArray();
            Condition(json, "x-amz-algorithm", "AWS4-HMAC-SHA256");
            Condition(json, "x-amz-credential", credential);
            Condition(json, "x-amz-date", amzDate);
            json.WriteStartArray(); json.WriteStringValue("content-length-range"); json.WriteNumberValue(1); json.WriteNumberValue(maxBytes); json.WriteEndArray();
            json.WriteEndArray();
            json.WriteEndObject();
        }
        var policy = Convert.ToBase64String(buffer.ToArray());

        var signingKey = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date), region), "s3"), "aws4_request");
        var signature = Convert.ToHexStringLower(Hmac(signingKey, policy));

        // Ordered: S3 reads the fields before the file, which must come last.
        return new OrderedDictionary<string, string>
        {
            ["key"] = key,
            ["bucket"] = bucket,
            ["policy"] = policy,
            ["x-amz-algorithm"] = "AWS4-HMAC-SHA256",
            ["x-amz-credential"] = credential,
            ["x-amz-date"] = amzDate,
            ["x-amz-signature"] = signature,
        };
    }

    private static void Condition(Utf8JsonWriter json, string name, string value)
    {
        json.WriteStartObject();
        json.WriteString(name, value);
        json.WriteEndObject();
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
}
