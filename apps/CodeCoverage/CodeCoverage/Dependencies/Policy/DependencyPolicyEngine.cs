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
/// <para><b>Platforms</b> (R7): the highest channel above the current one, previews only when the repository is already on
/// one; a major never auto-merges; computed locations are reported, never planned (R8).</para>
/// </remarks>
public static class DependencyPolicyEngine
{
    private static readonly IReadOnlyList<EUpdateLevel> LevelsDescending = [EUpdateLevel.Major, EUpdateLevel.Minor, EUpdateLevel.Patch];

    public static DependencyPolicyOutcome Evaluate(DependencyPolicyInput input)
    {
        var context = new Context(input);
        context.LiftHolds();
        context.RecordFirstSeen();

        var groups = new Dictionary<string, List<LookupRecord>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var record in input.Lookup.Records)
        {
            if (record.Candidates.Count == 0)
                continue;
            if (context.Screen(record) is { } screened)
            {
                context.Report(screened);
                continue;
            }
            var group = GroupOf(input.Config, record);
            if (!groups.TryGetValue(group, out var members))
            {
                groups[group] = members = [];
                order.Add(group);
            }
            members.Add(record);
        }

        foreach (var group in order)
            context.EvaluateGroup(group, groups[group]);

        foreach (var platform in input.Lookup.Platforms)
            context.EvaluatePlatform(platform);

        return context.Outcome();
    }

    /// <summary>D-C1 plus configuration: a configured group, else the first candidate <c>groupName</c>, else the record's,
    /// else the dependency name (one dependency across many package files moves together, §5.1 #8).</summary>
    public static string GroupOf(DependencyPolicyConfig config, LookupRecord record)
        => config.ConfiguredGroup(record.Ecosystem, record.DepName)
            ?? record.Candidates.Select(c => c.GroupName).FirstOrDefault(g => !string.IsNullOrWhiteSpace(g))
            ?? (string.IsNullOrWhiteSpace(record.GroupName) ? null : record.GroupName)
            ?? record.DepName;

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
        private readonly HashSet<(string Group, string DepNames, string Targets)> currentTargets = [];
        private readonly HashSet<(string Eco, string Dep, string Version)> red = input.Fleet.RedVersions
            .Where(r => !string.Equals(r.Repository, input.State.Repository, StringComparison.Ordinal) || input.State.Repository.Length == 0)
            .Select(r => (r.Ecosystem, r.DepName, r.Version)).ToHashSet();
        private readonly HashSet<(string Eco, string Dep, string Version)> own = input.Fleet.OwnReleases
            .Select(r => (r.Ecosystem, r.DepName, r.Version)).ToHashSet();
        private readonly HashSet<(string Eco, string Dep)> security = input.SecurityFixes.Select(s => (s.Ecosystem, s.DepName)).ToHashSet();

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
                    See(PlatformEcosystem(platform), PlatformEcosystem(platform), candidate.Channel);

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
                EDependencyReportReason.Cooldown or EDependencyReportReason.PreviewNotFollowed or EDependencyReportReason.NotInSecurityRun => available,
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

        private void Add(DependencyPlannedGroup group, IEnumerable<DependencyReportEntry> ifDeferred)
        {
            // R10: an out-of-slot security run carries only the fixes and what may auto-merge anyway.
            if (input.Mode == EDependencyRunMode.Security && !group.Security && !group.AutoMerge)
            {
                foreach (var entry in ifDeferred)
                    Report(entry);
                return;
            }
            planned.Add(group);
        }

        public void EvaluatePlatform(PlatformRecord platform)
        {
            var eco = PlatformEcosystem(platform);
            foreach (var location in platform.Locations.Where(l => l.Computed))
                platformReports.Add(new DependencyPlatformReport(platform.Sdk, location.File, location.Value, location.Reason ?? "a computed value is reported, never edited (R8)"));

            var current = platform.Current.Channels.FirstOrDefault();
            if (current is null || !platform.Locations.Any(l => !l.Computed) || platform.Candidates.Count == 0)
                return;

            var top = platform.Candidates[0];
            var target = platform.Candidates.FirstOrDefault(c => !c.Preview || platform.Current.Preview);
            DependencyReportEntry Entry(PlatformCandidate candidate, EUpdateLevel? level, EDependencyReportReason reason, string detail)
                => new($"platform:{platform.Sdk} {candidate.Channel}", eco, eco, platform.Locations.First(l => !l.Computed).File, current, candidate.Channel, level, reason, detail);

            if (top.Preview && !platform.Current.Preview)
                Report(Entry(top, null, EDependencyReportReason.PreviewNotFollowed, "a preview channel; previews are followed only when the repository is on one (R7)"));
            if (target is null)
                return;

            var group = $"platform:{platform.Sdk} {target.Channel}";
            var level = UpdateTypeFloor.Apply(EUpdateLevel.Minor, current, target.Channel);
            if (config.IgnoredBy(eco, eco) is { } why)
            {
                Report(Entry(target, level, EDependencyReportReason.Ignored, why));
                return;
            }
            var hold = config.FileHold(eco, eco) ?? next.Holds.FirstOrDefault(h => h.Group == group || (h.Group.Length == 0 && h.DepNames.Contains(eco)))?.Reason;
            if (hold is not null)
            {
                Report(Entry(target, level, EDependencyReportReason.Held, hold));
                return;
            }
            if (red.Contains((eco, eco, target.Channel)))
            {
                Report(Entry(target, level, EDependencyReportReason.FleetRed, "red in another repository of the fleet (Q15)"));
                return;
            }

            var targets = new[] { $"{eco}@{UpdateTypeFloor.TargetMajor(target.Channel)}" };
            currentTargets.Add((group, eco, targets[0]));
            var ceiling = config.Ceiling(eco, eco);
            if ((int)level > (int)ceiling)
            {
                Report(Entry(target, level, EDependencyReportReason.AboveAccountRule, $"above the account rule's ceiling ({ceiling.ToString().ToLowerInvariant()})"));
                return;
            }
            var threshold = config.Threshold(eco, eco);
            var approved = false;
            if ((int)level > (int)config.Inclusion(eco, eco))
            {
                if (!Approved(group, [eco], targets))
                {
                    Report(Entry(target, level, EDependencyReportReason.AwaitingApproval, $"platform {level.ToString().ToLowerInvariant()} {current} → {target.Channel}: comment 'approve {group}'"));
                    return;
                }
                approved = true;
            }

            var (inCooldown, until) = Cooldown(eco, eco, target.Channel, target.ReleaseTimestamp, securityFix: false);
            if (inCooldown)
            {
                Report(Entry(target, level, EDependencyReportReason.Cooldown, $"released less than {Days(config.Cooldown)} ago") with { Until = until });
                return;
            }

            var settings = config.File.Platform(platform.Sdk);
            var planGroup = new DependencyPlannedGroup
            {
                Group = group,
                Level = level,
                // R7: a platform major never auto-merges, whatever the file says.
                AutoMerge = (int)level <= (int)threshold && level != EUpdateLevel.Major && !approved,
                Approved = approved,
                DepNames = [eco],
                TargetMajors = targets,
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
                    Exclude = settings.Exclude.Count == 0 ? null : [.. settings.Exclude],
                },
            };
            Add(planGroup, [Entry(target, level, EDependencyReportReason.NotInSecurityRun, "left for the next scheduled slot (R10)")]);
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

        private bool Approved(string group, IReadOnlyList<string> depNames, IReadOnlyList<string> targets)
            => next.Approvals.Any(a => a.Group == group && a.ConfigBlob == input.ConfigBlob
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
        /// The bot's own action pin (<c>dependency-action@sha # dependency-update-v1</c>) is the bot's runtime, so it never waits out
        /// the cooldown: a repository would otherwise run the previous action for days after every release. Own-release detection
        /// cannot catch it, because the producing account (the MintPlayer organization) is not the consuming one (P2).
        /// </summary>
        private static bool IsBotRuntime(LookupRecord record)
            => (record.Ecosystem == "github-actions" || record.Manager == "github-actions")
                && string.Equals(record.DepName, Lifecycle.DependencyOnboarding.ActionRepository, StringComparison.Ordinal)
                && string.Equals(record.CurrentValue, Lifecycle.DependencyOnboarding.ActionTag, StringComparison.Ordinal);

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
        })],
    };
}
