namespace MintPlayer.Spark.Templates.Tests;

/// <summary>The manifest against the repository (docs/templates_dnx_terminal_PRD.md §4.2: the graph and coverage tests).</summary>
public class FeatureManifestTests
{
    private static readonly string[] Reasons = ["dependency", "not-an-app-feature", "unreleased", "needs-prerendering"];

    [Fact]
    public void Every_packable_library_is_in_the_manifest()
    {
        var manifest = TemplateRepository.Manifest();
        var listed = manifest.Core.Select(p => p.Project)
            .Concat(manifest.Features.Select(f => f.Project))
            .Concat(manifest.Excluded.Select(e => e.Project))
            .Select(TemplateRepository.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unlisted = Directory.EnumerateFiles(TemplateRepository.Libs, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsUnder(path, "content") && !IsUnder(path, "node_modules") && !IsUnder(path, "bin") && !IsUnder(path, "obj"))
            .Where(path => !File.ReadAllText(path).Contains("<IsPackable>false</IsPackable>"))
            .Where(path => !listed.Contains(Path.GetFullPath(path)))
            .Select(path => Path.GetRelativePath(TemplateRepository.Libs, path).Replace('\\', '/'))
            .ToList();

        unlisted.Should().BeEmpty("a new library must be a feature in features.json or be excluded with a reason");
    }

    [Fact]
    public void Every_manifest_entry_is_an_existing_project()
    {
        var manifest = TemplateRepository.Manifest();
        var missing = manifest.Core.Select(p => p.Project)
            .Concat(manifest.Features.Select(f => f.Project))
            .Concat(manifest.Excluded.Select(e => e.Project))
            .Where(project => !File.Exists(TemplateRepository.FullPath(project)))
            .ToList();

        missing.Should().BeEmpty();
    }

    [Fact]
    public void Every_exclusion_names_a_known_reason()
    {
        var unknown = TemplateRepository.Manifest().Excluded
            .Where(e => !Reasons.Contains(e.Reason))
            .Select(e => $"{e.Project}: '{e.Reason}'")
            .ToList();

        unknown.Should().BeEmpty();
    }

    [Fact]
    public void Each_feature_requires_exactly_the_features_its_project_references()
    {
        var manifest = TemplateRepository.Manifest();
        var featureByProject = manifest.Features.ToDictionary(f => TemplateRepository.FullPath(f.Project), StringComparer.OrdinalIgnoreCase);

        var mismatches = manifest.Features
            .Select(feature => (
                feature,
                referenced: TemplateRepository.ProjectReferences(TemplateRepository.FullPath(feature.Project))
                    .Where(featureByProject.ContainsKey)
                    .Select(path => featureByProject[path].Id)
                    .Order()
                    .ToList()))
            .Where(x => !x.referenced.SequenceEqual(x.feature.Requires.Order()))
            .Select(x => $"{x.feature.Id}: requires [{string.Join(", ", x.feature.Requires)}], references [{string.Join(", ", x.referenced)}]")
            .ToList();

        mismatches.Should().BeEmpty("a feature's prerequisites are the features its package depends on");
    }

    private static bool IsUnder(string path, string folder)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(folder);
}
