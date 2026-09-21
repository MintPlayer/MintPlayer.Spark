using System.Globalization;
using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Forge;

namespace CodeCoverage.Feedback;

/// <summary>
/// Renders the body of the sticky coverage comment. Pure — no GitHub, no
/// RavenDB, no configuration lookups beyond the two values handed in — so every
/// rule here is unit-testable.
/// <para>
/// The first line is always <see cref="Marker"/>: it is how the publisher
/// re-adopts its own comment when the stored id is gone (a human deleted it),
/// which is what keeps "one comment per PR" true rather than merely intended.
/// </para>
/// </summary>
public static class PullRequestCommentRenderer
{
    /// <summary>
    /// Identifies the comment as ours. An HTML comment, so it is invisible in
    /// the rendered body but present in the API's <c>body</c>.
    /// </summary>
    /// <remarks>
    /// The value lives in <see cref="CoverageCommentMarker"/>, in the library, because the
    /// publisher needs it too and now sits in a different assembly. Kept here as an alias so every
    /// call site in this file reads the same as before.
    /// </remarks>
    public const string Marker = CoverageCommentMarker.Value;

    /// <summary>
    /// What the comment says between the PR opening and CI finishing. Named
    /// after the sha so a reader can tell whether it is stale.
    /// </summary>
    public static string RenderPending(Entities.Repository repository, string sha, string? baseUrl)
    {
        var body = new StringBuilder();
        body.AppendLine(Marker);
        body.AppendLine("### Coverage");
        body.AppendLine();
        body.AppendLine($"Waiting for coverage for `{Short(sha)}`. This comment updates itself when the upload finalizes.");
        AppendFooter(body, repository, sha, baseUrl);
        return body.ToString();
    }

    /// <summary>
    /// The real thing. <paramref name="project"/> and <paramref name="patch"/> are the very verdicts the
    /// two check-runs publish, so the comment cannot disagree with the checks.
    /// </summary>
    /// <param name="badgeSignature">
    /// PR-scoped signature for a private repository's badge, or null. Never
    /// pass <c>Repository.BadgeToken</c>: it is manager-only, repo-wide and
    /// never expires, and a comment is read by every collaborator and gets
    /// quoted onward. Null on a private repo simply omits the image.
    /// </param>
    public static string Render(
        Entities.Repository repository,
        Entities.Commit commit,
        CheckVerdict project,
        CheckVerdict patch,
        CommitAssembly? assembly,
        string? baseUrl,
        string? badgeSignature)
    {
        var body = new StringBuilder();
        body.AppendLine(Marker);
        body.AppendLine("### Coverage");
        body.AppendLine();

        // ⚠️ Stated before the numbers, not after them. A reader who takes the table at face value
        // and stops has still been told the one thing that changes how to read it — that this
        // measurement came from a workflow the maintainers do not control, on code under review.
        // Put below the table, it would be a footnote on a claim already made.
        if (commit.ContributedFromFork)
        {
            body.AppendLine("> **Contributed from a fork.** These numbers were produced by a workflow in the "
                          + "contributor's repository, so they are reported for information only: they never "
                          + "gate this pull request, and they do not affect this repository's coverage or badge.");
            body.AppendLine();
        }

        var badge = BadgeMarkdown(repository, commit, baseUrl, badgeSignature);
        if (badge is not null)
        {
            body.AppendLine(badge);
            body.AppendLine();
        }

        body.AppendLine("| Check | Result |");
        body.AppendLine("| --- | --- |");
        body.AppendLine($"| Project | {Cell(project)} |");
        body.AppendLine($"| Patch | {Cell(patch)} |");
        body.AppendLine();

        if (commit.PullRequestBaseRef is { Length: > 0 } baseRef)
            body.AppendLine($"Compared against `{baseRef}`.");

        // Say it plainly when the number is a subset's total. The badge's
        // "coverage (partial)" label says the same thing in the image, but the
        // reason only fits here.
        if (assembly is not null && assembly.Completeness != CommitAssembly.Complete)
        {
            var reasons = assembly.IncompleteReasons.Count > 0
                ? string.Join(", ", assembly.IncompleteReasons.Select(r => $"`{r}`"))
                : "`unknown`";
            body.AppendLine();
            body.AppendLine($"> **Partial measurement.** This total does not cover the whole repository ({reasons}), "
                          + "so treat the number as a floor rather than a verdict.");
        }

        AppendFooter(body, repository, commit.Sha, baseUrl);
        return body.ToString();
    }

    /// <summary>
    /// The badge image, or null when there is nothing safe to link. A private
    /// repository needs a signature; without one there is no way to render an
    /// image that will load, and no acceptable way to make one.
    /// </summary>
    private static string? BadgeMarkdown(Entities.Repository repository, Entities.Commit commit, string? baseUrl, string? badgeSignature)
    {
        if (string.IsNullOrEmpty(baseUrl) || commit.PullRequestNumber is not { } pr) return null;

        var provider = repository.Provider.ToCanonicalString();
        var url = $"{baseUrl.TrimEnd('/')}/badge/{provider}/{repository.OwnerLogin}/{repository.Name}.svg?pr={pr}";
        if (repository.IsPrivate)
        {
            if (string.IsNullOrEmpty(badgeSignature)) return null;
            url += $"&sig={badgeSignature}";
        }

        return $"[![Coverage]({url})]({baseUrl.TrimEnd('/')}/{provider}/r/{repository.OwnerLogin}/{repository.Name})";
    }

    private static string Cell(CheckVerdict verdict)
    {
        var icon = verdict.Conclusion switch
        {
            "success" => "✅",
            "failure" => "❌",
            _ => "➖",
        };
        return $"{icon} {verdict.Title}";
    }

    private static void AppendFooter(StringBuilder body, Entities.Repository repository, string sha, string? baseUrl)
    {
        body.AppendLine();

        // ⚠ The commit deep-link is still GitHub-shaped. Every forge spells it differently
        // (GitLab interposes /-/, Bitbucket says /commits/) and a self-hosted GitLab is not even on
        // gitlab.com, so this cannot be a pure function of owner/name/sha - it needs the
        // integration, which this renderer does not have. Left as-is rather than guessed: the
        // comment is posted INTO the forge, so a wrong host is worse than a plain sha. M15 gives
        // IForgeIntegration a commit-URL builder and this becomes a lookup.
        var commitLink = repository.Provider == EForgeProvider.GitHub
            ? $"https://github.com/{repository.OwnerLogin}/{repository.Name}/commit/{sha}"
            : null;
        var report = string.IsNullOrEmpty(baseUrl)
            ? null
            : $"{baseUrl.TrimEnd('/')}/{repository.Provider.ToCanonicalString()}/r/{repository.OwnerLogin}/{repository.Name}";

        if (commitLink is null)
            body.Append(CultureInfo.InvariantCulture, $"<sub>Head `{Short(sha)}`");
        else
            body.Append(CultureInfo.InvariantCulture, $"<sub>Head [`{Short(sha)}`]({commitLink})");
        if (report is not null) body.Append(CultureInfo.InvariantCulture, $" · [full report]({report})");
        body.AppendLine("</sub>");
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
