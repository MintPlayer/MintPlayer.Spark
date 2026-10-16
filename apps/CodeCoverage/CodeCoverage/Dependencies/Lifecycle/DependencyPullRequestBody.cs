using System.Text;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;

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

        body.AppendLine("## Applied").AppendLine();
        if (report.Groups.Count == 0)
            body.AppendLine("Nothing this time.").AppendLine();
        else
        {
            body.AppendLine("| Group | Dependency | File | From | To | Update |").AppendLine("|---|---|---|---|---|---|");
            foreach (var row in report.Groups.SelectMany(g => g.Items.Select(i => (g, i))).Take(MaxRows))
                body.AppendLine($"| {Cell(row.g.Group)} | {Code(row.i.DepName)} | {Code(row.i.PackageFile)} | {Cell(row.i.From)} | {Cell(row.i.To)} | {Level(row.g)} |");
            More(body, report.Groups.Sum(g => g.Items.Count));
        }

        if (report.AwaitingApproval.Count > 0)
        {
            body.AppendLine("## Awaiting approval").AppendLine();
            body.AppendLine("Over the auto-merge threshold (`dependencies.autoMerge` in coverage.yml). A writer comments the line in the last column to take the group into this pull request.").AppendLine();
            body.AppendLine("| Group | Dependency | From | To | Comment |").AppendLine("|---|---|---|---|---|");
            foreach (var entry in report.AwaitingApproval.Take(MaxRows))
                body.AppendLine($"| {Cell(entry.Group)} | {Code(entry.DepName)} | {Cell(entry.Current)} | {Cell(entry.Target)} | {Code("approve " + entry.Group)} |");
            More(body, report.AwaitingApproval.Count);
        }

        if (report.Held.Count > 0)
        {
            body.AppendLine("## Held").AppendLine();
            body.AppendLine("| Group | Dependency | Wanted | Why |").AppendLine("|---|---|---|---|");
            foreach (var entry in report.Held.Take(MaxRows))
                body.AppendLine($"| {Cell(entry.Group)} | {Code(entry.DepName)} | {Cell(entry.Target)} | {Cell(entry.Detail)} |");
            More(body, report.Held.Count);
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
            foreach (var entry in report.Available.Take(MaxRows))
                body.AppendLine($"- {Code(entry.DepName)} {Cell(entry.Current)} → {Cell(entry.Target)}: {Cell(entry.Detail)}{(entry.Until is { } until ? $" (until {until:yyyy-MM-dd HH:mm} UTC)" : string.Empty)}");
            foreach (var failure in noAdapter)
                body.AppendLine($"- {Code(failure.Group)}: no apply adapter for its ecosystem yet.");
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
        body.AppendLine("Writers comment one per comment: `approve <group>`, `hold <group|package>`, `unhold <group|package>`, `bisect`, `refresh`. Commands never override the account's enforced rules.");
        body.AppendLine().AppendLine("</details>");

        var text = body.ToString();
        return text.Length <= MaxLength ? text : text[..(MaxLength - 40)] + "\n\n… (shortened to GitHub's limit)\n";
    }

    private static string Level(DependencyReportGroup group)
        => group.Level.ToString().ToLowerInvariant() + (group.Approved ? " (approved)" : string.Empty) + (group.Security ? " 🔒" : string.Empty);

    private static void More(StringBuilder body, int total)
    {
        if (total > MaxRows)
            body.AppendLine().AppendLine($"… and {total - MaxRows} more.");
        body.AppendLine();
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
