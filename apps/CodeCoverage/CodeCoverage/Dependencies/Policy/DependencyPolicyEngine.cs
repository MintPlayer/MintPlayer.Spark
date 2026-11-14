using System.Globalization;
using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.LookupReferences;

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
                continue;
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
        private readonly HashSet<LookupRecord> riding = new(ReferenceEqualityComparer.Instance);

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
                if (DependencyCompanions.Ecosystem(sdk) is not { } ecosystem || settings.Strategy != "replace" || settings.KeepPrevious > 0 || settings.LockPackageMajor)
                    continue;
                if (UpdateTypeFloor.LeadingNumber(platform.Current.Channels.FirstOrDefault()) is not { } major)
                    continue;
                var globs = config.Companions(sdk);
                if (globs.Count == 0)
                    continue;
                var list = new List<LookupRecord>();
                foreach (var record in records.Where(r => r.Ecosystem == ecosystem && !companionOf.ContainsKey(r)))
                {
                    if (!globs.Any(g => DependencyGlob.Matches(g, record.DepName)))
                        continue;
                    if (MajorOf(record) != major)
                        continue;
                    list.Add(record);
                    companionOf[record] = sdk;
                }
                if (list.Count > 0)
                    companions[sdk] = list;
            }
        }

        /// <summary>
        /// The record as the package groups see it: null for a companion riding in its SDK's group, and a companion without
        /// the candidates above its SDK's current major (they move only with the SDK, #606 D2); the record itself otherwise.
        /// </summary>
        public LookupRecord? ForGrouping(LookupRecord record)
        {
            if (riding.Contains(record))
                return null;
            if (!companionOf.ContainsKey(record))
                return record;
            var major = MajorOf(record);
            var within = record.Candidates.Where(c => !(MajorOf(c) > major)).ToList();
            return within.Count == 0 ? null : record with { Candidates = within };
        }

        /// <summary>The companions of an SDK whose next major is not riding: each is listed as moving with the platform.</summary>
        private void ReportStranded(string sdk)
        {
            foreach (var record in companions.GetValueOrDefault(sdk) ?? [])
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
                    or EDependencyReportReason.MovesWithPlatform => available,
                EDependencyReportReason.AwaitingApproval => awaiting,
                EDependencyReportReason.Ignored or EDependencyReportReason.Excluded or EDependencyReportReason.Skipped => ignored,
                _ => held,
            };
            if (!list.Any(e => e.Group == entry.Group && e.DepName == entry.DepName && e.PackageFile == entry.PackageFile && e.Target == entry.Target && e.Reason == entry.Reason))
                list.Add(entry);
        }

        public void EvaluateGroup(string group, List<LookupRecord> members)
        {
            var views = members.Select(m => (Record: m, Views: Candidates(m))).ToList();
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
                            $"{level.ToString().ToLowerInvariant()} update over the threshold ({threshold.ToString().ToLowerInvariant()}): comment 'approve {group}'") with { Until = v.InCooldown ? v.Until : null }));
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

                failures.AddRange(cooling);
                Plan(group, picks, threshold, approved);
                break;
            }

            foreach (var failure in failures)
                Report(failure);
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
            ReportStranded(platform.Sdk);
        }

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
                var hold = config.FileHold(eco, eco) ?? next.Holds.FirstOrDefault(h => h.Group == group || (h.Group.Length == 0 && h.DepNames.Contains(eco)))?.Reason;
                if (hold is not null)
                {
                    Report(Entry(target, level, EDependencyReportReason.Held, hold));
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
                        var pick = Candidates(record).FirstOrDefault(v => v.Problem is null && MajorOf(v.Candidate) == major
                            && (!toRelease || !IsPreviewToken(Token(v.Candidate))));
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
                    if (missing.Count > 0 && !Approved(group, [eco], targets, force: true))
                    {
                        var names = missing.Count <= 5 ? string.Join(", ", missing) : $"{string.Join(", ", missing.Take(5))} and {missing.Count - 5} more";
                        var waitsFor = toRelease ? $"their {major} release" : $"{major}.x";
                        Report(Entry(target, level, EDependencyReportReason.Pending,
                            $"waiting on {names} to release {waitsFor}: comment 'approve {group} --force' to move without them"));
                        return false;
                    }
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
                        Report(Entry(target, level, EDependencyReportReason.AwaitingApproval, $"platform {level.ToString().ToLowerInvariant()} {current} → {token}: comment 'approve {group}'"));
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
                var unedited = platform.Locations.Where(l => l.Computed && l.Channel == current && InPlatformScope(l.File, settings)
                    && l.Reason?.StartsWith("digest-pinned", StringComparison.Ordinal) != true).ToList();
                if (unedited.Count > 0 && !approved)
                {
                    if (!Approved(group, [eco], targets))
                    {
                        var files = string.Join(", ", unedited.Select(l => l.File).Distinct(StringComparer.Ordinal).Take(3));
                        Report(Entry(target, level, EDependencyReportReason.AwaitingApproval,
                            $"{unedited.Count} location(s) on {current} the bot cannot edit ({files}; see \"Not moved\"): comment 'approve {group}', "
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
                    // R7: a platform major never auto-merges, whatever the file says.
                    AutoMerge = (int)level <= (int)threshold && level != EUpdateLevel.Major && !approved,
                    Approved = approved,
                    DepNames = [eco],
                    TargetMajors = targets,
                    Items = [.. riders.Select(v => new DependencyPlannedItem(v.Record, v.Candidate, v.Level))],
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

                var (inCooldown, until) = Cooldown(record.Ecosystem, record.DepName, Token(candidate), candidate.ReleaseTimestamp,
                    securityFix, IsBotRuntime(record)
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
