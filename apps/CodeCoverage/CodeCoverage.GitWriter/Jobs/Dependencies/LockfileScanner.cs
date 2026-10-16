using System.Text.RegularExpressions;

namespace CodeCoverage.GitWriter.Jobs.Dependencies;

/// <summary>A lockfile's verdict: a refusal, or the package names it newly brings in.</summary>
public sealed record LockfileScan(string? Reason, string? Detail, IReadOnlyList<string> NewPackages)
{
    public bool Refused => Reason is not null;
}

/// <summary>
/// Gap B (docs/code-coverage/dependency-updates-PRD.md §13.11.4), bot-side: the apply runner's own provenance check ran on
/// a machine the attacker may own, so the bot reads the ADDED lockfile lines itself, ecosystem-neutrally:
/// <list type="bullet">
/// <item>no <c>git+</c>, <c>git:</c>, <c>github:</c>, <c>file:</c>, <c>link:</c>, <c>http:</c> or <c>ssh:</c> source;</item>
/// <item>every URL is <c>https</c>, without credentials, on an allowed host or one the base lockfile already names;</item>
/// <item>where the format records integrity, every changed entry has it (npm <c>resolved</c> → <c>integrity</c>, NuGet
/// <c>resolved</c> → <c>contentHash</c>, Cargo/poetry/uv <c>version</c> → <c>checksum</c>/<c>hash</c>, a go.sum
/// <c>h1:</c> on every line, pnpm's <c>resolution: {integrity</c>);</item>
/// <item>the package names it adds that the base did not hold are counted; over the cap is a human's call, not a refusal.</item>
/// </list>
/// </summary>
public static partial class LockfileScanner
{
    /// <summary>Per file name: the package-name pattern (group <c>n</c>), and an integrity rule.</summary>
    private sealed record Format(Regex Names, Regex? EntryMarker = null, Regex? Integrity = null, Regex? EveryLine = null, Regex? SameLineTrigger = null, Regex? SameLine = null);

    private static readonly Dictionary<string, Format> Formats = new(StringComparer.Ordinal)
    {
        ["package-lock.json"] = new(NpmNames(), JsonKey("resolved"), JsonKey("integrity")),
        ["npm-shrinkwrap.json"] = new(NpmNames(), JsonKey("resolved"), JsonKey("integrity")),
        ["yarn.lock"] = new(YarnNames(), YarnResolved(), YarnIntegrity()),
        ["pnpm-lock.yaml"] = new(PnpmNames(), SameLineTrigger: PnpmResolution(), SameLine: PnpmIntegrity()),
        ["bun.lock"] = new(BunNames()),
        ["packages.lock.json"] = new(NuGetNames(), JsonKey("resolved"), JsonKey("contentHash")),
        ["Cargo.lock"] = new(TomlName(), CargoRegistryVersion(), TomlKey("checksum")),
        ["go.sum"] = new(GoSumNames(), EveryLine: GoSumLine()),
        ["poetry.lock"] = new(TomlName(), TomlKey("version"), TomlHash()),
        ["uv.lock"] = new(TomlName(), TomlKey("version"), TomlHash()),
        ["Pipfile.lock"] = new(PipfileNames()),
        ["pdm.lock"] = new(TomlName()),
        ["composer.lock"] = new(ComposerNames()),
        ["gradle.lockfile"] = new(GradleNames()),
        ["Gemfile.lock"] = new(GemNames()),
    };

    [GeneratedRegex(@"""(?:[^""]*/)?node_modules/(?<n>(?:@[^/""]+/)?[^/""]+)""\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex NpmNames();

    [GeneratedRegex(@"^""?(?<n>@?[^@\s""]+)@", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex YarnNames();

    [GeneratedRegex(@"^\s+(?:resolved\s+""|resolution:\s)", RegexOptions.CultureInvariant)]
    private static partial Regex YarnResolved();

    [GeneratedRegex(@"^\s+(?:integrity\s+\S|checksum:\s+\S)", RegexOptions.CultureInvariant)]
    private static partial Regex YarnIntegrity();

    [GeneratedRegex(@"^  '?/?(?<n>(?:@[^@/\s']+/)?[^@/\s']+)@", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex PnpmNames();

    [GeneratedRegex(@"^\s+resolution:", RegexOptions.CultureInvariant)]
    private static partial Regex PnpmResolution();

    [GeneratedRegex(@"\{\s*integrity:\s*\S", RegexOptions.CultureInvariant)]
    private static partial Regex PnpmIntegrity();

    [GeneratedRegex(@"^\s+""(?<n>(?:@[^@/""\s]+/)?[^@/""\s]+)"":\s*\[", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex BunNames();

    [GeneratedRegex(@"^\s{6}""(?<n>[A-Za-z0-9_.-]+)"":\s*\{", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex NuGetNames();

    [GeneratedRegex(@"^name = ""(?<n>[^""]+)""", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex TomlName();

    [GeneratedRegex(@"^version = """, RegexOptions.CultureInvariant)]
    private static partial Regex CargoRegistryVersion();

    [GeneratedRegex(@"hash = ""sha256:[0-9a-f]{64}""", RegexOptions.CultureInvariant)]
    private static partial Regex TomlHash();

    [GeneratedRegex(@"^(?<n>\S+) v\S+ h1:", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex GoSumNames();

    [GeneratedRegex(@"\A\S+ v\S+ h1:[A-Za-z0-9+/]{43}=\z", RegexOptions.CultureInvariant)]
    private static partial Regex GoSumLine();

    [GeneratedRegex(@"^\s{8}""(?<n>[^""]+)"":\s*\{", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex PipfileNames();

    [GeneratedRegex(@"""name"":\s*""(?<n>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex ComposerNames();

    [GeneratedRegex(@"^(?<n>[^:=\s#]+:[^:=\s]+):", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex GradleNames();

    [GeneratedRegex(@"^    (?<n>[A-Za-z0-9_.-]+) \(", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex GemNames();

    /// <summary>A source spec that is not a registry: git, a forge shorthand, a local path or link, plain http, ssh.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_.+-])(?:git\+[a-z]+:|git:|github:|gitlab:|bitbucket:|file:|link:|http:|ssh:)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenSource();

    [GeneratedRegex(@"(?<![A-Za-z0-9])https://(?<auth>[^/\s""'<>@]*@)?(?<host>[^/\s""'<>:#?@]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HttpsUrl();

    private static Regex JsonKey(string key) => new($@"^\s*""{key}""\s*:\s*""\S", RegexOptions.CultureInvariant);

    private static Regex TomlKey(string key) => new($@"^{key} = ""\S", RegexOptions.CultureInvariant);

    public static bool IsKnownFormat(string fileName) => Formats.ContainsKey(fileName);

    /// <summary>Scans one changed lockfile.</summary>
    public static LockfileScan Scan(PatchFile file, string baseText, LockfileScanOptions options)
    {
        var name = DependencyFiles.FileName(file.Path);
        if (!Formats.TryGetValue(name, out var format))
            return new(DependencyPatchReasons.UnownedLockfile, $"{name} is not a lockfile format the bot reads.", []);
        var baseHosts = HttpsUrl().Matches(baseText).Select(m => m.Groups["host"].Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var allowed = options.AllowedHosts.Select(h => h.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

        var lineNumber = 0;
        string? pendingMarker = null;
        foreach (var hunk in file.Hunks)
        {
            lineNumber = hunk.NewStart - 1;
            foreach (var line in hunk.Lines)
            {
                if (line.Kind != '-')
                    lineNumber++;
                if (line.Kind != '+')
                    continue;
                var text = line.Text.TrimEnd('\r');
                if (ForbiddenSource().Match(text) is { Success: true } forbidden)
                    return new(DependencyPatchReasons.LockfileSource, $"Line {lineNumber} adds a '{forbidden.Value}' source, which is not a registry.", []);
                foreach (Match url in HttpsUrl().Matches(text))
                {
                    if (url.Groups["auth"].Success)
                        return new(DependencyPatchReasons.LockfileSource, $"Line {lineNumber} adds a URL with credentials.", []);
                    var host = url.Groups["host"].Value.ToLowerInvariant();
                    if (!allowed.Contains(host) && !baseHosts.Contains(host))
                        return new(DependencyPatchReasons.LockfileSource, $"Line {lineNumber} adds a URL on {host}, which is neither an allowed registry nor in the base lockfile.", []);
                }
                if (format.EveryLine is { } every && text.Length > 0 && !every.IsMatch(text))
                    return new(DependencyPatchReasons.LockfileIntegrity, $"Line {lineNumber} is not a hashed go.sum line.", []);
                if (format.SameLineTrigger is { } trigger && trigger.IsMatch(text) && !format.SameLine!.IsMatch(text))
                    return new(DependencyPatchReasons.LockfileIntegrity, $"Line {lineNumber} adds a resolution without an integrity value.", []);
                if (format.EntryMarker is { } marker)
                {
                    if (format.Integrity!.IsMatch(text))
                        pendingMarker = null;
                    else if (marker.IsMatch(text))
                    {
                        if (pendingMarker is not null)
                            return new(DependencyPatchReasons.LockfileIntegrity, $"{pendingMarker} changes an entry without adding its integrity value.", []);
                        pendingMarker = $"Line {lineNumber}";
                    }
                }
            }
        }
        if (pendingMarker is not null)
            return new(DependencyPatchReasons.LockfileIntegrity, $"{pendingMarker} changes an entry without adding its integrity value.", []);

        var baseNames = format.Names.Matches(baseText).Select(m => m.Groups["n"].Value).ToHashSet(StringComparer.Ordinal);
        var added = string.Join('\n', file.AddedLines.Select(l => l.TrimEnd('\r')));
        var newNames = format.Names.Matches(added).Select(m => m.Groups["n"].Value).Where(n => !baseNames.Contains(n)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList();
        return new(null, null, newNames);
    }
}
