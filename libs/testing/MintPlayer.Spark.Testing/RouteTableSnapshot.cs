using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// A deterministic, text dump of a host's route table: every route endpoint's HTTP methods and
/// pattern, plus the endpoint metadata that decides who may call it and how.
/// </summary>
/// <remarks>
/// Built for refactors that must not change any route or its security metadata (the
/// Endpoints-generator completion, <c>docs/endpoints_generator_completion_plan.md</c> M0): a committed
/// fixture per host, compared line by line, so a lost route, a new one, or a dropped
/// <c>RequireAuthorization</c> / antiforgery flag shows up as a one-line diff.
/// <para>
/// ⚠️ What it records is deliberately narrow. Only metadata whose meaning survives a change of
/// <em>how</em> an endpoint is declared (a lambda becoming a class, a group moving): authorization
/// (<see cref="IAuthorizeData"/>, <see cref="AuthorizationPolicy"/>, <see cref="IAllowAnonymous"/>),
/// antiforgery, rate limiting, CORS, request size limits, response and output caching, and the
/// accepted content types. Display names, handler types, endpoint-type markers and order are
/// excluded on purpose: they legitimately change in such a refactor and would bury the real diff.
/// </para>
/// <para>
/// Where ASP.NET Core reads a metadata kind as "the last one wins" (antiforgery, CORS, request size,
/// caching) the snapshot records that <b>effective</b> value, so a group-level flag that an endpoint
/// overrides reads as what the middleware will actually do. Authorization combines every
/// <see cref="IAuthorizeData"/>, so all of them are recorded (distinct, sorted).
/// </para>
/// </remarks>
public static class RouteTableSnapshot
{
    /// <summary>Set to <c>1</c> to rewrite the fixtures from the current hosts instead of comparing.</summary>
    public const string UpdateVariable = "SPARK_UPDATE_ROUTE_SNAPSHOT";

    /// <summary>One line per route endpoint, sorted by pattern, then methods, then metadata.</summary>
    /// <param name="services">The started host's root services; the endpoints are read from its <see cref="EndpointDataSource"/>.</param>
    public static IReadOnlyList<string> Capture(IServiceProvider services)
    {
        var dataSource = services.GetRequiredService<EndpointDataSource>();

        return dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(Describe)
            .OrderBy(e => e.Pattern, StringComparer.Ordinal)
            .ThenBy(e => e.Methods, StringComparer.Ordinal)
            .ThenBy(e => e.Line, StringComparer.Ordinal)
            .Select(e => e.Line)
            .ToList();
    }

    /// <summary>
    /// Compares <paramref name="actual"/> with the committed fixture, or rewrites the fixture when
    /// <see cref="UpdateVariable"/> is <c>1</c>.
    /// </summary>
    /// <param name="actual">The output of <see cref="Capture"/>.</param>
    /// <param name="repositoryRelativePath">
    /// The fixture's path relative to the repository root (the directory holding <c>nx.json</c>),
    /// so the update writes the source file rather than a copy in <c>bin/</c>.
    /// </param>
    /// <exception cref="RouteTableSnapshotMismatchException">The host and the fixture differ.</exception>
    public static void AssertMatchesFixture(IReadOnlyList<string> actual, string repositoryRelativePath)
    {
        if (actual.Count == 0)
            throw new RouteTableSnapshotMismatchException(
                $"The host exposed no route endpoints at all; refusing to compare or write '{repositoryRelativePath}'. "
                + "An empty snapshot is the shape of a capture that read the wrong data source.");

        var path = Path.Combine(RepositoryRoot(), repositoryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var content = string.Join("\n", actual) + "\n";

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return;
        }

        if (!File.Exists(path))
            throw new RouteTableSnapshotMismatchException(
                $"No route snapshot at '{path}'. Set {UpdateVariable}=1 and run the test once to create it, then review and commit it.");

        var expected = File.ReadAllText(path).Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var missing = MultisetExcept(expected, actual);
        var unexpected = MultisetExcept(actual, expected);
        if (missing.Count == 0 && unexpected.Count == 0)
            return;

        var message = new System.Text.StringBuilder()
            .AppendLine($"The route table differs from '{repositoryRelativePath}'.")
            .AppendLine($"If the change is intended, set {UpdateVariable}=1, re-run, and review the fixture diff.");
        if (missing.Count > 0)
        {
            message.AppendLine($"- In the fixture, not in the host ({missing.Count}):");
            foreach (var line in missing) message.AppendLine("  - " + line);
        }
        if (unexpected.Count > 0)
        {
            message.AppendLine($"+ In the host, not in the fixture ({unexpected.Count}):");
            foreach (var line in unexpected) message.AppendLine("  + " + line);
        }

        throw new RouteTableSnapshotMismatchException(message.ToString());
    }

    private static List<string> MultisetExcept(IEnumerable<string> left, IEnumerable<string> right)
    {
        var remaining = right.GroupBy(l => l, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var line in left)
        {
            if (remaining.TryGetValue(line, out var count) && count > 0)
                remaining[line] = count - 1;
            else
                result.Add(line);
        }
        return result;
    }

    private static (string Pattern, string Methods, string Line) Describe(RouteEndpoint endpoint)
    {
        var metadata = endpoint.Metadata;
        var pattern = NormalizePattern(endpoint.RoutePattern);

        var methods = metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } verbs
            ? string.Join(",", verbs.Select(v => v.ToUpperInvariant()).Distinct().OrderBy(v => v, StringComparer.Ordinal))
            : "*";

        var facts = new SortedSet<string>(StringComparer.Ordinal);

        if (metadata.GetMetadata<IAllowAnonymous>() is not null)
            facts.Add("allow-anonymous");

        foreach (var data in metadata.GetOrderedMetadata<IAuthorizeData>())
            facts.Add($"authorize(policy={data.Policy ?? ""};roles={data.Roles ?? ""};schemes={data.AuthenticationSchemes ?? ""})");

        foreach (var policy in metadata.GetOrderedMetadata<AuthorizationPolicy>())
        {
            var requirements = string.Join(",", policy.Requirements.Select(r => r.ToString()).OrderBy(r => r, StringComparer.Ordinal));
            var schemes = string.Join(",", policy.AuthenticationSchemes.OrderBy(s => s, StringComparer.Ordinal));
            facts.Add($"authorize-policy(requirements={requirements};schemes={schemes})");
        }

        if (metadata.GetMetadata<IAntiforgeryMetadata>() is { } antiforgery)
            facts.Add(antiforgery.RequiresValidation ? "antiforgery=required" : "antiforgery=not-required");

        if (metadata.GetMetadata<DisableRateLimitingAttribute>() is not null)
            facts.Add("rate-limit=disabled");
        else if (metadata.GetMetadata<EnableRateLimitingAttribute>() is { } rateLimit)
            facts.Add($"rate-limit=policy:{rateLimit.PolicyName ?? "(inline)"}");

        switch (metadata.GetMetadata<ICorsMetadata>())
        {
            case IDisableCorsAttribute:
                facts.Add("cors=disabled");
                break;
            case IEnableCorsAttribute enable:
                facts.Add($"cors=policy:{enable.PolicyName ?? "(default)"}");
                break;
            case ICorsPolicyMetadata inline:
                facts.Add($"cors=inline(origins={string.Join(",", inline.Policy.Origins.Order(StringComparer.Ordinal))};credentials={inline.Policy.SupportsCredentials})");
                break;
            case { } other:
                facts.Add($"cors={other.GetType().Name}");
                break;
        }

        if (metadata.GetMetadata<IRequestSizeLimitMetadata>() is { } sizeLimit)
            facts.Add($"request-size-limit={sizeLimit.MaxRequestBodySize?.ToString() ?? "unlimited"}");

        if (metadata.GetMetadata<ResponseCacheAttribute>() is { } responseCache)
            facts.Add($"response-cache(duration={responseCache.Duration};location={responseCache.Location};no-store={responseCache.NoStore};profile={responseCache.CacheProfileName ?? ""};vary-by-header={responseCache.VaryByHeader ?? ""})");

        if (metadata.GetMetadata<OutputCacheAttribute>() is { } outputCache)
            facts.Add($"output-cache(policy={outputCache.PolicyName ?? ""};duration={outputCache.Duration};no-store={outputCache.NoStore})");

        foreach (var accepts in metadata.GetOrderedMetadata<IAcceptsMetadata>())
        {
            if (accepts.ContentTypes.Count == 0) continue;
            facts.Add($"accepts({string.Join(",", accepts.ContentTypes.OrderBy(c => c, StringComparer.Ordinal))}{(accepts.IsOptional ? ";optional" : "")})");
        }

        var line = $"{methods} {pattern}" + (facts.Count > 0 ? " | " + string.Join(" | ", facts) : "");
        return (pattern, methods, line);
    }

    /// <summary>
    /// The pattern's text with the spelling differences routing ignores removed: a leading slash
    /// always, no doubled or trailing slash. A group prefix and an endpoint path concatenated by a
    /// generator and by <c>MapGroup</c> differ in exactly these ways and match the same requests.
    /// </summary>
    private static string NormalizePattern(RoutePattern pattern)
    {
        var raw = pattern.RawText ?? string.Join("/", pattern.PathSegments.Select(SegmentText));
        var normalized = "/" + string.Join("/", raw.Split('/', StringSplitOptions.RemoveEmptyEntries));
        return normalized;
    }

    private static string SegmentText(RoutePatternPathSegment segment)
        => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart parameter => "{" + (parameter.IsCatchAll ? "*" : "") + parameter.Name
                + string.Concat(parameter.ParameterPolicies.Select(p => ":" + p.Content))
                + (parameter.IsOptional ? "?" : "") + "}",
            _ => part.ToString(),
        }));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "nx.json")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException(
            $"Could not locate the repository root (a directory containing nx.json) above {AppContext.BaseDirectory}.");
    }
}

/// <summary>The host's route table and its committed snapshot differ; the message carries the diff.</summary>
public sealed class RouteTableSnapshotMismatchException(string message) : Exception(message);
