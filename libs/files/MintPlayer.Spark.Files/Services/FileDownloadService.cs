using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Endpoints;
using MintPlayer.Spark.Endpoints.PersistentObject;
using MintPlayer.Spark.Files.Storage;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Files.Services;

/// <summary>
/// Serving stored files (#700 D14, D15, §3.5). A <b>download</b> runs the object's read path (type right, row security,
/// attribute rights, the actions class's load hooks) and then redirects to a short-lived presigned URL, or, for a store
/// that proxies, serves the bytes itself. A <b>content</b> URL is the proxied store's presigned URL: a ticket protected by
/// the app's data-protection keys, naming one object, its type and disposition, valid until it expires.
/// </summary>
[Register(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)]
internal sealed partial class FileDownloadService
{
    public const string DownloadPath = SparkFileDownloadUrl.Path;
    public const string ContentPath = "/spark/files/content/";

    [Inject] private readonly IPersistentObjectReadPath readPath;
    [Inject] private readonly IFileStore fileStore;
    [Inject] private readonly FileContentTickets tickets;
    [Inject] private readonly IOptions<SparkFilesOptions> options;
    [Inject] private readonly TimeProvider clock;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>
    /// <c>GET /spark/files/download</c>: the file <paramref name="fileId"/> (or its <paramref name="variant"/>) that the
    /// attribute <paramref name="attribute"/> of <paramref name="id"/> holds, if the caller may read that attribute's value.
    /// Every refusal is the read path's own: 404, or the uniform 401 for an anonymous caller.
    /// </summary>
    public async Task<IResult> DownloadAsync(string? objectTypeId, string? id, string? attribute, string? fileId, string? variant, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        if (string.IsNullOrEmpty(objectTypeId) || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(attribute) || string.IsNullOrEmpty(fileId))
            return SparkDenial.RefuseJson(httpContext);

        var po = await readPath.LoadAsync(new PersistentObjectReferenceRequest { ObjectTypeId = objectTypeId, Id = id }, cancellationToken);
        // An attribute the caller may not read was removed from the object; inside it, only a file it holds is served.
        var file = po is null ? null : HeldValues(po, attribute).Select(value => value switch
        {
            SparkFile single when single.FileId == fileId => single,
            IEnumerable<SparkFile> list => list.FirstOrDefault(f => f?.FileId == fileId),
            _ => null,
        }).FirstOrDefault(f => f is not null);
        if (po?.Id is not { Length: > 0 } entityId || file is null || !IsServed(file, variant))
            return SparkDenial.RefuseJson(httpContext);

        var served = Describe(entityId, file, variant);
        var url = fileStore.CreateReadUrl(served.Key, new SparkFileReadRequest(served.ContentType, served.Disposition, clock.GetUtcNow() + options.Value.DownloadUrlLifetime));
        if (url is not null)
        {
            httpContext.Response.Headers.CacheControl = SparkFileServing.CacheControl;
            return TypedResults.Redirect(url.ToString());
        }

        var stream = await fileStore.OpenReadAsync(served.Key, cancellationToken);
        return stream is null ? SparkDenial.RefuseJson(httpContext) : await ServeAsync(httpContext, stream, served, cancellationToken);
    }

    /// <summary><c>GET /spark/files/content/{ticket}</c>: the object a valid, unexpired ticket names. Anything else is 404.</summary>
    public async Task<IResult> ContentAsync(string? ticket, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        if (tickets.Open(ticket) is not { } served)
            return TypedResults.NotFound();
        var stream = await fileStore.OpenReadAsync(served.Key, cancellationToken);
        return stream is null ? TypedResults.NotFound() : await ServeAsync(httpContext, stream, served, cancellationToken);
    }

    /// <summary>
    /// The URL a read response embeds for a file or variant the caller was shown, valid for
    /// <see cref="SparkFilesOptions.EmbeddedUrlLifetime"/>: the store's presigned URL, or a content ticket.
    /// </summary>
    public string? EmbeddedUrl(string entityId, SparkFile file, string? variant)
    {
        if (!IsServed(file, variant))
            return null;
        var served = Describe(entityId, file, variant);
        var expires = clock.GetUtcNow() + options.Value.EmbeddedUrlLifetime;
        return fileStore.CreateReadUrl(served.Key, new SparkFileReadRequest(served.ContentType, served.Disposition, expires))?.ToString()
            ?? ContentBase() + ContentPath + tickets.Issue(served, expires);
    }

    /// <summary>The checked download URL of a file in a top-level attribute: it never expires, and asks again every time.</summary>
    public string DownloadUrl(SparkFileLocation location)
        => SparkFileDownloadUrl.For(location, httpContextAccessor.HttpContext?.Request.PathBase.Value);

    /// <summary>
    /// The values <paramref name="attribute"/> names on the object as the read path returned it: a top-level attribute's value,
    /// or (#700 M10) for <c>Parent.Field</c>, that attribute's value in every row of the AsDetail <c>Parent</c>. Whatever the
    /// read path removed (an attribute, a row type's attribute, the AsDetail itself) is not there to be served.
    /// </summary>
    private static IEnumerable<object?> HeldValues(PersistentObject po, string attribute)
    {
        var parts = attribute.Split('.');
        if (parts.Length == 1)
            return po.Attributes
                .Where(a => a is not PersistentObjectAttributeAsDetail && string.Equals(a.Name, attribute, StringComparison.Ordinal))
                .Select(a => a.Value);
        if (parts.Length != 2)
            return [];

        var parent = po.Attributes.OfType<PersistentObjectAttributeAsDetail>().FirstOrDefault(a => string.Equals(a.Name, parts[0], StringComparison.Ordinal));
        if (parent is null)
            return [];
        var rows = parent.Objects ?? (parent.Object is { } single ? [single] : []);
        return rows
            .Where(row => row is not null)
            .SelectMany(row => row!.Attributes)
            .Where(a => a is not PersistentObjectAttributeAsDetail && string.Equals(a.Name, parts[1], StringComparison.Ordinal))
            .Select(a => a.Value);
    }

    /// <summary>Only a clean file is served (fail closed), and only a variant the image has.</summary>
    internal static bool IsServed(SparkFile file, string? variant)
        => file.ScanStatus == SparkFileScanStatus.Clean
            && !string.IsNullOrEmpty(file.FileId)
            && (variant is null || (file is SparkImage image && image.Variants.TryGetValue(variant, out var v) && !string.IsNullOrEmpty(v?.FileId)));

    private ServedFile Describe(string entityId, SparkFile file, string? variant)
    {
        var inline = SparkFileServing.IsInline(file, variant);
        return new ServedFile(
            fileStore.EntityKey(entityId, SparkFileServing.VariantOrFileId(file, variant)),
            SparkFileServing.ContentType(file, variant),
            SparkFileServing.ContentDisposition(file.FileName, inline),
            SparkFileServing.ETag(file, variant));
    }

    private string ContentBase()
        => options.Value.ContentOrigin is { } origin
            ? origin.TrimEnd('/')
            : httpContextAccessor.HttpContext?.Request.PathBase.Value ?? string.Empty;

    /// <summary>
    /// The bytes with the safe headers: the sniffed type, <c>nosniff</c>, a sandboxing CSP, the decided disposition,
    /// <c>private, no-store</c>, and the ETag. Range requests are honoured; a forward-only stream is spooled first.
    /// </summary>
    private static async Task<IResult> ServeAsync(HttpContext httpContext, Stream stream, ServedFile served, CancellationToken cancellationToken)
    {
        var headers = httpContext.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = SparkFileServing.ContentSecurityPolicy;
        headers.ContentDisposition = served.Disposition;
        headers.CacheControl = SparkFileServing.CacheControl;

        if (!stream.CanSeek && httpContext.Request.Headers.Range.Count > 0)
        {
            var forwardOnly = stream;
            await using (forwardOnly)
                stream = await SeekableSpool.SpoolAsync(forwardOnly, cancellationToken);
        }
        return TypedResults.Stream(stream, served.ContentType, fileDownloadName: null, lastModified: null,
            entityTag: new EntityTagHeaderValue("\"" + served.ETag + "\""), enableRangeProcessing: stream.CanSeek);
    }
}

/// <summary>One object as it is served.</summary>
internal sealed record ServedFile(string Key, string ContentType, string Disposition, string ETag);

/// <summary>
/// The tickets of content URLs: a <see cref="ServedFile"/> protected (encrypted and authenticated) by the app's
/// data-protection keys, with its expiry inside. Nothing about a ticket can be changed or extended without the keys.
/// </summary>
[Register(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton)]
internal sealed partial class FileContentTickets
{
    private const string Purpose = "MintPlayer.Spark.Files.Content.v1";

    [Inject] private readonly IDataProtectionProvider dataProtection;

    private ITimeLimitedDataProtector Protector => dataProtection.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    public string Issue(ServedFile served, DateTimeOffset expires)
        => Protector.Protect(JsonSerializer.Serialize(served), expires);

    public ServedFile? Open(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 4096)
            return null;
        try
        {
            return JsonSerializer.Deserialize<ServedFile>(Protector.Unprotect(ticket));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }
}
