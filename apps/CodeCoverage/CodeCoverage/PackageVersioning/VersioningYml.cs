using CodeCoverage.Versioning.Releases;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// The server's reader of <c>coverage.yml</c>'s <c>versioning:</c> section (PRD D17). The section's meaning, defaults and
/// validation are the engine's <see cref="VersioningConfig"/>; this only turns YAML into it.
/// </summary>
/// <remarks>⚠️ Read from the repository's DEFAULT branch only, like <c>DependenciesYml</c>: a branch cannot widen what is
/// tagged on it.</remarks>
public static class VersioningYml
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// The section, or null when the file has none (the repository is not versioned). An empty <c>versioning:</c> is a
    /// versioned repository with every default.
    /// </summary>
    public static VersioningYmlResult Parse(string? yml)
    {
        if (string.IsNullOrWhiteSpace(yml))
            return new(null, []);
        try
        {
            // Presence first: YamlDotNet reads an empty `versioning:` as null, which must still switch versioning on.
            var keys = Deserializer.Deserialize<Dictionary<string, object?>?>(yml);
            if (keys is null || !keys.ContainsKey("versioning"))
                return new(null, []);
            var config = Deserializer.Deserialize<FileRoot?>(yml)?.Versioning ?? new VersioningConfig();
            return new(config, config.Validate());
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or InvalidCastException)
        {
            return new(new VersioningConfig(), [$"coverage.yml could not be read: {ex.Message}"]);
        }
    }

    private sealed class FileRoot
    {
        public VersioningConfig? Versioning { get; set; }
    }
}

/// <param name="Config">Null when the repository is not versioned.</param>
/// <param name="Errors">Why the section cannot be used; a run is refused while there are any.</param>
public sealed record VersioningYmlResult(VersioningConfig? Config, IReadOnlyList<string> Errors);
