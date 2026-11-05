using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.Git.MergeQueue.Resolution;

/// <summary>
/// <c>keyed-merge</c> (#570, docs/coverage_backport_PRD.md §8.3): a three-way merge per key of the conflict regions of an
/// MSBuild file (<c>*.csproj</c>, <c>*.props</c>, <c>*.targets</c>) or a <c>package.json</c>.
/// <list type="number">
/// <item><b>Regions</b> come from the same plain diff3 run as keep-both (never zdiff3). Each section of a region is read
/// as keyed entries; a line that is not one (a group tag, a target, a multi-line comment) refuses the path.</item>
/// <item><b>Keys.</b> An MSBuild item is a collection entry keyed by its type, its <c>Include</c>/<c>Update</c>/<c>Remove</c>
/// value and its <c>Condition</c>; its value is its metadata, attributes and child elements alike. A property is a scalar
/// keyed by its name and <c>Condition</c>. A package.json line <c>"name": "value"</c> is keyed by its name. MSBuild keys
/// compare case-insensitively, as MSBuild and NuGet do.</item>
/// <item><b>Per key:</b> what one side left as the merge base had it takes the other side; equal values are kept once; an
/// item's metadata merges the same way per metadata name; a package version (<c>PackageReference</c>/<c>PackageVersion</c>
/// <c>Version</c>, a package.json range of one operator) both sides changed takes the highest semantic version. Anything
/// else both sides changed, and a key removed on one side and changed on the other, refuses.</item>
/// <item><b>Output:</b> per region the base branch's entries in their order, then the pull request's new ones, the
/// convention of keep-both. Text is edited, never re-serialised: an entry keeps its side's bytes, and a merged one is
/// the base branch's text with the changed values set. Comments and blank lines travel with the entry after them. A
/// package.json region's commas are set so the object stays valid.</item>
/// </list>
/// The rule's validators run on the result (msbuild-no-duplicate-keys, json-no-duplicate-keys).
/// </summary>
[Register(typeof(IConflictResolver), ServiceLifetime.Singleton)]
public sealed partial class KeyedMergeResolver : IConflictResolver
{
    public const long MaxBytes = ResolutionLimits.MaxParsedBytes;

    public string Strategy => MergeQueueWireRules.KeyedMerge;

    public bool RegeneratesByDefault => false;

    public long MaxSideBytes => MaxBytes;

    public bool CanResolve(string path, IReadOnlyDictionary<int, MergeTreeStage> stages, AutoResolveRule rule)
        => stages.ContainsKey(2) && stages.ContainsKey(3) && stages.Values.All(e => e.Mode == "100644");

    public async Task<ConflictResolution> ResolveAsync(ConflictInput input, CancellationToken cancellationToken)
    {
        if (DialectOf(input.Path) is not { } dialect)
            return Refuse("keyed-merge reads MSBuild files (*.csproj, *.props, *.targets) and package.json only.");
        if (!input.Stages.TryGetValue(1, out var @base))
            return Refuse("Both sides added the file (an add/add conflict): there is no merge base to merge keys against.");

        var sides = await ResolutionLimits.ReadSidesAsync(input, MaxBytes);
        if (sides.Problem is { } problem)
            return Refuse(problem);
        foreach (var (stage, text) in sides.Texts)
        {
            if (ConflictHunks.SplitLines(text).Any(l => ConflictHunks.LooksLikeMarker(l.TrimEnd('\r'))))
                return Refuse($"{ResolutionLimits.StageName(stage)} has a line that looks like a conflict marker.");
        }

        var (conflicts, output) = await input.Repository.MergeFileAsync(input.Stages[2].ObjectId, @base.ObjectId, input.Stages[3].ObjectId, MaxBytes * 3);
        if (output is null)
            return Refuse("The merged file is too large.");
        if (ConflictRepository.DecodeStrict(output) is not { } merged || Diff3Document.Parse(merged) is not { } document)
            return Refuse("The conflict regions could not be read.");
        if (conflicts == 0 && document.Regions.Any() || conflicts > 0 && !document.Regions.Any())
            return Refuse("The conflict regions do not match git's count.");

        var written = new List<string>();
        var regionNumber = 0;
        foreach (var piece in !dialect.Json ? WidenToItems(document.Pieces) : document.Pieces)
        {
            switch (piece)
            {
                case Diff3Piece.Common common:
                    written.AddRange(common.Lines);
                    break;
                case Diff3Piece.Region region:
                    regionNumber++;
                    // A package.json value is a package version only inside a dependency object; the package's own
                    // "version" (the conflict the census of the MintPlayer repositories found most) has no winner.
                    var ordered = !dialect.Json || dialect.DependencyObjects.Contains(EnclosingObject(written) ?? "");
                    var merge = MergeRegion(dialect, region, ordered);
                    if (merge.Refusal is { } refusal)
                        return Refuse($"Conflict region {regionNumber}: {refusal}");
                    written.AddRange(merge.Lines!);
                    break;
            }
        }
        return new ConflictResolution.Content(string.Concat(written));
    }

    /// <summary>
    /// One file format (K2, docs/coverage_backport_plan.md): which files, how its entries read (<see cref="Json"/>: one
    /// <c>"name": "value"</c> line each; otherwise MSBuild items and properties), which JSON objects hold package versions,
    /// and which keys there are platform requirements with no winner. A further format joins this list only for a
    /// repository that needs it, with real fixtures (K3).
    /// </summary>
    internal sealed class Dialect
    {
        private Dialect(string name, Func<string, bool> matches, bool json, IReadOnlySet<string> dependencyObjects, Func<string, bool> isPlatformKey)
            => (Name, Matches, Json, DependencyObjects, IsPlatformKey) = (name, matches, json, dependencyObjects, isPlatformKey);

        public string Name { get; }

        public Func<string, bool> Matches { get; }

        public bool Json { get; }

        public IReadOnlySet<string> DependencyObjects { get; }

        public Func<string, bool> IsPlatformKey { get; }

        public static Dialect MsBuild { get; } = new("msbuild",
            name => name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase),
            json: false, new HashSet<string>(), _ => false);

        public static Dialect PackageJson { get; } = new("package.json", name => name.Equals("package.json", StringComparison.Ordinal), json: true,
            new HashSet<string>(StringComparer.Ordinal) { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" }, _ => false);

        /// <summary>Composer: <c>require</c>/<c>require-dev</c>. <c>php</c>, <c>ext-*</c> and <c>lib-*</c> are requirements on
        /// the platform, not packages: on a release line they are the line's platform, so they never take the highest.</summary>
        public static Dialect Composer { get; } = new("composer.json", name => name.Equals("composer.json", StringComparison.Ordinal), json: true,
            new HashSet<string>(StringComparer.Ordinal) { "require", "require-dev" },
            key => key.Equals("php", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ext-", StringComparison.OrdinalIgnoreCase)
                   || key.StartsWith("lib-", StringComparison.OrdinalIgnoreCase) || key.StartsWith("php-", StringComparison.OrdinalIgnoreCase));

        public static IReadOnlyList<Dialect> All { get; } = [MsBuild, PackageJson, Composer];
    }

    /// <summary>The name of the package.json object the next line is in (<c>"dependencies": {</c> above it, at depth),
    /// read back over the lines before it; null at the top level or when it can't be told.</summary>
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

    internal static Dialect? DialectOf(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return Dialect.All.FirstOrDefault(d => d.Matches(name));
    }

    /// <summary>A region inside a multi-line item (a child <c>&lt;Version&gt;</c> line) is widened to the whole item: the
    /// item's opening tag above it and its closing tag below it move from the common text into all three sides, so the
    /// item is read as one entry and not its child as a property.</summary>
    internal static IReadOnlyList<Diff3Piece> WidenToItems(IReadOnlyList<Diff3Piece> pieces)
    {
        var list = pieces.Select(p => p switch
        {
            Diff3Piece.Common c => (Common: c.Lines.ToList(), Region: ((List<string> Ours, List<string> Base, List<string> Theirs)?)null),
            Diff3Piece.Region r => (Common: (List<string>?)null, Region: (r.Ours.ToList(), r.Base.ToList(), r.Theirs.ToList())),
            _ => throw new InvalidOperationException(),
        }).ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Region is not { } region || i == 0 || i == list.Count - 1 || list[i - 1].Common is not { } before
                || list[i + 1].Common is not { } after)
                continue;
            var open = -1;
            for (var k = before.Count - 1; k >= 0; k--)
            {
                var bare = before[k].Trim();
                if (bare.StartsWith("</", StringComparison.Ordinal) || SelfClosing().IsMatch(bare) || SingleLineComment().IsMatch(bare))
                    break;
                if (OpenTag().Match(bare) is { Success: true } m && Attributes(m.Groups["attrs"].Value) is { } attrs && attrs.Keys.Any(IsItemSpec))
                {
                    open = k;
                    break;
                }
            }
            if (open < 0)
                continue;
            var name = OpenTag().Match(before[open].Trim()).Groups["name"].Value;
            var close = after.FindIndex(l => l.Trim().Equals($"</{name}>", StringComparison.Ordinal));
            if (close < 0)
                continue;
            var head = before.GetRange(open, before.Count - open);
            var tail = after.GetRange(0, close + 1);
            before.RemoveRange(open, head.Count);
            after.RemoveRange(0, tail.Count);
            foreach (var side in new[] { region.Ours, region.Base, region.Theirs })
            {
                side.InsertRange(0, head);
                side.AddRange(tail);
            }
        }
        return list.Select(p => p.Region is { } r
            ? (Diff3Piece)new Diff3Piece.Region(r.Ours, r.Base, r.Theirs)
            : new Diff3Piece.Common(p.Common!)).ToList();
    }

    // ---------------------------------------------------------------- entries

    /// <summary>One keyed entry of a section: the lines before it that travel with it (comments, blank lines), its own
    /// lines, its key and its values by name (an item's metadata, or <c>""</c> for a scalar).</summary>
    internal sealed record Entry(IReadOnlyList<string> Leading, IReadOnlyList<string> Lines, string Key, string Kind,
        IReadOnlyDictionary<string, string> Values);

    internal sealed record Section(IReadOnlyList<Entry> Entries, IReadOnlyList<string> Trailing);

    private const string Item = "item", Property = "property", Dependency = "dependency";

    /// <summary>The value name of a scalar (a property, a package.json entry).</summary>
    private const string Scalar = "";

    internal static Section? Read(Dialect dialect, IReadOnlyList<string> lines)
    {
        var entries = new List<Entry>();
        var leading = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var bare = line.Trim();
            if (bare.Length == 0 || !dialect.Json && SingleLineComment().IsMatch(bare))
            {
                leading.Add(line);
                continue;
            }
            Entry? entry = null;
            if (dialect.Json)
            {
                if (JsonEntry().Match(bare) is { Success: true } m)
                    entry = new Entry(leading, [line], m.Groups["name"].Value, Dependency, new Dictionary<string, string> { [Scalar] = m.Groups["value"].Value });
            }
            else if (SelfClosing().Match(bare) is { Success: true } self)
                entry = MsBuildEntry(leading, [line], self.Groups["name"].Value, self.Groups["attrs"].Value, children: []);
            else if (PropertyLine().Match(bare) is { Success: true } prop && Attributes(prop.Groups["attrs"].Value) is { } propAttrs
                     && !propAttrs.Keys.Any(IsItemSpec))
                entry = new Entry(leading, [line], $"{Property}\0{prop.Groups["name"].Value}\0{Condition(propAttrs)}".ToLowerInvariant(), Property,
                    new Dictionary<string, string> { [Scalar] = prop.Groups["value"].Value });
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
                entry = MsBuildEntry(leading, own, open.Groups["name"].Value, open.Groups["attrs"].Value, children);
            }
            if (entry is null)
                return null;
            entries.Add(entry);
            leading = [];
        }
        return new Section(entries, leading);
    }

    private static Entry? MsBuildEntry(List<string> leading, List<string> lines, string name, string attrs, List<(string Name, string Value)> children)
    {
        if (Attributes(attrs) is not { } attributes)
            return null;
        var spec = attributes.Keys.FirstOrDefault(IsItemSpec);
        if (spec is null)
        {
            // Without an item spec only an empty property (<Foo />, at most a Condition) is read, as a scalar; a
            // <Message Text="..." /> in a target or an element with children is no entry.
            return children.Count == 0 && attributes.Keys.All(k => k.Equals("Condition", StringComparison.OrdinalIgnoreCase))
                ? new Entry(leading, lines, $"{Property}\0{name}\0{Condition(attributes)}".ToLowerInvariant(), Property, new Dictionary<string, string> { [Scalar] = "" })
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
        return new Entry(leading, lines, $"{Item}\0{name}\0{spec}\0{attributes[spec].Trim()}\0{Condition(attributes)}".ToLowerInvariant(), name, values);
    }

    private static bool IsItemSpec(string attribute)
        => attribute.Equals("Include", StringComparison.OrdinalIgnoreCase) || attribute.Equals("Update", StringComparison.OrdinalIgnoreCase)
           || attribute.Equals("Remove", StringComparison.OrdinalIgnoreCase);

    private static string Condition(IReadOnlyDictionary<string, string> attributes)
        => attributes.FirstOrDefault(a => a.Key.Equals("Condition", StringComparison.OrdinalIgnoreCase)).Value?.Trim() ?? "";

    /// <summary>The attributes of a tag, by name in order; null when the text holds anything else or a name twice.</summary>
    private static Dictionary<string, string>? Attributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rest = AttributePattern().Replace(text, m =>
        {
            result.TryAdd(m.Groups["name"].Value, m.Groups["dq"].Success ? m.Groups["dq"].Value : m.Groups["sq"].Value);
            return "";
        });
        return rest.Trim().Length == 0 && result.Count == AttributePattern().Matches(text).Count ? result : null;
    }

    // ---------------------------------------------------------------- merge

    internal sealed record RegionMerge(IReadOnlyList<string>? Lines, string? Refusal);

    internal static RegionMerge MergeRegion(Dialect dialect, Diff3Piece.Region region, bool versionsOrdered)
    {
        var ours = Read(dialect, region.Ours);
        var @base = Read(dialect, region.Base);
        var theirs = Read(dialect, region.Theirs);
        if (ours is null || @base is null || theirs is null)
            return new(null, "a line that is not a keyed entry (only items, properties and package.json name/value lines merge by key).");
        if (Duplicate(ours) || Duplicate(@base) || Duplicate(theirs))
            return new(null, "a side holds one key twice.");

        var baseByKey = @base.Entries.ToDictionary(e => e.Key, StringComparer.Ordinal);
        var theirsByKey = theirs.Entries.ToDictionary(e => e.Key, StringComparer.Ordinal);
        var oursKeys = ours.Entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var output = new List<Entry>();

        foreach (var o in ours.Entries)
        {
            baseByKey.TryGetValue(o.Key, out var b);
            theirsByKey.TryGetValue(o.Key, out var t);
            if (t is null)
            {
                if (b is null)
                    output.Add(o); // the base branch's insertion
                else if (!Same(o, b))
                    return new(null, "an entry removed on one side and changed on the other.");
                continue; // the pull request removed it, the base branch left it alone
            }
            var merged = Merge(dialect, b, o, t, versionsOrdered);
            if (merged.Refusal is { } refusal)
                return new(null, refusal);
            output.Add(merged.Entry!);
        }
        foreach (var t in theirs.Entries.Where(e => !oursKeys.Contains(e.Key)))
        {
            if (!baseByKey.TryGetValue(t.Key, out var b))
                output.Add(t); // the pull request's insertion, after the base branch's entries
            else if (!Same(t, b))
                return new(null, "an entry removed on one side and changed on the other.");
        }

        var trailing = SameLines(theirs.Trailing, @base.Trailing) || SameLines(theirs.Trailing, ours.Trailing) ? ours.Trailing
            : SameLines(ours.Trailing, @base.Trailing) ? theirs.Trailing
            : null;
        if (trailing is null)
            return new(null, "comments or blank lines after the last entry changed on both sides.");

        var lines = new List<string>();
        foreach (var entry in output)
        {
            lines.AddRange(entry.Leading);
            lines.AddRange(entry.Lines);
        }
        lines.AddRange(trailing);
        if (dialect.Json)
            Commas(lines, output, ours, theirs);
        return new(lines, null);
    }

    private static bool Duplicate(Section section) => section.Entries.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count() != section.Entries.Count;

    private static bool SameLines(IReadOnlyList<string> a, IReadOnlyList<string> b) => a.SequenceEqual(b, StringComparer.Ordinal);

    /// <summary>The same entry by meaning: its values compared by name (an item's metadata order and spacing do not count),
    /// and the comments that travel with it.</summary>
    private static bool Same(Entry a, Entry b)
        => a.Values.Count == b.Values.Count && a.Values.All(v => b.Values.TryGetValue(v.Key, out var other) && other == v.Value)
           && SameLines(a.Leading.Select(l => l.Trim()).ToList(), b.Leading.Select(l => l.Trim()).ToList());

    private sealed record EntryMerge(Entry? Entry, string? Refusal);

    /// <summary>One key present on both sides. <paramref name="b"/> is null when both inserted it.</summary>
    private static EntryMerge Merge(Dialect dialect, Entry? b, Entry o, Entry t, bool versionsOrdered)
    {
        if (Same(o, t))
            return new(o, null);
        if (b is not null && Same(b, o))
            return new(t, null);
        if (b is not null && Same(b, t))
            return new(o, null);
        if (o.Kind == Property)
            return new(null, "a property changed on both sides (a property is one value; neither side wins).");

        // Both changed: per value name, the same three-way rule; a package version takes the highest.
        var names = o.Values.Keys.Concat(t.Values.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            string? vb = null;
            b?.Values.TryGetValue(name, out vb);
            o.Values.TryGetValue(name, out var vo);
            t.Values.TryGetValue(name, out var vt);
            if (vo == vt || vt == vb)
                result[name] = vo;
            else if (vo == vb)
                result[name] = vt;
            else if (versionsOrdered && IsPackageVersion(dialect, o.Key, o.Kind, name) && vo is not null && vt is not null && Highest(dialect, vo, vt) is { } highest)
            {
                // K1: the highest only when both sides raised it; a lowered version is deliberate (a pin away from a
                // broken release) and the highest would undo it silently. Both inserted (no base): nothing to lower.
                if (vb is not null && (Highest(dialect, vb, vo) != vo || Highest(dialect, vb, vt) != vt))
                    return new(null, "a package version lowered on one side and changed on the other (a pin is deliberate; the highest would undo it).");
                result[name] = highest;
            }
            else
                return new(null, "an entry changed on both sides, in a value that has no order (only a package version takes the highest).");
        }
        if (!SameLines(t.Leading, o.Leading) && (b is null || !SameLines(t.Leading, b.Leading)) && (b is null || !SameLines(o.Leading, b.Leading)))
            return new(null, "the comments before an entry changed on both sides.");
        var leading = b is not null && SameLines(o.Leading, b.Leading) ? t.Leading : o.Leading;
        return Rewrite(dialect, o, result) is { } lines
            ? new(o with { Leading = leading, Lines = lines, Values = result.Where(r => r.Value is not null).ToDictionary(r => r.Key, r => r.Value!, StringComparer.OrdinalIgnoreCase) }, null)
            : new(null, "an entry whose merged values cannot be written back into its text.");
    }

    private static bool IsPackageVersion(Dialect dialect, string key, string kind, string name)
        => dialect.Json && name == Scalar && !dialect.IsPlatformKey(key)
           || !dialect.Json && name.Equals("Version", StringComparison.OrdinalIgnoreCase)
              && (kind.Equals("PackageReference", StringComparison.OrdinalIgnoreCase) || kind.Equals("PackageVersion", StringComparison.OrdinalIgnoreCase)
                  || kind.Equals("GlobalPackageReference", StringComparison.OrdinalIgnoreCase));

    /// <summary>The base branch's entry text with every value set to <paramref name="values"/> (null removes it); null when
    /// a value can't be placed (a new child metadata in a single-line item, a value with a quote).</summary>
    private static IReadOnlyList<string>? Rewrite(Dialect dialect, Entry entry, IReadOnlyDictionary<string, string?> values)
    {
        if (values.Values.Any(v => v is not null && (v.Contains('"') || v.Contains('<') || v.Contains('\n'))))
            return null;
        if (dialect.Json)
        {
            var line = entry.Lines[0];
            var m = JsonEntry().Match(line.Trim());
            var start = line.IndexOf(m.Groups["value"].Value, line.IndexOf(':'), StringComparison.Ordinal);
            return [line[..start] + values[Scalar] + line[(start + m.Groups["value"].Length)..]];
        }

        var lines = entry.Lines.ToList();
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
                var head = lines[0][..end].TrimEnd();
                lines[0] = $"{head} {name}=\"{value}\" {lines[0][end..]}";
            }
            else
                return null;
        }
        return lines;
    }

    /// <summary>The highest of two versions, or null when either has no order (a float, a range, a property, two
    /// package.json operators).</summary>
    internal static string? Highest(Dialect dialect, string a, string b)
    {
        string prefixA = "", prefixB = "";
        if (dialect.Json)
        {
            prefixA = RangeOperator(a);
            prefixB = RangeOperator(b);
            if (prefixA != prefixB)
                return null;
        }
        if (SemanticVersion.Parse(a[prefixA.Length..]) is not { } va || SemanticVersion.Parse(b[prefixB.Length..]) is not { } vb)
            return null;
        return va.CompareTo(vb) >= 0 ? a : b;
    }

    private static string RangeOperator(string range) => range.StartsWith('^') || range.StartsWith('~') ? range[..1] : "";

    /// <summary>Every merged package.json entry but the last ends in a comma; the last one as the sides' last entry
    /// did (the object goes on after the region, or it closes).</summary>
    private static void Commas(List<string> lines, List<Entry> output, Section ours, Section theirs)
    {
        if (output.Count == 0)
            return;
        var lastOf = ours.Entries.Count > 0 ? ours.Entries[^1] : theirs.Entries.Count > 0 ? theirs.Entries[^1] : null;
        var lastHasComma = lastOf is not null && lastOf.Lines[0].TrimEnd().EndsWith(',');
        var index = 0;
        for (var e = 0; e < output.Count; e++)
        {
            index += output[e].Leading.Count;
            lines[index] = WithComma(lines[index], e < output.Count - 1 || lastHasComma);
            index += output[e].Lines.Count;
        }
    }

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

    private static ConflictResolution.Refused Refuse(string detail) => new(EvictionReason.ResolverRefused, detail);

    [GeneratedRegex(@"^""(?<name>[^""\\]+)""\s*:\s*[\{\[]$", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectStart();

    [GeneratedRegex(@"^<!--.*-->$", RegexOptions.CultureInvariant)]
    private static partial Regex SingleLineComment();

    [GeneratedRegex(@"^""(?<name>[^""\\]+)""\s*:\s*""(?<value>[^""\\]*)""\s*,?$", RegexOptions.CultureInvariant)]
    private static partial Regex JsonEntry();

    [GeneratedRegex(@"^<(?<name>[A-Za-z_][\w.-]*)(?<attrs>(\s+[^<>]*?)?)\s*/>$", RegexOptions.CultureInvariant)]
    private static partial Regex SelfClosing();

    [GeneratedRegex(@"^<(?<name>[A-Za-z_][\w.-]*)(?<attrs>(\s+[^<>]*?)?)>(?<value>[^<]*)</\k<name>>$", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyLine();

    [GeneratedRegex(@"^<(?<name>[A-Za-z_][\w.-]*)(?<attrs>\s+[^<>]*?)>$", RegexOptions.CultureInvariant)]
    private static partial Regex OpenTag();

    [GeneratedRegex(@"(?<name>[A-Za-z_][\w.:-]*)\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)')", RegexOptions.CultureInvariant)]
    private static partial Regex AttributePattern();
}

/// <summary>A NuGet/SemVer 2 version: 2 to 4 numeric parts, an optional prerelease, build metadata ignored. Floating
/// versions, ranges and property references do not parse.</summary>
internal sealed partial record SemanticVersion(long[] Numbers, string[] Prerelease) : IComparable<SemanticVersion>
{
    public static SemanticVersion? Parse(string text)
    {
        if (Pattern().Match(text.Trim()) is not { Success: true } m)
            return null;
        var numbers = m.Groups["numbers"].Value.Split('.').Select(n => long.Parse(n, CultureInfo.InvariantCulture)).ToList();
        while (numbers.Count < 4)
            numbers.Add(0);
        return new SemanticVersion([.. numbers], m.Groups["pre"].Success ? m.Groups["pre"].Value.Split('.') : []);
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
            return 1;
        for (var i = 0; i < 4; i++)
        {
            var c = Numbers[i].CompareTo(other.Numbers[i]);
            if (c != 0)
                return c;
        }
        if (Prerelease.Length == 0 || other.Prerelease.Length == 0)
            return other.Prerelease.Length.CompareTo(Prerelease.Length) is var r && Prerelease.Length == 0 && other.Prerelease.Length == 0 ? 0 : Prerelease.Length == 0 ? 1 : -1;
        for (var i = 0; i < Math.Min(Prerelease.Length, other.Prerelease.Length); i++)
        {
            var (a, b) = (Prerelease[i], other.Prerelease[i]);
            var numericA = long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var na);
            var numericB = long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var nb);
            var c = numericA && numericB ? na.CompareTo(nb) : numericA ? -1 : numericB ? 1 : string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant());
            if (c != 0)
                return c;
        }
        return Prerelease.Length.CompareTo(other.Prerelease.Length);
    }

    [GeneratedRegex(@"^(?<numbers>\d{1,9}\.\d{1,9}(\.\d{1,9}){0,2})(-(?<pre>[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*))?(\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
