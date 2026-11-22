using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Files;
using MintPlayer.Spark.Files.Staging;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Organizations;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Files;

/// <summary>
/// A Spark host with <c>AddFiles()</c> on the embedded RavenDB, a principal taken from the <c>X-Og-User</c> header and
/// one type, <see cref="FiCar"/>: <c>Photos</c> (a list of images, at most 3) and <c>Document</c> (a PDF of at most
/// <see cref="DocumentMaxBytes"/>). Fixture names start with <c>Fi</c>: actions classes are matched by simple name.
/// </summary>
public abstract class FilesTestHost : SparkTestDriver
{
    internal static readonly Guid CarTypeId = Guid.Parse("0f11e000-0000-4000-8000-000000000001");
    internal const long DocumentMaxBytes = 4096;

    private SparkEndpointFactory<FiContext>? factory;

    public override async Task DisposeAsync()
    {
        if (factory is not null)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    internal SparkEndpointFactory<FiContext> Factory => factory ??= new SparkEndpointFactory<FiContext>(
        Store,
        [WithExtraModel(CarModel()), ServiceModel()],
        configureServices: services =>
        {
            services.AddSingleton<IStartupFilter, OgPrincipalStartupFilter>();
            ExtraServices?.Invoke(services);
        },
        configureSpark: spark =>
        {
            spark.AddFiles(files =>
            {
                files.ConfigureOptions(o => o.MaxFileBytes = 1024 * 1024);
                ConfigureFiles?.Invoke(files);
            });
            ExtraSpark?.Invoke(spark);
        },
        security: Security,
        // Development validates scopes: a scoped Files service resolved from the root fails here, as in a developer's app.
        environment: HostEnvironment);

    /// <summary>The host's environment; Development unless a test needs another before the host boots.</summary>
    internal string HostEnvironment { get; set; } = "Development";

    /// <summary>The host's security; permissive unless a test narrows it before the host boots.</summary>
    internal SparkTestSecurity Security { get; set; } = SparkTestSecurity.Permissive;

    internal Action<FilesBuilder>? ConfigureFiles { get; set; }

    /// <summary>Changes the car's model before the host boots (an attribute's <c>fileOptions</c>, say).</summary>
    internal Action<EntityTypeFile>? ExtraModel { get; set; }

    private EntityTypeFile WithExtraModel(EntityTypeFile model)
    {
        ExtraModel?.Invoke(model);
        return model;
    }

    internal Action<IServiceCollection>? ExtraServices { get; set; }

    internal Action<ISparkBuilder>? ExtraSpark { get; set; }

    internal static EntityTypeFile CarModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = CarTypeId,
            Name = "FiCar",
            ClrType = typeof(FiCar).FullName!,
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string" },
                new() { Id = Guid.NewGuid(), Name = "Photos", DataType = "Image", IsArray = true, FileOptions = new SparkFileOptions { MaxCount = 3 } },
                new() { Id = Guid.NewGuid(), Name = "Document", DataType = "BinaryFile", FileOptions = new SparkFileOptions { Accept = [".pdf"], MaxBytes = DocumentMaxBytes } },
                new() { Id = Guid.NewGuid(), Name = "Services", DataType = "AsDetail", IsArray = true, AsDetailType = typeof(FiService).FullName },
            ],
        },
    };

    internal static readonly Guid ServiceTypeId = Guid.Parse("0f11e000-0000-4000-8000-000000000002");

    /// <summary>The AsDetail row type (M10): a description and an <c>Invoice</c> PDF.</summary>
    internal static EntityTypeFile ServiceModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = ServiceTypeId,
            Name = "FiService",
            ClrType = typeof(FiService).FullName!,
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Description", DataType = "string" },
                new() { Id = Guid.NewGuid(), Name = "Invoice", DataType = "BinaryFile", FileOptions = new SparkFileOptions { Accept = [".pdf"], MaxBytes = DocumentMaxBytes } },
            ],
        },
    };

    /// <summary>One posted <c>Services</c> row: <paramref name="key"/> null for a new row.</summary>
    internal static object ServiceRow(string? key, string description, object? invoice) => new
    {
        id = key,
        name = "FiService",
        objectTypeId = ServiceTypeId.ToString(),
        attributes = new object[]
        {
            new { name = "Description", value = description, isValueChanged = true },
            new { name = "Invoice", value = invoice, isValueChanged = true },
        },
    };

    /// <summary>The posted <c>Services</c> AsDetail attribute with <paramref name="rows"/>.</summary>
    internal static object ServicesAttribute(params object[] rows) => new
    {
        name = "Services",
        dataType = "AsDetail",
        isArray = true,
        asDetailType = typeof(FiService).FullName,
        objects = rows,
        isValueChanged = true,
    };

    /// <summary>A save posting <paramref name="attributes"/> as given (an AsDetail attribute among them).</summary>
    internal async Task<(HttpStatusCode Status, JsonElement Body)> SaveCarRawAsync(string? userId, string? id, params object[] attributes)
    {
        var persistentObject = new Dictionary<string, object?>
        {
            ["name"] = "FiCar",
            ["objectTypeId"] = CarTypeId.ToString(),
            ["attributes"] = attributes,
        };
        if (id is not null)
        {
            persistentObject["id"] = id;
            persistentObject["etag"] = await StoredEtag.OfAsync(Store, id);
        }
        return await PostJsonAsync(id is null ? "/spark/po/create" : "/spark/po/update", Wire.Typed(CarTypeId, new { persistentObject }, id), userId);
    }

    // ---- the three steps -------------------------------------------------------------------------------

    internal Task<(HttpStatusCode Status, JsonElement Body)> PrepareAsync(string? userId, string attribute, string? id, params (string Name, long Length)[] files)
        => PostJsonAsync("/spark/files/prepare", new
        {
            objectTypeId = CarTypeId,
            attribute,
            id,
            files = files.Select(f => new { fileName = f.Name, length = f.Length }),
        }, userId);

    /// <summary>Prepares one slot for <paramref name="fixture"/> and returns its token.</summary>
    internal async Task<string> PrepareOneAsync(string userId, string attribute, string fixture, string? asName = null, string? id = null)
    {
        var (status, body) = await PrepareAsync(userId, attribute, id, (asName ?? fixture, FileSnifferTests.Fixture(fixture).Length));
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        return body.GetProperty("files")[0].GetProperty("token").GetString()!;
    }

    internal Task<HttpResponseMessage> UploadAsync(string? userId, string token, string fileName, byte[] bytes, bool withXsrf = true)
        => UploadAsync(userId, token, fileName, new ByteArrayContent(bytes), withXsrf);

    internal async Task<HttpResponseMessage> UploadAsync(string? userId, string token, string fileName, HttpContent file, bool withXsrf = true)
    {
        using var client = Factory.CreateClient();
        var (cookie, xsrf) = await OrganizationsTestHost.MintAsync(client, userId);
        var form = new MultipartFormDataContent { { new StringContent(token), "token" } };
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        var request = new HttpRequestMessage(HttpMethod.Post, "/spark/files/upload") { Content = form };
        request.Headers.Add("Cookie", cookie);
        if (withXsrf)
            request.Headers.Add("X-XSRF-TOKEN", xsrf);
        if (userId is not null)
            request.Headers.Add(OgPrincipalStartupFilter.Header, userId);
        return await client.SendAsync(request);
    }

    internal Task<(HttpStatusCode Status, JsonElement Body)> CommitAsync(string? userId, params string[] tokens)
        => PostJsonAsync("/spark/files/commit", new { tokens }, userId);

    /// <summary>Prepare, upload and commit <paramref name="fixture"/>; returns the token and the committed item.</summary>
    internal async Task<(string Token, JsonElement Committed)> StageAsync(string userId, string attribute, string fixture, string? asName = null, string? id = null)
    {
        var name = asName ?? fixture;
        var token = await PrepareOneAsync(userId, attribute, fixture, name, id);
        using (var upload = await UploadAsync(userId, token, name, FileSnifferTests.Fixture(fixture)))
            upload.StatusCode.Should().Be(HttpStatusCode.NoContent, await upload.Content.ReadAsStringAsync());
        var (status, body) = await CommitAsync(userId, token);
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        return (token, body.GetProperty("files")[0]);
    }

    internal async Task<(HttpStatusCode Status, JsonElement Body)> PostJsonAsync(string url, object payload, string? userId, bool withXsrf = true)
    {
        using var client = Factory.CreateClient();
        var (cookie, xsrf) = await OrganizationsTestHost.MintAsync(client, userId);
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.Add("Cookie", cookie);
        if (withXsrf)
            request.Headers.Add("X-XSRF-TOKEN", xsrf);
        if (userId is not null)
            request.Headers.Add(OgPrincipalStartupFilter.Header, userId);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    internal async Task<SparkFileUpload?> StagedAsync(string token)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<SparkFileUpload>(SparkFileUpload.IdFor(token));
    }

    internal async Task<int> StagedAttachmentCountAsync(string token)
    {
        using var session = Store.OpenAsyncSession();
        var upload = await session.LoadAsync<SparkFileUpload>(SparkFileUpload.IdFor(token));
        return upload is null ? 0 : session.Advanced.Attachments.GetNames(upload).Length;
    }

    /// <summary>A create (no <paramref name="id"/>) or update of a car through <c>/spark/po</c>, posting only <paramref name="attributes"/>.</summary>
    internal async Task<(HttpStatusCode Status, JsonElement Body)> SaveCarAsync(string? userId, string? id, params (string Name, object? Value)[] attributes)
    {
        var persistentObject = new Dictionary<string, object?>
        {
            ["name"] = "FiCar",
            ["objectTypeId"] = CarTypeId.ToString(),
            ["attributes"] = attributes.Select(a => new { name = a.Name, value = a.Value, isValueChanged = true }).ToArray(),
        };
        if (id is not null)
        {
            persistentObject["id"] = id;
            persistentObject["etag"] = await StoredEtag.OfAsync(Store, id);
        }
        return await PostJsonAsync(id is null ? "/spark/po/create" : "/spark/po/update", Wire.Typed(CarTypeId, new { persistentObject }, id), userId);
    }

    /// <summary>Creates a car holding the staged <paramref name="fixture"/> in <paramref name="attribute"/>; returns its id.</summary>
    internal async Task<string> CreateCarWithAsync(string userId, string attribute, string fixture)
    {
        var (token, _) = await StageAsync(userId, attribute, fixture);
        object value = attribute == "Photos" ? new[] { new { uploadToken = token } } : new { uploadToken = token };
        var (status, body) = await SaveCarAsync(userId, null, ("Name", "Golf"), (attribute, value));
        status.Should().Be(HttpStatusCode.Created, body.ToString());
        return body.GetProperty("result").GetProperty("id").GetString()!;
    }

    internal async Task<FiCar> LoadCarAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<FiCar>(id))!;
    }

    internal async Task<string[]> CarAttachmentsAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        var car = await session.LoadAsync<FiCar>(id);
        return car is null ? [] : session.Advanced.Attachments.GetNames(car).Select(a => a.Name).Order(StringComparer.Ordinal).ToArray();
    }

    internal async Task<byte[]?> CarAttachmentBytesAsync(string id, string fileId)
    {
        using var session = Store.OpenAsyncSession();
        using var attachment = await session.Advanced.Attachments.GetAsync(id, fileId);
        if (attachment is null)
            return null;
        using var buffer = new MemoryStream();
        await attachment.Stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    internal async Task<string> SeedCarAsync(string name = "Golf")
    {
        using var session = Store.OpenAsyncSession();
        var car = new FiCar { Name = name };
        await session.StoreAsync(car);
        await session.SaveChangesAsync();
        return car.Id!;
    }
}

public class FiCar
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public List<SparkImage> Photos { get; set; } = [];
    public SparkFile? Document { get; set; }
    public List<FiService> Services { get; set; } = [];
}

/// <summary>An AsDetail row holding a file (M10). A plain class: rows are matched by <c>Id</c>.</summary>
public class FiService
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? Description { get; set; }
    public SparkFile? Invoice { get; set; }
}

/// <summary>Every car is visible: the subject of these tests is files, so rows are public on purpose.</summary>
public class FiCarActions(MintPlayer.Spark.Services.IEntityMapper mapper) : MintPlayer.Spark.Actions.DefaultPersistentObjectActions<FiCar>(mapper), MintPlayer.Spark.Abstractions.Authorization.ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture: every car is visible; the subject is file attributes.";
}

public class FiContext : SparkContext
{
    public IQueryable<FiCar> Cars => Session.Query<FiCar>();
}
