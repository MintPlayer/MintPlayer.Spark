using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Files;
using MintPlayer.Spark.Files.FileSystem;
using MintPlayer.Spark.Files.Scanning;
using MintPlayer.Spark.Files.Services;
using MintPlayer.Spark.Files.Staging;
using MintPlayer.Spark.Files.Storage;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Organizations;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Files.Stores;

/// <summary>
/// #536: Spark.Files on the database <c>SPARK_TEST_DATABASE</c> picks, with the file system store. <c>AddFiles()</c>
/// registers the uploads' table (D36); prepare, upload, commit and the claim run on the neutral session; the reconcile
/// reads the entity's file ids through its stored type (a store with a schema) or raw (RavenDB); the deferred scan's sweep
/// streams the staging collection. A store without attachments refuses the default store, naming the packages to use.
/// </summary>
public class NeutralFilesStoreTests : SparkTestDriver
{
    private const string Alice = "users/alice";
    private readonly string root = Path.Combine(Path.GetTempPath(), "spark-fs-neutral-" + Guid.NewGuid().ToString("N"));
    private SparkEndpointFactory<FiContext>? factory;

    public override async Task DisposeAsync()
    {
        if (factory is not null)
            await factory.DisposeAsync();
        await base.DisposeAsync();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private SparkEndpointFactory<FiContext> Host(Action<FilesBuilder>? configure = null) => factory ??= new SparkEndpointFactory<FiContext>(
        Database,
        [FilesTestHost.CarModel(), FilesTestHost.ServiceModel()],
        configureServices: services => services.AddSingleton<IStartupFilter, OgPrincipalStartupFilter>(),
        configureSpark: spark => spark.AddFiles(files =>
        {
            files.ConfigureOptions(o => o.MaxFileBytes = 1024 * 1024);
            (configure ?? (f => f.UseFileSystem(root)))(files);
        }),
        security: SparkTestSecurity.Permissive,
        environment: "Development");

    private ObjectFileStore FileStore => (ObjectFileStore)Host().GetService<IFileStore>();

    [Fact]
    public async Task A_claimed_file_is_stored_and_the_reconcile_keeps_it_but_removes_an_orphan()
    {
        var token = await StageAsync("scan.pdf");
        var (status, body) = await PostJsonAsync("/spark/po/create", Wire.Typed(FilesTestHost.CarTypeId, new
        {
            persistentObject = new
            {
                name = "FiCar",
                objectTypeId = FilesTestHost.CarTypeId.ToString(),
                attributes = new object[]
                {
                    new { name = "Name", value = "Golf", isValueChanged = true },
                    new { name = "Document", value = new { uploadToken = token }, isValueChanged = true },
                },
            },
        }, null));
        status.Should().Be(HttpStatusCode.Created, body.ToString());
        var id = body.GetProperty("result").GetProperty("id").GetString()!;

        FiCar? car;
        using (var session = Host().GetService<ISparkStore>().OpenSession())
            car = await session.LoadAsync<FiCar>(id);
        var held = FileStore.EntityKey(id, car!.Document!.FileId);
        var orphan = FileStore.EntityKey(id, "orphan");
        await FileStore.WriteAsync(orphan, new MemoryStream([7]), null, CancellationToken.None);
        foreach (var key in new[] { held, orphan })
            File.SetLastWriteTimeUtc(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)), DateTime.UtcNow.AddDays(-1));

        var deleted = await Host().GetService<FilesReconciler>().ReconcileAsync(CancellationToken.None);

        deleted.Should().Equal(orphan);
        await using var stream = await FileStore.OpenReadAsync(held, CancellationToken.None);
        (stream is not null).Should().BeTrue("the car holds the file");
    }

    [Fact]
    public async Task The_deferred_sweep_queues_a_committed_upload_that_waits_for_its_scan()
    {
        var token = Guid.NewGuid().ToString("N");
        using (var session = Host().GetService<ISparkStore>().OpenSession())
        {
            await session.StoreAsync(new SparkFileUpload
            {
                Id = SparkFileUpload.IdFor(token),
                Token = token,
                UserId = Alice,
                EntityTypeId = FilesTestHost.CarTypeId,
                Attribute = "Document",
                State = SparkFileUploadState.Committed,
                ScanStatus = SparkFileScanStatus.Pending,
                FileName = "scan.pdf",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            }, SparkFileUpload.IdFor(token).Value);
            await session.SaveChangesAsync();
        }

        (await Host().GetService<DeferredFileScanner>().SweepAsync(CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public void Without_a_file_store_only_RavenDB_has_a_default()
    {
        if (Database.ProviderName == SparkStoreProviders.RavenDb)
        {
            Host(_ => { }).GetService<IFileStore>().Should().BeOfType<RavenAttachmentFileStore>();
            return;
        }
        // The host refuses to start: its startup check resolves the store.
        var act = () => Host(_ => { });
        act.Should().Throw<InvalidOperationException>().WithMessage("*has none: choose a file store*UseFileSystem*");
    }

    private async Task<string> StageAsync(string fixture)
    {
        var bytes = FileSnifferTests.Fixture(fixture);
        var (status, body) = await PostJsonAsync("/spark/files/prepare", new
        {
            objectTypeId = FilesTestHost.CarTypeId,
            attribute = "Document",
            id = (string?)null,
            files = new[] { new { fileName = fixture, length = (long)bytes.Length } },
        });
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var token = body.GetProperty("files")[0].GetProperty("token").GetString()!;

        using (var client = Host().CreateClient())
        {
            var (cookie, xsrf) = await OrganizationsTestHost.MintAsync(client, Alice);
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            var form = new MultipartFormDataContent { { new StringContent(token), "token" }, { file, "file", fixture } };
            var request = new HttpRequestMessage(HttpMethod.Post, "/spark/files/upload") { Content = form };
            request.Headers.Add("Cookie", cookie);
            request.Headers.Add("X-XSRF-TOKEN", xsrf);
            request.Headers.Add(OgPrincipalStartupFilter.Header, Alice);
            using var upload = await client.SendAsync(request);
            upload.StatusCode.Should().Be(HttpStatusCode.NoContent, await upload.Content.ReadAsStringAsync());
        }

        (status, body) = await PostJsonAsync("/spark/files/commit", new { tokens = new[] { token } });
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        return token;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostJsonAsync(string url, object payload)
    {
        using var client = Host().CreateClient();
        var (cookie, xsrf) = await OrganizationsTestHost.MintAsync(client, Alice);
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-XSRF-TOKEN", xsrf);
        request.Headers.Add(OgPrincipalStartupFilter.Header, Alice);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }
}
