using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.AspNetCore.SpaServices.Xsrf;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.ReferenceData.Contracts;
using MintPlayer.Spark.ReferenceData.Search;
using Raven.Client.Documents;

namespace MintPlayer.Spark.ReferenceData.Api;

/// <summary>
/// <c>/api/v1</c>, the public reference-data API (PRD §4.11, RD-DEF 3): outside <c>/spark</c>, anonymous by design and
/// never a 401 (ng-spark's auth interceptor would send a consumer's user to its sign-in page); a named any-origin CORS
/// policy on this group only (GET and HEAD, no credentials); no XSRF cookie (it would force <c>Cache-Control: private</c>);
/// the named token-bucket rate-limit policy, partitioned per IPv4 address or IPv6 /64. Not <c>ShortCircuit()</c>,
/// which would skip the rate limiter too.
/// </summary>
internal class RefDataApiGroup : IEndpointGroup
{
    public static string Prefix => "/api/v1";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services)
    {
        group.AllowAnonymous();
        group.RequireCors(SparkReferenceDataExtensions.CorsPolicy);
        group.RequireRateLimiting(SparkReferenceDataExtensions.RateLimitPolicy);
        group.WithMetadata(new SkipXsrfTokenAttribute());
    }
}

/// <summary>Shared request handling: query parsing, the 400 envelope, the ETag (304 without a query) and cache headers.</summary>
internal static class RefApi
{
    public static DateOnly AsOf(string? asOf, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(asOf)) return today;
        return DateOnly.TryParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : throw new RefQueryException("asOf must be yyyy-MM-dd.");
    }

    public static string? Country(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return null;
        return RefIds.IsCountryCode(country) ? country.ToLowerInvariant() : throw new RefQueryException("country must be an ISO 3166-1 alpha-2 code.");
    }

    public static string? Id(string? id, string collection)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return RefIds.IsValid(id, collection) ? id : throw new RefQueryException($"'{id}' is not a {collection} id.");
    }

    public static string? Lang(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return null;
        return MintPlayer.Spark.Abstractions.LanguageTag.IsWellFormed(lang) && lang.Length <= 35 ? lang : throw new RefQueryException("lang must be an RFC 5646 tag.");
    }

    /// <summary>
    /// Answers with <paramref name="produce"/>'s JSON, or a 304 when <c>If-None-Match</c> carries this request's ETag
    /// (<c>W/"{release}:{sha256(normalized request, effective asOf, API version)}"</c>), which needs no query: the release
    /// is in memory. A <see cref="RefQueryException"/> is a 400 with an <see cref="ApiProblem"/> body.
    /// </summary>
    public static async Task<IResult> CachedAsync<T>(HttpContext http, RefDataCurrentRelease currentRelease, string effectiveAsOf, TimeSpan maxAge, Func<CancellationToken, Task<T>> produce)
    {
        var ct = http.RequestAborted;
        var release = await currentRelease.GetAsync(ct);
        var normalized = new StringBuilder("v1|").Append(http.Request.Path.Value).Append('|').Append(effectiveAsOf);
        foreach (var pair in http.Request.Query.OrderBy(q => q.Key, StringComparer.OrdinalIgnoreCase))
            normalized.Append('|').Append(pair.Key.ToLowerInvariant()).Append('=').Append(pair.Value.ToString());
        var etag = $"W/\"{release}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToString())))[..32]}\"";

        http.Response.Headers.CacheControl = $"public, max-age={(int)maxAge.TotalSeconds}";
        http.Response.Headers.ETag = etag;
        if (http.Request.Headers.IfNoneMatch.Any(v => v is not null && v.Split(',').Any(t => t.Trim() == etag)))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        return Results.Json(await produce(ct));
    }

    public static IResult Problem(RefQueryException ex)
        => Results.Json(new ApiProblem("bad-request", ex.Message), statusCode: StatusCodes.Status400BadRequest);
}

/// <summary><c>GET /api/v1/suggest?q=kruish&amp;country=be&amp;kinds=&amp;within=&amp;asOf=&amp;lang=nl&amp;limit=10</c>.</summary>
[MemberOf<RefDataApiGroup>]
internal sealed partial class SuggestEndpoint : IGetEndpoint
{
    public static string Path => "/suggest";

    [QueryParam] public string? Q { get; set; }
    [QueryParam] public string? Country { get; set; }
    [QueryParam] public string? Kinds { get; set; }
    [QueryParam] public string? Within { get; set; }
    [QueryParam] public string? AsOf { get; set; }
    [QueryParam] public string? Lang { get; set; }
    [QueryParam] public string? Limit { get; set; }

    [Inject] private readonly RefApiService api;
    [Inject] private readonly RefTypeahead typeahead;
    [Inject] private readonly RefDataCurrentRelease release;
    [Inject] private readonly IOptions<SparkReferenceDataOptions> options;
    [Inject] private readonly IDocumentStore store;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        try
        {
            var asOf = RefApi.AsOf(AsOf, api.Today);
            var country = RefApi.Country(Country);
            var kinds = (Kinds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (kinds.Count > 0)
                await ValidateKinds(country, kinds, httpContext.RequestAborted);
            var query = new SuggestQuery(Q ?? string.Empty, country, kinds, RefApi.Id(Within, RefIds.Places), asOf, RefApi.Lang(Lang),
                typeahead.Limit(int.TryParse(Limit, NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? l : null));
            typeahead.Fold(query.Q);
            return await RefApi.CachedAsync(httpContext, release, RefApiService.Date(asOf), options.Value.Api.CacheMaxAge,
                ct => api.SuggestAsync(query, ct));
        }
        catch (RefQueryException ex)
        {
            return RefApi.Problem(ex);
        }
    }

    /// <summary><c>kinds</c> must come from the country profile's levels (an allow-list, never spliced).</summary>
    private async Task ValidateKinds(string? country, List<string> kinds, CancellationToken cancellationToken)
    {
        if (country is null)
            throw new RefQueryException("kinds needs country.");
        using var session = store.OpenAsyncSession();
        var profile = (await session.LoadAsync<RefCountry>(RefIds.CountryId(country), cancellationToken))?.Profile;
        var allowed = (profile?.Levels.Select(l => l.Key) ?? []).Append("postal-locality").ToHashSet(StringComparer.Ordinal);
        if (kinds.FirstOrDefault(k => !allowed.Contains(k)) is { } unknown)
            throw new RefQueryException($"'{unknown}' is not a level of {country}.");
    }
}

/// <summary><c>GET /api/v1/streets/suggest?q=kerk&amp;country=be&amp;municipality=places/be/nis/45068&amp;postalCode=9770</c>.</summary>
[MemberOf<RefDataApiGroup>]
internal sealed partial class StreetSuggestEndpoint : IGetEndpoint
{
    public static string Path => "/streets/suggest";

    [QueryParam] public string? Q { get; set; }
    [QueryParam] public string? Country { get; set; }
    [QueryParam] public string? Municipality { get; set; }
    [QueryParam] public string? PostalCode { get; set; }
    [QueryParam] public string? AsOf { get; set; }
    [QueryParam] public string? Lang { get; set; }
    [QueryParam] public string? Limit { get; set; }

    [Inject] private readonly RefApiService api;
    [Inject] private readonly RefTypeahead typeahead;
    [Inject] private readonly RefDataCurrentRelease release;
    [Inject] private readonly IOptions<SparkReferenceDataOptions> options;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        try
        {
            var asOf = RefApi.AsOf(AsOf, api.Today);
            var country = RefApi.Country(Country);
            var postalCode = string.IsNullOrWhiteSpace(PostalCode) ? null : PostalCode.Trim();
            if (postalCode is not null && (postalCode.Length > 16 || !postalCode.All(char.IsAsciiLetterOrDigit)))
                throw new RefQueryException("postalCode must be letters and digits.");
            var query = new StreetQuery(Q ?? string.Empty, country, RefApi.Id(Municipality, RefIds.Places), postalCode, asOf, RefApi.Lang(Lang),
                typeahead.Limit(int.TryParse(Limit, NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? l : null));
            typeahead.Fold(query.Q);
            if (query.Municipality is null && query.PostalCode is null)
                throw new RefQueryException("A street query needs municipality or postalCode.");
            return await RefApi.CachedAsync(httpContext, release, RefApiService.Date(asOf), options.Value.Api.CacheMaxAge,
                ct => api.StreetsAsync(query, ct));
        }
        catch (RefQueryException ex)
        {
            return RefApi.Problem(ex);
        }
    }
}

/// <summary><c>GET /api/v1/places/{id}?asOf=&amp;lang=</c>: the id's path after <c>places/</c>, e.g. <c>/api/v1/places/be/nis/45068</c>.</summary>
[MemberOf<RefDataApiGroup>]
internal sealed partial class PlaceEndpoint : IGetEndpoint
{
    public static string Path => "/places/{**placePath}";

    [RouteParam] public string PlacePath { get; set; } = string.Empty;
    [QueryParam] public string? AsOf { get; set; }
    [QueryParam] public string? Lang { get; set; }

    [Inject] private readonly RefApiService api;
    [Inject] private readonly RefDataCurrentRelease release;
    [Inject] private readonly IOptions<SparkReferenceDataOptions> options;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        try
        {
            var id = RefApi.Id(RefIds.Places + "/" + PlacePath, RefIds.Places)!;
            var asOf = RefApi.AsOf(AsOf, api.Today);
            var lang = RefApi.Lang(Lang);
            return await RefApi.CachedAsync(httpContext, release, RefApiService.Date(asOf), options.Value.Api.CacheMaxAge,
                ct => api.PlaceAsync(id, asOf, lang, ct));
        }
        catch (RefQueryException ex)
        {
            return RefApi.Problem(ex);
        }
    }
}

/// <summary><c>GET /api/v1/resolve?ids=a,b,c&amp;asOf=&amp;lang=</c> (at most 50 ids).</summary>
[MemberOf<RefDataApiGroup>]
internal sealed partial class ResolveEndpoint : IGetEndpoint
{
    public static string Path => "/resolve";

    [QueryParam] public string? Ids { get; set; }
    [QueryParam] public string? AsOf { get; set; }
    [QueryParam] public string? Lang { get; set; }

    [Inject] private readonly RefApiService api;
    [Inject] private readonly RefDataCurrentRelease release;
    [Inject] private readonly IOptions<SparkReferenceDataOptions> options;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        try
        {
            var ids = (Ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(id => RefApi.Id(id, RefIds.Places)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (ids.Count is 0 or > 50)
                throw new RefQueryException("ids must name 1 to 50 places.");
            var asOf = RefApi.AsOf(AsOf, api.Today);
            var lang = RefApi.Lang(Lang);
            return await RefApi.CachedAsync(httpContext, release, RefApiService.Date(asOf), options.Value.Api.CacheMaxAge,
                ct => api.ResolveAsync(ids, asOf, lang, ct));
        }
        catch (RefQueryException ex)
        {
            return RefApi.Problem(ex);
        }
    }
}

/// <summary><c>GET /api/v1/by-code/{cc}/{scheme}/{code}?asOf=</c>: consumers never compute ids from codes (§4.3).</summary>
[MemberOf<RefDataApiGroup>]
internal sealed partial class ByCodeEndpoint : IGetEndpoint
{
    public static string Path => "/by-code/{cc}/{scheme}/{code}";

    [RouteParam] public string Cc { get; set; } = string.Empty;
    [RouteParam] public string Scheme { get; set; } = string.Empty;
    [RouteParam] public string Code { get; set; } = string.Empty;
    [QueryParam] public string? AsOf { get; set; }
    [QueryParam] public string? Lang { get; set; }

    [Inject] private readonly RefApiService api;
    [Inject] private readonly RefDataCurrentRelease release;
    [Inject] private readonly IOptions<SparkReferenceDataOptions> options;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        try
        {
            var country = RefApi.Country(Cc) ?? throw new RefQueryException("cc is required.");
            if (Scheme.Length is 0 or > 32 || Code.Length is 0 or > 64 || !(Scheme + Code).All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_'))
                throw new RefQueryException("scheme and code must be letters, digits, '-', '.' or '_'.");
            var asOf = RefApi.AsOf(AsOf, api.Today);
            var lang = RefApi.Lang(Lang);
            return await RefApi.CachedAsync(httpContext, release, RefApiService.Date(asOf), options.Value.Api.CacheMaxAge,
                ct => api.ByCodeAsync(country, Scheme.ToLowerInvariant(), Code, asOf, lang, ct));
        }
        catch (RefQueryException ex)
        {
            return RefApi.Problem(ex);
        }
    }
}

/// <summary><c>GET /api/v1/postalcodes/{cc}/{code}</c>.</summary>
[MemberOf<RefDataApiGroup>]
internal sealed partial class PostalCodeEndpoint : IGetEndpoint
{
    public static string Path => "/postalcodes/{cc}/{code}";

    [RouteParam] public string Cc { get; set; } = string.Empty;
    [RouteParam] public string Code { get; set; } = string.Empty;
    [QueryParam] public string? Lang { get; set; }

    [Inject] private readonly RefApiService api;
    [Inject] private readonly RefDataCurrentRelease release;
    [Inject] private readonly IOptions<SparkReferenceDataOptions> options;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        try
        {
            var country = RefApi.Country(Cc) ?? throw new RefQueryException("cc is required.");
            if (Code.Length is 0 or > 16 || !Code.All(char.IsAsciiLetterOrDigit))
                throw new RefQueryException("code must be letters and digits.");
            var lang = RefApi.Lang(Lang);
            return await RefApi.CachedAsync(httpContext, release, RefApiService.Date(api.Today), options.Value.Api.CacheMaxAge,
                ct => api.PostalCodeAsync(country, Code, lang, ct));
        }
        catch (RefQueryException ex)
        {
            return RefApi.Problem(ex);
        }
    }
}

/// <summary><c>GET /api/v1/countries?lang=</c>.</summary>
[MemberOf<RefDataApiGroup>]
internal sealed partial class CountriesEndpoint : IGetEndpoint
{
    public static string Path => "/countries";

    [QueryParam] public string? Lang { get; set; }

    [Inject] private readonly RefApiService api;
    [Inject] private readonly RefDataCurrentRelease release;
    [Inject] private readonly IOptions<SparkReferenceDataOptions> options;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        try
        {
            var lang = RefApi.Lang(Lang);
            return await RefApi.CachedAsync(httpContext, release, string.Empty, options.Value.Api.CacheMaxAge, ct => api.CountriesAsync(lang, ct));
        }
        catch (RefQueryException ex)
        {
            return RefApi.Problem(ex);
        }
    }
}

/// <summary><c>GET /api/v1/countries/{cc}/profile?lang=</c>; an unknown country is an empty 200 body of <c>null</c>.</summary>
[MemberOf<RefDataApiGroup>]
internal sealed partial class CountryProfileEndpoint : IGetEndpoint
{
    public static string Path => "/countries/{cc}/profile";

    [RouteParam] public string Cc { get; set; } = string.Empty;
    [QueryParam] public string? Lang { get; set; }

    [Inject] private readonly RefApiService api;
    [Inject] private readonly RefDataCurrentRelease release;
    [Inject] private readonly IOptions<SparkReferenceDataOptions> options;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        try
        {
            var country = RefApi.Country(Cc) ?? throw new RefQueryException("cc is required.");
            var lang = RefApi.Lang(Lang);
            return await RefApi.CachedAsync(httpContext, release, string.Empty, options.Value.Api.CacheMaxAge, ct => api.ProfileAsync(country, lang, ct));
        }
        catch (RefQueryException ex)
        {
            return RefApi.Problem(ex);
        }
    }
}

/// <summary><c>GET /api/v1/timezones?lang=</c>: CLDR exemplar cities (§4.13), the D36 provider's source.</summary>
[MemberOf<RefDataApiGroup>]
internal sealed partial class TimeZonesEndpoint : IGetEndpoint
{
    public static string Path => "/timezones";

    [QueryParam] public string? Lang { get; set; }

    [Inject] private readonly RefApiService api;
    [Inject] private readonly RefDataCurrentRelease release;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        try
        {
            var lang = RefApi.Lang(Lang) ?? "en";
            return await RefApi.CachedAsync(httpContext, release, string.Empty, TimeSpan.FromHours(1), ct => api.TimeZonesAsync(lang, ct));
        }
        catch (RefQueryException ex)
        {
            return RefApi.Problem(ex);
        }
    }
}

/// <summary><c>GET /api/v1/sources</c>: attribution and terms (§4.12), the service's self-description.</summary>
[MemberOf<RefDataApiGroup>]
internal sealed partial class SourcesEndpoint : IGetEndpoint
{
    public static string Path => "/sources";

    [Inject] private readonly RefApiService api;
    [Inject] private readonly RefDataCurrentRelease release;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => RefApi.CachedAsync(httpContext, release, string.Empty, TimeSpan.FromHours(1), ct => api.SourcesAsync(ct));
}

/// <summary><c>GET /api/v1/releases/latest</c>: the latest release's manifest (consumers' conditional GET, RD6).</summary>
[MemberOf<RefDataApiGroup>]
internal sealed partial class LatestReleaseEndpoint : IGetEndpoint
{
    public static string Path => "/releases/latest";

    [Inject] private readonly RefDataCurrentRelease release;
    [Inject] private readonly IOptions<SparkReferenceDataOptions> options;
    [Inject] private readonly IHostEnvironment environment;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var current = await release.GetAsync(httpContext.RequestAborted);
        var file = ReleaseFiles.Resolve(environment, options.Value, current, MintPlayer.Spark.ReferenceData.Releases.ReleaseFormat.ManifestFile);
        if (file is null || !File.Exists(file))
            return Results.NotFound();
        httpContext.Response.Headers.CacheControl = "public, max-age=60";
        return Results.File(file, "application/json", enableRangeProcessing: false,
            entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{current}\""));
    }
}

/// <summary><c>GET /api/v1/releases/{release}/{**file}</c>: release files, immutable.</summary>
[MemberOf<RefDataApiGroup>]
internal sealed partial class ReleaseFileEndpoint : IGetEndpoint
{
    public static string Path => "/releases/{release}/{**file}";

    [RouteParam] public string Release { get; set; } = string.Empty;
    [RouteParam] public string File { get; set; } = string.Empty;

    [Inject] private readonly IOptions<SparkReferenceDataOptions> options;
    [Inject] private readonly IHostEnvironment environment;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var path = ReleaseFiles.Resolve(environment, options.Value, Release, File);
        if (path is null || !System.IO.File.Exists(path))
            return Task.FromResult(Results.NotFound());
        httpContext.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        var type = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json" : "application/x-ndjson";
        return Task.FromResult(Results.File(path, type));
    }
}

/// <summary>Maps a release and a relative file onto the release store, refusing anything that leaves it.</summary>
internal static class ReleaseFiles
{
    public static string? Resolve(IHostEnvironment environment, SparkReferenceDataOptions options, string release, string file)
    {
        if (release is "none" || release.Length > 32 || !release.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            return null;
        if (file.Length is 0 or > 128 || file.Contains("..", StringComparison.Ordinal) || !file.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/'))
            return null;
        // The basic seed set is a release too (RD9): its files are the committed seed directory.
        var root = release == MintPlayer.Spark.ReferenceData.Seed.RefDataSeed.Release
            ? Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Seed.Directory))
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Releases.Directory, release));
        var full = Path.GetFullPath(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
