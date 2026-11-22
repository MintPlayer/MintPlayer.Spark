using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using MintPlayer.Spark.Files.Storage;

namespace MintPlayer.Spark.Files.AzureBlob;

/// <summary>Where <see cref="AzureBlobFileStore"/> keeps its files.</summary>
public sealed class AzureBlobFileStoreOptions
{
    /// <summary>A key prefix inside the container (<c>myapp/</c>), for a container several apps share. Optional.</summary>
    public string? Prefix { get; set; }

    /// <summary>
    /// Browsers upload straight to the container through a SAS URL (create and write on one blob, until the upload window
    /// ends). Needs a client that can sign (a shared key) and CORS on the account for the app's origin. Without either, bytes
    /// go through Spark. Default true.
    /// </summary>
    public bool DirectUploads { get; set; } = true;

    /// <summary>
    /// Browsers read straight from the container through a read-only SAS on one blob, short-lived, issued only after Spark
    /// checked the read; it overrides the response's type and disposition. Needs a client that can sign. Without it, bytes are
    /// proxied through Spark. Default true.
    /// </summary>
    public bool DirectDownloads { get; set; } = true;
}

/// <summary>
/// File bytes in an Azure Blob Storage container (#700 D12). The container must be private: files are served only through
/// Spark's download endpoint. A SAS cannot limit a blob's size, so a direct upload is capped where Spark reads it back at
/// commit, and a larger one is refused and deleted there.
/// </summary>
public sealed class AzureBlobFileStore : ObjectFileStore
{
    private readonly BlobContainerClient container;
    private readonly AzureBlobFileStoreOptions options;
    private readonly string prefix;

    public AzureBlobFileStore(BlobContainerClient container, AzureBlobFileStoreOptions options)
    {
        this.container = container;
        this.options = options;
        prefix = NormalizePrefix(options.Prefix);
    }

    public override Task<SparkFileUploadTarget?> CreateUploadTargetAsync(string key, long maxBytes, DateTimeOffset expires, CancellationToken cancellationToken)
    {
        var blob = Blob(key);
        if (!options.DirectUploads || !blob.CanGenerateSasUri)
            return Task.FromResult<SparkFileUploadTarget?>(null);
        var uri = blob.GenerateSasUri(BlobSasPermissions.Create | BlobSasPermissions.Write, expires);
        return Task.FromResult<SparkFileUploadTarget?>(new SparkFileUploadTarget(
            uri.ToString(),
            SparkFileUploadTarget.Put,
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["x-ms-blob-type"] = "BlockBlob" }));
    }

    public override Uri? CreateReadUrl(string key, SparkFileReadRequest request)
    {
        var blob = Blob(key);
        if (!options.DirectDownloads || !blob.CanGenerateSasUri)
            return null;
        return blob.GenerateSasUri(new BlobSasBuilder(BlobSasPermissions.Read, request.Expires)
        {
            BlobContainerName = blob.BlobContainerName,
            BlobName = blob.Name,
            Resource = "b",
            ContentType = request.ContentType,
            ContentDisposition = request.ContentDisposition,
            CacheControl = SparkFileServing.CacheControl,
        });
    }

    public override async Task WriteAsync(string key, Stream content, string? contentType, CancellationToken cancellationToken)
        => await Blob(key).UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType ?? "application/octet-stream" },
        }, cancellationToken);

    public override async Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var download = await Blob(key).DownloadStreamingAsync(cancellationToken: cancellationToken);
            return download.Value.Content;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public override async Task DeleteAsync(string key, CancellationToken cancellationToken)
        => await Blob(key).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);

    /// <summary>Streamed through the app: a server-side copy needs the source readable by URL (a SAS or OAuth), which a shared-key-free setup lacks.</summary>
    public override async Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken)
    {
        await using var source = await OpenReadAsync(sourceKey, cancellationToken)
            ?? throw new FileNotFoundException("The source file is gone.", sourceKey);
        await WriteAsync(destinationKey, source, contentType: null, cancellationToken);
    }

    public override async IAsyncEnumerable<SparkStoredObject> ListAsync(string keyPrefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in container.GetBlobsAsync(BlobTraits.None, BlobStates.None,prefix + EnsurePrefix(keyPrefix), cancellationToken: cancellationToken))
        {
            yield return new SparkStoredObject(item.Name[prefix.Length..], item.Properties.LastModified ?? DateTimeOffset.MinValue);
        }
    }

    private BlobClient Blob(string key) => container.GetBlobClient(prefix + EnsureKey(key));

    internal static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return string.Empty;
        var trimmed = prefix.Trim('/');
        if (trimmed.Length == 0 || trimmed.Split('/').Any(s => s.Length == 0 || s is "." or ".." || !s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("A store prefix is segments of letters, digits, '-', '_' and '.'.", nameof(prefix));
        return trimmed + "/";
    }
}
