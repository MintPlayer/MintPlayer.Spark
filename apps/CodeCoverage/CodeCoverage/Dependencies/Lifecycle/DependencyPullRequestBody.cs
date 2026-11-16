using System.Text;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;

namespace CodeCoverage.Dependencies.Lifecycle;

/// <summary>
/// The rolling pull request's title and body (§7.3, R6, R9, R11, R19): what is applied, what is held and why, what waits for
/// an approval (with the <c>approve &lt;group&gt;</c> to type), what is available but not applied, the coverage delta, the
/// migration markers and the review notes. Rebuilt in full on every publish; never parsed back (the commands read the stored
/// report, not the body).
/// </summary>
public static class DependencyPullRequestBody
{
    /// <summary>The marker the bot finds its own pull request by, beside its branch.</summary>
    public const string Marker = "<!-- mintplayer:dependency-updates -->";

    /// <summary>GitHub's limit on a pull request body.</summary>
    public const int MaxLength = 65_000;

    private const int MaxRows = 60;

    public static string Title(DependencyUpdateRun run, DependencyRunReport report)
    {
        var count = report.Groups.Count;
        var what = count == 1 ? $"Update {report.Groups[0].Group}" : $"Update dependencies ({count} groups)";
        // #568 D13: a line's pull request names its line, so it is told apart from the default branch's at a glance.
        if (run.Line is { } line) what = $"[{line}] {what}";
        return run.Trigger == EDependencyRunTrigger.Security ? $"{what}, with security fixes" : what;
    }

    public static string Render(DependencyUpdateRun run, DependencyRunReport report, string? enqueueRefusal)
    {
        var body = new StringBuilder();
        body.AppendLine(Marker);
        if (run.Bisect is { Culprit: null } bisect && run.State == EDependencyRunState.Bisecting)
            body.AppendLine($"> ⚠️ **Bisecting (step {bisect.Spent} of {bisect.Budget}): don't merge.** CI failed on the full update; the bot is testing it without {Words(bisect.Excluded)}.").AppendLine();
        if (run.PullRequestIsDraft)
            body.AppendLine("> 🧪 The first dependency pull request after onboarding is a draft: review it and mark it ready; it never auto-merges.").AppendLine();
        if (run.MigratedGroups.Count > 0)
            body.AppendLine($"> 🛠️ **Contains code migrations** ({Words(run.MigratedGroups)}). It never auto-merges: review the changed source.").AppendLine();

        // companion-version-maps-PRD §6.5: a section with an error falls back to its defaults, so the problems come first.
        if (run.ConfigProblems.Count > 0)
        {
            body.AppendLine("## Configuration problems").AppendLine();
            body.AppendLine($"{Code("coverage.yml")} on the default branch has problems; a section with one runs on its defaults until it is fixed.").AppendLine();
            foreach (var problem in run.ConfigProblems.Take(MaxRows))
                body.AppendLine($"- {Cell(problem)}");
            body.AppendLine();
        }

        body.AppendLine("## Applied").AppendLine();
        if (report.Groups.Count == 0)
            body.AppendLine("Nothing this time.").AppendLine();
        else
        {
            body.AppendLine("| Group | Dependency | File | From | To | Update |").AppendLine("|---|---|---|---|---|---|");
            // companion-pins-PRD D1: one dependency moving the same way in many files is one row, and every group gets its first
            // row before any group gets a second, so the cap never hides a group (#669 hid the one that broke the build).
            var rows = report.Groups.SelectMany(g => g.Items
                    .GroupBy(i => (i.DepName, i.From, i.To))
                    .Select((same, index) => (Group: g, Item: same.First(), Files: same.Select(i => i.PackageFile).Distinct(StringComparer.Ordinal).Count(), Index: index)))
                .Select((row, order) => (row.Group, row.Item, row.Files, row.Index, Order: order))
                .ToList();
            foreach (var row in rows.OrderBy(r => r.Index == 0 ? 0 : 1).ThenBy(r => r.Order).Take(MaxRows).OrderBy(r => r.Order))
                body.AppendLine($"| {Cell(row.Group.Group)} | {Code(row.Item.DepName)} | {(row.Files == 1 ? Code(row.Item.PackageFile) : $"{row.Files} files")} | {Cell(row.Item.From)} | {Cell(row.Item.To)} | {Level(row.Group)} |");
            More(body, rows.Count);

            // companion-pins-PRD D2: a value whose text around the version changes (boky/postfix v4.3.0 → 4.4.0 in #669) is named.
            var reformatted = rows.Where(r => FormatChanges(r.Item.From, r.Item.To))
                .Select(r => $"- {Code(r.Item.DepName)}: the tag's format changes ({Code(r.Item.From)} → {Code(r.Item.To)}); check the new tag is the one you mean.")
                .Distinct(StringComparer.Ordinal).ToList();
            if (reformatted.Count > 0)
            {
                foreach (var line in reformatted.Take(MaxRows))
                    body.AppendLine(line);
                body.AppendLine();
            }
        }

        if (report.Pins.Count > 0)
        {
            body.AppendLine("## Pinned for a fix on this branch").AppendLine();
            body.AppendLine("Commits pushed on this branch change more than the dependency files, so the versions they were written against stay as they were until this pull request is merged or closed.").AppendLine();
            body.AppendLine("| Group | Dependency | Kept at | Now available |").AppendLine("|---|---|---|---|");
            foreach (var pin in report.Pins.Take(MaxRows))
                body.AppendLine($"| {Cell(pin.Group)} | {Code(pin.DepName)} | {Cell(pin.Version)} | {Cell(pin.Wanted)} |");
            More(body, report.Pins.Count);
        }

        if (report.AwaitingApproval.Count > 0)
        {
            body.AppendLine("## Awaiting approval").AppendLine();
            body.AppendLine("A writer comments the line in the last column to take the group into this pull request.").AppendLine();
            body.AppendLine("| Group | Dependency | From | To | Reason | Comment |").AppendLine("|---|---|---|---|---|---|");
            Rows(body, report.AwaitingApproval.Select(entry => $"| {Cell(entry.Group)} | {Code(entry.DepName)} | {Cell(entry.Current)} | {Cell(entry.Target)} | {Cell(ApprovalReason(entry))} | {Code("/approve " + entry.Group)} |"));
        }

        if (report.Held.Count > 0)
        {
            body.AppendLine("## Held").AppendLine();
            body.AppendLine("| Group | Dependency | Wanted | Why |").AppendLine("|---|---|---|---|");
            Rows(body, report.Held.Select(entry => $"| {Cell(entry.Group)} | {Code(entry.DepName)} | {Cell(entry.Target)} | {Cell(entry.Detail)} |"));
        }

        var noAdapter = report.Failures.Where(f => f.NoAdapter).ToList();
        var failed = report.Failures.Where(f => !f.NoAdapter).ToList();
        if (failed.Count > 0)
        {
            body.AppendLine("## Not applied").AppendLine();
            foreach (var failure in failed.Take(MaxRows))
                body.AppendLine($"- {Code(failure.Group)}: {failure.Status}{(failure.Message is { Length: > 0 } m ? $" ({Cell(m)})" : string.Empty)}. Held until the lookup changes (R2).");
            body.AppendLine();
        }
        if (report.Available.Count > 0 || noAdapter.Count > 0)
        {
            body.AppendLine("## Available, not applied").AppendLine();
            // One line per dependency and version: the same pin in two workflow files is one decision (P2).
            foreach (var line in report.Available.Select(entry => $"- {Code(entry.DepName)} {Cell(entry.Current)} → {Cell(entry.Target)}: {Cell(entry.Detail)}{(entry.Until is { } until ? $" (until {until:yyyy-MM-dd HH:mm} UTC)" : string.Empty)}").Distinct(StringComparer.Ordinal).Take(MaxRows))
                body.AppendLine(line);
            foreach (var failure in noAdapter)
                body.AppendLine($"- {Code(failure.Group)}: no apply adapter for its ecosystem yet.");
            body.AppendLine();
        }

        if (report.PlatformReports.Count > 0 || report.NotFollowed.Count > 0)
            body.AppendLine("## Not moved").AppendLine();
        if (report.PlatformReports.Count > 0)
        {
            // platform-locations-PRD R3: every location a platform move leaves as it was, so a partial move is never silent.
            body.AppendLine("Platform locations this pull request leaves as they are. Move them by hand where they should follow.").AppendLine();
            body.AppendLine("| Platform | File | Value | Why |").AppendLine("|---|---|---|---|");
            Rows(body, report.PlatformReports.Select(r => $"| {Cell(r.Sdk)} | {Code(r.File)} | {Code(r.Value)} | {Cell(r.Reason)} |"));
        }
        if (report.NotFollowed.Count > 0)
        {
            // companion-pins-PRD G4: a coverage.yml follow rule that moved nothing is said, never skipped in silence.
            body.AppendLine("`follow:` rules in coverage.yml that moved nothing this time:").AppendLine();
            foreach (var message in report.NotFollowed.Take(MaxRows))
                body.AppendLine($"- {Cell(message)}");
            body.AppendLine();
        }

        if (report.NotLookedUp.Count > 0)
        {
            // follower-cooldown-PRD G4 (S4): a feed that refused the lookup hides every update it holds; say so, never stay silent.
            body.AppendLine("## Not looked up").AppendLine();
            body.AppendLine("A feed refused the lookup, so these dependencies' updates are unknown. For GitHub Packages, give the lookup workflow "
                + "`permissions: packages: read` (the current template has it); for another private feed, pass a `host-rules` secret.").AppendLine();
            foreach (var message in report.NotLookedUp.Take(MaxRows))
                body.AppendLine($"- {Cell(message)}");
            body.AppendLine();
        }

        if (run.CoverageNote is { Length: > 0 } coverage)
            body.AppendLine("## Coverage").AppendLine().AppendLine(coverage).AppendLine();

        var notes = new List<string>();
        notes.AddRange(report.MigrationNotes);
        if (report.NeedsHumanDetail is { } human)
            notes.Add(human);
        if (report.Groups.Any(g => g.Platform && g.Level == Policy.EUpdateLevel.Major))
            notes.Add("A platform major moves the SDK under every project; it never auto-merges.");
        notes.AddRange(UnlinedProducerNotes(run, report));
        if (enqueueRefusal is { Length: > 0 })
            notes.Add($"Not auto-merged: {enqueueRefusal}");
        notes.AddRange(report.LiftedHolds.Select(h => $"Hold lifted: {h}."));
        notes.AddRange(report.ExpiredApprovals.Select(g => $"The approval of `{g}` expired (a newer major, another membership, or another coverage.yml)."));
        if (notes.Count > 0)
        {
            body.AppendLine("## Review notes").AppendLine();
            foreach (var note in notes.Take(MaxRows))
                body.AppendLine($"- {note}");
            body.AppendLine();
        }

        body.AppendLine("<details><summary>Commands</summary>").AppendLine();
        body.AppendLine("Writers comment one per comment: `/approve <group>`, `/hold <group|package>`, `/unhold <group|package>`, `/bisect`, `/refresh`. "
            + "The bot reacts on the comment: 👀 on it, 🚀 started, 🎉 done, 👍 nothing to change, 👎 refused or failed. Commands never override the account's enforced rules.");
        body.AppendLine().AppendLine("</details>");

        var text = body.ToString();
        return text.Length <= MaxLength ? text : text[..(MaxLength - 40)] + "\n\n… (shortened to GitHub's limit)\n";
    }

    /// <summary>
    /// #606 D4: a platform major in a repository that releases packages without release lines. Its packages move to the new
    /// platform with this merge, and nothing keeps releasing for the previous one.
    /// </summary>
    internal static IEnumerable<string> UnlinedProducerNotes(DependencyUpdateRun run, DependencyRunReport report)
        => report.Groups
            .Where(g => g.Platform && g.Level == Policy.EUpdateLevel.Major)
            .Select(g => (g.Group, Sdk: g.Group["platform:".Length..].Split(' ')[0]))
            .Where(g => run.FileUnlinedSdks.Contains(g.Sdk, StringComparer.Ordinal))
            .Select(g => $"⚠️ `{g.Group}` replaces the {g.Sdk} platform this repository's packages target, and coverage.yml keeps no release lines: "
                + "consumers on the previous platform stop receiving releases. Add `versioning.lines` to keep a line for them, "
                + $"or `dependencies.platforms.{g.Sdk}.strategy: add` to target both.");

    /// <summary>companion-pins-PRD D2: the text before or after the version (a <c>v</c> prefix, a <c>-noble</c> variant) differs
    /// between <paramref name="from"/> and <paramref name="to"/>. A prerelease moving to its release is no format change.</summary>
    internal static bool FormatChanges(string from, string to)
    {
        if (DependencyFollowRule.DefaultVersion(from) is not { } a || DependencyFollowRule.DefaultVersion(to) is not { } b)
            return false;
        var (fromAt, toAt) = (from.IndexOf(a, StringComparison.Ordinal), to.IndexOf(b, StringComparison.Ordinal));
        return from[..fromAt] != to[..toAt] || from[(fromAt + a.Length)..] != to[(toAt + b.Length)..];
    }

    private static string Level(DependencyReportGroup group)
        => group.Level.ToString().ToLowerInvariant() + (group.Approved ? " (approved)" : string.Empty) + (group.Security ? " 🔒" : string.Empty);

    /// <summary>Table rows without a file column: the same dependency in two files renders, and counts, once.</summary>
    private static void Rows(StringBuilder body, IEnumerable<string> rows)
    {
        var distinct = rows.Distinct(StringComparer.Ordinal).ToList();
        foreach (var row in distinct.Take(MaxRows))
            body.AppendLine(row);
        More(body, distinct.Count);
    }

    private static void More(StringBuilder body, int total)
    {
        if (total > MaxRows)
            body.AppendLine().AppendLine($"… and {total - MaxRows} more.");
        body.AppendLine();
    }

    /// <summary>
    /// prerelease-dependencies-PRD D1: why a group waits (the threshold, a platform major, P11), as the policy wrote it, without
    /// the "comment '/approve …'" the last column already gives.
    /// </summary>
    private static string? ApprovalReason(DependencyReportEntry entry)
    {
        var command = $": comment '/approve {entry.Group}'";
        return entry.Detail?.Replace(command + ", ", "; ", StringComparison.Ordinal).Replace(command, string.Empty, StringComparison.Ordinal);
    }

    private static string Words(IEnumerable<string> groups) => string.Join(", ", groups.Select(g => $"`{g}`"));

    /// <summary>A code span in a table cell, copyable as it is (an <c>approve</c> line): only a pipe or a backtick could break it.</summary>
    private static string Code(string? text)
        => $"`{(text ?? "—").Replace("|", "\\|", StringComparison.Ordinal).Replace("`", "'", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)}`";

    /// <summary>A table cell: no pipe and no line break can break the table, and no HTML or mention is rendered.</summary>
    private static string Cell(string? text)
        => (text ?? "—").Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal).Replace("@", "@​", StringComparison.Ordinal);
}
