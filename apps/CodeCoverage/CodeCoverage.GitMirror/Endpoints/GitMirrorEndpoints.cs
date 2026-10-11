using System.Text.Json;
using CodeCoverage.GitMirror.Git;
using CodeCoverage.GitMirror.Graph;
using CodeCoverage.GitMirror.Mirror;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.GitMirror.Endpoints;

// The sidecar's internal API (the coverage-git contract). Reachable only on the coverage-git network (D4, D24); every
// route parameter and query value is validated before it reaches a path or a git argument, and a refusal is a bare 400.

/// <summary><c>GET /healthz</c>: always 200 <c>ok</c> (the compose healthcheck).</summary>
internal sealed class GitMirrorHealthEndpoint : IGetEndpoint
{
    public static string Path => "/healthz";

    public Task<IResult> HandleAsync(HttpContext httpContext) => Task.FromResult(Results.Text("ok"));
}

/// <summary><c>GET /stats</c>: the volume's numbers and the mirrors' (HostMetrics, §9).</summary>
internal sealed partial class GitMirrorStatsEndpoint : IGetEndpoint
{
    public static string Path => "/stats";

    [Inject] private readonly DiskGuard disk;
    [Inject] private readonly MirrorPaths paths;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var mirrors = paths.AllMirrors().ToList();
        return Task.FromResult(Results.Json(new
        {
            freeBytes = disk.FreeBytes,
            totalBytes = disk.TotalBytes,
            mirrorCount = mirrors.Count,
            mirrorBytes = mirrors.Sum(MirrorPaths.SizeOf),
        }, GitMirrorHost.Json));
    }
}

/// <summary><c>POST /repos/{provider}/{repoId}/fetch</c>: clone or fetch, joining one in flight (D19).</summary>
internal sealed partial class GitMirrorFetchEndpoint : IPostEndpoint<FetchRequestWire>
{
    public static string Path => "/repos/{provider}/{repoId}/fetch";

    private const int MaxBodyBytes = 64 * 1024;

    [RouteParam] public string Provider { get; set; } = "";
    [RouteParam] public string RepoId { get; set; } = "";

    [Inject] private readonly MirrorService mirrors;
    [Inject] private readonly IOptions<GitMirrorOptions> options;

    // The body is read under MaxBodyBytes; a refusal (too large, malformed, JSON null) is a bare 400.
    protected override async ValueTask<FetchRequestWire?> BindRequestAsync(HttpContext context)
        => await ReadBodyAsync(context.Request.Body, context.Request.ContentLength, context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(Results.BadRequest());

    public override async Task<IResult> HandleAsync(FetchRequestWire request, CancellationToken cancellationToken)
    {
        if (RepoKey.TryCreate(Provider, RepoId) is not { } key)
            return Results.BadRequest();
        if (CloneUrl(request.CloneUrl, options.Value.AllowInsecureHttp) is not { } url)
            return Results.BadRequest();
        GitCredential? credential = null;
        if (request.Credential is { } wire)
        {
            if (!IsCredentialPart(wire.Username) || !IsCredentialPart(wire.Password) || wire.Username!.Contains(':'))
                return Results.BadRequest();
            credential = new GitCredential(wire.Username!, wire.Password!);
        }

        // Not tied to the request: a caller that gives up (its HttpClient timeout) must not abort a clone another caller
        // may join. The quota's timeout bounds the operation.
        var response = await mirrors.FetchAsync(key, url, credential, FetchQuota.From(request.Quota));
        return Results.Json(response, GitMirrorHost.Json);
    }

    /// <summary>An absolute https URL with a host and no user info (a credential in the URL would be persisted in the
    /// mirror's config). The scheme check is explicit: on Linux <c>Uri.TryCreate("/x", Absolute)</c> succeeds as
    /// <c>file:///x</c>.</summary>
    internal static Uri? CloneUrl(string? value, bool allowInsecureHttp)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var url))
            return null;
        var schemeOk = url.Scheme == Uri.UriSchemeHttps || (allowInsecureHttp && url.Scheme == Uri.UriSchemeHttp);
        if (!schemeOk || string.IsNullOrEmpty(url.Host) || url.UserInfo.Length > 0 || url.Fragment.Length > 0)
            return null;
        return url;
    }

    private static bool IsCredentialPart(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= 4096 && !value.Any(char.IsControl);

    private static async Task<FetchRequestWire?> ReadBodyAsync(Stream body, long? contentLength, CancellationToken cancellationToken)
    {
        if (contentLength > MaxBodyBytes)
            return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        try
        {
            return JsonSerializer.Deserialize<FetchRequestWire>(buffer.ToArray(), GitMirrorHost.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary><c>DELETE /repos/{provider}/{repoId}</c>: 204 always (idempotent); removes the mirror and its snapshots.</summary>
internal sealed partial class GitMirrorDeleteEndpoint : IDeleteEndpoint
{
    public static string Path => "/repos/{provider}/{repoId}";

    [RouteParam] public string Provider { get; set; } = "";
    [RouteParam] public string RepoId { get; set; } = "";

    [Inject] private readonly MirrorService mirrors;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (RepoKey.TryCreate(Provider, RepoId) is not { } key)
            return Results.BadRequest();
        await mirrors.DeleteAsync(key);
        return Results.NoContent();
    }
}

/// <summary><c>GET /repos/{provider}/{repoId}/graph</c>: one page of the lazily laid-out graph (PRD §4.3, D21, D27).</summary>
internal sealed partial class GitMirrorGraphEndpoint : IGetEndpoint
{
    public static string Path => "/repos/{provider}/{repoId}/graph";

    [RouteParam] public string Provider { get; set; } = "";
    [RouteParam] public string RepoId { get; set; } = "";

    // Bound as strings so that every refusal is the bare 400 below, never the binder's problem details.
    [QueryParam] public string? From { get; set; }
    [QueryParam] public string? Count { get; set; }
    [QueryParam("ref")] public string? Ref { get; set; }
    [QueryParam] public string? At { get; set; }
    [QueryParam] public string? Snapshot { get; set; }
    [QueryParam] public string? FirstParent { get; set; }
    [QueryParam] public string? Grep { get; set; }
    [QueryParam] public string? Author { get; set; }

    [Inject] private readonly GraphService graph;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (RepoKey.TryCreate(Provider, RepoId) is not { } key || Parse() is not { } query)
            return Results.BadRequest();
        return await graph.GetPageAsync(key, query, httpContext.RequestAborted) switch
        {
            GraphOutcome.Missing => Results.Json(new { state = "missing" }, GitMirrorHost.Json, statusCode: StatusCodes.Status404NotFound),
            GraphOutcome.NotFound => Results.Json(new { state = "not-found" }, GitMirrorHost.Json, statusCode: StatusCodes.Status404NotFound),
            GraphOutcome.Stale stale => Results.Json(new { state = "stale", snapshotId = stale.SnapshotId }, GitMirrorHost.Json, statusCode: StatusCodes.Status410Gone),
            GraphOutcome.Page page => Results.Json(page.Value, GitMirrorHost.Json),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    /// <summary>The query, or null when any value is out of range.</summary>
    private GraphQuery? Parse()
    {
        var from = 0;
        if (From is not null && (!int.TryParse(From, out from) || from < 0))
            return null;
        var count = 100;
        if (Count is not null && (!int.TryParse(Count, out count) || count < 1 || count > GraphService.MaxCount))
            return null;

        string? reference = Value(Ref);
        if (reference is not null && !GraphFilter.IsValidRef(reference))
            return null;

        string? at = Value(At)?.ToLowerInvariant();
        if (at is not null && !GraphService.IsShaPrefix(at))
            return null;

        string? snapshot = Value(Snapshot);
        if (snapshot is not null && (snapshot.Length > 64 || !snapshot.All(char.IsAsciiHexDigitLower)))
            return null;

        var firstParent = false;
        if (Value(FirstParent) is { } fp && !bool.TryParse(fp, out firstParent))
            return null;

        if (!Search(Grep, out var grep) || !Search(Author, out var author))
            return null;

        return new GraphQuery(from, count, new GraphFilter(reference, firstParent, grep, author), at, snapshot);
    }

    private static string? Value(string? bound) => string.IsNullOrEmpty(bound) ? null : bound;

    private static bool Search(string? bound, out string? value)
    {
        value = Value(bound);
        return value is null || (value.Length <= GraphFilter.MaxSearchLength && !value.Any(char.IsControl));
    }
}
