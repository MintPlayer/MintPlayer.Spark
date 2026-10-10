using System.Globalization;
using System.Text.Json;
using SeoTester.Checks;
using SeoTester.Scoring;

namespace SeoTester.PageSpeed;

/// <summary>One CrUX metric at the 75th percentile, as PageSpeed Insights reports it (<c>loadingExperience.metrics</c>).</summary>
/// <param name="Category"><c>FAST</c> (good), <c>AVERAGE</c> (needs improvement) or <c>SLOW</c> (poor).</param>
public sealed record FieldMetric(string Name, double Percentile, string Category);

/// <summary>A Lighthouse insight that did not pass, for "main opportunities".</summary>
public sealed record PsiOpportunity(string Id, string Title, string? DisplayValue);

/// <summary>
/// What SeoTester keeps of one PageSpeed Insights answer (<c>docs/seo_analyzer_PRD.md</c> §4.2, spike S5): the
/// performance score, the lab metrics, the insights that did not pass and the CrUX field data. Never the multi-MB JSON.
/// </summary>
public sealed record PsiSummary(
    string Url,
    string FormFactor,
    string LighthouseVersion,
    int? PerformanceScore,
    IReadOnlyDictionary<string, double> LabMetrics,
    IReadOnlyList<PsiOpportunity> Opportunities,
    IReadOnlyList<FieldMetric> Field,
    bool FieldIsOrigin);

/// <summary>How one PageSpeed Insights call ended.</summary>
public enum PsiOutcomeKind
{
    Measured,
    /// <summary>429: the project's quota is spent (the anonymous project's is 0, S5); retry later, never the site's fault.</summary>
    QuotaExhausted,
    /// <summary>Lighthouse could not test the page (the site's answer) or the API refused the request.</summary>
    Failed,
}

public sealed record PsiOutcome(PsiOutcomeKind Kind, PsiSummary? Summary, string? Reason)
{
    public static PsiOutcome Measured(PsiSummary summary) => new(PsiOutcomeKind.Measured, summary, null);
}

/// <summary>
/// Reads a PageSpeed Insights v5 <c>runPagespeed</c> answer (spike S5, real answers in the test fixtures: Lighthouse
/// 13.5.0). Untrusted input: a depth limit, every field optional, nothing thrown for a strange body.
/// </summary>
public static class PsiReport
{
    /// <summary>The lab metrics kept: Lighthouse 13's weighted five (TBT 30, LCP 25, CLS 25, FCP 10, SI 10).</summary>
    public static readonly string[] LabMetricIds =
        ["largest-contentful-paint", "cumulative-layout-shift", "total-blocking-time", "first-contentful-paint", "speed-index"];

    /// <summary>
    /// The response fields SeoTester reads (a partial response: ~30 % smaller, S5). A field mask cannot name map
    /// entries (<c>audits/*/score</c> is refused with 400), so the audits come whole.
    /// </summary>
    public const string FieldMask = "id,loadingExperience,originLoadingExperience,analysisUTCTimestamp,"
        + "lighthouseResult(lighthouseVersion,requestedUrl,finalUrl,fetchTime,configSettings/formFactor,categories/performance/score,audits)";

    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 64 };

    /// <summary>Parses an answer by its HTTP status and body.</summary>
    public static PsiOutcome Parse(int status, string? body, string requestedUrl)
    {
        if (status == 429)
            return new PsiOutcome(PsiOutcomeKind.QuotaExhausted, null, ErrorMessage(body) ?? "quota exhausted");
        if (status is < 200 or >= 300)
            return new PsiOutcome(PsiOutcomeKind.Failed, null, ErrorMessage(body) ?? $"PageSpeed Insights answered {status}");
        if (string.IsNullOrEmpty(body))
            return new PsiOutcome(PsiOutcomeKind.Failed, null, "empty answer");
        try
        {
            using var document = JsonDocument.Parse(body, Options);
            return Read(document.RootElement, requestedUrl) is { } summary
                ? PsiOutcome.Measured(summary)
                : new PsiOutcome(PsiOutcomeKind.Failed, null, "no Lighthouse result");
        }
        catch (JsonException)
        {
            return new PsiOutcome(PsiOutcomeKind.Failed, null, "not JSON");
        }
    }

    private static PsiSummary? Read(JsonElement root, string requestedUrl)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("lighthouseResult", out var lh) || lh.ValueKind != JsonValueKind.Object)
            return null;

        int? score = null;
        if (lh.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Object
            && categories.TryGetProperty("performance", out var performance) && performance.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number)
            score = (int)Math.Round(s.GetDouble() * 100, MidpointRounding.AwayFromZero);

        var lab = new Dictionary<string, double>(StringComparer.Ordinal);
        var opportunities = new List<(PsiOpportunity Opportunity, double Savings)>();
        if (lh.TryGetProperty("audits", out var audits) && audits.ValueKind == JsonValueKind.Object)
        {
            foreach (var audit in audits.EnumerateObject())
            {
                if (audit.Value.ValueKind != JsonValueKind.Object)
                    continue;
                if (LabMetricIds.Contains(audit.Name) && audit.Value.TryGetProperty("numericValue", out var value) && value.ValueKind == JsonValueKind.Number)
                    lab[audit.Name] = value.GetDouble();
                // Lighthouse 13's insights (weight 0, S5) are its opportunities; one that scores below 0.9 is shown.
                if (audit.Name.EndsWith("-insight", StringComparison.Ordinal)
                    && audit.Value.TryGetProperty("score", out var insightScore) && insightScore.ValueKind == JsonValueKind.Number && insightScore.GetDouble() < 0.9)
                    opportunities.Add((new PsiOpportunity(audit.Name, Text(audit.Value, "title") ?? audit.Name, Text(audit.Value, "displayValue")), Savings(audit.Value)));
            }
        }

        var (field, isOrigin) = FieldData(root);
        var formFactor = lh.TryGetProperty("configSettings", out var config) && config.ValueKind == JsonValueKind.Object ? Text(config, "formFactor") : null;
        return new PsiSummary(
            Text(lh, "finalUrl") ?? Text(root, "id") ?? requestedUrl,
            formFactor ?? "mobile",
            Text(lh, "lighthouseVersion") ?? "unknown",
            score,
            lab,
            opportunities.OrderByDescending(o => o.Savings).ThenBy(o => o.Opportunity.Id, StringComparer.Ordinal).Take(5).Select(o => o.Opportunity).ToList(),
            field,
            isOrigin);
    }

    /// <summary>The page's own CrUX data, else the origin's (a small page often has none of its own), else nothing.</summary>
    private static (IReadOnlyList<FieldMetric> Metrics, bool IsOrigin) FieldData(JsonElement root)
    {
        var page = Metrics(root, "loadingExperience");
        if (page.Count > 0)
            return (page, false);
        var origin = Metrics(root, "originLoadingExperience");
        return (origin, origin.Count > 0);
    }

    private static List<FieldMetric> Metrics(JsonElement root, string name)
    {
        var result = new List<FieldMetric>();
        if (!root.TryGetProperty(name, out var experience) || experience.ValueKind != JsonValueKind.Object
            || !experience.TryGetProperty("metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var metric in metrics.EnumerateObject())
        {
            if (metric.Value.ValueKind == JsonValueKind.Object
                && metric.Value.TryGetProperty("percentile", out var p) && p.ValueKind == JsonValueKind.Number
                && Text(metric.Value, "category") is { } category)
                result.Add(new FieldMetric(metric.Name, p.GetDouble(), category));
        }
        return result;
    }

    /// <summary>An insight's estimated LCP + FCP savings in ms, to order them by impact.</summary>
    private static double Savings(JsonElement audit)
    {
        if (!audit.TryGetProperty("metricSavings", out var savings) || savings.ValueKind != JsonValueKind.Object)
            return 0;
        double total = 0;
        foreach (var metric in savings.EnumerateObject())
            if (metric.Name is "LCP" or "FCP" && metric.Value.ValueKind == JsonValueKind.Number)
                total += metric.Value.GetDouble();
        return total;
    }

    private static string? ErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith('{'))
            return null;
        try
        {
            using var document = JsonDocument.Parse(body, Options);
            return document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                ? Text(error, "message") is { } message ? Cut(message, 200) : null
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Cut(string value, int max) => value.Length > max ? value[..max] : value;
}

/// <summary>
/// The two checks PageSpeed Insights measures (§3.1, D9): <c>performance.lab-score</c> (Lighthouse's performance score
/// on Google's hardware) and <c>performance.core-web-vitals</c> (CrUX: what real Chrome users saw over 28 days).
/// </summary>
public static class PsiChecks
{
    public const string LabScore = "performance.lab-score";
    public const string CoreWebVitals = "performance.core-web-vitals";

    public static IReadOnlyList<string> Ids { get; } = [CoreWebVitals, LabScore];

    /// <summary>The Core Web Vitals and their "good" limits (web.dev): LCP 2.5 s, INP 200 ms, CLS 0.1.</summary>
    private static readonly (string Metric, string Name, string Limit, Func<double, string> Show)[] Vitals =
    [
        ("LARGEST_CONTENTFUL_PAINT_MS", "Largest Contentful Paint", "2.5 s", p => (p / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s"),
        ("INTERACTION_TO_NEXT_PAINT", "Interaction to Next Paint", "200 ms", p => p.ToString("0", CultureInfo.InvariantCulture) + " ms"),
        // CrUX reports CLS × 100 as the percentile (S5: example.com's 0 is 0.00).
        ("CUMULATIVE_LAYOUT_SHIFT_SCORE", "Cumulative Layout Shift", "0.1", p => (p / 100).ToString("0.00", CultureInfo.InvariantCulture)),
    ];

    /// <summary>The page's findings: lab score 90+ passes, 50–89 warns, below 50 fails; field data by CrUX's own categories.</summary>
    public static IReadOnlyList<CheckFinding> Evaluate(PsiSummary summary)
    {
        var findings = new List<CheckFinding>();
        var url = summary.Url;
        if (summary.PerformanceScore is { } score)
        {
            var status = score >= 90 ? CheckStatus.Pass : score >= 50 ? CheckStatus.Warn : CheckStatus.Fail;
            findings.Add(Finding.Make(LabScore, status, [("url", url), ("n", score), ("value", string.Join(", ", summary.Opportunities.Take(3).Select(o => o.Title)))]));
        }
        else
        {
            findings.Add(NotMeasured(LabScore, "Lighthouse gave no performance score"));
        }

        var measured = Vitals.Select(v => (Vital: v, Metric: summary.Field.FirstOrDefault(f => f.Name == v.Metric))).Where(v => v.Metric is not null).ToList();
        if (measured.Count == 0)
        {
            findings.Add(NotMeasured(CoreWebVitals, "the Chrome UX Report has no field data for this page or its origin"));
            return findings;
        }
        var worst = measured.OrderByDescending(v => Rank(v.Metric!.Category)).First();
        var worstStatus = Rank(worst.Metric!.Category) switch { 2 => CheckStatus.Fail, 1 => CheckStatus.Warn, _ => CheckStatus.Pass };
        findings.Add(Finding.Make(CoreWebVitals, worstStatus,
            [("url", url), ("value", worst.Vital.Name), ("n", worst.Vital.Show(worst.Metric.Percentile)), ("limit", worst.Vital.Limit), ("origin", summary.FieldIsOrigin ? "true" : null)]));
        return findings;
    }

    /// <summary>Not applicable, with why (excluded from the score, §7; the report says "not measured").</summary>
    public static CheckFinding NotMeasured(string checkId, string reason)
        => Finding.Make(checkId, CheckStatus.NotApplicable, [("reason", reason)]);

    private static int Rank(string category) => category switch { "SLOW" => 2, "AVERAGE" => 1, _ => 0 };
}
