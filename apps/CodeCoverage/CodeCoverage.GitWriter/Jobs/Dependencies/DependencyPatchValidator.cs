using System.Text;
using System.Text.Json;
using CodeCoverage.Git.MergeQueue.Resolution;

namespace CodeCoverage.GitWriter.Jobs.Dependencies;

/// <summary>
/// The <c>dependencies</c> patch profile (docs/code-coverage/dependency-updates-PRD.md §13.5 P5, R14, R15, gaps A–C): the
/// apply workflow's <c>git diff --binary</c> artifact, checked against the plan BEFORE the writer applies it. The apply
/// runner installed fresh packages, so its patch is untrusted; parsing only, nothing of the repository runs.
/// <list type="number">
/// <item>The strict shape of <see cref="PatchValidator"/> (modes, symlinks, binaries, copies, exact hunks, caps), with
/// <c>.github/</c> open only to the workflow carve-out.</item>
/// <item>Every profile refuses the bot's plan path (<c>.mintplayer/</c>, R14) and <c>coverage.yml</c>; then the deny-list
/// (<see cref="DependencyFiles.IsDenied"/>) wins over every allow rule; then no file may be added, deleted or renamed.
/// The one exception to the deny-list (#613 D23, owner): an EDIT of an existing <c>Directory.Build.props</c>/<c>.targets</c>
/// (<see cref="DependencyFiles.IsSubstitutionOnly"/>) passes when every changed line is a planned NuGet substitution or a
/// platform location swap; anything else in it is <see cref="DependencyPatchReasons.DeniedPath"/>.</item>
/// <item>Platform groups (#613 D20–D22, D24): the request's <see cref="DependencyPatchRequest.Platforms"/> carry each
/// SDK move with the locations the lookup reported; a changed line in such a file must be
/// <see cref="PlatformSubstitution.Explain"/>'s token swap, and an applied platform group needs at least one.</item>
/// <item>The allow-list is the plan's package files that match a code-defined name for their datasource AND exist at
/// <c>expected_sha</c> as regular files, plus their code-defined lockfiles; the report can select, never widen.</item>
/// <item>Manifests: <see cref="SubstitutionCheck"/> on every changed line pair; <c>global.json</c>'s
/// <c>msbuild-sdks</c> are never touched.</item>
/// <item>Lockfiles: <see cref="LockfileScanner"/> (gap B); each must belong to an applied entry.</item>
/// <item>Workflows and <c>action.yml</c>: <see cref="WorkflowRefPatchRule"/>, then
/// <see cref="GitHubWorkflowValidator.ValidateChange"/> on the file before and after the patch (gap C).</item>
/// <item>The applied groups are derived from the patch: whole groups only, equal to <c>result.json</c>'s claim.</item>
/// </list>
/// </summary>
public static class DependencyPatchValidator
{
    public static async Task<DependencyPatchVerdict> ValidateAsync(ReadOnlyMemory<byte> patch, DependencyPatchRequest request, IBaseTree baseTree,
        CancellationToken cancellationToken = default)
    {
        var files = new List<PatchFile>();
        var shape = PatchValidator.Parse(patch.Span, PathPolicy, request.MaxBytes, files);
        if (!shape.Accepted)
            return DependencyPatchVerdict.From(shape);

        foreach (var file in files)
        {
            // #613 item 3: an existing Directory.Build.* may get strict substitutions; a new, deleted or renamed one stays denied.
            if ((file.IsNew || file.IsDeleted || file.IsRename) && (DependencyFiles.IsDenied(file.Path) || file.IsRename && DependencyFiles.IsDenied(file.OldPath)))
                return DependencyPatchVerdict.Reject(DependencyPatchReasons.DeniedPath, "The deny-list wins over every allow rule.", file.IsRename ? [file.OldPath, file.NewPath] : [file.Path]);
            if (file.IsNew || file.IsDeleted || file.IsRename)
                return DependencyPatchVerdict.Reject(DependencyPatchReasons.AddedOrDeleted,
                    "The dependencies profile only edits files that exist; it adds, deletes and renames none.", file.IsRename ? [file.OldPath, file.NewPath] : [file.Path]);
        }

        // The allow-list: planned package files that a code-defined pattern admits and the base holds as regular files.
        var manifests = new Dictionary<string, List<DependencyPlanEntry>>(StringComparer.Ordinal);
        var workflows = new Dictionary<string, List<DependencyPlanEntry>>(StringComparer.Ordinal);
        foreach (var entry in request.Plan)
        {
            var path = entry.PackageFile;
            // #613 item 3: Directory.Build.props/.targets hold NuGet references too; edited only by SubstitutionCheck.
            var substitutionOnly = DependencyFiles.IsSubstitutionOnly(path) && entry.Datasource == "nuget";
            if (!PatchValidator.IsWritablePath(path) || DependencyFiles.IsPlanPath(path) || DependencyFiles.IsCoverageConfig(path)
                || DependencyFiles.IsDenied(path) && !substitutionOnly)
                continue;
            var target = DependencyFiles.IsWorkflowFile(path) ? DependencyFiles.WorkflowDatasources.Contains(entry.Datasource) ? workflows : null
                : DependencyFiles.IsManifest(entry.Datasource, path) || substitutionOnly ? manifests : null;
            if (target is null || await baseTree.ModeAsync(path, cancellationToken) != "100644")
                continue;
            if (!target.TryGetValue(path, out var list))
                target[path] = list = [];
            list.Add(entry);
        }

        // #613 item 2: the files the lookup reported a platform location in, for the plan's platform groups.
        var platforms = new Dictionary<string, List<(DependencyPlatformMove, DependencyPlatformLocation)>>(StringComparer.Ordinal);
        foreach (var move in request.Platforms)
        {
            foreach (var location in move.Locations)
            {
                var path = location.File;
                if (!PatchValidator.IsWritablePath(path) || DependencyFiles.IsPlanPath(path) || DependencyFiles.IsCoverageConfig(path)
                    || DependencyFiles.IsDenied(path) && !DependencyFiles.IsSubstitutionOnly(path)
                    || PatchValidator.IsUnderGitHub(path) && !DependencyFiles.IsWorkflowFile(path)
                    || DependencyFiles.LockfileNames.Contains(DependencyFiles.FileName(path)))
                    continue;
                if (!platforms.TryGetValue(path, out var list))
                {
                    if (await baseTree.ModeAsync(path, cancellationToken) != "100644")
                        continue;
                    platforms[path] = list = [];
                }
                list.Add((move, location));
            }
        }

        var used = new HashSet<DependencyPlanEntry>();
        var platformApplied = new HashSet<string>(StringComparer.Ordinal);
        var lockfiles = new List<PatchFile>();
        var requiresWorkflows = false;
        foreach (var file in files)
        {
            var path = file.Path;
            var platformHere = platforms.GetValueOrDefault(path) ?? [];
            var workflowEntries = workflows.GetValueOrDefault(path);
            if (workflowEntries is not null || platformHere.Count > 0 && DependencyFiles.IsWorkflowFile(path))
            {
                var (workflowRefused, workflowApplied, workflowPlatforms) = await WorkflowAsync(file, workflowEntries ?? [], platformHere, request, baseTree, cancellationToken);
                if (workflowRefused is not null)
                    return workflowRefused;
                used.UnionWith(workflowApplied);
                platformApplied.UnionWith(workflowPlatforms);
                requiresWorkflows |= DependencyFiles.NeedsWorkflowsPermission(path);
                continue;
            }
            var manifestEntries = manifests.GetValueOrDefault(path);
            if (manifestEntries is not null || platformHere.Count > 0)
            {
                if (await ManifestAsync(file, manifestEntries ?? [], platformHere, baseTree, used, platformApplied, cancellationToken) is { } refused)
                    return refused;
                continue;
            }
            if (DependencyFiles.LockfileNames.Contains(DependencyFiles.FileName(path)))
            {
                lockfiles.Add(file);
                continue;
            }
            if (DependencyFiles.IsSubstitutionOnly(path))
                return DependencyPatchVerdict.Reject(DependencyPatchReasons.DeniedPath,
                    "Directory.Build.props/.targets change only by a planned NuGet swap or a reported platform location, and this patch plans neither here.", path);
            return DependencyPatchVerdict.Reject(DependencyPatchReasons.OutsidePlannedFiles,
                "Not a planned package file at the planned commit, nor a lockfile of one, nor a workflow the plan updates, nor a reported platform location.", path);
        }

        // Lockfile-only entries apply when a lockfile of theirs changed; every changed lockfile needs an applied owner.
        foreach (var entry in request.Plan.Where(e => e.IsLockfileOnly && manifests.ContainsKey(e.PackageFile)))
        {
            if (lockfiles.Any(l => DependencyFiles.IsLockfileOf(entry.Datasource, entry.PackageFile, l.Path)))
                used.Add(entry);
        }
        var newPackages = new List<string>();
        foreach (var lockfile in lockfiles)
        {
            if (!used.Any(e => DependencyFiles.IsLockfileOf(e.Datasource, e.PackageFile, lockfile.Path)))
                return DependencyPatchVerdict.Reject(DependencyPatchReasons.UnownedLockfile, "No applied planned entry owns this lockfile.", lockfile.Path);
            if (await baseTree.ModeAsync(lockfile.Path, cancellationToken) != "100644" || await baseTree.ReadTextAsync(lockfile.Path, cancellationToken) is not { } baseText)
                return DependencyPatchVerdict.Reject(DependencyPatchReasons.UnownedLockfile, "The lockfile is not a readable regular file at the planned commit.", lockfile.Path);
            var scan = LockfileScanner.Scan(lockfile, baseText, request.Lockfiles);
            if (scan.Refused)
                return DependencyPatchVerdict.Reject(scan.Reason!, scan.Detail, lockfile.Path);
            newPackages.AddRange(scan.NewPackages.Select(n => $"{lockfile.Path}: {n}"));
        }

        // Groups, derived from the patch: whole groups only, and exactly the ones result.json claims.
        var applied = used.Select(e => e.GroupName).Concat(platformApplied).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        foreach (var group in applied)
        {
            var missing = request.Plan.Where(e => e.GroupName == group && !used.Contains(e)).Select(e => $"{e.DepName} in {e.PackageFile}").ToList();
            // #613 item 2: a platform group's companions ride its SDK move; they never apply without it.
            if (request.Platforms.Any(p => p.Group == group) && !platformApplied.Contains(group))
                missing.Add("its SDK move");
            if (missing.Count > 0)
                return DependencyPatchVerdict.Reject(DependencyPatchReasons.PartialGroup,
                    $"Group '{group}' is applied without {string.Join(", ", missing)}; a group moves together.");
        }
        if (request.ClaimedAppliedGroups is { } claimed)
        {
            var claimedSet = claimed.ToHashSet(StringComparer.Ordinal);
            var extra = applied.Where(g => !claimedSet.Contains(g)).ToList();
            var absent = claimedSet.Where(g => !applied.Contains(g, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToList();
            if (extra.Count > 0 || absent.Count > 0)
                return DependencyPatchVerdict.Reject(DependencyPatchReasons.GroupMismatch,
                    $"The patch applies [{string.Join(", ", applied)}] but result.json claims [{string.Join(", ", claimedSet.Order(StringComparer.Ordinal))}].");
        }

        var paths = files.Select(f => f.Path).ToList();
        return newPackages.Count > request.Lockfiles.MaxNewPackages
            ? new DependencyPatchVerdict(DependencyPatchOutcome.NeedsHuman, null,
                $"The lockfiles bring in {newPackages.Count} new packages; more than {request.Lockfiles.MaxNewPackages} needs a human.", paths, applied, requiresWorkflows, newPackages)
            : new DependencyPatchVerdict(DependencyPatchOutcome.Accepted, null, null, paths, applied, requiresWorkflows, newPackages);
    }

    /// <summary>The path rule of every dependency profile: git's safe shape, the plan path and <c>coverage.yml</c>
    /// refused, <c>.github/</c> open only to workflow files, the deny-list.</summary>
    private static PatchVerdict? PathPolicy(string path)
    {
        if (DependencyFiles.IsPlanPath(path))
            return PatchVerdict.Reject(DependencyPatchReasons.PlanPath, "The bot's plan file (R14) is never part of a patch.", path);
        if (!PatchValidator.IsWritablePath(path) || PatchValidator.IsUnderGitHub(path) && !DependencyFiles.IsWorkflowFile(path))
            return PatchVerdict.Reject(PatchReasons.UnsafePath, null, path);
        // #613 item 3: Directory.Build.props/.targets pass here and are held to strict substitutions below; an added one is
        // refused there with every other added file.
        if (DependencyFiles.IsCoverageConfig(path) || DependencyFiles.IsDenied(path) && !DependencyFiles.IsSubstitutionOnly(path))
            return PatchVerdict.Reject(DependencyPatchReasons.DeniedPath, "The deny-list wins over every allow rule.", path);
        return null;
    }

    /// <summary>A manifest, or a file holding a reported platform location: every changed line is a planned
    /// <see cref="SubstitutionCheck"/> swap or a <see cref="PlatformSubstitution"/> of a reported location (#613 item 2).</summary>
    private static async Task<DependencyPatchVerdict?> ManifestAsync(PatchFile file, List<DependencyPlanEntry> entries,
        List<(DependencyPlatformMove, DependencyPlatformLocation)> platform, IBaseTree baseTree, HashSet<DependencyPlanEntry> used,
        HashSet<string> platformApplied, CancellationToken cancellationToken)
    {
        (int From, int To)? sdks = null;
        if (DependencyFiles.FileName(file.Path) == "global.json")
        {
            var globalJson = await baseTree.ReadTextAsync(file.Path, cancellationToken);
            sdks = globalJson is null ? null : MsBuildSdksLines(globalJson);
            if (sdks is null)
                return DependencyPatchVerdict.Reject(DependencyPatchReasons.UnplannedChange, "global.json at the planned commit is not readable JSON.", file.Path);
        }
        foreach (var hunk in file.Hunks)
        {
            foreach (var block in hunk.Blocks())
            {
                if (block.Removed.Count != block.Added.Count)
                    return DependencyPatchVerdict.Reject(DependencyPatchReasons.NotASubstitution,
                        $"Line {block.OldLine}: {block.Removed.Count} line(s) replaced by {block.Added.Count}; a planned update changes values in place.", file.Path);
                for (var k = 0; k < block.Removed.Count; k++)
                {
                    var line = block.OldLine + k;
                    if (sdks is { } s && line >= s.From && line <= s.To)
                        return DependencyPatchVerdict.Reject(DependencyPatchReasons.DeniedPath, $"Line {line}: global.json's msbuild-sdks are never edited.", file.Path);
                    var above = block.Before.Concat(block.Removed.Take(k)).ToList();
                    if (entries.Count > 0 && SubstitutionCheck.Explain(block.Removed[k], block.Added[k], above, entries) is { } explained)
                    {
                        used.UnionWith(explained);
                        continue;
                    }
                    if (PlatformSubstitution.Explain(block.Removed[k], block.Added[k], platform) is { } group)
                    {
                        platformApplied.Add(group);
                        continue;
                    }
                    return DependencyPatchVerdict.Reject(DependencyPatchReasons.UnplannedChange,
                        $"Line {line}: the change is not a planned current → new swap on a line naming the dependency, nor a planned platform move of a reported location.", file.Path);
                }
            }
        }
        return null;
    }

    /// <summary>The 1-based line range of global.json's <c>msbuild-sdks</c> value, (0, -1) when it has none, or null when
    /// the file is not JSON.</summary>
    private static (int From, int To)? MsBuildSdksLines(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text.StartsWith('﻿') ? text[1..] : text);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals("msbuild-sdks"))
                {
                    var from = LineOf(bytes, reader.TokenStartIndex);
                    reader.Read();
                    reader.Skip();
                    return (from, LineOf(bytes, reader.TokenStartIndex));
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return (0, -1);
    }

    private static int LineOf(byte[] bytes, long offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
                line++;
        }
        return line;
    }

    /// <summary>One workflow file: the line rule, then the YAML comparison of the file before and after the patch.</summary>
    private static async Task<(DependencyPatchVerdict? Refused, IReadOnlyList<DependencyPlanEntry> Entries, IReadOnlySet<string> Platforms)> WorkflowAsync(
        PatchFile file, List<DependencyPlanEntry> entries, List<(DependencyPlatformMove Move, DependencyPlatformLocation Location)> platform,
        DependencyPatchRequest request, IBaseTree baseTree, CancellationToken cancellationToken)
    {
        var none = new HashSet<string>();
        var (plan, reason, detail) = WorkflowRefPatchRule.Check(file, entries, platform);
        if (plan is null)
            return (DependencyPatchVerdict.Reject(reason!, detail, file.Path), [], none);
        if (await baseTree.ReadTextAsync(file.Path, cancellationToken) is not { } before || file.ApplyTo(before) is not { } after)
            return (DependencyPatchVerdict.Reject(PatchReasons.ApplyFailed, "The patch does not apply to the file at the planned commit.", file.Path), [], none);
        var rules = new WorkflowChangeRules(plan.Uses, plan.With)
        {
            TargetActions = request.TargetActions,
            PlatformChange = platform.Count == 0 ? null : (old, @new) => PlatformSubstitution.Explain(old, @new, platform) is not null,
        };
        var change = GitHubWorkflowValidator.ValidateChange(before, after, rules);
        if (!change.Verdict.Valid)
            return (DependencyPatchVerdict.Reject(PatchReasons.InvalidContent,
                $"After the patch, the file fails github-workflow at {change.Verdict.Position}: {change.Verdict.Problem}.", file.Path), [], none);
        // Every pair the line rule accepted must be a node change the comparison found: belt and braces, since the
        // comparison already refuses every unplanned difference.
        var covered = plan.Uses.All(u => change.Changes.Any(c => c.Old == u.Old && c.New == u.New))
                      && plan.With.All(w => change.Changes.Any(c => c.Path.EndsWith($".with.{w.Key}", StringComparison.Ordinal) && c.Old == w.Old && c.New == w.New));
        if (!covered)
            return (DependencyPatchVerdict.Reject(DependencyPatchReasons.WorkflowEdit, "A changed line is not a uses: or with: value of a step.", file.Path), [], none);
        return (null, plan.Entries, plan.PlatformGroups);
    }
}
