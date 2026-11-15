using System.Globalization;
using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.Git.MergeQueue.Resolution.Manifests;
using CodeCoverage.LookupReferences;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Dependencies.Policy;

/// <summary>
/// The dependency policy on records only (docs/code-coverage/dependency-updates-PRD.md §13.2, N4): lookup records plus
/// configuration plus stored state in, a plan and the pull request body's lists out. A pure function: no database, no
/// clock, no registry.
/// </summary>
/// <remarks>
/// <para><b>Per record</b>, each candidate is read through R1 and R17 only: its <c>updateType</c> label raised by the version
/// floor, its <c>releaseTimestamp</c> (or the bot's first-seen time, D-S12a) against the cooldown, and its version token
/// compared for equality with fleet-red versions and hold snapshots. No version is ever parsed beyond the floor.</para>
/// <para><b>Per group</b> (D-C1: a configured group, else the lookup's <c>groupName</c>, else the dependency name), the
/// engine tries the levels present from the largest down. At each level every member takes its best candidate within it;
/// the group passes when no member is held (R2 holds, human holds, fleet-red, R1 markers), the group is under the
/// enforced ceiling (Q9), within its threshold or approved (R6), and old enough (Q16) — falling back, member by member, to
/// a smaller candidate of the same level that is. The first level that passes is planned; every level above it is
/// reported (awaiting approval, cooldown, held).</para>
/// <para><b>Platforms</b> (R7): the highest channel above the current one on the SDK's track (#606 D8: lts, stable or
/// preview); a major never auto-merges; computed locations are reported, never planned (R8). Platforms are evaluated before
/// the package groups: on a major move the SDK's companions ride in its group at the new major, and the move is pending
/// until each one has a release there (#606 D2, D3).</para>
/// </remarks>
public static class DependencyPolicyEngine
{
    private static readonly IReadOnlyList<EUpdateLevel> LevelsDescending = [EUpdateLevel.Major, EUpdateLevel.Minor, EUpdateLevel.Patch];

    /// <summary>
    /// platform-locations-PRD P10/S6: the <c>runs.using</c> Node runtimes GitHub runs for a JavaScript action. GitHub
    /// publishes no feed of them (the list lives in the runner's source and the metadata-syntax docs), so it is this table,
    /// sent with every node move: updating it is a bot deploy, never an action release. node16 is gone from the runners.
    /// </summary>
    public static readonly IReadOnlyList<string> GitHubActionRuntimes = ["node20", "node24"];

    public static DependencyPolicyOutcome Evaluate(DependencyPolicyInput input)
    {
        var context = new Context(input);
        context.LiftHolds();
        context.RecordFirstSeen();

        var considered = new List<LookupRecord>();
        foreach (var record in input.Lookup.Records)
        {
            if (record.Candidates.Count == 0)
            {
                // companion-pins-PRD M3: a member with no update still holds a lockstep group at its current version (H2:
                // lockstep is the group's, implied when a follow names any member).
                context.AddIdle(GroupOf(input.Config, record), record);
                continue;
            }
            if (context.Screen(record) is { } screened)
            {
                context.Report(screened);
                continue;
            }
            considered.Add(record);
        }

        // #606 D2: the platforms go first, so a companion that rides in its SDK's group leaves the package groups.
        context.FindCompanions(considered, input.Lookup.Platforms);
        foreach (var platform in input.Lookup.Platforms)
            context.EvaluatePlatform(platform);
        context.ApplyCompanionRanges();

        var groups = new Dictionary<string, List<LookupRecord>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var original in considered)
        {
            if (context.ForGrouping(original) is not { } record)
                continue;
            var group = GroupOf(input.Config, record);
            if (!groups.TryGetValue(group, out var members))
            {
                groups[group] = members = [];
                order.Add(group);
            }
            members.Add(record);
        }
        context.JoinBlockedGroups(groups, order);

        foreach (var group in order)
            context.EvaluateGroup(group, groups[group]);

        return context.Outcome();
    }

    /// <summary>D-C1 plus configuration: a configured group, else the first candidate <c>groupName</c>, else the record's,
    /// else the dependency name (one dependency across many package files moves together, §5.1 #8).
    /// The bot's runtime is a group of its own (<c>MintPlayer/MintPlayer.Spark dependency-update-v1</c>): its pin shares the
    /// dependency name with the repository's other actions from that repository (<c>versioning-v1</c>), which do cool down,
    /// and one of them in its cooldown held the runtime back with it (SpaServices #111, 2026-11-13).</summary>
    public static string GroupOf(DependencyPolicyConfig config, LookupRecord record)
        => config.ConfiguredGroup(record.Ecosystem, record.DepName)
            ?? (IsBotRuntime(record) ? $"{record.DepName} {record.CurrentValue}" : null)
            ?? record.Candidates.Select(c => c.GroupName).FirstOrDefault(g => !string.IsNullOrWhiteSpace(g))
            ?? (string.IsNullOrWhiteSpace(record.GroupName) ? null : record.GroupName)
            ?? record.DepName;

    /// <summary>
    /// The bot's own action pin (<c>dependency-action@sha # dependency-update-v1</c>) is the bot's runtime, so it never waits out
    /// the cooldown: a repository would otherwise run the previous action for days after every release. Own-release detection
    /// cannot catch it, because the producing account (the MintPlayer organization) is not the consuming one (P2).
    /// </summary>
    private static bool IsBotRuntime(LookupRecord record)
        => (record.Ecosystem == "github-actions" || record.Manager == "github-actions")
            && string.Equals(record.DepName, Lifecycle.DependencyOnboarding.ActionRepository, StringComparison.Ordinal)
            && string.Equals(record.CurrentValue, Lifecycle.DependencyOnboarding.ActionTag, StringComparison.Ordinal);

    /// <summary>The token that identifies a candidate: the digest of a digest update (a new digest of the same tag is a new
    /// release), else its version, else its value. A token, never parsed.</summary>
    public static string Token(LookupCandidate candidate)
        => (candidate.UpdateType is EUpdateType.Digest or EUpdateType.PinDigest ? candidate.Digest : null)
            ?? candidate.Version ?? candidate.Value ?? candidate.Digest ?? string.Empty;

    /// <summary>
    /// A digest update Renovate reported without a <c>newVersion</c> (S12: a Docker <c>python:3.11-slim</c> re-push) keeps
    /// its version by definition, so the current version stands in for the missing one (D-P5). Anything else is returned
    /// as it is, and a missing version stays a hold (R1).
    /// </summary>
    public static LookupCandidate Effective(LookupRecord record, LookupCandidate candidate)
        => candidate is { Version: null, UpdateType: EUpdateType.Digest or EUpdateType.PinDigest, Digest: not null } && record.CurrentVersion is { } current
            ? candidate with { Version = current, Missing = [.. candidate.Missing.Where(m => m != "version")] }
            : candidate;

    private sealed class View(LookupRecord record, LookupCandidate candidate, int index)
    {
        public LookupRecord Record { get; } = record;
        public LookupCandidate Candidate { get; } = candidate;
        public int Index { get; } = index;
        public EUpdateLevel Level { get; set; } = EUpdateLevel.Major;
        public EDependencyReportReason? Problem { get; set; }
        public string? Detail { get; set; }
        public DateTimeOffset? Until { get; set; }
        public bool Security { get; set; }
        public bool InCooldown { get; set; }
    }

    private sealed class Context(DependencyPolicyInput input)
    {
        private readonly DependencyPolicyConfig config = input.Config;
        private readonly DateTimeOffset now = input.Now;
        private readonly DependencyUpdateState next = Copy(input.State);
        private readonly Dictionary<(string Eco, string Dep, string Token), DateTimeOffset> firstSeen = new();
        private readonly List<DependencyPlannedGroup> planned = [];
        private readonly List<DependencyReportEntry> available = [];
        private readonly List<DependencyReportEntry> held = [];
        private readonly List<DependencyReportEntry> awaiting = [];
        private readonly List<DependencyReportEntry> ignored = [];
        private readonly List<DependencyPlatformReport> platformReports = [];
        private readonly List<DependencyHold> lifted = [];
        /// <summary>prerelease-track-scenarios-PRD D5: platform groups held in this evaluation; the alignment under the same
        /// name waits with them.</summary>
        private readonly HashSet<string> heldPlatformGroups = new(StringComparer.Ordinal);
        /// <summary>#655: per group, the blockers whose held groups joined it (<see cref="JoinBlockedGroups"/>).</summary>
        private readonly Dictionary<string, HashSet<string>> joinedBlockers = new(StringComparer.Ordinal);
        private readonly HashSet<(string Group, string DepNames, string Targets)> currentTargets = [];
        private readonly HashSet<(string Eco, string Dep, string Version)> red = input.Fleet.RedVersions
            .Where(r => !string.Equals(r.Repository, input.State.Repository, StringComparison.Ordinal) || input.State.Repository.Length == 0)
            .Select(r => (r.Ecosystem, r.DepName, r.Version)).ToHashSet();
        private readonly HashSet<(string Eco, string Dep, string Version)> own = input.Fleet.OwnReleases
            .Select(r => (r.Ecosystem, r.DepName, r.Version)).ToHashSet();
        private readonly HashSet<(string Eco, string Dep)> security = input.SecurityFixes.Select(s => (s.Ecosystem, s.DepName)).ToHashSet();

        // #606 D2: each SDK's companion records (on the SDK's current major), and the ones riding in its planned group.
        private readonly Dictionary<string, List<LookupRecord>> companions = new(StringComparer.Ordinal);
        private readonly Dictionary<LookupRecord, string> companionOf = new(ReferenceEqualityComparer.Instance);
        /// <summary>prerelease-track-scenarios-PRD D8: the channel's major of each SDK the repository is on a preview of.</summary>
        private readonly Dictionary<string, long> previewMajor = new(StringComparer.Ordinal);
        private readonly HashSet<LookupRecord> riding = new(ReferenceEqualityComparer.Instance);
        /// <summary>follower-cooldown-PRD H1: per SDK, the companions on an older major in a project that targets only the SDK's
        /// channel. They move to the channel's major, with the platform group or in one of their own.</summary>
        private readonly Dictionary<string, List<LookupRecord>> lagging = new(StringComparer.Ordinal);

        /// <summary>companion-version-maps-PRD §6.2: a record whose first matching companion entry carries a range
        /// (<c>versions</c> or <c>bundled</c>), with the key channel of its project and its range for the repository's SDK.</summary>
        private sealed record Ranged(string Sdk, DependencyCompanionEntry Entry, string? Channel, string? RepositorySdk, CompanionRange? Current);

        private sealed record CompanionRange(NpmRange Range, string Label);

        private readonly Dictionary<LookupRecord, Ranged> ranged = new(ReferenceEqualityComparer.Instance);

        /// <summary>§6.3: the ranged records below their range, which align like H1.</summary>
        private readonly Dictionary<string, List<LookupRecord>> rangedBehind = new(StringComparer.Ordinal);

        /// <summary>§6.3: the ranged records that do not ride, with only the candidates inside their range.</summary>
        private readonly Dictionary<LookupRecord, LookupRecord?> rangedView = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// multi-target-dependency-split-PRD §7.0: one platform package of one MSBuild file in a project that targets two or more
        /// channels, with every occurrence the lookup found (one per conditioned ItemGroup, or the one unconditioned). It
        /// moves per TFM, in the platform group, as one item with a layout, and never in a package group.
        /// </summary>
        private sealed record Split(string Sdk, string File, string DepName, List<LookupRecord> Records, DependencyCompanionEntry? Entry, string? RepositorySdk);

        private readonly Dictionary<string, List<Split>> splits = new(StringComparer.Ordinal);
        private readonly HashSet<LookupRecord> splitting = new(ReferenceEqualityComparer.Instance);

        /// <summary>Per SDK, the non-computed TFM list of each file that holds one (a project, a Directory.Build.props).</summary>
        private readonly Dictionary<string, Dictionary<string, List<string>>> tfmLists = new(StringComparer.Ordinal);

        private static readonly System.Text.RegularExpressions.Regex CanonicalCondition = new(@"^'\$\(TargetFramework\)' == '([A-Za-z0-9.+-]+)'$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        private static List<string> TfmTokens(string value) => [.. value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        private static string DirectoryOf(string path) => path.LastIndexOf('/') is var i and >= 0 ? path[..i] : string.Empty;

        /// <summary>
        /// §7.0: the TFMs a package file builds for, from <paramref name="lists"/> (holder file → TFM list): a project's own
        /// list, else the nearest Directory.Build.props's; a Directory.Packages.props builds for every list under its folder.
        /// The holder is the file whose list it is (null for a Directory.Packages.props, or when none is known).
        /// </summary>
        private static (string? Holder, List<string> Tfms) FileTfms(IReadOnlyDictionary<string, List<string>> lists, string file)
        {
            if (string.Equals(System.IO.Path.GetFileName(file), "Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
            {
                var folder = DirectoryOf(file);
                var under = lists.Where(p => folder.Length == 0 || p.Key.StartsWith(folder + "/", StringComparison.Ordinal)).SelectMany(p => p.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => TfmChannel(t) is { } c ? ChannelOrder(c) : (-1, 0)).ThenBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
                return (null, under);
            }
            if (lists.TryGetValue(file, out var own))
                return (file, own);
            for (var dir = DirectoryOf(file); ; dir = DirectoryOf(dir))
            {
                var props = dir.Length == 0 ? "Directory.Build.props" : $"{dir}/Directory.Build.props";
                if (lists.TryGetValue(props, out var inherited))
                    return (props, inherited);
                if (dir.Length == 0)
                    return (null, []);
            }
        }

        private static bool IsMultiTarget(IEnumerable<string> tfms) => tfms.Select(TfmChannel).OfType<string>().Distinct(StringComparer.Ordinal).Count() >= 2;

        /// <summary>
        /// §7.0: collects the platform packages of multi-target files. A (file, package) with an occurrence under a condition the
        /// lookup could not map, or under any other than the canonical <c>'$(TargetFramework)' == 'X'</c>, is not split (D-f):
        /// it stays an ordinary package and the reason is listed.
        /// </summary>
        private void CollectSplits(PlatformRecord platform, string ecosystem, IReadOnlyList<LookupRecord> records)
        {
            var sdk = platform.Sdk;
            var computed = platform.Locations.Where(l => l.Kind == "tfm" && l.Computed).Select(l => l.File).ToHashSet(StringComparer.Ordinal);
            var lists = platform.Locations.Where(l => l.Kind == "tfm" && !l.Computed && !computed.Contains(l.File)).GroupBy(l => l.File, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.SelectMany(l => TfmTokens(l.Value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.Ordinal);
            tfmLists[sdk] = lists;
            var repositorySdk = RepositorySdk(platform);
            var found = new List<Split>();
            foreach (var key in records.Where(r => r.Ecosystem == ecosystem && !splitting.Contains(r))
                .GroupBy(r => (r.PackageFile, Dep: r.DepName.ToLowerInvariant())))
            {
                var first = key.First();
                var entry = config.CompanionEntry(sdk, first.DepName);
                if (entry is null && !config.IsAccountPackage(first.Ecosystem, first.DepName))
                    continue;
                if (!IsMultiTarget(FileTfms(lists, first.PackageFile).Tfms))
                    continue;
                if (key.FirstOrDefault(r => r.Condition is { } c && (r.TargetFrameworks is not [_] || !CanonicalCondition.IsMatch(c))) is { } odd)
                {
                    Report(new DependencyReportEntry($"platform:{sdk}", odd.Ecosystem, odd.DepName, odd.PackageFile, odd.CurrentValue, null, null,
                        EDependencyReportReason.NeedsHuman,
                        $"under a condition the bot does not split ({odd.Condition}): moved as one package for every TFM (multi-target-dependency-split D-f)"));
                    continue;
                }
                found.Add(new Split(sdk, first.PackageFile, first.DepName, [.. key], entry, repositorySdk));
                splitting.UnionWith(key);
            }
            if (found.Count > 0)
                splits[sdk] = found;
        }

        /// <summary>
        /// P3 (M6, M13): a multi-target list's next value: the move's channel added (beside the newest channel's TFMs, with
        /// their platform suffix), then the end-of-life channels dropped, oldest first, while more than
        /// <paramref name="keepPrevious"/> older channels remain. A TFM without a channel always stays.
        /// </summary>
        private static List<string> NextTfms(IReadOnlyList<string> tfms, string? addChannel, IReadOnlyCollection<string> endOfLife, int keepPrevious)
        {
            var list = tfms.ToList();
            var channels = list.Select(TfmChannel).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            if (channels.Count == 0)
                return list;
            var newest = channels.MaxBy(ChannelOrder)!;
            if (addChannel is not null && !channels.Contains(addChannel))
            {
                var at = list.FindLastIndex(t => TfmChannel(t) == newest) + 1;
                var added = list.Where(t => TfmChannel(t) == newest).Select(t => $"net{addChannel}{t[(t.IndexOf('-') is var dash and >= 0 ? dash : t.Length)..]}").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                list.InsertRange(at, added);
                newest = addChannel;
            }
            var older = list.Select(TfmChannel).OfType<string>().Distinct(StringComparer.Ordinal).Where(c => c != newest).OrderBy(ChannelOrder).ToList();
            var remaining = older.Count;
            foreach (var channel in older.Where(endOfLife.Contains))
            {
                if (remaining <= keepPrevious)
                    break;
                list.RemoveAll(t => TfmChannel(t) == channel);
                remaining--;
            }
            return list;
        }

        /// <summary>The occurrence a TFM has today: the record conditioned on it, else the unconditioned one, else none (a new TFM).</summary>
        private static LookupRecord? OccurrenceFor(Split split, string tfm)
            => split.Records.FirstOrDefault(r => r.TargetFrameworks?.Contains(tfm, StringComparer.OrdinalIgnoreCase) == true)
                ?? split.Records.FirstOrDefault(r => r.Condition is null);

        /// <summary>
        /// §7.0: the plan item of a split for the file's next TFM list, or null when nothing changes. Each channel TFM takes the
        /// highest release on its channel's major (or on the major it already has, when that is higher), inside the companion
        /// range keyed by that channel, never below what it has; a TFM without a channel takes the lowest channel's version.
        /// A new TFM without a release on its major is <paramref name="missing"/>. When every TFM ends on one version and the
        /// file has one unconditioned occurrence, the item is an ordinary one (no layout).
        /// </summary>
        private DependencyPlannedItem? SplitItem(Split split, IReadOnlyList<string> tfms, string? moveTo, bool previewTarget, List<string> missing)
        {
            var views = split.Records.SelectMany(Candidates).Where(v => v.Problem is null).ToList();
            var explicitPreview = split.Records.Any(ExplicitPreview);
            var layout = new List<(string Tfm, string? Version, View? Pick)>();
            foreach (var tfm in tfms)
            {
                if (TfmChannel(tfm) is not { } channel)
                {
                    layout.Add((tfm, null, null));
                    continue;
                }
                var current = OccurrenceFor(split, tfm) is { } occurrence ? occurrence.CurrentVersion ?? occurrence.CurrentValue : null;
                // A package that follows the platform's major (a channel rule, an account package) takes the channel's major;
                // a ranged one (versions map, bundled) has its own majors (Roslyn 5.x on .NET 11), so its range decides, and
                // without a key for this channel it stays on its major.
                var follows = split.Entry is null or { IsChannelRule: true };
                var range = follows ? null : RangeOf(split.Entry!, split.RepositorySdk, channel);
                var major = follows
                    ? Math.Max(UpdateTypeFloor.LeadingNumber(channel) ?? 0, UpdateTypeFloor.LeadingNumber(current) ?? 0)
                    : UpdateTypeFloor.LeadingNumber(current) ?? 0;
                // A TFM rides past the cooldown only when it has to move: no version yet (a new TFM), or one outside its channel's
                // range or major. One that already fits waits like any project's record does, or a single-target project on the
                // same package keeps the old version for the cooldown while this TFM moves, and restore refuses the downgrade
                // (Tools #217, NU1605 on Microsoft.CodeAnalysis.CSharp.Workspaces 5.12.0 vs 5.9.0).
                var mustMove = current is null || (range is not null ? !Admits(range, current) : UpdateTypeFloor.LeadingNumber(current) != major);
                var moving = channel == moveTo && mustMove;
                var newer = views.Where(v => (range is not null || MajorOf(v.Candidate) == major) && (range is null || Admits(range, Token(v.Candidate)))
                        && (current is null || Compare(Token(v.Candidate), current) > 0))
                    .OrderByDescending(v => SemVer.TryParse(Token(v.Candidate), out var s) ? s : null).ToList();
                // A prerelease is offered on a move to a preview channel, or by a rule's track: preview. A TFM on a prerelease
                // follows prereleases only until a release on its major exists (prerelease-track-scenarios-PRD D3, the owner's
                // rule: "if a release within the channel is available ... it should pick that one").
                var preview = moving && previewTarget || explicitPreview
                    || IsPreviewToken(current) && !newer.Any(v => !IsPreviewToken(Token(v.Candidate)));
                var fits = newer.Where(v => preview || !IsPreviewToken(Token(v.Candidate))).ToList();
                var pick = fits.FirstOrDefault(v => moving || !v.InCooldown);
                if (fits.FirstOrDefault() is { InCooldown: true } cooling && cooling != pick && !moving)
                    Report(Entry($"platform:{split.Sdk}", cooling, EDependencyReportReason.Cooldown, $"released less than {Days(config.Cooldown)} ago") with { Until = cooling.Until });
                if (pick is null && current is null)
                {
                    missing.Add(split.DepName);
                    return null;
                }
                layout.Add((tfm, pick is null ? current : Token(pick.Candidate), pick));
            }
            if (!layout.Any(l => l.Version is not null))
                return null;
            var lowest = layout.Where(l => l.Version is not null).MinBy(l => ChannelOrder(TfmChannel(l.Tfm)!));
            layout = [.. layout.Select(l => l.Version is null ? (l.Tfm, lowest.Version, lowest.Pick) : l)];

            var unconditioned = split.Records.Where(r => r.Condition is null).ToList();
            var sameStructure = unconditioned.Count == 1 && split.Records.Count == 1
                || unconditioned.Count == 0 && split.Records.SelectMany(r => r.TargetFrameworks!).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(tfms);
            var oneVersion = layout.Select(l => l.Version).Distinct(StringComparer.Ordinal).Count() == 1;
            var unchanged = layout.All(l => (OccurrenceFor(split, l.Tfm) is { } r ? r.CurrentVersion ?? r.CurrentValue : null) == l.Version);
            if (unchanged && (sameStructure && (unconditioned.Count == 0 || oneVersion)))
                return null;

            var best = layout.Where(l => l.Pick is not null).Select(l => l.Pick!).MaxBy(v => SemVer.TryParse(Token(v.Candidate), out var s) ? s : null);
            var top = layout.Select(l => l.Version!).MaxBy(v => SemVer.TryParse(v, out var s) ? s : null)!;
            var primary = unconditioned.FirstOrDefault() ?? split.Records[0];
            var candidate = best is not null && Token(best.Candidate) == top
                ? best.Candidate
                : new LookupCandidate { Version = top, Value = top, UpdateType = EUpdateType.Patch };
            var level = layout.Where(l => l.Pick is not null).Select(l => l.Pick!.Level).DefaultIfEmpty(EUpdateLevel.Patch).Max();
            var record = primary with { LockFiles = [.. split.Records.SelectMany(r => r.LockFiles).Distinct(StringComparer.Ordinal)] };
            if (oneVersion && unconditioned.Count == 1 && split.Records.Count == 1)
                return new DependencyPlannedItem(record, candidate, level);
            return new DependencyPlannedItem(record, candidate, level)
            {
                Layout = [.. layout.Select(l => new PlanTfmVersion { TargetFramework = l.Tfm, Version = l.Version! })],
            };
        }

        /// <summary>The split items of an SDK for its files' next TFM lists (<paramref name="next"/>: holder → list).</summary>
        private List<DependencyPlannedItem> SplitItems(string sdk, IReadOnlyDictionary<string, List<string>> next, string? moveTo, bool previewTarget, List<string> missing)
        {
            var items = new List<DependencyPlannedItem>();
            foreach (var split in splits.GetValueOrDefault(sdk) ?? [])
            {
                if (SplitItem(split, FileTfms(next, split.File).Tfms, moveTo, previewTarget, missing) is { } item)
                    items.Add(item);
            }
            return items;
        }

        /// <summary>The channel of a .NET TFM token (<c>net11.0</c>, <c>net11.0-windows</c> → <c>11.0</c>); null for
        /// <c>netstandard2.0</c> and .NET Framework (<c>net481</c>).</summary>
        private static string? TfmChannel(string tfm)
            => System.Text.RegularExpressions.Regex.Match(tfm, @"^net(\d+\.\d+)(-|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) is { Success: true } m ? m.Groups[1].Value : null;

        private static long? MajorOf(LookupRecord record) => UpdateTypeFloor.LeadingNumber(record.CurrentVersion ?? record.CurrentValue);

        private static long? MajorOf(LookupCandidate candidate) => UpdateTypeFloor.LeadingNumber(candidate.Version ?? candidate.Value);

        /// <summary>
        /// #606 D2: the records that move with an SDK's major. A record is a companion of an SDK whose platform record the
        /// lookup has, when it lives in the SDK's package ecosystem, matches a companion glob, and is on the SDK's current
        /// major today (<c>System.IO.Abstractions</c> 22.x matches <c>System.*</c> but is not a .NET 10 companion). A platform
        /// kept beside the previous one (<c>strategy: add</c>, <c>keepPrevious</c>, <c>lockPackageMajor</c>) has none: its
        /// packages must keep building for the older target too.
        /// </summary>
        public void FindCompanions(IReadOnlyList<LookupRecord> records, IEnumerable<PlatformRecord> platforms)
        {
            foreach (var platform in platforms)
            {
                var sdk = platform.Sdk;
                var settings = config.File.Platform(sdk);
                if (DependencyCompanions.Ecosystem(sdk) is not { } ecosystem || settings.LockPackageMajor)
                    continue;
                // multi-target-dependency-split-PRD §7.0: a multi-target project's platform packages move per TFM, whatever the
                // strategy (M13: such a project always adds the new channel). lockPackageMajor still keeps every package's major.
                if (sdk == "dotnet")
                    CollectSplits(platform, ecosystem, records);
                if (settings.Strategy != "replace" || settings.KeepPrevious > 0)
                    continue;
                if (UpdateTypeFloor.LeadingNumber(platform.Current.Channels.FirstOrDefault()) is not { } major)
                    continue;
                if (config.Companions(sdk).Count == 0)
                    continue;
                // follower-cooldown-PRD FS4/S2: a project whose every tfm names no channel (netstandard2.0, net48) has no
                // platform to move, so its references are ordinary packages. A file without a tfm (Directory.Packages.props)
                // can't be told and stays a companion.
                var standardOnly = platform.Locations.Where(l => l.Kind == "tfm").GroupBy(l => l.File, StringComparer.Ordinal)
                    .Where(g => g.All(l => l.Channel is null)).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
                // follower-cooldown-PRD H1: a project that targets only the platform's channel (every token of every tfm, none
                // computed), and a tool manifest, which runs on the SDK.
                var channel = platform.Current.Channels[0];
                var onChannel = platform.Locations.Where(l => l.Kind == "tfm").GroupBy(l => l.File, StringComparer.Ordinal)
                    .Where(g => g.All(l => !l.Computed && l.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .All(t => TfmChannel(t) == channel))).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
                // prerelease-track-scenarios-PRD D4: a Directory.Packages.props sets the version for every project under its
                // folder, so it targets only the channel when each of them does (and at least one is known).
                var tfmFiles = platform.Locations.Where(l => l.Kind == "tfm").Select(l => l.File).Distinct(StringComparer.Ordinal).ToList();
                bool TargetsOnlyTheChannel(string file)
                {
                    if (onChannel.Contains(file) || sdk == "dotnet" && file.EndsWith("dotnet-tools.json", StringComparison.Ordinal))
                        return true;
                    if (!string.Equals(System.IO.Path.GetFileName(file), "Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
                        return false;
                    var folder = DirectoryOf(file);
                    var under = tfmFiles.Where(f => folder.Length == 0 || f.StartsWith(folder + "/", StringComparison.Ordinal)).ToList();
                    return under.Count > 0 && under.All(onChannel.Contains);
                }
                // companion-version-maps-PRD §6.2: a project's key channel is its lowest (it must load there too); a file
                // with no TFM (Directory.Packages.props, a tool manifest) takes the repository's.
                var lowest = platform.Locations.Where(l => l.Kind == "tfm" && !l.Computed)
                    .SelectMany(l => l.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => (l.File, Channel: TfmChannel(t))))
                    .Where(t => t.Channel is not null).GroupBy(t => t.File, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Select(t => t.Channel!).MinBy(ChannelOrder)!, StringComparer.Ordinal);
                var repositorySdk = RepositorySdk(platform);
                var list = new List<LookupRecord>();
                var behind = new List<LookupRecord>();
                var below = new List<LookupRecord>();
                foreach (var record in records.Where(r => r.Ecosystem == ecosystem && !companionOf.ContainsKey(r) && !ranged.ContainsKey(r) && !splitting.Contains(r)
                    && !standardOnly.Contains(r.PackageFile)))
                {
                    // follower-cooldown-PRD G2: an upstream package of the account rides like a Microsoft one.
                    // companion-version-maps-PRD §6.1: the first matching entry decides; a versions/bundled entry gives a range.
                    var entry = config.CompanionEntry(sdk, record.DepName);
                    if (entry is { IsChannelRule: false })
                    {
                        var keyChannel = lowest.GetValueOrDefault(record.PackageFile) ?? channel;
                        var one = new Ranged(sdk, entry, keyChannel, repositorySdk, RangeOf(entry, repositorySdk, keyChannel));
                        ranged[record] = one;
                        if (one.Current is { } range && !Admits(range, record.CurrentVersion ?? record.CurrentValue)
                            && record.Candidates.Any(c => Admits(range, c.Version) && Compare(c.Version, record.CurrentVersion ?? record.CurrentValue) > 0))
                            below.Add(record);
                        continue;
                    }
                    if (entry is null && !config.IsAccountPackage(record.Ecosystem, record.DepName))
                        continue;
                    // follower-cooldown-PRD H2 (Tools #218): on a released channel, a prerelease of its major (11.0.0-rc.1 on
                    // net11.0) is behind the release like an older major, and rides to it with them, or a sibling's 11.0.0 needs
                    // it and restore refuses. prerelease-track-scenarios-PRD D2: not when a rule chose track: preview for it.
                    var preReleaseBehind = MajorOf(record) == major && !platform.Current.Preview && !ExplicitPreview(record)
                        && IsPreviewToken(record.CurrentVersion ?? record.CurrentValue) && TargetsOnlyTheChannel(record.PackageFile)
                        && record.Candidates.Any(c => MajorOf(c) == major && !IsPreviewToken(c.Version));
                    if (MajorOf(record) == major && !preReleaseBehind)
                        list.Add(record);
                    else if ((preReleaseBehind || MajorOf(record) < major) && TargetsOnlyTheChannel(record.PackageFile) && record.Candidates.Any(c => MajorOf(c) == major))
                        behind.Add(record);
                    else
                        continue;
                    companionOf[record] = sdk;
                }
                if (platform.Current.Preview)
                    previewMajor[sdk] = major;
                if (list.Count > 0)
                    companions[sdk] = list;
                if (behind.Count > 0)
                    lagging[sdk] = behind;
                if (below.Count > 0)
                    rangedBehind[sdk] = below;
            }
        }

        /// <summary>companion-version-maps-PRD S4: the repository's SDK version: <c>global.json</c>'s, else the lowest exact
        /// <c>setup-dotnet</c> version, else the newest SDK of the repository's channel (what a floating <c>11.0.x</c> installs).</summary>
        private static string? RepositorySdk(PlatformRecord platform)
        {
            static bool Exact(string value) => SemVer.TryParse(value, out _);
            var exact = platform.Locations.Where(l => !l.Computed && Exact(l.Value));
            return exact.FirstOrDefault(l => l.Kind == "global.json")?.Value
                ?? exact.Where(l => l.Kind == "setup-action").Select(l => l.Value).MinBy(v => SemVer.TryParse(v, out var s) ? s : null)
                ?? platform.Candidates.FirstOrDefault(c => c.Channel == platform.Current.Channels.FirstOrDefault())?.LatestSdk;
        }

        /// <summary>The SDK feature band of an SDK version: <c>11.0.100-rc.2</c>, <c>11.0.105</c> → <c>11.0.1xx</c>.</summary>
        private static string? Band(string? sdk)
            => SemVer.TryParse(sdk, out var v) && v.Patch >= 100 ? $"{v.Major}.{v.Minor}.{v.Patch / 100}xx" : null;

        private static (int, int) ChannelOrder(string channel)
        {
            var parts = channel.Split('.');
            return (int.TryParse(parts[0], CultureInfo.InvariantCulture, out var major) ? major : 0,
                parts.Length > 1 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var minor) ? minor : 0);
        }

        /// <summary>§6.2: the range an entry gives a record on <paramref name="sdk"/>, keyed by its band first, then by the
        /// record's channel; for a <c>bundled</c> entry, <c>&lt;=major.minor</c> of the component that SDK ships (§6.4).</summary>
        private CompanionRange? RangeOf(DependencyCompanionEntry entry, string? sdk, string? channel)
        {
            if (entry.Bundled is { } component)
            {
                var shipped = Band(sdk) is { } sdkBand && input.SdkComponents.FirstOrDefault(c => c.Band == sdkBand) is { } found
                    ? component switch
                    {
                        EDependencyBundledComponent.Msbuild => found.Msbuild,
                        EDependencyBundledComponent.Roslyn => found.Roslyn,
                        _ => found.Nuget,
                    }
                    : null;
                if (!SemVer.TryParse(shipped, out var version) || !NpmRange.TryParse($"<={version.Major}.{version.Minor}", out var ceiling))
                    return null;
                return new CompanionRange(ceiling, $"<={version.Major}.{version.Minor}, the {component.ToString().ToLowerInvariant()} SDK {sdk} ships");
            }
            if (entry.Versions is not { } map)
                return null;
            var key = Band(sdk) is { } band && map.ContainsKey(band) ? band : channel is not null && map.ContainsKey(channel) ? channel : null;
            return key is not null && NpmRange.TryParse(map[key], out var range) ? new CompanionRange(range, $"{map[key]} for {key}") : null;
        }

        private static bool Admits(CompanionRange range, string? version) => version is not null && range.Range.Admits(version);

        private static int Compare(string? a, string? b)
            => SemVer.TryParse(a, out var x) && SemVer.TryParse(b, out var y) ? x.CompareTo(y) : 0;

        /// <summary>§6.3: the highest release of a ranged record inside <paramref name="range"/> that may move now.</summary>
        private View? RangeRider(LookupRecord record, CompanionRange range, bool skipCooldown)
            => Candidates(record).Where(v => v.Problem is null && (skipCooldown || !v.InCooldown) && Admits(range, Token(v.Candidate)))
                .OrderByDescending(v => SemVer.TryParse(Token(v.Candidate), out var s) ? s : null).FirstOrDefault();

        /// <summary>
        /// companion-version-maps-PRD §6.3: a ranged record that does not ride keeps only its candidates inside its range for
        /// the package groups; the highest one above it is listed as available. A record with no range is left alone.
        /// </summary>
        public void ApplyCompanionRanges()
        {
            foreach (var (record, one) in ranged)
            {
                if (riding.Contains(record) || one.Current is not { } range)
                    continue;
                var within = record.Candidates.Where(c => Admits(range, c.Version)).ToList();
                var above = record.Candidates.Where(c => !Admits(range, c.Version) && Compare(c.Version, record.CurrentVersion ?? record.CurrentValue) > 0)
                    .MaxBy(c => SemVer.TryParse(c.Version, out var s) ? s : null);
                if (above is not null)
                    Report(new DependencyReportEntry($"platform:{one.Sdk}", record.Ecosystem, record.DepName, record.PackageFile, record.CurrentValue,
                        Token(above), null, EDependencyReportReason.AboveCompanionRange,
                        $"above {range.Label} (platforms.{one.Sdk}.companions; companion-version-maps)"));
                rangedView[record] = within.Count == 0 ? null : record with { Candidates = within };
            }
        }

        /// <summary>
        /// The record as the package groups see it: null for a companion riding in its SDK's group, and a companion without
        /// the candidates above its SDK's current major (they move only with the SDK, #606 D2); the record itself otherwise.
        /// </summary>
        public LookupRecord? ForGrouping(LookupRecord record)
        {
            // multi-target-dependency-split-PRD §7.0: a split package moves only in the platform group, so no package group
            // can move one of its TFMs while the platform group moves another (Tools #216, NU1605).
            if (riding.Contains(record) || splitting.Contains(record))
                return null;
            if (rangedView.TryGetValue(record, out var view))
                return view;
            if (!companionOf.ContainsKey(record))
                return record;
            var major = MajorOf(record);
            var within = record.Candidates.Where(c => !(MajorOf(c) > major)).ToList();
            return within.Count == 0 ? null : record with { Candidates = within };
        }

        /// <summary>The companions of an SDK whose next major is not riding: each is listed as moving with the platform.</summary>
        private void ReportStranded(string sdk)
        {
            foreach (var record in (companions.GetValueOrDefault(sdk) ?? []).Concat(lagging.GetValueOrDefault(sdk) ?? []))
            {
                if (riding.Contains(record))
                    continue;
                var major = MajorOf(record);
                var above = record.Candidates.FirstOrDefault(c => MajorOf(c) > major);
                if (above is null)
                    continue;
                Report(new DependencyReportEntry($"platform:{sdk}", record.Ecosystem, record.DepName, record.PackageFile, record.CurrentValue,
                    Token(above), EUpdateLevel.Major, EDependencyReportReason.MovesWithPlatform,
                    $"a companion of {sdk}: its next major moves with the SDK's major, in the platform:{sdk} group (#606 D2)"));
            }
        }

        public void LiftHolds()
        {
            var fingerprint = DependencyHolds.Fingerprint(input.Lookup);
            var standing = new List<DependencyHold>();
            foreach (var hold in next.Holds)
            {
                if (DependencyHolds.Lifts(hold, input.Lookup, fingerprint))
                {
                    lifted.Add(hold);
                    continue;
                }
                if (input.IsScheduledSlot && hold.Kind is EDependencyHoldKind.Blocker or EDependencyHoldKind.Unattributed)
                    hold.SlotsHeld++;
                standing.Add(hold);
            }
            next.Holds = standing;
            if (input.IsScheduledSlot)
                next.Slots++;
            next.LastEvaluatedAtUtc = now.UtcDateTime;
        }

        /// <summary>
        /// #653 D2: a group held on a blocker that has its own group in this run joins that group. The resolver showed the
        /// two must move together (SpaServices: <c>MintPlayer.SourceGenerators</c> 13.1.0 needs
        /// <c>MintPlayer.SourceGenerators.Attributes</c> 13.1.0), so they are planned, approved and applied as one. The hold
        /// stays, and is what keeps them together, until the blocker's current version moves (D1); its retry clock is reset
        /// so the group does not split again after <see cref="DependencyHolds.RetrySlots"/> slots.
        /// </summary>
        public void JoinBlockedGroups(Dictionary<string, List<LookupRecord>> groups, List<string> order)
        {
            // #655: a hold on a plan that left out its own blocker, which is a member of the group with something to offer,
            // says the plan was incomplete, not that the group cannot resolve (SpaServices: the joined group planned
            // MintPlayer.SourceGenerators 12.2.0 alone, refused on MintPlayer.SourceGenerators.Attributes). It lifts.
            foreach (var hold in next.Holds.Where(h => h.Kind == EDependencyHoldKind.Blocker && groups.ContainsKey(h.Group)).ToList())
            {
                if (!hold.Blockers.Any(b => !hold.DepNames.Contains(b.DepName, StringComparer.Ordinal)
                        && groups[hold.Group].Any(r => string.Equals(r.DepName, b.DepName, StringComparison.Ordinal) && r.Candidates.Count > 0)))
                    continue;
                next.Holds.Remove(hold);
                lifted.Add(hold);
            }

            var joined = true;
            while (joined)
            {
                joined = false;
                foreach (var hold in next.Holds.Where(h => h.Kind == EDependencyHoldKind.Blocker && groups.ContainsKey(h.Group)))
                {
                    var target = hold.Blockers
                        .Select(b => order.FirstOrDefault(g => g != hold.Group && groups[g].Any(r => string.Equals(r.DepName, b.DepName, StringComparison.Ordinal))))
                        .FirstOrDefault(g => g is not null);
                    if (target is null)
                        continue;
                    groups[target].AddRange(groups[hold.Group]);
                    groups.Remove(hold.Group);
                    order.Remove(hold.Group);
                    if (!joinedBlockers.TryGetValue(target, out var required))
                        joinedBlockers[target] = required = new HashSet<string>(StringComparer.Ordinal);
                    required.UnionWith(hold.Blockers.Select(b => b.DepName));
                    if (joinedBlockers.Remove(hold.Group, out var carried))
                        required.UnionWith(carried);
                    hold.SlotsHeld = 0;
                    joined = true;
                    break;
                }
            }
        }

        public void RecordFirstSeen()
        {
            foreach (var seen in next.FirstSeen)
                firstSeen[(seen.Ecosystem, seen.DepName, seen.Version)] = new DateTimeOffset(DateTime.SpecifyKind(seen.FirstSeenAtUtc, DateTimeKind.Utc));

            void See(string eco, string dep, string token)
            {
                if (token.Length == 0 || firstSeen.ContainsKey((eco, dep, token)))
                    return;
                firstSeen[(eco, dep, token)] = now;
                next.FirstSeen.Add(new DependencySeenVersion { Ecosystem = eco, DepName = dep, Version = token, FirstSeenAtUtc = now.UtcDateTime });
            }

            foreach (var record in input.Lookup.Records)
                foreach (var candidate in record.Candidates)
                    See(record.Ecosystem, record.DepName, Token(candidate));
            foreach (var platform in input.Lookup.Platforms)
                foreach (var candidate in platform.Candidates)
                    See(PlatformEcosystem(platform), PlatformEcosystem(platform), PlatformToken(candidate, platform.Current.Channels.FirstOrDefault()));

            if (next.FirstSeen.Count > DependencyUpdateState.MaxFirstSeen)
                next.FirstSeen = [.. next.FirstSeen.OrderByDescending(s => s.FirstSeenAtUtc).Take(DependencyUpdateState.MaxFirstSeen)];
        }

        /// <summary>A record that never reaches a group: excluded, ignored, skipped, or with paths the plan schema refuses.</summary>
        public DependencyReportEntry? Screen(LookupRecord record)
        {
            var best = record.Candidates[0];
            DependencyReportEntry Entry(EDependencyReportReason reason, string detail)
                => new(record.DepName, record.Ecosystem, record.DepName, record.PackageFile, record.CurrentValue, Token(best), null, reason, detail);

            if (record.SkipReason is { Length: > 0 } skip)
                return Entry(EDependencyReportReason.Skipped, $"skipped by the lookup: {skip}");
            if (!IsSafePath(record.PackageFile) || record.LockFiles.Any(l => !IsSafePath(l)))
                return Entry(EDependencyReportReason.NeedsHuman, "a package or lock file path outside the repository (gap A)");
            if (config.IsExcludedPath(record.PackageFile))
                return Entry(EDependencyReportReason.Excluded, "under dependencies.exclude");
            if (config.IgnoredBy(record.Ecosystem, record.DepName) is { } why)
                return Entry(EDependencyReportReason.Ignored, why);
            return null;
        }

        public void Report(DependencyReportEntry entry)
        {
            var list = entry.Reason switch
            {
                EDependencyReportReason.Cooldown or EDependencyReportReason.PreviewNotFollowed or EDependencyReportReason.NotInSecurityRun
                    or EDependencyReportReason.MovesWithPlatform or EDependencyReportReason.Lockstep or EDependencyReportReason.AboveCompanionRange => available,
                EDependencyReportReason.AwaitingApproval => awaiting,
                EDependencyReportReason.Ignored or EDependencyReportReason.Excluded or EDependencyReportReason.Skipped => ignored,
                _ => held,
            };
            if (!list.Any(e => e.Group == entry.Group && e.DepName == entry.DepName && e.PackageFile == entry.PackageFile && e.Target == entry.Target && e.Reason == entry.Reason))
                list.Add(entry);
        }

        private readonly Dictionary<string, List<LookupRecord>> idle = new(StringComparer.Ordinal);

        /// <summary>companion-pins-PRD M3: a dependency without an update, which joins its group with nothing to offer when the
        /// group is lockstep.</summary>
        public void AddIdle(string group, LookupRecord record)
        {
            if (!idle.TryGetValue(group, out var records))
                idle[group] = records = [];
            records.Add(record);
        }

        public void EvaluateGroup(string group, List<LookupRecord> members)
        {
            var idleMembers = idle.GetValueOrDefault(group) ?? [];
            var lockstep = members.Concat(idleMembers).Any(m => config.Lockstep(m.Ecosystem, m.DepName));
            var views = (lockstep ? members.Concat(idleMembers) : members).Select(m => (Record: m, Views: Candidates(m))).ToList();
            // prerelease-dependencies-PRD D3: the newest prerelease the track drops is listed even when a stable release
            // beside it is planned, with the setting that would follow it.
            foreach (var (_, candidates) in views)
                if (candidates.FirstOrDefault(v => v.Problem == EDependencyReportReason.PreviewNotFollowed) is { } skipped)
                    Report(Entry(group, skipped, EDependencyReportReason.PreviewNotFollowed, skipped.Detail!));
            var levels = views.SelectMany(v => v.Views).Select(v => v.Level).Distinct().OrderDescending().ToList();
            var groupHold = next.Holds.FirstOrDefault(h => h.Group == group);
            var failures = new List<DependencyReportEntry>();

            foreach (var tier in levels)
            {
                // Each member's best candidate within the tier that no member-level gate stops (cooldown aside).
                var ideal = new List<View>();
                var best = new List<View>();
                var tierFailures = new List<DependencyReportEntry>();
                foreach (var (record, candidates) in views)
                {
                    var within = candidates.Where(v => v.Level <= tier).ToList();
                    if (within.Count == 0)
                        continue;
                    best.Add(within[0]);
                    var pick = within.FirstOrDefault(v => v.Problem is null);
                    if (pick is null)
                        tierFailures.Add(Entry(group, within[0], within[0].Problem!.Value, within[0].Detail!));
                    else
                        ideal.Add(pick);
                }
                if (ideal.Count == 0 && tierFailures.Count == 0)
                    continue;
                // #655: a group joined on a blocker moves only in a tier where the blocker moves too; without it, the plan is
                // the one the resolver already refused (a member whose candidates sit in a lower tier, e.g. 12.2.0 a minor
                // for MintPlayer.SourceGenerators while every MintPlayer.SourceGenerators.Attributes candidate is a major).
                if (joinedBlockers.TryGetValue(group, out var required)
                    && required.Any(dep => views.Any(v => v.Views.Count > 0 && string.Equals(v.Record.DepName, dep, StringComparison.Ordinal))
                        && !best.Any(v => string.Equals(v.Record.DepName, dep, StringComparison.Ordinal))))
                    continue;

                var depNames = DepNames(ideal.Select(v => v.Record.DepName));
                var targets = Targets(ideal.Select(v => (v.Record.DepName, Token(v.Candidate))));
                // What the group offers at this tier, held or not: an approval of it stays valid while a hold stands.
                currentTargets.Add((group, string.Join('\n', depNames), string.Join('\n', targets)));
                currentTargets.Add((group, string.Join('\n', DepNames(best.Select(v => v.Record.DepName))),
                    string.Join('\n', Targets(best.Select(v => (v.Record.DepName, Token(v.Candidate)))))));

                if (groupHold is not null)
                {
                    failures.AddRange(ideal.Select(v => Entry(group, v, EDependencyReportReason.Held, groupHold.Reason)));
                    failures.AddRange(tierFailures);
                    continue;
                }
                if (tierFailures.Count > 0)
                {
                    failures.AddRange(tierFailures);
                    continue;
                }

                var level = ideal.Max(v => v.Level);
                var ceiling = ideal.Select(v => config.Ceiling(v.Record.Ecosystem, v.Record.DepName)).Min();
                if ((int)level > (int)ceiling)
                {
                    failures.AddRange(ideal.Select(v => Entry(group, v, EDependencyReportReason.AboveAccountRule, $"above the account rule's ceiling ({ceiling.ToString().ToLowerInvariant()})")));
                    continue;
                }

                var threshold = ideal.Select(v => config.Threshold(v.Record.Ecosystem, v.Record.DepName)).Min();
                var inclusion = ideal.Select(v => config.Inclusion(v.Record.Ecosystem, v.Record.DepName)).Min();
                var approved = false;
                if ((int)level > (int)inclusion)
                {
                    if (!Approved(group, depNames, targets))
                    {
                        failures.AddRange(ideal.Select(v => Entry(group, v, EDependencyReportReason.AwaitingApproval,
                            $"{level.ToString().ToLowerInvariant()} update over the threshold ({threshold.ToString().ToLowerInvariant()}): comment '/approve {group}'") with { Until = v.InCooldown ? v.Until : null }));
                        continue;
                    }
                    approved = true;
                }

                // The cooldown, with a fallback per member to a smaller candidate of the same tier that is old enough.
                var picks = new List<View>();
                var cooling = new List<DependencyReportEntry>();
                foreach (var pick in ideal)
                {
                    if (!pick.InCooldown)
                    {
                        picks.Add(pick);
                        continue;
                    }
                    var fallback = views.First(v => ReferenceEquals(v.Record, pick.Record)).Views
                        .FirstOrDefault(v => v.Level <= tier && v.Problem is null && !v.InCooldown);
                    if (fallback is null)
                        cooling.Add(Entry(group, pick, EDependencyReportReason.Cooldown, $"released less than {Days(config.Cooldown)} ago") with { Until = pick.Until });
                    else
                    {
                        cooling.Add(Entry(group, pick, EDependencyReportReason.Cooldown, $"released less than {Days(config.Cooldown)} ago; {Token(fallback.Candidate)} planned meanwhile") with { Until = pick.Until });
                        picks.Add(fallback);
                    }
                }
                if (picks.Count < ideal.Count)
                {
                    failures.AddRange(cooling);
                    // SpaServices #111: what the cooling members hold back is listed too, or it vanishes from the pull request.
                    var coolingPicks = ideal.Where(v => v.InCooldown && !picks.Any(p => ReferenceEquals(p.Record, v.Record))).ToList();
                    var waitsFor = string.Join(", ", DepNames(coolingPicks.Select(v => v.Record.DepName)));
                    var until = coolingPicks.Max(v => v.Until);
                    failures.AddRange(picks.Select(v => Entry(group, v, EDependencyReportReason.Cooldown,
                        $"moves with its group, which waits for {waitsFor} (released less than {Days(config.Cooldown)} ago)") with { Until = until }));
                    continue;
                }

                // companion-pins-PRD M3: a lockstep group moves only to a version every member has (S6, #669's half-move).
                if (lockstep)
                {
                    var (aligned, missing) = Lockstepped(tier, views, picks);
                    if (aligned is null)
                    {
                        failures.AddRange(cooling);
                        failures.AddRange(picks.Select(v => Entry(group, v, EDependencyReportReason.Lockstep, missing!)));
                        continue;
                    }
                    picks = aligned;
                }

                failures.AddRange(cooling);
                Plan(group, picks, threshold, approved);
                break;
            }

            foreach (var failure in failures)
                Report(failure);
        }

        /// <summary>
        /// companion-pins-PRD M3: the picks moved to one version every member has, or null with why. A member's versions are
        /// its current one and the candidates it may take in this tier (no problem, out of cooldown), each read as its first
        /// <c>x.y.z[-pre]</c> (<c>v1.64.1-noble</c> and <c>1.64.1</c> are the same release, S7). The highest version all
        /// members share that moves at least one wins; a member already at it gets no pick.
        /// </summary>
        private static (List<View>? Picks, string? Missing) Lockstepped(EUpdateLevel tier, List<(LookupRecord Record, List<View> Views)> views, List<View> picks)
        {
            var members = views.Select(m =>
            {
                var current = DependencyFollowRule.DefaultVersion(m.Record.CurrentVersion ?? m.Record.CurrentValue ?? string.Empty);
                var options = m.Views.Where(v => v.Level <= tier && v.Problem is null && !v.InCooldown)
                    .Select(v => (Version: DependencyFollowRule.DefaultVersion(Token(v.Candidate)), View: v))
                    .Where(o => o.Version is not null)
                    .ToList();
                return (m.Record, Current: current, Options: options);
            }).ToList();
            string? VersionOf(View view) => DependencyFollowRule.DefaultVersion(Token(view.Candidate));
            bool Has((LookupRecord Record, string? Current, List<(string? Version, View View)> Options) member, string version)
                => member.Current == version || member.Options.Any(o => o.Version == version);

            var wanted = picks.Select(VersionOf).ToList();
            if (wanted.All(v => v is not null) && wanted.Distinct(StringComparer.Ordinal).Count() == 1 && members.All(m => Has(m, wanted[0]!)))
                return (picks, null);

            var common = members.SelectMany(m => m.Options.Select(o => o.Version!)).Distinct(StringComparer.Ordinal)
                .Where(v => members.All(m => Has(m, v)) && members.Any(m => m.Current != v))
                .OrderDescending(Comparer<string>.Create((a, b) => SemanticVersion.Compare(a, b) ?? string.CompareOrdinal(a, b)))
                .FirstOrDefault();
            if (common is not null)
                return ([.. members.Where(m => m.Current != common).Select(m => m.Options.First(o => o.Version == common).View)], null);

            if (wanted.Where(v => v is not null).OrderDescending(Comparer<string?>.Create((a, b) => SemanticVersion.Compare(a!, b!) ?? string.CompareOrdinal(a, b))).FirstOrDefault() is not { } target)
                return (null, "lockstep: no member's version reads as x.y.z");
            var lacking = members.First(m => !Has(m, target));
            return (null, $"lockstep: {lacking.Record.DepName} has no {target} yet");
        }

        private void Plan(string group, List<View> picks, EDependencyThreshold threshold, bool approved)
        {
            var level = picks.Max(v => v.Level);
            var platformMajor = level == EUpdateLevel.Major && picks.Any(v => v.Record.Ecosystem.StartsWith("platform:", StringComparison.Ordinal));
            var repoCode = picks.Any(v => config.RepoCodeManagers.Contains(v.Record.Manager));
            var migrate = approved && level == EUpdateLevel.Major
                ? picks.Select(v => config.MigrateWorkflow(v.Record.DepName)).FirstOrDefault(w => w is not null)
                : null;
            var groupPlan = new DependencyPlannedGroup
            {
                Group = group,
                Level = level,
                AutoMerge = (int)level <= (int)threshold && !approved && !platformMajor && !repoCode && migrate is null,
                Approved = approved,
                Security = picks.Any(v => v.Security),
                MigrateWorkflow = migrate,
                Items = [.. picks.Select(v => new DependencyPlannedItem(v.Record, v.Candidate, v.Level))],
                DepNames = DepNames(picks.Select(v => v.Record.DepName)),
                TargetMajors = Targets(picks.Select(v => (v.Record.DepName, Token(v.Candidate)))),
            };
            Add(groupPlan, picks.Select(v => Entry(group, v, EDependencyReportReason.NotInSecurityRun, "left for the next scheduled slot (R10)")));
        }

        private bool Add(DependencyPlannedGroup group, IEnumerable<DependencyReportEntry> ifDeferred)
        {
            // R10: an out-of-slot security run carries only the fixes and what may auto-merge anyway.
            if (input.Mode == EDependencyRunMode.Security && !group.Security && !group.AutoMerge)
            {
                foreach (var entry in ifDeferred)
                    Report(entry);
                return false;
            }
            planned.Add(group);
            return true;
        }

        public void EvaluatePlatform(PlatformRecord platform)
        {
            EvaluatePlatformMove(platform);
            AlignLagging(platform);
            ReportStranded(platform.Sdk);
        }

        /// <summary>
        /// follower-cooldown-PRD H1: the platform packages of a project that targets only the SDK's channel, still on an older
        /// major, take the channel's major. With no platform move planned to ride in (the channel is released, or the move
        /// waits), they are a group of their own under the platform's name, at the platform's in-channel level (a patch).
        /// </summary>
        private void AlignLagging(PlatformRecord platform)
        {
            if (planned.Any(g => g.Platform?.Sdk == platform.Sdk) || platform.Current.Channels.Count == 0)
                return;
            var current = platform.Current.Channels[0];
            var picks = LaggingRiders(lagging.GetValueOrDefault(platform.Sdk) ?? [], UpdateTypeFloor.LeadingNumber(current), platform.Current.Preview);
            // companion-version-maps-PRD §6.3: a record below its range aligns the same way, to the highest release inside it.
            picks.AddRange((rangedBehind.GetValueOrDefault(platform.Sdk) ?? []).Select(r => RangeRider(r, ranged[r].Current!, skipCooldown: false)).OfType<View>());
            // multi-target-dependency-split-PRD §7.0: a multi-target project's packages move per TFM here too (M12: each TFM
            // to its own channel's major, no approval), on the TFMs the project has.
            var splitItems = SplitItems(platform.Sdk, tfmLists.GetValueOrDefault(platform.Sdk) ?? new Dictionary<string, List<string>>(), null, platform.Current.Preview, []);
            if (picks.Count == 0 && splitItems.Count == 0)
                return;
            var group = $"platform:{platform.Sdk} {current}";
            List<DependencyPlannedItem> items = [.. picks.Select(v => new DependencyPlannedItem(v.Record, v.Candidate, v.Level)), .. splitItems];
            // prerelease-track-scenarios-PRD D5: the group carries the platform's name, so a hold on it (the move's, held above,
            // or this group's own refusal) holds it too, judged against these items like the move's.
            if (heldPlatformGroups.Contains(group))
                return;
            if (next.Holds.FirstOrDefault(h => h.Group == group) is { } hold)
            {
                if (DependencyHolds.StandsFor(hold, items.Select(i => i.ToPlanItem())))
                {
                    foreach (var pick in picks)
                        Report(Entry(group, pick, EDependencyReportReason.Held, hold.Reason));
                    return;
                }
                next.Holds.Remove(hold);
                lifted.Add(hold);
            }
            var eco = items[0].Record.Ecosystem;
            var groupPlan = new DependencyPlannedGroup
            {
                Group = group,
                Level = EUpdateLevel.Patch,
                AutoMerge = (int)EUpdateLevel.Patch <= (int)config.Threshold(eco, items[0].Record.DepName),
                DepNames = [.. items.Select(i => i.Record.DepName).Distinct(StringComparer.Ordinal)],
                Items = items,
                TargetMajors = Targets(items.Select(i => (i.Record.DepName, Token(i.Candidate)))),
            };
            if (Add(groupPlan, picks.Select(v => Entry(group, v, EDependencyReportReason.NotInSecurityRun, "left for the next scheduled slot (R10)"))))
                riding.UnionWith(picks.Select(v => v.Record));
        }

        /// <summary>H1: each lagging record's release on <paramref name="major"/> that may move now (no problem, not cooling
        /// down; a prerelease only towards a preview channel). A record without one stays behind and is listed.</summary>
        private List<View> LaggingRiders(IEnumerable<LookupRecord> behind, long? major, bool preview)
            => [.. behind.Select(r => Candidates(r).FirstOrDefault(v => v.Problem is null && !v.InCooldown && MajorOf(v.Candidate) == major
                    && (preview || ExplicitPreview(r) || !IsPreviewToken(Token(v.Candidate)))))
                .OfType<View>()];

        /// <summary>prerelease-track-scenarios-PRD D1/D2: a coverage.yml or account rule put the package on track: preview. Only
        /// such a choice keeps a package on prereleases where the platform's own logic would take a release on the major.</summary>
        private bool ExplicitPreview(LookupRecord record)
            => config.PackageTrackOf(record.Ecosystem, record.DepName, record.CurrentVersion ?? record.CurrentValue) is { Track: EDependencyTrack.Preview, Explicit: true };

        /// <summary>#606 D8: whether a channel is on the SDK's track (lts: an LTS release; stable: no preview; preview: any).</summary>
        private static bool OnTrack(PlatformCandidate candidate, EDependencyTrack track) => track switch
        {
            EDependencyTrack.Lts => candidate.Lts == true && !candidate.Preview,
            EDependencyTrack.Preview => true,
            _ => !candidate.Preview,
        };

        /// <summary>
        /// What a platform candidate moves to: its channel, or, for the row of the channel the repository is on (#617 D29),
        /// the version inside it (the SDK where the table has one), so a cooldown, a red version and an approval name it.
        /// </summary>
        private static string PlatformToken(PlatformCandidate candidate, string? current)
            => candidate.Channel == current ? candidate.LatestSdk ?? candidate.LatestVersion ?? candidate.Channel : candidate.Channel;

        /// <summary>
        /// Whether a platform location is in the move's scope: <c>locations</c> (when set) and <c>exclude</c> path globs, the
        /// same rule as the action's <c>inScope</c> (dependency-action/src/platform/applyPlatform.ts:27-31).
        /// </summary>
        internal static bool InPlatformScope(string file, DependencyPlatformSettings settings)
        {
            static bool Matches(string glob, string path)
            {
                var source = string.Join(".*", glob.Split("**").Select(part => string.Join("[^/]*", part.Split('*').Select(System.Text.RegularExpressions.Regex.Escape))));
                return System.Text.RegularExpressions.Regex.IsMatch(path, $"^{source}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            if (settings.Locations.Count > 0 && !settings.Locations.Any(g => Matches(g, file))) return false;
            return !settings.Exclude.Any(g => Matches(g, file));
        }

        /// <summary>prerelease-dependencies-PRD H2: location kinds whose value names only a channel (<c>net11.0</c>, a
        /// template's framework choice), never a version inside it, so a move inside the channel leaves them as they are.</summary>
        private static readonly HashSet<string> ChannelOnlyKinds = new(StringComparer.Ordinal) { "tfm", "tfm-property", "template-framework" };

        private static bool IsPreviewToken(string? version) => version?.Contains('-', StringComparison.Ordinal) == true;

        private void EvaluatePlatformMove(PlatformRecord platform)
        {
            // platform-locations-PRD S2: dependencies.exclude leaves a path's platform alone as well as its package files (Spark's
            // test fixtures were its only rust/go/php/python locations). An excluded location neither moves nor sets the
            // repository's channel ("every channel found in an editable location").
            var kept = platform.Locations.Where(l => !config.IsExcludedPath(l.File)).ToList();
            if (kept.Count != platform.Locations.Count)
            {
                var channels = kept.Where(l => !l.Computed && l.Channel is not null).Select(l => l.Channel!).ToHashSet(StringComparer.Ordinal);
                platform = platform with { Locations = kept, Current = platform.Current with { Channels = [.. platform.Current.Channels.Where(channels.Contains)] } };
            }
            var eco = PlatformEcosystem(platform);
            foreach (var location in platform.Locations.Where(l => l.Computed))
                platformReports.Add(new DependencyPlatformReport(platform.Sdk, location.File, location.Value, location.Reason ?? "a computed value is reported, never edited (R8)"));

            var current = platform.Current.Channels.FirstOrDefault();
            if (current is null || !platform.Locations.Any(l => !l.Computed) || platform.Candidates.Count == 0)
                return;

            var track = config.PlatformTrack(platform.Sdk);
            // #617 D29: the table's row for the current channel is the move inside it (an RC to its release, a patch). It is
            // on any track unless it is a preview: an lts repository on an STS channel still takes that channel's patches.
            bool Follows(PlatformCandidate c) => c.Channel == current ? !c.Preview || track == EDependencyTrack.Preview : OnTrack(c, track);
            var top = platform.Candidates[0];
            var target = platform.Candidates.FirstOrDefault(Follows);
            DependencyReportEntry Entry(PlatformCandidate candidate, EUpdateLevel? level, EDependencyReportReason reason, string detail)
                => new($"platform:{platform.Sdk} {candidate.Channel}", eco, eco, platform.Locations.First(l => !l.Computed).File, current, PlatformToken(candidate, current), level, reason, detail);

            if (!Follows(top))
                Report(Entry(top, null, EDependencyReportReason.PreviewNotFollowed, track == EDependencyTrack.Lts && !top.Preview
                    ? $"not an LTS release; {platform.Sdk} follows the lts track (platforms.{platform.Sdk}.track)"
                    : $"a preview channel; {platform.Sdk} follows the {track.ToString().ToLowerInvariant()} track (set platforms.{platform.Sdk}.track: preview to follow it)"));
            if (target is null || PlanMove(target))
                return;
            // #617 D36: a channel move that is not planned (awaiting approval, pending, cooling down) leaves the patches of the
            // channel the repository is on, which are offered instead. Never both: the two edit the same pins.
            if (target.Channel != current && platform.Candidates.FirstOrDefault(c => c.Channel == current && Follows(c)) is { } patch)
                PlanMove(patch);

            bool PlanMove(PlatformCandidate target)
            {
                var group = $"platform:{platform.Sdk} {target.Channel}";
                var inChannel = target.Channel == current;
                // #617 D32: a move inside the channel is a patch, under the account's rules like any other.
                var level = inChannel ? EUpdateLevel.Patch : UpdateTypeFloor.Apply(EUpdateLevel.Minor, current, target.Channel);
                var token = PlatformToken(target, current);
                if (config.IgnoredBy(eco, eco) is { } why)
                {
                    Report(Entry(target, level, EDependencyReportReason.Ignored, why));
                    return false;
                }
                // prerelease-track-scenarios-PRD D5: the bot's own hold (R2) is judged against the items planned below; a
                // writer's hold or the file's holds the group as it is.
                var stateHold = next.Holds.FirstOrDefault(h => h.Group == group || (h.Group.Length == 0 && h.DepNames.Contains(eco)));
                var botHold = stateHold is { Kind: EDependencyHoldKind.Blocker or EDependencyHoldKind.Unattributed } ? stateHold : null;
                var hold = config.FileHold(eco, eco) ?? (botHold is null ? stateHold?.Reason : null);
                if (hold is not null)
                {
                    Report(Entry(target, level, EDependencyReportReason.Held, hold));
                    heldPlatformGroups.Add(group);
                    return false;
                }
                if (red.Contains((eco, eco, token)))
                {
                    Report(Entry(target, level, EDependencyReportReason.FleetRed, "red in another repository of the fleet (Q15)"));
                    return false;
                }

                // #617: an in-channel move's target is the version it moves to, so an approval of the channel move (or of an
                // earlier patch) never carries over to it.
                var targets = new[] { $"{eco}@{(inChannel ? token : UpdateTypeFloor.TargetMajor(target.Channel))}" };
                currentTargets.Add((group, eco, targets[0]));

                // #606 D2/D3: on a major move, every companion rides at the new major; the move waits until each one has a
                // release there, unless a writer approved it with --force. #617 D33: a preview's companions ride to the release
                // the same way (each one's stable version on the major); a move between two releases carries none.
                var riders = new List<View>();
                var missing = new List<string>();
                var fromMajor = UpdateTypeFloor.LeadingNumber(current);
                var toMajor = UpdateTypeFloor.LeadingNumber(target.Channel);
                var majorMove = toMajor > fromMajor;
                var toRelease = inChannel && platform.Current.Preview && !target.Preview;
                if (companions.GetValueOrDefault(platform.Sdk) is { } family && toMajor is { } major && (majorMove || toRelease))
                {
                    foreach (var record in family)
                    {
                        // prerelease-track-scenarios-PRD D2: a companion a rule put on track: preview keeps its prereleases.
                        var pick = Candidates(record).FirstOrDefault(v => v.Problem is null && MajorOf(v.Candidate) == major
                            && (!toRelease || ExplicitPreview(record) || !IsPreviewToken(Token(v.Candidate))));
                        if (pick is null)
                        {
                            // #617 D33: a companion already on the release has nothing to ride with.
                            if (!toRelease || IsPreviewToken(record.CurrentVersion ?? record.CurrentValue))
                                missing.Add(record.DepName);
                        }
                        else
                            riders.Add(pick);
                    }
                    missing = [.. missing.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
                }
                // follower-cooldown-PRD H1: the older-major packages of the channel's projects ride to the target major. One
                // without a release there stays behind (listed), and never holds the move: it is behind already.
                if (lagging.GetValueOrDefault(platform.Sdk) is { } behind && toMajor is { } toM)
                    riders.AddRange(LaggingRiders(behind, toM, target.Preview));
                // companion-version-maps-PRD §6.3: a ranged record rides to the highest release inside its range for the target
                // SDK (a project on the channel being moved takes the target channel's key). It never holds the move.
                foreach (var (record, one) in ranged.Where(p => p.Value.Sdk == platform.Sdk))
                {
                    var keyChannel = one.Channel == current ? target.Channel : one.Channel;
                    if (RangeOf(one.Entry, target.LatestSdk ?? one.RepositorySdk, keyChannel) is { } range
                        && !(Admits(range, record.CurrentVersion ?? record.CurrentValue) && one.Current is { } now && range.Range.Text == now.Range.Text)
                        && RangeRider(record, range, skipCooldown: true) is { } rider)
                        riders.Add(rider);
                }
                // multi-target-dependency-split-PRD P3 (M6, M13): each multi-target project's next TFM list (a major move adds
                // the target channel; an end-of-life channel leaves, keepPrevious kept), and its platform packages per TFM on
                // it. A new TFM needs the package's release on its major, like a companion (#606 D3).
                var platformSettings = config.File.Platform(platform.Sdk);
                List<PlanTargetFrameworks> tfmPlan = [];
                var splitItems = new List<DependencyPlannedItem>();
                if (tfmLists.TryGetValue(platform.Sdk, out var lists))
                {
                    var endOfLife = platform.Current.EndOfLife ?? [];
                    var nextLists = lists.ToDictionary(p => p.Key, p => IsMultiTarget(p.Value) && InPlatformScope(p.Key, platformSettings)
                        ? NextTfms(p.Value, inChannel ? null : target.Channel, endOfLife, platformSettings.KeepPrevious)
                        : p.Value, StringComparer.Ordinal);
                    tfmPlan = [.. nextLists.Where(p => IsMultiTarget(lists[p.Key]) && InPlatformScope(p.Key, platformSettings) && (!inChannel || !p.Value.SequenceEqual(lists[p.Key])))
                        .OrderBy(p => p.Key, StringComparer.Ordinal)
                        .Select(p => new PlanTargetFrameworks { File = p.Key, Value = string.Join(';', p.Value) })];
                    splitItems = SplitItems(platform.Sdk, nextLists, target.Channel, target.Preview, missing);
                    missing = [.. missing.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
                }
                if (botHold is not null)
                {
                    var planned = riders.Select(v => new DependencyPlannedItem(v.Record, v.Candidate, v.Level)).Concat(splitItems).Select(i => i.ToPlanItem());
                    if (DependencyHolds.StandsFor(botHold, planned))
                    {
                        Report(Entry(target, level, EDependencyReportReason.Held, botHold.Reason));
                        heldPlatformGroups.Add(group);
                        return false;
                    }
                    next.Holds.Remove(botHold);
                    lifted.Add(botHold);
                }
                if (missing.Count > 0 && !Approved(group, [eco], targets, force: true))
                {
                    var names = missing.Count <= 5 ? string.Join(", ", missing) : $"{string.Join(", ", missing.Take(5))} and {missing.Count - 5} more";
                    var waitsFor = toRelease ? $"their {toMajor} release" : $"{toMajor}.x";
                    Report(Entry(target, level, EDependencyReportReason.Pending,
                        $"waiting on {names} to release {waitsFor}: comment '/approve {group} --force' to move without them"));
                    return false;
                }

                var ceiling = config.Ceiling(eco, eco);
                if ((int)level > (int)ceiling)
                {
                    Report(Entry(target, level, EDependencyReportReason.AboveAccountRule, $"above the account rule's ceiling ({ceiling.ToString().ToLowerInvariant()})"));
                    return false;
                }
                var threshold = config.Threshold(eco, eco);
                var approved = false;
                if ((int)level > (int)config.Inclusion(eco, eco))
                {
                    if (!Approved(group, [eco], targets))
                    {
                        Report(Entry(target, level, EDependencyReportReason.AwaitingApproval, $"platform {level.ToString().ToLowerInvariant()} {current} → {token}: comment '/approve {group}'"));
                        return false;
                    }
                    approved = true;
                }

                // platform-locations-PRD P11: a location on the channel being moved that the bot cannot edit (a computed value)
                // would stay behind. The move then waits for a writer, who sees the "Not moved" list first; listing the file
                // under exclude says it stays on purpose. Older channels and ranges are information only.
                var settings = config.File.Platform(platform.Sdk);
                // A digest-pinned image is "computed" for the platform but planned as a docker record of its own (locations.ts:220,
                // 242), so it does not stay behind.
                // prerelease-dependencies-PRD H2: a move inside the channel (an RC to its release, a patch) changes no TFM, so a
                // computed TFM on that channel is already right and stays as it is (Dotnet.Tools PR #210 waited on two
                // conditioned net11.0 for 11.0.100-rc.1 → 11.0.100). H6/H7: every workflow moves together, so a setup-* input
                // whose expression the scan can't resolve holds the move as well, whatever channel it hides.
                var unedited = platform.Locations.Where(l => l.Computed && InPlatformScope(l.File, settings)
                    && (l.Channel == current && !(target.Channel == current && ChannelOnlyKinds.Contains(l.Kind))
                        || l.Channel is null && l.Reason == PlatformLocation.UnresolvedExpression)
                    && l.Reason?.StartsWith("digest-pinned", StringComparison.Ordinal) != true).ToList();
                if (unedited.Count > 0 && !approved)
                {
                    if (!Approved(group, [eco], targets))
                    {
                        var files = string.Join(", ", unedited.Select(l => l.File).Distinct(StringComparer.Ordinal).Take(3));
                        Report(Entry(target, level, EDependencyReportReason.AwaitingApproval,
                            $"{unedited.Count} location(s) the bot cannot edit would stay behind ({files}; see \"Not moved\"): comment '/approve {group}', "
                            + $"or list them under platforms.{platform.Sdk}.exclude to leave them on purpose"));
                        return false;
                    }
                    approved = true;
                }

                var (inCooldown, until) = Cooldown(eco, eco, token, target.ReleaseTimestamp, securityFix: false);
                if (inCooldown)
                {
                    Report(Entry(target, level, EDependencyReportReason.Cooldown, $"released less than {Days(config.Cooldown)} ago") with { Until = until });
                    return false;
                }

                var planGroup = new DependencyPlannedGroup
                {
                    Group = group,
                    Level = level,
                    // R7: a platform major never auto-merges, unless the repository opted in with platforms.<sdk>.autoMerge: major
                    // (prerelease-dependencies-PRD H3, amends D-P18/R7). The enqueue gate checks the opt-in a second time.
                    AutoMerge = (int)level <= (int)threshold && (level != EUpdateLevel.Major || settings.AutoMergeMajor) && !approved,
                    Approved = approved,
                    DepNames = [eco],
                    TargetMajors = targets,
                    Items = [.. riders.Select(v => new DependencyPlannedItem(v.Record, v.Candidate, v.Level)), .. splitItems],
                    Platform = new PlanPlatform
                    {
                        Sdk = platform.Sdk,
                        FromChannel = current,
                        ToChannel = target.Channel,
                        ToVersion = target.LatestVersion,
                        SdkVersion = target.LatestSdk,
                        Strategy = settings.Strategy,
                        KeepPrevious = settings.KeepPrevious,
                        LockPackageMajor = settings.LockPackageMajor,
                        Locations = settings.Locations.Count == 0 ? null : [.. settings.Locations],
                        // S2: the apply scans the tree itself, so it gets dependencies.exclude too.
                        Exclude = settings.Exclude.Concat(config.File.Exclude).Distinct(StringComparer.Ordinal).ToList() is { Count: > 0 } exclude ? exclude : null,
                        Lagging = settings.Lagging == DependencyPlatformSettings.LaggingAuto ? null : settings.Lagging,
                        ActionRuntimes = platform.Sdk == "node" ? [.. GitHubActionRuntimes] : null,
                        TargetFrameworks = tfmPlan.Count == 0 ? null : tfmPlan,
                    },
                };
                if (!Add(planGroup, [Entry(target, level, EDependencyReportReason.NotInSecurityRun, "left for the next scheduled slot (R10)")]))
                    return false;
                riding.UnionWith(riders.Select(v => v.Record));
                return true;
            }
        }

        public DependencyPolicyOutcome Outcome()
        {
            // Gap E: an approval stands only while its group still offers exactly what was approved, under the same file.
            var expired = next.Approvals.Where(a => a.ConfigBlob != input.ConfigBlob
                || !currentTargets.Contains((a.Group, string.Join('\n', a.DepNames), string.Join('\n', a.TargetMajors)))).ToList();
            next.Approvals = [.. next.Approvals.Except(expired)];

            var ordered = planned
                .OrderBy(g => g.Platform is null ? 0 : 1)
                .ThenBy(g => g.Security ? 0 : 1)
                .ThenBy(g => g.Level)
                .ThenBy(g => g.Group, StringComparer.Ordinal)
                .ToList();
            return new DependencyPolicyOutcome
            {
                Groups = ordered,
                Available = available,
                Held = held,
                AwaitingApproval = awaiting,
                Ignored = ignored,
                PlatformReports = platformReports,
                // companion-pins-PRD G4: a follow rule the lookup could not apply ("follow[i]") is a "not moved" line.
                NotFollowed = [.. input.Lookup.Problems.Where(p => p.Source.StartsWith("follow[", StringComparison.Ordinal)).Select(p => p.Message).Distinct(StringComparer.Ordinal)],
                NotLookedUp = [.. input.Lookup.Problems.Where(p => p.Source == LookupProblem.LookupWarning).Select(p => p.Message).Distinct(StringComparer.Ordinal)],
                NextState = next,
                LiftedHolds = lifted,
                ExpiredApprovals = expired,
            };
        }

        private bool Approved(string group, IReadOnlyList<string> depNames, IReadOnlyList<string> targets, bool force = false)
            => next.Approvals.Any(a => a.Group == group && a.ConfigBlob == input.ConfigBlob && (!force || a.Force)
                && a.DepNames.SequenceEqual(depNames, StringComparer.Ordinal) && a.TargetMajors.SequenceEqual(targets, StringComparer.Ordinal));

        /// <summary>Every candidate of a record through R1, R17 and the member-level gates, best first.</summary>
        private List<View> Candidates(LookupRecord record)
        {
            var views = new List<View>();
            var securityFix = security.Contains((record.Ecosystem, record.DepName));
            var fileHold = config.FileHold(record.Ecosystem, record.DepName);
            var depHold = next.Holds.FirstOrDefault(h => h.Group.Length == 0 && h.DepNames.Contains(record.DepName, StringComparer.Ordinal));
            foreach (var (original, index) in record.Candidates.Select((c, i) => (c, i)))
            {
                var candidate = Effective(record, original);
                var view = new View(record, candidate, index) { Security = securityFix };
                var labeled = UpdateTypeFloor.Labeled(candidate.UpdateType);
                if (labeled is { } label)
                    view.Level = UpdateTypeFloor.Apply(label, record.CurrentVersion ?? record.CurrentValue, candidate.Version ?? candidate.Value);

                var lockfileOnly = candidate.Value is not null && candidate.Value == record.CurrentValue;
                var digestChange = record.CurrentDigest is not null && candidate.Digest is not null && candidate.Digest != record.CurrentDigest;
                if (candidate.UpdateType is not null && lockfileOnly && !digestChange && record.LockFiles.Count == 0)
                    continue; // nothing the apply could change: same value, no lockfile, no pinned digest.

                if (candidate.UpdateType is null || candidate.Missing.Contains("updateType"))
                    (view.Problem, view.Detail) = (EDependencyReportReason.MissingField, "the lookup reported no updateType (R1)");
                else if (candidate.Version is null)
                    (view.Problem, view.Detail) = (EDependencyReportReason.MissingField, "the lookup reported no version (R1)");
                else if (candidate.Value is null && candidate.Digest is null)
                    (view.Problem, view.Detail) = (EDependencyReportReason.MissingField, "the lookup reported no value (R1)");
                else if (labeled is null)
                    (view.Problem, view.Detail) = (EDependencyReportReason.NeedsHuman, $"a {candidate.UpdateType.ToString()!.ToLowerInvariant()} is never planned without a human (gap D)");
                else if (record.CurrentValue is null)
                    (view.Problem, view.Detail) = (EDependencyReportReason.NeedsHuman, "the lookup reported no current value");
                else if (red.Contains((record.Ecosystem, record.DepName, candidate.Version)))
                    (view.Problem, view.Detail) = (EDependencyReportReason.FleetRed, "red in another repository of the fleet (Q15)");
                else if (fileHold is not null)
                    (view.Problem, view.Detail) = (EDependencyReportReason.Held, fileHold);
                else if (depHold is not null)
                    (view.Problem, view.Detail) = (EDependencyReportReason.Held, depHold.Reason);
                else if (LineCeiling(view.Level, securityFix) is { } ceiling)
                    (view.Problem, view.Detail) = (EDependencyReportReason.AboveLineCeiling, ceiling);
                // prerelease-dependencies-PRD D2/H1: a prerelease only on the package's preview track. Renovate's lookup is
                // wider (H5 rule order, a companion glob), and an enforced account rule narrows the track only here.
                // prerelease-track-scenarios-PRD D8: on an SDK the repository is on a preview of, a companion's prerelease on the
                // channel's major is the platform's own phase, so it is offered unless a rule chose track: stable.
                else if (DependencyPolicyConfig.IsPrerelease(record.Ecosystem, candidate.Version)
                    && config.PackageTrackOf(record.Ecosystem, record.DepName, record.CurrentVersion ?? record.CurrentValue) is { Track: EDependencyTrack.Stable } track
                    && !(track is { Explicit: false } && companionOf.TryGetValue(record, out var companionSdk)
                        && previewMajor.TryGetValue(companionSdk, out var channelMajor) && MajorOf(candidate) == channelMajor))
                    (view.Problem, view.Detail) = (EDependencyReportReason.PreviewNotFollowed, $"a prerelease; {track.Why}");

                var (inCooldown, until) = Cooldown(record.Ecosystem, record.DepName, Token(candidate), candidate.ReleaseTimestamp,
                    securityFix, IsBotRuntime(record) || CatchesUp(record, candidate)
                        || own.Contains((record.Ecosystem, record.DepName, candidate.Version ?? string.Empty)) && !config.RepoCodeManagers.Contains(record.Manager));
                view.InCooldown = inCooldown;
                view.Until = until;
                views.Add(view);
            }
            return [.. views
                .OrderByDescending(v => v.Level)
                .ThenByDescending(v => UpdateTypeFloor.LeadingNumber(v.Candidate.Version) ?? long.MinValue)
                .ThenBy(v => v.Index)];
        }

        /// <summary>
        /// #568 D13: why a release line's run does not take an update of <paramref name="level"/>, or null when it does. A line
        /// never takes a major (a platform-anchored package stays under the line's major); a security fix goes up to a minor;
        /// otherwise <c>dependencies.lines</c> decides.
        /// </summary>
        private string? LineCeiling(EUpdateLevel level, bool securityFix)
        {
            if (input.LineUpdates is not { } lines) return null;
            if (securityFix)
                return level <= EUpdateLevel.Minor ? null : "a release line takes no major: this security fix needs a major, so a human backports it";
            return lines switch
            {
                EDependencyLineUpdates.Security => "a release line here takes security fixes only (dependencies.lines: security)",
                EDependencyLineUpdates.All when level <= EUpdateLevel.Minor => null,
                EDependencyLineUpdates.All => "a release line takes no major (dependencies.lines: all)",
                _ when level <= EUpdateLevel.Patch => null,
                _ => "a release line takes patches only (dependencies.lines: patch)",
            };
        }


        /// <summary>
        /// follower-cooldown-PRD FC2: a follower moving to the release its leader already runs only catches up (the
        /// half-applied move of #669); the leader took that release, so there is nothing new to wait for. The follower's
        /// candidate carries the leader's token (follow.ts, E2).
        /// </summary>
        private bool CatchesUp(LookupRecord record, LookupCandidate candidate)
            => record.Manager == FollowManager && candidate.Version is { } token
                && input.Lookup.Records.Any(r => r.Manager != FollowManager && r.Ecosystem == record.Ecosystem && r.DepName == record.DepName
                    && (r.CurrentVersion ?? r.CurrentValue) == token);

        /// <summary>The lookup's manager for a <c>follow:</c> pin (follow.ts; the writer's <c>DependencyPatchValidator.FollowManager</c>).</summary>
        private const string FollowManager = "follow";

        private (bool InCooldown, DateTimeOffset Until) Cooldown(string eco, string dep, string token, string? releaseTimestamp, bool securityFix, bool ownRelease = false)
        {
            DateTimeOffset? released = DateTimeOffset.TryParse(releaseTimestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) && parsed <= now ? parsed : null;
            var start = released ?? firstSeen.GetValueOrDefault((eco, dep, token), now);
            var until = start + config.Cooldown;
            // R10: a security fix skips the cooldown; §7.6: so does a confirmed own release.
            return (!securityFix && !ownRelease && until > now, until);
        }

        private static DependencyReportEntry Entry(string group, View view, EDependencyReportReason reason, string detail)
            => new(group, view.Record.Ecosystem, view.Record.DepName, view.Record.PackageFile, view.Record.CurrentValue, Token(view.Candidate), view.Level, reason, detail)
            {
                Until = reason == EDependencyReportReason.Cooldown ? view.Until : null,
            };
    }

    private static IReadOnlyList<string> DepNames(IEnumerable<string> names) => [.. names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static IReadOnlyList<string> Targets(IEnumerable<(string DepName, string Token)> picks)
        => [.. picks.Select(p => $"{p.DepName}@{UpdateTypeFloor.TargetMajor(p.Token)}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static string PlatformEcosystem(PlatformRecord platform) => $"platform:{platform.Sdk}";

    private static string Days(TimeSpan span) => span.TotalDays == 1 ? "1 day" : $"{span.TotalDays:0.#} days";

    /// <summary>A repository-relative path the plan schema accepts: no <c>..</c> segment, not absolute, no backslash.</summary>
    internal static bool IsSafePath(string path)
        => path.Length > 0 && !path.StartsWith('/') && !path.Contains('\\') && !path.Split('/').Any(s => s == "..") && !path.Contains(':');

    internal static DependencyUpdateState Copy(DependencyUpdateState state) => new()
    {
        Id = state.Id,
        Repository = state.Repository,
        Slots = state.Slots,
        LastEvaluatedAtUtc = state.LastEvaluatedAtUtc,
        FirstSeen = [.. state.FirstSeen.Select(s => new DependencySeenVersion { Ecosystem = s.Ecosystem, DepName = s.DepName, Version = s.Version, FirstSeenAtUtc = s.FirstSeenAtUtc })],
        Holds = [.. state.Holds.Select(DependencyHolds.Copy)],
        Approvals = [.. state.Approvals.Select(a => new DependencyApproval
        {
            Group = a.Group,
            DepNames = [.. a.DepNames],
            TargetMajors = [.. a.TargetMajors],
            ConfigBlob = a.ConfigBlob,
            ApprovedBy = a.ApprovedBy,
            ApprovedAtUtc = a.ApprovedAtUtc,
            CommentId = a.CommentId,
            Force = a.Force,
        })],
    };
}
