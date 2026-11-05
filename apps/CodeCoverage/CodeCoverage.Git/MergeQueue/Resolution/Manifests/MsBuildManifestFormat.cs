using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.Git.MergeQueue.Resolution.Manifests;

/// <summary>
/// MSBuild files (<c>*.csproj</c>, <c>*.props</c> with <c>Directory.Packages.props</c>, <c>*.targets</c>). An item is a
/// collection entry keyed by its type, its <c>Include</c>/<c>Update</c>/<c>Remove</c> value and its <c>Condition</c>,
/// case-insensitively as MSBuild and NuGet compare them; its values are its metadata, attributes and one-line child
/// elements alike. A property is a scalar keyed by its name and <c>Condition</c>. Only a package item's <c>Version</c>
/// (<c>PackageReference</c>, <c>PackageVersion</c>, <c>GlobalPackageReference</c>) is a version, in NuGet's order.
/// The key matches the <c>msbuild-no-duplicate-keys</c> validator's.
/// </summary>
[Register(typeof(IManifestFormat), ServiceLifetime.Singleton)]
public sealed partial class MsBuildManifestFormat : IManifestFormat
{
    public string Name => "msbuild";

    public bool Handles(string fileName)
        => fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
           || fileName.EndsWith(".targets", StringComparison.OrdinalIgnoreCase);

    public (int Above, int Below) Enclose(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        // Back over the lines above to an item's opening tag, stopping at anything that ends an entry.
        for (var k = before.Count - 1; k >= 0; k--)
        {
            var bare = before[k].Trim();
            if (bare.StartsWith("</", StringComparison.Ordinal) || SelfClosing().IsMatch(bare) || SingleLineComment().IsMatch(bare))
                return (0, 0);
            if (OpenTag().Match(bare) is { Success: true } m && Attributes(m.Groups["attrs"].Value) is { } attrs && attrs.Keys.Any(IsItemSpec))
            {
                var close = -1;
                for (var j = 0; j < after.Count && close < 0; j++)
                {
                    if (after[j].Trim().Equals($"</{m.Groups["name"].Value}>", StringComparison.Ordinal))
                        close = j;
                }
                return close < 0 ? (0, 0) : (before.Count - k, close + 1);
            }
        }
        return (0, 0);
    }

    public ManifestSection? Read(IReadOnlyList<string> lines)
    {
        var entries = new List<ManifestEntry>();
        var leading = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var bare = line.Trim();
            if (bare.Length == 0 || SingleLineComment().IsMatch(bare))
            {
                leading.Add(line);
                continue;
            }
            ManifestEntry? entry = null;
            if (SelfClosing().Match(bare) is { Success: true } self)
                entry = Entry(leading, [line], self.Groups["name"].Value, self.Groups["attrs"].Value, children: []);
            else if (PropertyLine().Match(bare) is { Success: true } prop && Attributes(prop.Groups["attrs"].Value) is { } propAttrs
                     && !propAttrs.Keys.Any(IsItemSpec))
                entry = Property(leading, [line], prop.Groups["name"].Value, propAttrs, prop.Groups["value"].Value);
            else if (OpenTag().Match(bare) is { Success: true } open)
            {
                // An item with child metadata: one child per line, up to its closing tag.
                var children = new List<(string Name, string Value)>();
                var own = new List<string> { line };
                var closed = false;
                for (i++; i < lines.Count; i++)
                {
                    own.Add(lines[i]);
                    var inner = lines[i].Trim();
                    if (inner.Equals($"</{open.Groups["name"].Value}>", StringComparison.Ordinal))
                    {
                        closed = true;
                        break;
                    }
                    if (PropertyLine().Match(inner) is not { Success: true } child || child.Groups["attrs"].Value.Trim().Length > 0)
                        return null;
                    children.Add((child.Groups["name"].Value, child.Groups["value"].Value));
                }
                if (!closed)
                    return null;
                entry = Entry(leading, own, open.Groups["name"].Value, open.Groups["attrs"].Value, children);
            }
            if (entry is null)
                return null;
            entries.Add(entry);
            leading = [];
        }
        return new ManifestSection(entries, leading);
    }

    public int? CompareVersions(ManifestEntry entry, string valueName, string a, string b, IReadOnlyList<string> before)
        => !entry.Scalar && valueName.Equals("Version", StringComparison.OrdinalIgnoreCase)
           && (entry.Kind.Equals("PackageReference", StringComparison.OrdinalIgnoreCase) || entry.Kind.Equals("PackageVersion", StringComparison.OrdinalIgnoreCase)
               || entry.Kind.Equals("GlobalPackageReference", StringComparison.OrdinalIgnoreCase))
            ? SemanticVersion.Compare(a, b)
            : null;

    public IReadOnlyList<string>? Write(ManifestEntry entry, IReadOnlyDictionary<string, string?> values)
    {
        if (values.Values.Any(v => v is not null && (v.Contains('"') || v.Contains('<') || v.Contains('&') || v.Contains('\n'))))
            return null;
        var lines = entry.Lines.ToList();
        if (entry.Scalar)
        {
            if (lines.Count != 1 || !values.TryGetValue(ManifestEntry.ScalarValue, out var scalar) || scalar is null)
                return null;
            var open = lines[0].IndexOf('>') + 1;
            var close = lines[0].LastIndexOf("</", StringComparison.Ordinal);
            return open <= 0 || close < open ? null : [lines[0][..open] + scalar + lines[0][close..]];
        }
        var childLines = lines.Count > 1;
        foreach (var (name, value) in values)
        {
            entry.Values.TryGetValue(name, out var old);
            if (old == value)
                continue;
            // Attributes live on the first line; child metadata on its own lines.
            var attribute = new Regex($@"(\s){Regex.Escape(name)}\s*=\s*""[^""]*""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var childIndex = lines.FindIndex(1, Math.Max(0, lines.Count - 1), l => PropertyLine().Match(l.Trim()) is { Success: true } c
                && c.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (old is not null && attribute.IsMatch(lines[0]))
                lines[0] = value is null ? attribute.Replace(lines[0], "", 1) : attribute.Replace(lines[0], $"$1{name}=\"{value}\"", 1);
            else if (old is not null && childIndex > 0)
            {
                if (value is null)
                    lines.RemoveAt(childIndex);
                else
                {
                    var child = lines[childIndex];
                    var open = child.IndexOf('>') + 1;
                    var close = child.LastIndexOf("</", StringComparison.Ordinal);
                    lines[childIndex] = child[..open] + value + child[close..];
                }
            }
            else if (old is null && value is not null && !childLines)
            {
                var end = lines[0].LastIndexOf("/>", StringComparison.Ordinal);
                if (end < 0)
                    return null;
                lines[0] = $"{lines[0][..end].TrimEnd()} {name}=\"{value}\" {lines[0][end..]}";
            }
            else
                return null;
        }
        return lines;
    }

    public IReadOnlyList<string> Join(IReadOnlyList<ManifestEntry> merged, IReadOnlyList<string> trailing, ManifestSection ours, ManifestSection theirs)
        => ManifestFormats.Concatenate(merged, trailing);

    private static ManifestEntry Property(List<string> leading, List<string> lines, string name, IReadOnlyDictionary<string, string> attributes, string value)
        => new(leading, lines, $"property\0{name}\0{Condition(attributes)}".ToLowerInvariant(), name, Scalar: true,
            new Dictionary<string, string> { [ManifestEntry.ScalarValue] = value });

    private static ManifestEntry? Entry(List<string> leading, List<string> lines, string name, string attrs, List<(string Name, string Value)> children)
    {
        if (Attributes(attrs) is not { } attributes)
            return null;
        var spec = attributes.Keys.FirstOrDefault(IsItemSpec);
        if (spec is null)
        {
            // Without an item spec only an empty property (<Foo />, at most a Condition) is an entry; a
            // <Message Text="..." /> in a target or an element with children is not.
            return children.Count == 0 && attributes.Keys.All(k => k.Equals("Condition", StringComparison.OrdinalIgnoreCase))
                ? Property(leading, lines, name, attributes, "")
                : null;
        }
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in attributes)
        {
            if (!IsItemSpec(key) && !key.Equals("Condition", StringComparison.OrdinalIgnoreCase) && !values.TryAdd(key, value))
                return null;
        }
        foreach (var (key, value) in children)
        {
            if (!values.TryAdd(key, value))
                return null;
        }
        return new ManifestEntry(leading, lines, $"item\0{name}\0{spec}\0{attributes[spec].Trim()}\0{Condition(attributes)}".ToLowerInvariant(), name,
            Scalar: false, values);
    }

    private static bool IsItemSpec(string attribute)
        => attribute.Equals("Include", StringComparison.OrdinalIgnoreCase) || attribute.Equals("Update", StringComparison.OrdinalIgnoreCase)
           || attribute.Equals("Remove", StringComparison.OrdinalIgnoreCase);

    private static string Condition(IReadOnlyDictionary<string, string> attributes)
        => attributes.FirstOrDefault(a => a.Key.Equals("Condition", StringComparison.OrdinalIgnoreCase)).Value?.Trim() ?? "";

    /// <summary>A tag's attributes by name; null when the text holds anything else or a name twice.</summary>
    private static Dictionary<string, string>? Attributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = AttributePattern().Matches(text);
        foreach (Match m in matches)
            result.TryAdd(m.Groups["name"].Value, m.Groups["dq"].Success ? m.Groups["dq"].Value : m.Groups["sq"].Value);
        return AttributePattern().Replace(text, "").Trim().Length == 0 && result.Count == matches.Count ? result : null;
    }

    [GeneratedRegex(@"^<!--.*-->$", RegexOptions.CultureInvariant)]
    private static partial Regex SingleLineComment();

    [GeneratedRegex(@"^<(?<name>[A-Za-z_][\w.-]*)(?<attrs>(\s+[^<>]*?)?)\s*/>$", RegexOptions.CultureInvariant)]
    private static partial Regex SelfClosing();

    [GeneratedRegex(@"^<(?<name>[A-Za-z_][\w.-]*)(?<attrs>(\s+[^<>]*?)?)>(?<value>[^<]*)</\k<name>>$", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyLine();

    [GeneratedRegex(@"^<(?<name>[A-Za-z_][\w.-]*)(?<attrs>\s+[^<>]*?)>$", RegexOptions.CultureInvariant)]
    private static partial Regex OpenTag();

    [GeneratedRegex(@"(?<name>[A-Za-z_][\w.:-]*)\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)')", RegexOptions.CultureInvariant)]
    private static partial Regex AttributePattern();
}
