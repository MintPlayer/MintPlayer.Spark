using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Files.Services;

namespace MintPlayer.Spark.Files.Endpoints;

internal sealed class FilesGroup : IEndpointGroup
{
    public static string Prefix => "/spark/files";
}

internal static class FilesHttp
{
    public static IResult ToResult<T>(FilesResult<T> result, Func<T, IResult>? ok = null) => result.Outcome switch
    {
        FilesOutcome.Ok => ok is null ? TypedResults.Ok(result.Value) : ok(result.Value!),
        FilesOutcome.Invalid => TypedResults.BadRequest(new { error = result.Error }),
        FilesOutcome.Unauthorized => TypedResults.Unauthorized(),
        FilesOutcome.Forbidden => TypedResults.StatusCode(StatusCodes.Status403Forbidden),
        FilesOutcome.TooLarge => TypedResults.Json(new { error = result.Error }, statusCode: StatusCodes.Status413PayloadTooLarge),
        FilesOutcome.TooManyRequests => TypedResults.Json(new { error = result.Error }, statusCode: StatusCodes.Status429TooManyRequests),
        _ => TypedResults.NotFound(),
    };

    public static void RequireAntiforgery(RouteHandlerBuilder builder) => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
}

/// <summary><c>POST /spark/files/prepare</c>: one upload slot per file, for an attribute the caller may set.</summary>
[MemberOf<FilesGroup>]
internal sealed partial class PrepareFileUploads : IPostEndpoint<PrepareFilesInput>
{
    public static string Path => "/prepare";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => FilesHttp.RequireAntiforgery(builder);

    [Inject] private readonly FileUploadService uploads;

    public override async Task<IResult> HandleAsync(PrepareFilesInput request, CancellationToken cancellationToken)
        => FilesHttp.ToResult(await uploads.PrepareAsync(request, cancellationToken));
}

/// <summary>
/// <c>POST /spark/files/upload</c>: the bytes of a prepared slot, for a store without direct uploads. Multipart, read
/// with <see cref="MultipartReader"/> so nothing is buffered: the <c>token</c> field first, then the <c>file</c>.
/// </summary>
[MemberOf<FilesGroup>]
internal sealed partial class UploadFile : IEndpoint
{
    /// <summary>Room for the multipart framing and the token field around the file.</summary>
    internal const long FramingSlack = 64 * 1024;

    public static string Path => "/upload";

    public static IEnumerable<string> Methods => ["POST"];

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        FilesHttp.RequireAntiforgery(builder);
        // Kestrel refuses a larger body before the handler reads it; the slot's own cap cuts in while streaming.
        var max = services.GetRequiredService<IOptions<SparkFilesOptions>>().Value.MaxFileBytes;
        builder.WithMetadata(new RequestSizeLimitAttribute(max + FramingSlack));
    }

    [Inject] private readonly FileUploadService uploads;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var request = httpContext.Request;
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value is not { Length: > 0 and <= 200 } boundary)
            return TypedResults.BadRequest(new { error = "Send the file as multipart/form-data." });

        var reader = new MultipartReader(boundary, request.Body);
        string? token = null;
        try
        {
            while (await reader.ReadNextSectionAsync(httpContext.RequestAborted) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) || !disposition.DispositionType.Equals("form-data"))
                    return TypedResults.BadRequest(new { error = "Malformed multipart body." });

                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                if (disposition.FileName.HasValue || disposition.FileNameStar.HasValue)
                {
                    if (name != "file")
                        return TypedResults.BadRequest(new { error = "The file part must be named 'file'." });
                    return FilesHttp.ToResult(await uploads.UploadAsync(token, section.Body, httpContext.RequestAborted), _ => TypedResults.NoContent());
                }

                if (name == "token")
                {
                    using var text = new StreamReader(new LengthLimitedStream(section.Body, 128));
                    token = (await text.ReadToEndAsync(httpContext.RequestAborted)).Trim();
                }
            }
        }
        catch (InvalidDataException)
        {
            return TypedResults.BadRequest(new { error = "Malformed multipart body." });
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TypedResults.Json(new { error = "The file is too large." }, statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        return TypedResults.BadRequest(new { error = "No file part." });
    }
}

/// <summary>
/// <c>GET /spark/files/download?type=&amp;id=&amp;attribute=&amp;file=[&amp;variant=]</c>: the object's read path decides, then a
/// redirect to a short-lived presigned URL or the proxied bytes (D14). A read with no side effect, so no antiforgery token.
/// </summary>
[MemberOf<FilesGroup>]
internal sealed partial class DownloadFile : IEndpoint
{
    public static string Path => "/download";

    public static IEnumerable<string> Methods => ["GET"];

    [Inject] private readonly FileDownloadService downloads;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var query = httpContext.Request.Query;
        return downloads.DownloadAsync(query["type"], query["id"], query["attribute"], query["file"], query["variant"], httpContext.RequestAborted);
    }
}

/// <summary>
/// <c>GET /spark/files/content/{ticket}</c>: the bytes a content ticket names, until it expires (D15). The ticket is the
/// credential, as a presigned URL is, so no sign-in is asked for: a cookie-less <c>ContentOrigin</c> can serve it.
/// </summary>
[MemberOf<FilesGroup>]
internal sealed partial class FileContent : IEndpoint
{
    public static string Path => "/content/{ticket}";

    public static IEnumerable<string> Methods => ["GET"];

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.AllowAnonymous();

    [Inject] private readonly FileDownloadService downloads;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => downloads.ContentAsync(httpContext.Request.RouteValues["ticket"] as string, httpContext.RequestAborted);
}

/// <summary><c>POST /spark/files/commit</c>: checks the uploaded bytes of each token; a clean, accepted file becomes claimable.</summary>
[MemberOf<FilesGroup>]
internal sealed partial class CommitFileUploads : IPostEndpoint<CommitFilesInput>
{
    public static string Path => "/commit";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => FilesHttp.RequireAntiforgery(builder);

    [Inject] private readonly FileUploadService uploads;

    public override async Task<IResult> HandleAsync(CommitFilesInput request, CancellationToken cancellationToken)
        => FilesHttp.ToResult(await uploads.CommitAsync(request, cancellationToken));
}
