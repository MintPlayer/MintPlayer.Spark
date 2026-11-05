using System.Text.RegularExpressions;

namespace CodeCoverage.Git.Backports;

/// <summary>A platform value the pull request changed that normalization left to git (D8's safety rule): the line has
/// to see it, in the merge or in its conflict. <paramref name="Before"/> is empty for a value the pull request added
/// where the line has nothing to take a value from.</summary>
public sealed record PlatformChange(string Path, string Marker, string Before, string After);

/// <summary>
/// A configured first-party package (<c>backport.markers.packages</c>) the pull request raised wholly above the line's
/// major, which normalization therefore dropped (#570 D11): the line keeps <paramref name="LineValue"/>, so it probably
/// lacks what the newer upstream release brought. <paramref name="Kind"/> is <c>npm</c> or <c>nuget</c>, as the
/// versioning engine names package kinds.
/// </summary>
public sealed record DependencyRaise(string Path, string Kind, string Package, string Before, string After, string LineValue);

/// <summary>The merge base and the change with their platform values rewritten to the release line's; null where the
/// input was null (the file is absent on that side).</summary>
public sealed record MarkerNormalization(string? Base, string? Theirs, IReadOnlyList<PlatformChange> PlatformChanges)
{
    /// <summary>The first-party raises normalization dropped (#570 D11).</summary>
    public IReadOnlyList<DependencyRaise> Raises { get; init; } = [];
}

/// <summary>
/// S2, marker normalization (#570, PRD §5.3 step 1, D8 as sharpened by S1 in §8.1). Before a backport's three-way merge,
/// each platform value ("marker": a target framework, a framework package's version, the SDK of global.json, a
/// workflow's setup version, a framework's npm range) of the merge base and of the change is rewritten to the release
/// line's own value, so a hunk that only moved the platform vanishes and a real edit next to a marker merges clean.
/// <para>
/// It works per occurrence, keyed by marker name and its ordinal among that name (a package's name is its id):
/// </para>
/// <list type="bullet">
/// <item>the pull request left the value as it was, or moved it wholly above the line's major: base and change take the
/// line's value;</item>
/// <item>the pull request changed it and the line's own major (or a value without one) is involved: left to git and
/// reported, so a real platform edit (net10.0 → net10.0;net11.0) never vanishes;</item>
/// <item>an occurrence the pull request added takes the line's value of the same marker, else of a sibling of its family
/// (another framework package), when it is wholly above the line; with nothing to take a value from it is reported;</item>
/// <item>an occurrence the pull request removed: the base takes the line's value, so the removal applies;</item>
/// <item>an occurrence the line does not have is left as it is.</item>
/// </list>
/// Only the value's characters are replaced, so line endings, spacing and quoting stay byte for byte. Lockfiles are not
/// markers: they are regenerated (the merge queue's lockfile strategy), never normalized.
/// </summary>
public static class PlatformMarkers
{
    private sealed record Kind(string Id, Func<string, bool> Handles, Regex Pattern, string? FixedName, Func<string, string> Family, bool TargetFramework,
        Func<string, bool>? Accepts = null)
    {
        /// <summary>A repository's own first-party packages (<c>markers.packages</c>), as opposed to the built-in platform kinds.</summary>
        public bool FirstParty => Accepts is not null;
    }

    private sealed record Occurrence(Kind Kind, string Name, string Key, string Value, int Start);

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    private static readonly string[] MsBuildExtensions = [".csproj", ".fsproj", ".vbproj", ".props", ".targets"];

    private static readonly Kind[] BuiltIn =
    [
        new(PlatformMarkerOptions.Tfm, IsMsBuild, new(@"<(?<name>TargetFrameworks?)>(?<value>[^<]*)</\k<name>>", Options), null, name => name, TargetFramework: true),
        new(PlatformMarkerOptions.FrameworkPackages, IsMsBuild,
            new(@"<(?:PackageReference|PackageVersion)\b[^>]*?\bInclude=""(?<name>Microsoft\.(?:AspNetCore|Extensions|EntityFrameworkCore)\b[^""]*)""[^>]*?\bVersion=""(?<value>[^""]*)""", Options | RegexOptions.IgnoreCase),
            null, _ => "dotnet-framework", false),
        new(PlatformMarkerOptions.GlobalJson, path => FileName(path).Equals("global.json", StringComparison.OrdinalIgnoreCase),
            new(@"""sdk""\s*:\s*\{[^{}]*?""version""\s*:\s*""(?<value>[^""]*)""", Options), "global.json sdk", name => name, false),
        new(PlatformMarkerOptions.SetupAction, IsWorkflow,
            new(@"^[ \t]*(?:-[ \t]*)?(?<name>dotnet-version|node-version)[ \t]*:[ \t]*['""]?(?<value>[^'""\s#\[\]{},]+)", Options | RegexOptions.Multiline),
            null, name => name, false),
        new(PlatformMarkerOptions.NpmFrameworks, IsPackageJson,
            new(@"""(?<name>@angular/[^""]+|react|react-dom|vue)""\s*:\s*""(?<value>[^""]*)""", Options), null,
            name => name.StartsWith("@angular/", StringComparison.Ordinal) ? "@angular" : name, false),
    ];

    private static readonly Regex AnyMsBuildPackage =
        new(@"<(?:PackageReference|PackageVersion)\b[^>]*?\bInclude=""(?<name>[^""]+)""[^>]*?\bVersion=""(?<value>[^""]*)""", Options | RegexOptions.IgnoreCase);

    private static readonly Regex AnyNpmPackage = new(@"""(?<name>[^""]+)""\s*:\s*""(?<value>[^""]*)""", Options);

    /// <summary>The built-in kinds a repository kept, then one MSBuild and one npm kind for its own packages (D8); a
    /// package matching several patterns belongs to the first pattern's family.</summary>
    private static Kind[] Kinds(PlatformMarkerOptions? options)
    {
        if (options is null)
            return BuiltIn;
        var kinds = BuiltIn.Where(k => !options.Disable.Contains(k.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (options.Packages.Count > 0)
        {
            var patterns = options.Packages
                .Select(p => (Pattern: p, Regex: new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*", StringComparison.Ordinal) + "$", Options | RegexOptions.IgnoreCase)))
                .ToArray();
            bool Accepts(string name) => patterns.Any(p => p.Regex.IsMatch(name));
            string Family(string name) => "package:" + patterns.First(p => p.Regex.IsMatch(name)).Pattern;
            kinds.Add(new("packages", IsMsBuild, AnyMsBuildPackage, null, Family, false, Accepts));
            kinds.Add(new("packages", IsPackageJson, AnyNpmPackage, null, Family, false, Accepts));
        }
        return [.. kinds];
    }

    /// <summary>Whether <paramref name="path"/> can hold markers, so whether <see cref="Normalize"/> needs its blobs.</summary>
    public static bool Handles(string path, PlatformMarkerOptions? options = null)
    {
        path = path.Replace('\\', '/');
        return Kinds(options).Any(k => k.Handles(path));
    }

    /// <summary>Normalizes one file of a backport. <paramref name="ours"/> is the release line's copy (null when the
    /// line lacks the file), <paramref name="base"/> and <paramref name="theirs"/> the merge base's and the change's.
    /// Null when <paramref name="path"/> holds no markers. <paramref name="options"/> is the repository's
    /// <c>backport.markers</c>; null keeps the built-in catalog.</summary>
    public static MarkerNormalization? Normalize(string path, string? ours, string? @base, string? theirs, PlatformMarkerOptions? options = null)
    {
        path = path.Replace('\\', '/');
        var kinds = Kinds(options).Where(k => k.Handles(path)).ToArray();
        if (kinds.Length == 0)
            return null;

        var line = Read(kinds, ours);
        var before = Read(kinds, @base);
        var after = Read(kinds, theirs);
        var lineByKey = line.ToDictionary(o => o.Key);
        var baseByKey = before.ToDictionary(o => o.Key);
        var theirsByKey = after.ToDictionary(o => o.Key);

        var baseEdits = new List<(Occurrence At, string Value)>();
        var theirsEdits = new List<(Occurrence At, string Value)>();
        var changes = new List<PlatformChange>();
        var raises = new List<DependencyRaise>();

        foreach (var key in before.Select(o => o.Key).Union(after.Select(o => o.Key)))
        {
            baseByKey.TryGetValue(key, out var b);
            theirsByKey.TryGetValue(key, out var t);
            lineByKey.TryGetValue(key, out var l);

            if (b is not null && t is not null)
            {
                if (l is null)
                    continue;
                if (b.Value == t.Value || WhollyAbove(b.Kind, [b.Value, t.Value], l.Value))
                {
                    baseEdits.Add((b, l.Value));
                    theirsEdits.Add((t, l.Value));
                    if (b.Value != t.Value && t.Kind.FirstParty)
                        raises.Add(new(path, IsPackageJson(path) ? "npm" : "nuget", t.Name, b.Value, t.Value, l.Value));
                }
                else
                {
                    changes.Add(new(path, t.Name, b.Value, t.Value));
                }
            }
            else if (t is not null)
            {
                var source = l ?? line.FirstOrDefault(o => o.Kind == t.Kind && o.Kind.Family(o.Name) == t.Kind.Family(t.Name));
                if (source is null)
                    changes.Add(new(path, t.Name, "", t.Value));
                else if (WhollyAbove(t.Kind, [t.Value], source.Value))
                    theirsEdits.Add((t, source.Value));
            }
            else if (l is not null)
            {
                baseEdits.Add((b!, l.Value));
            }
        }

        return new(Rewrite(@base, baseEdits), Rewrite(theirs, theirsEdits), changes) { Raises = raises };
    }

    private static List<Occurrence> Read(Kind[] kinds, string? text)
    {
        var occurrences = new List<Occurrence>();
        if (text is null)
            return occurrences;
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var taken = new HashSet<int>();
        foreach (var kind in kinds)
        {
            foreach (Match match in kind.Pattern.Matches(text))
            {
                var name = kind.FixedName ?? match.Groups["name"].Value;
                var value = match.Groups["value"];
                // A configured pattern can name a built-in marker too (Microsoft.Extensions.*): it is one occurrence.
                if (kind.Accepts is { } accepts && !accepts(name) || !taken.Add(value.Index))
                    continue;
                var id = name.ToLowerInvariant();
                ordinals[id] = ordinals.GetValueOrDefault(id) + 1;
                occurrences.Add(new(kind, name, $"{id}#{ordinals[id]}", value.Value, value.Index));
            }
        }
        return occurrences;
    }

    /// <summary>Every value has a major and every major lies above the line's highest: the change is the newer platform's
    /// business alone. False when the line's value has no major (netstandard2.0, lts/*), so nothing is guessed.</summary>
    private static bool WhollyAbove(Kind kind, string[] values, string lineValue)
    {
        var lineMajors = Majors(kind, lineValue);
        if (lineMajors.Count == 0)
            return false;
        var highest = lineMajors.Max();
        return values.All(value => Majors(kind, value) is { Count: > 0 } majors && majors.All(m => m > highest));
    }

    private static readonly Regex TfmMajor = new(@"^\s*net(?:coreapp)?(?<major>\d+)\.", Options | RegexOptions.IgnoreCase);
    private static readonly Regex FirstNumber = new(@"\d+", Options);

    private static List<int> Majors(Kind kind, string value)
    {
        if (kind.TargetFramework)
        {
            return value.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(token => TfmMajor.Match(token))
                .Where(m => m.Success && int.TryParse(m.Groups["major"].Value, out _))
                .Select(m => int.Parse(m.Groups["major"].Value))
                .ToList();
        }
        var number = FirstNumber.Match(value);
        return number.Success && int.TryParse(number.Value, out var major) ? [major] : [];
    }

    private static string? Rewrite(string? text, List<(Occurrence At, string Value)> edits)
    {
        if (text is null || edits.Count == 0)
            return text;
        var result = text;
        foreach (var (at, value) in edits.OrderByDescending(e => e.At.Start))
            result = string.Concat(result.AsSpan(0, at.Start), value, result.AsSpan(at.Start + at.Value.Length));
        return result;
    }

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static bool IsPackageJson(string path) => FileName(path) == "package.json";

    private static bool IsMsBuild(string path) => MsBuildExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static bool IsWorkflow(string path)
        => path.StartsWith(".github/", StringComparison.Ordinal) && (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
}
