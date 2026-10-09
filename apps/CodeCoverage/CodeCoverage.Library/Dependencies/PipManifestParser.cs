using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace CodeCoverage.Dependencies;

/// <summary>
/// Python: <c>requirements*.txt</c> and <c>pyproject.toml</c> (PEP 621 <c>[project]</c>, PEP 735
/// <c>[dependency-groups]</c> and Poetry's <c>[tool.poetry]</c>).
/// </summary>
/// <remarks>
/// Every name is normalized per PEP 503 (lowercase, runs of <c>-_.</c> become one <c>-</c>), because
/// that is how the index compares them: <c>Foo_Bar</c> and <c>foo-bar</c> are one package, and an
/// unnormalized comparison would miss the edge.
/// </remarks>
public static partial class PipManifestParser
{
    /// <summary>Optional-dependency and group names that are development tooling rather than features.</summary>
    private static readonly HashSet<string> DevGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "dev", "develop", "development", "test", "tests", "testing", "lint", "linting", "docs", "doc", "typing", "types",
    };

    /// <summary>PEP 503 normalization.</summary>
    public static string Normalize(string name) => Separators().Replace(name.Trim(), "-").ToLowerInvariant();

    public static ManifestParseResult ParseRequirements(string path, string content)
    {
        var collector = new ManifestCollector(path);
        var fileName = path[(path.LastIndexOf('/') + 1)..];
        var dev = fileName.Contains("dev", StringComparison.OrdinalIgnoreCase) || fileName.Contains("test", StringComparison.OrdinalIgnoreCase);

        foreach (var raw in JoinContinuations(content))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            // Options (-r other.txt, -e ., --index-url …) and bare paths name no package.
            if (line[0] is '-' or '.' or '/') continue;

            if (TryParseRequirement(line, out var name, out var constraint))
                collector.Consume(ManifestEcosystems.Pip, name, constraint, dev);
        }

        return collector.Result();
    }

    public static ManifestParseResult ParsePyProject(string path, string content)
    {
        var collector = new ManifestCollector(path);
        TomlTable root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(content)!;
        }
        catch (Exception ex)
        {
            return ManifestParseResult.Failed($"Invalid TOML: {ex.Message}");
        }

        if (root is null) return ManifestParseResult.Empty;

        // PEP 621.
        if (Table(root, "project") is { } project)
        {
            if (project.TryGetValue("name", out var name) && name is string projectName)
                collector.Produce(ManifestEcosystems.Pip, Normalize(projectName));

            ConsumeRequirementArray(collector, Array(project, "dependencies"), dev: false);

            if (Table(project, "optional-dependencies") is { } optional)
                foreach (var (group, value) in optional)
                    ConsumeRequirementArray(collector, value as TomlArray, DevGroups.Contains(group));
        }

        // PEP 735: groups are development-time by definition — they are never installed by a consumer.
        if (Table(root, "dependency-groups") is { } groups)
            foreach (var (_, value) in groups)
                ConsumeRequirementArray(collector, value as TomlArray, dev: true);

        // Poetry.
        if (Table(root, "tool") is { } tool && Table(tool, "poetry") is { } poetry)
        {
            var packageMode = !poetry.TryGetValue("package-mode", out var mode) || mode is not false;
            if (packageMode && poetry.TryGetValue("name", out var poetryName) && poetryName is string poetryPackage)
                collector.Produce(ManifestEcosystems.Pip, Normalize(poetryPackage));

            ConsumePoetryTable(collector, Table(poetry, "dependencies"), dev: false);
            ConsumePoetryTable(collector, Table(poetry, "dev-dependencies"), dev: true);
            if (Table(poetry, "group") is { } poetryGroups)
                foreach (var (group, value) in poetryGroups)
                    if (value is TomlTable groupTable)
                        ConsumePoetryTable(collector, Table(groupTable, "dependencies"), dev: !string.Equals(group, "main", StringComparison.OrdinalIgnoreCase));
        }

        return collector.Result();
    }

    /// <summary>
    /// One PEP 508 requirement: <c>name[extras] (constraint) ; marker</c> or <c>name @ url</c>.
    /// </summary>
    public static bool TryParseRequirement(string requirement, out string name, out string? constraint)
    {
        name = string.Empty;
        constraint = null;

        var match = RequirementPattern().Match(requirement.Trim());
        if (!match.Success) return false;

        var rest = match.Groups["rest"].Value;
        // A bare URL ("git+https://…") has no package name in front of it.
        if (rest.StartsWith("://", StringComparison.Ordinal) || rest.StartsWith('+') || rest.StartsWith(':')) return false;

        name = Normalize(match.Groups["name"].Value);
        var semicolon = rest.IndexOf(';');
        if (semicolon >= 0) rest = rest[..semicolon];
        rest = rest.Trim();
        if (rest.StartsWith('@')) rest = string.Empty; // direct reference: no version range
        constraint = rest.Trim('(', ')', ' ').Length > 0 ? rest.Trim('(', ')', ' ') : null;
        return true;
    }

    private static void ConsumeRequirementArray(ManifestCollector collector, TomlArray? array, bool dev)
    {
        if (array is null) return;
        foreach (var item in array)
            if (item is string requirement && TryParseRequirement(requirement, out var name, out var constraint))
                collector.Consume(ManifestEcosystems.Pip, name, constraint, dev);
    }

    private static void ConsumePoetryTable(ManifestCollector collector, TomlTable? table, bool dev)
    {
        if (table is null) return;
        foreach (var (key, value) in table)
        {
            if (string.Equals(key, "python", StringComparison.OrdinalIgnoreCase)) continue;

            var constraint = value switch
            {
                string version => version,
                TomlTable detail when detail.TryGetValue("version", out var v) && v is string version => version,
                _ => null,
            };

            // A path dependency is a sibling in the same tree, not a published package.
            if (value is TomlTable local && local.ContainsKey("path")) continue;

            collector.Consume(ManifestEcosystems.Pip, Normalize(key), constraint, dev);
        }
    }

    private static TomlTable? Table(TomlTable parent, string key)
        => parent.TryGetValue(key, out var value) ? value as TomlTable : null;

    private static TomlArray? Array(TomlTable parent, string key)
        => parent.TryGetValue(key, out var value) ? value as TomlArray : null;

    private static IEnumerable<string> JoinContinuations(string content)
    {
        var pending = string.Empty;
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.EndsWith('\\'))
            {
                pending += trimmed[..^1] + " ";
                continue;
            }
            yield return pending + trimmed;
            pending = string.Empty;
        }
        if (pending.Length > 0) yield return pending;
    }

    /// <summary>pip treats <c>#</c> as a comment at the start of a line or after whitespace.</summary>
    private static string StripComment(string line)
    {
        for (var i = 0; i < line.Length; i++)
            if (line[i] == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
                return line[..i];
        return line;
    }

    [GeneratedRegex(@"[-_.]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"^(?<name>[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?)\s*(?:\[[^\]]*\])?\s*(?<rest>.*)$", RegexOptions.Singleline)]
    private static partial Regex RequirementPattern();
}
