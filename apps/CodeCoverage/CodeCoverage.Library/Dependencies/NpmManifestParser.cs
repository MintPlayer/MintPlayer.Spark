using System.Text.Json;

namespace CodeCoverage.Dependencies;

/// <summary>
/// <c>package.json</c>: produces its <c>name</c> unless <c>"private": true</c>; consumes
/// <c>dependencies</c>, <c>devDependencies</c> (dev), <c>peerDependencies</c> and
/// <c>optionalDependencies</c>.
/// </summary>
/// <remarks>
/// Specs pointing inside the repository or the machine (<c>workspace:</c>, <c>file:</c>,
/// <c>link:</c>) are skipped: they name a sibling, not a published package. An alias
/// (<c>"x": "npm:real@^1"</c>) consumes the real package, since that is what gets installed.
/// </remarks>
public static class NpmManifestParser
{
    private static readonly (string Section, bool Dev)[] Sections =
    [
        ("dependencies", false),
        ("devDependencies", true),
        ("peerDependencies", false),
        ("optionalDependencies", false),
    ];

    public static ManifestParseResult Parse(string path, string content)
    {
        var collector = new ManifestCollector(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            return ManifestParseResult.Failed($"Invalid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ManifestParseResult.Failed("package.json is not a JSON object.");

            var isPrivate = root.TryGetProperty("private", out var privateElement) && privateElement.ValueKind == JsonValueKind.True;
            if (!isPrivate && root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                collector.Produce(ManifestEcosystems.Npm, name.GetString()!.Trim());

            foreach (var (section, dev) in Sections)
            {
                if (!root.TryGetProperty(section, out var dependencies) || dependencies.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var dependency in dependencies.EnumerateObject())
                {
                    var spec = dependency.Value.ValueKind == JsonValueKind.String ? dependency.Value.GetString()!.Trim() : null;
                    if (spec is not null && IsLocal(spec)) continue;

                    var packageName = dependency.Name.Trim();
                    if (spec is not null && spec.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
                        (packageName, spec) = SplitAlias(spec["npm:".Length..], packageName);

                    collector.Consume(ManifestEcosystems.Npm, packageName, spec, dev);
                }
            }
        }

        return collector.Result();
    }

    private static bool IsLocal(string spec)
        => spec.StartsWith("workspace:", StringComparison.OrdinalIgnoreCase)
           || spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
           || spec.StartsWith("link:", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>real@^1</c> or <c>@scope/real@^1</c> into name and range.</summary>
    private static (string Name, string? Spec) SplitAlias(string alias, string fallback)
    {
        var at = alias.LastIndexOf('@');
        if (at <= 0) return (alias.Length > 0 ? alias : fallback, null);
        return (alias[..at], alias[(at + 1)..]);
    }
}
