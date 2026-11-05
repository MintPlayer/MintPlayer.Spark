using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.Git.MergeQueue.Resolution.Manifests;

/// <summary>
/// A JSON manifest whose dependencies are <c>"name": "range"</c> lines inside named objects (npm, Composer). Every entry
/// is a scalar keyed by its name. A value is a version only inside one of <see cref="DependencyObjects"/>, read back
/// over the merged text above the region, and not for a <see cref="IsPlatformKey"/>; two ranges are ordered only when
/// they share one operator (<c>^</c>, <c>~</c> or none), as two operators are two intents.
/// </summary>
public abstract partial class JsonManifestFormat : IManifestFormat
{
    public abstract string Name { get; }

    protected abstract string FileName { get; }

    /// <summary>The objects whose values are package version ranges.</summary>
    protected abstract IReadOnlySet<string> DependencyObjects { get; }

    /// <summary>A key that is a requirement on the platform (Composer's <c>php</c>): on a release line it is the line's
    /// platform, so it never takes the highest.</summary>
    protected virtual bool IsPlatformKey(string key) => false;

    public bool Handles(string fileName) => fileName.Equals(FileName, StringComparison.Ordinal);

    public (int Above, int Below) Enclose(IReadOnlyList<string> before, IReadOnlyList<string> after) => (0, 0);

    public ManifestSection? Read(IReadOnlyList<string> lines)
    {
        var entries = new List<ManifestEntry>();
        var leading = new List<string>();
        foreach (var line in lines)
        {
            var bare = line.Trim();
            if (bare.Length == 0)
            {
                leading.Add(line);
                continue;
            }
            if (Entry().Match(bare) is not { Success: true } m)
                return null;
            var name = m.Groups["name"].Value;
            entries.Add(new ManifestEntry(leading, [line], name, Kind: "", Scalar: true,
                new Dictionary<string, string> { [ManifestEntry.ScalarValue] = m.Groups["value"].Value }));
            leading = [];
        }
        return new ManifestSection(entries, leading);
    }

    public int? CompareVersions(ManifestEntry entry, string valueName, string a, string b, IReadOnlyList<string> before)
    {
        if (valueName != ManifestEntry.ScalarValue || IsPlatformKey(entry.Key) || !DependencyObjects.Contains(EnclosingObject(before) ?? ""))
            return null;
        var (operatorA, operatorB) = (RangeOperator(a), RangeOperator(b));
        return operatorA == operatorB ? SemanticVersion.Compare(a[operatorA.Length..], b[operatorB.Length..]) : null;
    }

    public IReadOnlyList<string>? Write(ManifestEntry entry, IReadOnlyDictionary<string, string?> values)
    {
        if (entry.Lines.Count != 1 || !values.TryGetValue(ManifestEntry.ScalarValue, out var value) || value is null
            || value.Contains('"') || value.Contains('\\') || value.Contains('\n'))
            return null;
        var line = entry.Lines[0];
        var m = Entry().Match(line.Trim());
        var start = line.IndexOf('"', line.IndexOf(':')) + 1;
        return [line[..start] + value + line[(start + m.Groups["value"].Length)..]];
    }

    /// <summary>Every merged entry but the last ends in a comma; the last one as the sides' last entry did (the object
    /// goes on after the region, or it closes).</summary>
    public IReadOnlyList<string> Join(IReadOnlyList<ManifestEntry> merged, IReadOnlyList<string> trailing, ManifestSection ours, ManifestSection theirs)
    {
        var last = ours.Entries.Count > 0 ? ours.Entries[^1] : theirs.Entries.Count > 0 ? theirs.Entries[^1] : null;
        var lastHasComma = last is not null && last.Lines[0].TrimEnd().EndsWith(',');
        var lines = new List<string>();
        for (var e = 0; e < merged.Count; e++)
        {
            lines.AddRange(merged[e].Leading);
            lines.Add(WithComma(merged[e].Lines[0], e < merged.Count - 1 || lastHasComma));
        }
        lines.AddRange(trailing);
        return lines;
    }

    /// <summary>The name of the object the next line is in (<c>"dependencies": {</c> above it, at depth), read back
    /// over the lines before it; null at the top level or when it can't be told.</summary>
    internal static string? EnclosingObject(IReadOnlyList<string> before)
    {
        var depth = 0;
        for (var i = before.Count - 1; i >= 0; i--)
        {
            var bare = before[i].Trim();
            if (bare.StartsWith('}') || bare.StartsWith(']'))
                depth++;
            if (bare.EndsWith('{') || bare.EndsWith('['))
            {
                if (depth == 0)
                    return ObjectStart().Match(bare) is { Success: true } m ? m.Groups["name"].Value : null;
                depth--;
            }
        }
        return null;
    }

    private static string RangeOperator(string range) => range.StartsWith('^') || range.StartsWith('~') ? range[..1] : "";

    private static string WithComma(string line, bool comma)
    {
        var body = line.TrimEnd('\n').TrimEnd('\r');
        var ending = line[body.Length..];
        var trimmed = body.TrimEnd();
        var spaces = body[trimmed.Length..];
        if (trimmed.EndsWith(','))
            trimmed = trimmed[..^1];
        return trimmed + (comma ? "," : "") + spaces + ending;
    }

    [GeneratedRegex(@"^""(?<name>[^""\\]+)""\s*:\s*""(?<value>[^""\\]*)""\s*,?$", RegexOptions.CultureInvariant)]
    private static partial Regex Entry();

    [GeneratedRegex(@"^""(?<name>[^""\\]+)""\s*:\s*[\{\[]$", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectStart();
}

/// <summary><c>package.json</c>: <c>dependencies</c>, <c>devDependencies</c>, <c>peerDependencies</c>,
/// <c>optionalDependencies</c>. The package's own <c>"version"</c> and <c>engines</c> are no dependency: the census of
/// the MintPlayer repositories found the own version the most frequent same-key conflict (PRD §8.1).</summary>
[Register(typeof(IManifestFormat), ServiceLifetime.Singleton)]
public sealed class NpmManifestFormat : JsonManifestFormat
{
    private static readonly HashSet<string> Objects = new(StringComparer.Ordinal) { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" };

    public override string Name => "npm";

    protected override string FileName => "package.json";

    protected override IReadOnlySet<string> DependencyObjects => Objects;
}

/// <summary><c>composer.json</c>: <c>require</c>, <c>require-dev</c>. <c>php</c>, <c>php-*</c>, <c>ext-*</c> and
/// <c>lib-*</c> are platform requirements, not packages.</summary>
[Register(typeof(IManifestFormat), ServiceLifetime.Singleton)]
public sealed class ComposerManifestFormat : JsonManifestFormat
{
    private static readonly HashSet<string> Objects = new(StringComparer.Ordinal) { "require", "require-dev" };

    public override string Name => "composer";

    protected override string FileName => "composer.json";

    protected override IReadOnlySet<string> DependencyObjects => Objects;

    protected override bool IsPlatformKey(string key)
        => key.Equals("php", StringComparison.OrdinalIgnoreCase) || key.StartsWith("php-", StringComparison.OrdinalIgnoreCase)
           || key.StartsWith("ext-", StringComparison.OrdinalIgnoreCase) || key.StartsWith("lib-", StringComparison.OrdinalIgnoreCase);
}
