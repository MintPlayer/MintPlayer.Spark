using System.Net;
using System.Net.Http.Headers;
using Azure.Storage.Blobs;
using MintPlayer.Spark.Files.AzureBlob;
using MintPlayer.Spark.Files.FileSystem;
using MintPlayer.Spark.Files.S3;
using MintPlayer.Spark.Files.Storage;

namespace MintPlayer.Spark.Tests.Files.Stores;

/// <summary>
/// What every object store must do (#700 D12, M5): round-trip bytes from a forward-only stream, answer a missing key with
/// null, overwrite, copy, list by the layout's prefixes, refuse any key the layout did not make before any I/O, and, where
/// it offers one, accept a direct upload at its target. One class per store; a store needing a server is gated on its
/// environment variables (<see cref="StoreGateAttribute"/>), which CI sets.
/// </summary>
public abstract class FileStoreConformanceTests : IAsyncLifetime
{
    private ObjectFileStore? store;

    protected ObjectFileStore Store => store ?? throw new InvalidOperationException("The store's server is not configured.");

    /// <summary>A fresh, empty store for this test.</summary>
    protected abstract Task<ObjectFileStore> CreateStoreAsync();

    protected virtual Task CleanUpAsync() => Task.CompletedTask;

    /// <summary>Whether the store caps a direct upload's size itself (an S3 POST policy does, an Azure SAS can't).</summary>
    protected virtual bool CapsDirectUploads => false;

    public async Task InitializeAsync()
    {
        if (GetType().GetCustomAttributes(typeof(StoreGateAttribute), false).OfType<StoreGateAttribute>().FirstOrDefault()?.MissingReason() is null)
            store = await CreateStoreAsync();
    }

    public async Task DisposeAsync()
    {
        if (store is not null)
            await CleanUpAsync();
        (store as IDisposable)?.Dispose();
    }

    private static byte[] Bytes(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private async Task<byte[]?> ReadAsync(string key)
    {
        await using var stream = await Store.OpenReadAsync(key, CancellationToken.None);
        if (stream is null)
            return null;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private async Task<string[]> ListAsync(string prefix)
    {
        var keys = new List<string>();
        await foreach (var item in Store.ListAsync(prefix, CancellationToken.None))
            keys.Add(item.Key);
        return keys.Order(StringComparer.Ordinal).ToArray();
    }

    private static Func<Task> Act(Func<Task> action) => action;

    private static Action Act(Action action) => action;

    private static string Token() => Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());

    [StoreFact]
    public async Task Bytes_round_trip_from_a_forward_only_stream()
    {
        var key = Store.StagingKey(Token());
        var bytes = Bytes(300_000);

        await Store.WriteAsync(key, new ForwardOnlyStream(bytes), "application/pdf", CancellationToken.None);

        (await ReadAsync(key)).Should().Equal(bytes);
    }

    [StoreFact]
    public async Task A_missing_key_reads_null_and_deletes_quietly()
    {
        var key = Store.EntityKey("cars/404-A", "nothing");

        (await ReadAsync(key)).Should().BeNull();
        await Store.DeleteAsync(key, CancellationToken.None);
        (await ReadAsync(key)).Should().BeNull();
    }

    [StoreFact]
    public async Task A_write_replaces_and_a_delete_removes()
    {
        var key = Store.EntityKey("cars/1-A", "file1");
        await Store.WriteAsync(key, new MemoryStream(Bytes(100, 1)), null, CancellationToken.None);
        await Store.WriteAsync(key, new MemoryStream(Bytes(50, 2)), null, CancellationToken.None);

        (await ReadAsync(key)).Should().Equal(Bytes(50, 2));

        await Store.DeleteAsync(key, CancellationToken.None);
        (await ReadAsync(key)).Should().BeNull();
    }

    [StoreFact]
    public async Task A_copy_leaves_the_source_and_writes_the_destination()
    {
        var source = Store.StagingKey(Token());
        var destination = Store.EntityKey("cars/1-A", "copied");
        await Store.WriteAsync(source, new MemoryStream(Bytes(2048)), null, CancellationToken.None);

        await Store.CopyAsync(source, destination, CancellationToken.None);

        (await ReadAsync(destination)).Should().Equal(Bytes(2048));
        (await ReadAsync(source)).Should().Equal(Bytes(2048));
    }

    [StoreFact]
    public async Task Listing_stays_inside_its_prefix()
    {
        var staged = Store.StagingKey(Token());
        var first = Store.EntityKey("cars/1-A", "a");
        var variant = Store.EntityKey("cars/1-A", "a.thumb");
        var other = Store.EntityKey("cars/12-A", "b");
        foreach (var key in new[] { staged, first, variant, other })
            await Store.WriteAsync(key, new MemoryStream([1, 2, 3]), null, CancellationToken.None);

        (await ListAsync(ObjectFileStore.StagingRoot)).Should().Equal(staged);
        (await ListAsync(Store.EntityPrefix("cars/1-A"))).Should().Equal(new[] { first, variant }.Order(StringComparer.Ordinal));
        (await ListAsync(ObjectFileStore.EntitiesRoot)).Should().HaveCount(3);

        await foreach (var item in Store.ListAsync(Store.EntityPrefix("cars/12-A"), CancellationToken.None))
            item.LastModified.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    }

    [StoreFact]
    public async Task Keys_and_prefixes_the_layout_did_not_make_are_refused()
    {
        string[] keys = ["../escape", "staging/../../etc/passwd", "staging/a/b", "entities/x", "entities/eA/../../x", "/staging/abc", "other/abc", "staging/", "", "staging/abc.", "staging/abc.a.b", "staging/abc..x", "staging/abc./x"];
        foreach (var key in keys)
        {
            await Act(() => Store.WriteAsync(key, new MemoryStream([1]), null, CancellationToken.None)).Should().ThrowAsync<ArgumentException>();
            await Act(() => Store.OpenReadAsync(key, CancellationToken.None)).Should().ThrowAsync<ArgumentException>();
            await Act(() => Store.DeleteAsync(key, CancellationToken.None)).Should().ThrowAsync<ArgumentException>();
        }
        foreach (var prefix in new[] { "", "../", "entities", "entities/../", "staging/abc" })
            await Act(async () => { await foreach (var _ in Store.ListAsync(prefix, CancellationToken.None)) { } }).Should().ThrowAsync<ArgumentException>();
        Act(() => Store.EntityKey("cars/1-A", "../x")).Should().Throw<ArgumentException>();
        Act(() => Store.StagingKey("a/b")).Should().Throw<ArgumentException>();
        Act(() => Store.StagingVariantKey("tok", "../x")).Should().Throw<ArgumentException>();
        Act(() => Store.StagingVariantKey("tok", "a.b")).Should().Throw<ArgumentException>();
    }

    [StoreFact]
    public async Task A_staged_variant_is_a_key_of_its_own_beside_the_original()
    {
        var original = Store.StagingKey("tokv");
        var variant = Store.StagingVariantKey("tokv", "thumb");
        await Store.WriteAsync(original, new MemoryStream([1]), "image/png", CancellationToken.None);
        await Store.WriteAsync(variant, new MemoryStream([2, 3]), "image/webp", CancellationToken.None);

        (await ReadAsync(variant)).Should().Equal(2, 3);
        (await ReadAsync(original)).Should().Equal(1);
        ObjectFileStore.TryParseStaging(variant, out var token).Should().BeTrue();
        token.Should().Be("tokv");
    }

    [StoreFact]
    public void Entity_keys_parse_back_without_their_variant()
    {
        ObjectFileStore.TryParseEntity(Store.EntityKey("Cars/1-A", "f00d.thumb"), out var entityId, out var fileId).Should().BeTrue();
        entityId.Should().Be("Cars/1-A");
        fileId.Should().Be("f00d");
        ObjectFileStore.TryParseStaging(Store.StagingKey("tok_en-1"), out var token).Should().BeTrue();
        token.Should().Be("tok_en-1");
        ObjectFileStore.TryParseEntity(Store.StagingKey("x"), out _, out _).Should().BeFalse();
    }

    [StoreFact]
    public async Task Erase_removes_the_file_and_its_variants_only()
    {
        string[] erased = [Store.EntityKey("cars/1-A", "f1"), Store.EntityKey("cars/1-A", "f1.thumb")];
        string[] kept = [Store.EntityKey("cars/1-A", "f10"), Store.EntityKey("cars/1-A", "f2"), Store.EntityKey("cars/2-A", "f1")];
        foreach (var key in erased.Concat(kept))
            await Store.WriteAsync(key, new MemoryStream([1]), null, CancellationToken.None);

        await Store.EraseAsync("cars/1-A", "f1", CancellationToken.None);

        foreach (var key in erased)
            (await ReadAsync(key)).Should().BeNull();
        foreach (var key in kept)
            (await ReadAsync(key)).Should().NotBeNull();
    }

    [StoreFact]
    public async Task A_direct_upload_lands_under_its_key()
    {
        var key = Store.StagingKey(Token());
        var target = await Store.CreateUploadTargetAsync(key, 4096, DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);
        if (target is null)
            return; // Bytes go through Spark for this store: nothing to send directly.

        using var response = await SendAsync(target, Bytes(1000));

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
        (await ReadAsync(key)).Should().Equal(Bytes(1000));
    }

    [StoreFact]
    public async Task A_direct_upload_above_the_cap_is_refused_where_the_store_can_cap()
    {
        var key = Store.StagingKey(Token());
        var target = await Store.CreateUploadTargetAsync(key, 1024, DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);
        if (target is null || !CapsDirectUploads)
            return; // Capped at commit instead (FileUploadService reads the bytes back).

        using var response = await SendAsync(target, Bytes(4096));

        response.IsSuccessStatusCode.Should().BeFalse();
        (await ReadAsync(key)).Should().BeNull();
    }

    [StoreFact]
    public async Task A_read_url_serves_the_object_with_the_asked_headers_until_it_expires()
    {
        var key = Store.EntityKey("cars/1-A", "f" + Token());
        await Store.WriteAsync(key, new MemoryStream(Bytes(700)), "application/octet-stream", CancellationToken.None);
        var disposition = SparkFileServing.ContentDisposition("Überschrift 1.pdf", inline: false);
        var url = Store.CreateReadUrl(key, new SparkFileReadRequest("application/pdf", disposition, DateTimeOffset.UtcNow.AddMinutes(1)));
        if (url is null)
            return; // Proxied through Spark for this store (FileDownloadServiceTests cover that path).

        using var http = new HttpClient();
        using (var response = await http.GetAsync(url))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            (await response.Content.ReadAsByteArrayAsync()).Should().Equal(Bytes(700));
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
            response.Content.Headers.ContentDisposition!.ToString().Should().Be(disposition);
            FileDownloadTests.NeverCached(response);
        }

        // A read URL names one object: it does not open a neighbour.
        var other = Store.EntityKey("cars/1-A", "f" + Token());
        await Store.WriteAsync(other, new MemoryStream(Bytes(10, seed: 2)), null, CancellationToken.None);
        var forged = url.ToString().Replace(Uri.EscapeDataString(key[(key.LastIndexOf('/') + 1)..]), Uri.EscapeDataString(other[(other.LastIndexOf('/') + 1)..]));
        using (var response = await http.GetAsync(forged))
            response.IsSuccessStatusCode.Should().BeFalse();

        var expiring = Store.CreateReadUrl(key, new SparkFileReadRequest("application/pdf", disposition, DateTimeOffset.UtcNow.AddSeconds(2)))!;
        await Task.Delay(TimeSpan.FromSeconds(4));
        using (var response = await http.GetAsync(expiring))
            response.IsSuccessStatusCode.Should().BeFalse("an expired read URL is refused by the store's server");
    }

    private static async Task<HttpResponseMessage> SendAsync(SparkFileUploadTarget target, byte[] bytes)
    {
        using var http = new HttpClient();
        if (target.Method == SparkFileUploadTarget.Put)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, target.Url) { Content = new ByteArrayContent(bytes) };
            foreach (var (name, value) in target.Headers)
                request.Headers.TryAddWithoutValidation(name, value);
            return await http.SendAsync(request);
        }

        var form = new MultipartFormDataContent();
        foreach (var (name, value) in target.Fields)
            form.Add(new StringContent(value), name);
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "upload.bin");
        return await http.PostAsync(target.Url, form);
    }

    /// <summary>A request body: readable once, front to back, length unknown.</summary>
    private sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class FileSystemFileStoreConformanceTests : FileStoreConformanceTests
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "spark-fs-store-" + Guid.NewGuid().ToString("N"));

    protected override Task<ObjectFileStore> CreateStoreAsync()
        => Task.FromResult<ObjectFileStore>(new FileSystemFileStore(new FileSystemFileStoreOptions { RootPath = root }));

    protected override Task CleanUpAsync()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        return Task.CompletedTask;
    }
}

/// <summary>Against Azurite (CI) or a real account: <c>SPARK_AZURITE_CONNECTION_STRING</c>. Each test owns a container.</summary>
[StoreGate(ConnectionVariable)]
public sealed class AzureBlobFileStoreConformanceTests : FileStoreConformanceTests
{
    internal const string ConnectionVariable = "SPARK_AZURITE_CONNECTION_STRING";
    private BlobContainerClient? container;

    protected override async Task<ObjectFileStore> CreateStoreAsync()
    {
        container = new BlobContainerClient(Environment.GetEnvironmentVariable(ConnectionVariable), "spark-" + Guid.NewGuid().ToString("N"));
        await container.CreateIfNotExistsAsync();
        return new AzureBlobFileStore(container, new AzureBlobFileStoreOptions { Prefix = "app/" });
    }

    protected override async Task CleanUpAsync() => await container!.DeleteIfExistsAsync();
}

/// <summary>
/// Against Garage (CI) or any S3-compatible server: <c>SPARK_S3_URL</c>, <c>SPARK_S3_BUCKET</c>, <c>SPARK_S3_ACCESS_KEY</c>,
/// <c>SPARK_S3_SECRET_KEY</c> and optionally <c>SPARK_S3_REGION</c> (default <c>garage</c>). Each test owns a key prefix.
/// </summary>
[StoreGate("SPARK_S3_URL", "SPARK_S3_BUCKET", "SPARK_S3_ACCESS_KEY", "SPARK_S3_SECRET_KEY")]
public sealed class S3FileStoreConformanceTests : FileStoreConformanceTests
{
    private S3FileStore? s3;

    protected override bool CapsDirectUploads => true;

    internal static S3FileStoreOptions Options(string prefix) => new()
    {
        ServiceUrl = Environment.GetEnvironmentVariable("SPARK_S3_URL"),
        Bucket = Environment.GetEnvironmentVariable("SPARK_S3_BUCKET")!,
        AccessKey = Environment.GetEnvironmentVariable("SPARK_S3_ACCESS_KEY")!,
        SecretKey = Environment.GetEnvironmentVariable("SPARK_S3_SECRET_KEY")!,
        Region = Environment.GetEnvironmentVariable("SPARK_S3_REGION") is { Length: > 0 } region ? region : "garage",
        Prefix = prefix,
    };

    protected override Task<ObjectFileStore> CreateStoreAsync()
        => Task.FromResult<ObjectFileStore>(s3 = new S3FileStore(Options("t" + Guid.NewGuid().ToString("N"))));

    protected override async Task CleanUpAsync()
    {
        foreach (var root in new[] { ObjectFileStore.StagingRoot, ObjectFileStore.EntitiesRoot })
        {
            var keys = new List<string>();
            await foreach (var item in s3!.ListAsync(root, CancellationToken.None))
                keys.Add(item.Key);
            foreach (var key in keys)
                await s3.DeleteAsync(key, CancellationToken.None);
        }
    }
}
