using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// The exact set of route endpoints a host serves without authentication (RX3, FR5): the anonymous
/// surface a reviewer must see. <c>securityPosture.txt</c> lists Spark rights only, so an
/// <c>[AllowAnonymous]</c> or an unannotated endpoint never moves a reviewed artefact on its own.
/// </summary>
/// <remarks>
/// <para>
/// One line per endpoint: <c>{METHODS} {pattern} | allow-anonymous</c> when it carries
/// <see cref="IAllowAnonymous"/> (which wins over any <c>[Authorize]</c>), or
/// <c>{METHODS} {pattern} | unannotated</c> when it carries no authorization metadata at all. Spark has
/// no fallback policy, so an unannotated endpoint is reachable anonymously; most of <c>/spark/*</c> is,
/// on purpose, because it authorizes inside the handler against <c>security.json</c>.
/// </para>
/// <para>
/// ⚠️ <b>The assertion is an exact set.</b> A bound passes while an endpoint is swapped for another;
/// equality makes every addition fail, so the reviewer writes the new line down. If it fails, decide
/// first whether the endpoint should be reachable without credentials; adding the line is the last step.
/// </para>
/// </remarks>
public static class AnonymousSurface
{
    /// <summary>Every anonymous route endpoint of the started host, sorted ordinally.</summary>
    public static IReadOnlyList<string> Capture(IServiceProvider services)
        => services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(Describe)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList();

    /// <summary>Asserts <paramref name="actual"/> equals <paramref name="expected"/> exactly; the message carries the diff.</summary>
    /// <exception cref="AnonymousSurfaceMismatchException">The sets differ.</exception>
    public static void AssertExactly(IReadOnlyList<string> actual, IEnumerable<string> expected)
    {
        if (actual.Count == 0)
            throw new AnonymousSurfaceMismatchException("The host exposed no anonymous route endpoint at all; a capture that read the wrong data source looks like this.");

        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        var added = actual.Where(l => !expectedSet.Contains(l)).ToList();
        var removed = expectedSet.Where(l => !actualSet.Contains(l)).OrderBy(l => l, StringComparer.Ordinal).ToList();
        if (added.Count == 0 && removed.Count == 0)
            return;

        var message = new System.Text.StringBuilder()
            .AppendLine("The anonymous surface changed. Decide whether each endpoint below should be reachable without credentials before editing the expected set.");
        if (added.Count > 0)
        {
            message.AppendLine($"+ Anonymous in the host, not in the expected set ({added.Count}):");
            foreach (var line in added) message.AppendLine("  + " + line);
        }
        if (removed.Count > 0)
        {
            message.AppendLine($"- In the expected set, not anonymous in the host ({removed.Count}):");
            foreach (var line in removed) message.AppendLine("  - " + line);
        }
        throw new AnonymousSurfaceMismatchException(message.ToString());
    }

    /// <summary>The endpoint's line, or <see langword="null"/> when it requires authentication.</summary>
    internal static string? Describe(RouteEndpoint endpoint)
    {
        var metadata = endpoint.Metadata;
        string kind;
        if (metadata.GetMetadata<IAllowAnonymous>() is not null)
            kind = "allow-anonymous";
        else if (metadata.GetMetadata<IAuthorizeData>() is null && metadata.GetMetadata<AuthorizationPolicy>() is null)
            kind = "unannotated";
        else
            return null;

        var methods = metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } verbs
            ? string.Join(",", verbs.Select(v => v.ToUpperInvariant()).Distinct().OrderBy(v => v, StringComparer.Ordinal))
            : "*";
        return $"{methods} {RouteTableSnapshot.NormalizePattern(endpoint.RoutePattern)} | {kind}";
    }
}

/// <summary>The host's anonymous surface and the expected set differ; the message carries the diff.</summary>
public sealed class AnonymousSurfaceMismatchException(string message) : Exception(message);
