using CodeCoverage.Git.Backports;
using CodeCoverage.Versioning.Releases;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// The <c>backport:</c> section of <c>coverage.yml</c> (#570, PRD §5.8), read from the DEFAULT branch like
/// <c>mergeQueue:</c> (O16): a line never governs how changes reach it. Absent means the defaults. Errors are returned,
/// never thrown; a section with errors is reported on the backport and the defaults apply.
/// <code>
/// backport:
///   onConflict: comment          # comment (default, D7) | draft: push the conflict markers, open a draft pull request
///   enqueue: true                # default (D12): hand each backport pull request to the merge queue, when it queues the line
///   defaultLabels: all           # D5: all (default) | fixes: older lines only for fixes, the current line always (D16) | off
///   fixLabels: [bug]             # what marks a fix under `fixes`, beside a fix: or fix(…): title
///   trialPick: warn              # segment G (Q10): warn (default): comment the lines the backport would conflict on | refuse: the queue does not merge | off
///   trialPicks:                  # Q10: the same, per line
///     dotnet-11.x: refuse
///   lines:                       # D4: override the lifecycle data per line
///     dotnet-8.x: keep           # keep past end of life | retire: no longer supported
///   markers:                     # D8
///     disable: [setup-action]    # tfm | framework-packages | global.json | setup-action | npm-frameworks
///     packages: ["@mintplayer/ng-bootstrap", "MintPlayer.Spark*"]
/// </code>
/// </summary>
public static class BackportYml
{
    public const string Comment = "comment";
    public const string Draft = "draft";
    public const string All = "all";
    public const string Fixes = "fixes";
    public const string Off = "off";
    public const string Warn = "warn";
    public const string Refuse = "refuse";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static BackportYmlResult Parse(string? yml)
    {
        if (string.IsNullOrWhiteSpace(yml))
            return new(BackportSection.Default, []);

        FileBackport? file;
        try
        {
            file = Deserializer.Deserialize<FileRoot?>(yml)?.Backport;
        }
        catch (Exception ex)
        {
            return new(BackportSection.Default, [$"coverage.yml could not be read: {ex.Message}"]);
        }
        if (file is null)
            return new(BackportSection.Default, []);

        var errors = new List<string>();
        var onConflict = file.OnConflict?.Trim() ?? Comment;
        if (onConflict is not (Comment or Draft))
        {
            errors.Add($"backport.onConflict '{onConflict}' is not one of: {Comment}, {Draft}.");
            onConflict = Comment;
        }

        var disable = (file.Markers?.Disable ?? []).Select(k => k?.Trim() ?? "").ToList();
        foreach (var kind in disable.Where(k => !PlatformMarkerOptions.KindNames.Contains(k)))
            errors.Add($"backport.markers.disable '{kind}' is not a marker kind: {string.Join(", ", PlatformMarkerOptions.KindNames)}.");
        var packages = (file.Markers?.Packages ?? []).Select(p => p?.Trim() ?? "").ToList();
        foreach (var package in packages.Where(p => !PlatformMarkerOptions.PackagePattern.IsMatch(p)))
            errors.Add($"backport.markers.packages '{package}' is not a package name or pattern (letters, digits, @ / . _ - and *).");

        var enqueue = file.Enqueue ?? true;

        // #570 M8b, D5. A misspelt mode labels nothing rather than everything.
        var defaultLabels = file.DefaultLabels?.Trim() ?? All;
        if (defaultLabels is not (All or Fixes or Off))
        {
            errors.Add($"backport.defaultLabels '{defaultLabels}' is not one of: {All}, {Fixes}, {Off}.");
            defaultLabels = Off;
        }
        // Segment G. A misspelt mode warns: it never refuses a merge the owner did not ask to refuse.
        var trialPick = file.TrialPick?.Trim() ?? Warn;
        if (trialPick is not (Warn or Refuse or Off))
        {
            errors.Add($"backport.trialPick '{trialPick}' is not one of: {Warn}, {Refuse}, {Off}.");
            trialPick = Warn;
        }
        var trialPicks = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in file.TrialPicks ?? [])
        {
            var name = key?.Trim() ?? "";
            var mode = value?.Trim() ?? "";
            if (ReleaseLine.TryParse(name) is null)
                errors.Add($"backport.trialPicks '{name}' is not a line name such as `dotnet-10.x`.");
            else if (mode is not (Warn or Refuse or Off))
                errors.Add($"backport.trialPicks.{name} '{mode}' is not one of: {Warn}, {Refuse}, {Off}.");
            else
                trialPicks[name] = mode;
        }
        var fixLabels = file.FixLabels is null ? BackportSection.Default.FixLabels
            : [.. file.FixLabels.Select(l => l?.Trim() ?? "").Where(l => l.Length is > 0 and <= 50).Distinct(StringComparer.OrdinalIgnoreCase)];

        // #570 M8, D4: the owner's override per line, keyed by the line's name.
        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in file.Lines ?? [])
        {
            var name = key?.Trim() ?? "";
            var setting = value?.Trim() ?? "";
            if (ReleaseLine.TryParse(name) is null)
                errors.Add($"backport.lines '{name}' is not a line name such as `dotnet-10.x`.");
            else if (setting is not (SupportedLines.Keep or SupportedLines.Retire))
                errors.Add($"backport.lines.{name} '{setting}' is not one of: {SupportedLines.Keep}, {SupportedLines.Retire}.");
            else
                lines[name] = setting;
        }

        if (errors.Count > 0)
            return new(BackportSection.Default with { OnConflict = onConflict, Enqueue = enqueue, DefaultLabels = defaultLabels, FixLabels = fixLabels, TrialPick = trialPick }, errors);
        return new(new BackportSection(onConflict,
            new PlatformMarkerOptions([.. disable.Distinct(StringComparer.OrdinalIgnoreCase)], [.. packages.Distinct(StringComparer.OrdinalIgnoreCase)]), enqueue)
        {
            Lines = lines,
            DefaultLabels = defaultLabels,
            FixLabels = fixLabels,
            TrialPick = trialPick,
            TrialPicks = trialPicks,
        }, []);
    }

    private sealed class FileRoot
    {
        public FileBackport? Backport { get; set; }
    }

    private sealed class FileBackport
    {
        public string? OnConflict { get; set; }
        public bool? Enqueue { get; set; }
        public string? DefaultLabels { get; set; }
        public List<string?>? FixLabels { get; set; }
        public string? TrialPick { get; set; }
        public Dictionary<string, string?>? TrialPicks { get; set; }
        public Dictionary<string, string?>? Lines { get; set; }
        public FileMarkers? Markers { get; set; }
    }

    private sealed class FileMarkers
    {
        public List<string?>? Disable { get; set; }
        public List<string?>? Packages { get; set; }
    }
}

/// <param name="OnConflict"><see cref="BackportYml.Comment"/> or <see cref="BackportYml.Draft"/>.</param>
/// <param name="Enqueue">Whether an opened backport pull request goes to the merge queue (#570 D12), when the queue is on
/// and queues its line.</param>
public sealed record BackportSection(string OnConflict, PlatformMarkerOptions Markers, bool Enqueue = true)
{
    /// <summary><c>backport.lines</c> (#570 M8, D4): <c>keep</c> or <c>retire</c> per line name, overriding the lifecycle data.</summary>
    public IReadOnlyDictionary<string, string> Lines { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary><c>backport.defaultLabels</c> (#570 M8b, D5): <see cref="BackportYml.All"/>, <see cref="BackportYml.Fixes"/> or <see cref="BackportYml.Off"/>.</summary>
    public string DefaultLabels { get; init; } = BackportYml.All;

    /// <summary><c>backport.fixLabels</c>: the labels that mark a pull request as a fix under <c>defaultLabels: fixes</c>, beside a <c>fix:</c> title.</summary>
    public IReadOnlyList<string> FixLabels { get; init; } = ["bug"];

    /// <summary><c>backport.trialPick</c> (segment G): <see cref="BackportYml.Warn"/>, <see cref="BackportYml.Refuse"/> or
    /// <see cref="BackportYml.Off"/>; what the merge queue does when a labelled line's backport would conflict.</summary>
    public string TrialPick { get; init; } = BackportYml.Warn;

    /// <summary><c>backport.trialPicks</c> (Q10, "configurable per line"): a line's own mode, by the line's name.</summary>
    public IReadOnlyDictionary<string, string> TrialPicks { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The trial pick mode of the line named <paramref name="line"/>: its own, else <see cref="TrialPick"/>.</summary>
    public string TrialPickFor(string line) => TrialPicks.TryGetValue(line, out var mode) ? mode : TrialPick;

    public static readonly BackportSection Default = new(BackportYml.Comment, new PlatformMarkerOptions([], []));
}

public sealed record BackportYmlResult(BackportSection Section, IReadOnlyList<string> Errors);
