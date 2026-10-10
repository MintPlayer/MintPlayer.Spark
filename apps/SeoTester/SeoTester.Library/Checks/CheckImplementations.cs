using SeoTester.Catalogue;
using SeoTester.Scoring;

namespace SeoTester.Checks;

/// <summary>
/// Which catalogue entries have an implementation (<c>docs/seo_analyzer_PRD.md</c> §3.1: "an entry without an
/// implementation shows as unavailable, and an implementation without an entry is not run"). M9 implements every
/// check of the raw HTML, the headers, TLS, DNS, the registries and the crawl; M10 the ones that need the rendered
/// page (Chromium, <see cref="RenderChecks"/>) or PageSpeed Insights (<see cref="PageSpeed.PsiChecks"/>).
/// </summary>
public static class CheckImplementations
{
    /// <summary>Checks the pipeline measures (page, site and crawl level; rendered; PageSpeed Insights).</summary>
    public static IReadOnlySet<string> Implemented { get; } = new HashSet<string>(
        PageChecks.Ids.Concat(SiteChecks.Ids).Concat(CrawlChecks.Ids).Concat(RenderChecks.Ids).Concat(PageSpeed.PsiChecks.Ids), StringComparer.Ordinal);

    public static bool IsImplemented(string checkId) => Implemented.Contains(checkId);
}

/// <summary>One check's tally over a run: the scoring outcome plus what the report shows.</summary>
/// <param name="Detail">The first failing (else warning) finding's placeholders, for the issue's sentence.</param>
public sealed record CheckTally(CheckOutcome Outcome, IReadOnlyList<string> SampleUrls, IReadOnlyDictionary<string, string>? Detail);

/// <summary>
/// Adds a run's findings up per check (§7): one vote per (check, URL), the worst status winning when two sources
/// judged the same URL (a page check and the crawl's look at it), and site-level findings once.
/// </summary>
public sealed class RunTally
{
    private readonly Dictionary<(string Check, string Url), CheckFinding> votes = new();

    public void Add(string? url, CheckFinding finding)
    {
        if (!CheckImplementations.IsImplemented(finding.CheckId))
            return;
        var key = (finding.CheckId, url ?? string.Empty);
        if (!votes.TryGetValue(key, out var existing) || Rank(finding.Status) > Rank(existing.Status))
            votes[key] = finding;
    }

    public IReadOnlyDictionary<string, CheckTally> Tallies()
    {
        var result = new Dictionary<string, CheckTally>(StringComparer.Ordinal);
        foreach (var check in votes.GroupBy(v => v.Key.Check, StringComparer.Ordinal))
        {
            int pass = 0, warn = 0, fail = 0, na = 0, error = 0;
            foreach (var vote in check)
            {
                switch (vote.Value.Status)
                {
                    case CheckStatus.Pass: pass++; break;
                    case CheckStatus.Warn: warn++; break;
                    case CheckStatus.Fail: fail++; break;
                    case CheckStatus.NotApplicable: na++; break;
                    default: error++; break;
                }
            }
            var flagged = check.Where(v => v.Value.Status is CheckStatus.Fail or CheckStatus.Warn)
                .OrderByDescending(v => Rank(v.Value.Status)).ThenBy(v => v.Key.Url, StringComparer.Ordinal).ToList();
            var samples = flagged.Select(v => v.Key.Url).Where(u => u.Length > 0).Distinct().Take(5).ToList();
            // Nothing flagged: a "why not measured" (a reason of a not-applicable or error finding) is kept instead.
            var detail = flagged.FirstOrDefault().Value?.Detail
                ?? check.Select(v => v.Value).FirstOrDefault(f => (f.Status == CheckStatus.NotApplicable && f.Detail?.ContainsKey("reason") == true) || (f.Status == CheckStatus.Error && f.Detail is not null))?.Detail;
            result[check.Key] = new CheckTally(new CheckOutcome(check.Key, pass, warn, fail, na, error), samples, detail);
        }
        return result;
    }

    private static int Rank(CheckStatus status) => status switch
    {
        CheckStatus.Fail => 4,
        CheckStatus.Warn => 3,
        CheckStatus.Pass => 2,
        CheckStatus.Error => 1,
        _ => 0,
    };
}
