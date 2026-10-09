using System.Text.Json;

namespace CodeCoverage.Dependencies;

/// <summary>
/// <c>composer.json</c>: produces <c>name</c> unless <c>"type": "project"</c> (an application, not a
/// library); consumes <c>require</c> and <c>require-dev</c> (dev).
/// </summary>
/// <remarks>
/// Platform requirements (<c>php</c>, <c>ext-*</c>, <c>lib-*</c>, <c>composer-plugin-api</c>, …) are
/// skipped: they are never <c>vendor/package</c>, which is how every Packagist package is named.
/// Names are lowercased, as Packagist compares them.
/// </remarks>
public static class ComposerManifestParser
{
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
                return ManifestParseResult.Failed("composer.json is not a JSON object.");

            var isProject = root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                            && string.Equals(type.GetString(), "project", StringComparison.OrdinalIgnoreCase);
            if (!isProject && root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && IsPackageName(name.GetString()!))
                collector.Produce(ManifestEcosystems.Composer, name.GetString()!.Trim().ToLowerInvariant());

            foreach (var (section, dev) in new[] { ("require", false), ("require-dev", true) })
            {
                if (!root.TryGetProperty(section, out var requirements) || requirements.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var requirement in requirements.EnumerateObject())
                {
                    if (!IsPackageName(requirement.Name)) continue;
                    var constraint = requirement.Value.ValueKind == JsonValueKind.String ? requirement.Value.GetString() : null;
                    collector.Consume(ManifestEcosystems.Composer, requirement.Name.Trim().ToLowerInvariant(), constraint, dev);
                }
            }
        }

        return collector.Result();
    }

    /// <summary><c>vendor/package</c>; everything else is a platform requirement.</summary>
    private static bool IsPackageName(string name)
    {
        var parts = name.Trim().Split('/');
        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0;
    }
}
