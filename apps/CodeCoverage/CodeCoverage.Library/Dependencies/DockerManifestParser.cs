using System.Text.RegularExpressions;

namespace CodeCoverage.Dependencies;

/// <summary>
/// Container images: <c>FROM</c> in a <c>Dockerfile*</c> and <c>image:</c> in a compose file. Only
/// <c>ghcr.io/&lt;owner&gt;/&lt;name&gt;</c> images are recorded — the registry where a repository
/// publishes its own images, and so the only one that can produce an edge between two repositories.
/// </summary>
/// <remarks>
/// The tag and digest are stripped and the name lowercased (registries compare lowercase). An image
/// reference built from a variable (<c>${REGISTRY}/…</c>) is skipped: its real value is unknowable
/// without the build. Every repository implicitly produces <see cref="ProducedName"/>; the dependency
/// graph adds that itself.
/// </remarks>
public static partial class DockerManifestParser
{
    private const string Registry = "ghcr.io/";

    public static ManifestParseResult ParseDockerfile(string path, string content)
    {
        var collector = new ManifestCollector(path);
        foreach (var raw in content.Split('\n'))
        {
            var match = FromPattern().Match(raw);
            if (match.Success) Add(collector, match.Groups["image"].Value);
        }
        return collector.Result();
    }

    public static ManifestParseResult ParseCompose(string path, string content)
    {
        var collector = new ManifestCollector(path);
        foreach (var raw in content.Split('\n'))
        {
            var match = ImagePattern().Match(raw);
            if (match.Success) Add(collector, match.Groups["image"].Value);
        }
        return collector.Result();
    }

    /// <summary>The implicit product of a repository: <c>ghcr.io/{fullName}</c>, lowercased.</summary>
    public static string ProducedName(string fullName) => (Registry + fullName).ToLowerInvariant();

    /// <summary>
    /// <c>ghcr.io/Owner/Name:tag@sha256:…</c> to <c>ghcr.io/owner/name</c>; null for anything that is
    /// not a ghcr.io image with at least an owner and a name.
    /// </summary>
    public static string? NormalizeImage(string image)
    {
        image = image.Trim().Trim('"', '\'');
        if (!image.StartsWith(Registry, StringComparison.OrdinalIgnoreCase)) return null;
        if (image.Contains('$')) return null;

        var at = image.IndexOf('@');
        if (at >= 0) image = image[..at];
        var lastSlash = image.LastIndexOf('/');
        var colon = image.IndexOf(':', lastSlash + 1);
        if (colon >= 0) image = image[..colon];

        var segments = image[Registry.Length..].Split('/');
        if (segments.Length < 2 || segments.Any(s => s.Length == 0)) return null;
        return image.ToLowerInvariant();
    }

    private static void Add(ManifestCollector collector, string image)
    {
        if (NormalizeImage(image) is { } name)
            collector.Consume(ManifestEcosystems.Docker, name, constraint: TagOf(image), dev: false);
    }

    private static string? TagOf(string image)
    {
        image = image.Trim().Trim('"', '\'');
        var at = image.IndexOf('@');
        if (at >= 0) return image[(at + 1)..];
        var colon = image.IndexOf(':', image.LastIndexOf('/') + 1);
        return colon >= 0 ? image[(colon + 1)..] : null;
    }

    [GeneratedRegex(@"^\s*FROM\s+(?:--\S+\s+)*(?<image>\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex FromPattern();

    [GeneratedRegex(@"^\s*(?:-\s+)?image\s*:\s*(?<image>\S+)")]
    private static partial Regex ImagePattern();
}
