using System.Globalization;
using System.Net;
using System.Text;
using SeoTester.Entities;

namespace SeoTester.Reports;

/// <summary>
/// A finished run's report as one self-contained HTML page (<c>docs/seo_analyzer_PRD.md</c> F14, D9): inline CSS
/// (print rules included), no script, no image, no external resource, so Chromium prints it without a single network
/// request. English or Dutch; the texts are the catalogue's current ones (D32), the statuses and scores the run's own.
/// Every value is encoded.
/// </summary>
public static class ReportPage
{
    public static IReadOnlyList<string> Cultures { get; } = ["en", "nl"];

    public static string CultureOf(string? culture) => culture is { } c && c.StartsWith("nl", StringComparison.OrdinalIgnoreCase) ? "nl" : "en";

    public static string Render(AuditRun run, IReadOnlyDictionary<string, CheckDefinition> checks, string culture, DateTime generatedAtUtc)
    {
        var nl = CultureOf(culture) == "nl";
        string T(string en, string nlText) => nl ? nlText : en;
        static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        string Title(string id) => checks.TryGetValue(id, out var d) ? (nl ? d.TitleNl : d.TitleEn) is { Length: > 0 } t ? t : id : id;
        string? Fix(string id) => checks.TryGetValue(id, out var d) ? nl ? d.HowToFixNl ?? d.HowToFixEn : d.HowToFixEn : null;
        string CategoryOf(string id) => checks.TryGetValue(id, out var d) ? d.Category : id.Split('.')[0];
        var format = CultureInfo.GetCultureInfo(nl ? "nl-BE" : "en-GB");

        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"").Append(nl ? "nl" : "en").Append("\"><head><meta charset=\"utf-8\"><title>")
            .Append(E(T("SEO report", "SEO-rapport") + " – " + run.Origin)).Append("</title><style>")
            .Append("""
                @page { size: A4; margin: 16mm 14mm; }
                body { font: 10.5pt/1.45 Arial, Helvetica, sans-serif; color: #1b1f24; margin: 0; }
                h1 { font-size: 20pt; margin: 0 0 2mm; } h2 { font-size: 13pt; margin: 7mm 0 2mm; border-bottom: 1px solid #ccd; padding-bottom: 1mm; }
                h3 { font-size: 11pt; margin: 4mm 0 1mm; break-after: avoid; }
                .meta { color: #555; } .score { font-size: 28pt; font-weight: bold; }
                .grade { display: inline-block; min-width: 9mm; text-align: center; border-radius: 2mm; color: #fff; font-weight: bold; padding: 0 2mm; }
                .A, .B { background: #1a7f37; } .C, .D { background: #9a6700; } .F { background: #cf222e; } .none { background: #6e7781; }
                table { border-collapse: collapse; width: 100%; } td, th { text-align: left; padding: 1mm 2mm; border-bottom: 1px solid #e4e6ea; vertical-align: top; }
                .issue { break-inside: avoid; margin-bottom: 3mm; } .fail { color: #cf222e; font-weight: bold; } .warn { color: #9a6700; font-weight: bold; }
                .urls { font-family: Consolas, monospace; font-size: 8.5pt; color: #444; word-break: break-all; } .fix { color: #333; }
                """)
            .Append("</style></head><body>");

        html.Append("<h1>").Append(E(T("SEO report", "SEO-rapport"))).Append("</h1>");
        html.Append("<p class=\"meta\">").Append(E(run.Origin)).Append(" · ")
            .Append(E(T("audited", "geaudit"))).Append(' ').Append(E((run.CompletedAtUtc ?? run.StartedAtUtc)?.ToString("d MMMM yyyy HH:mm 'UTC'", format)))
            .Append(" · ").Append(E(T($"{run.PagesDone} pages", $"{run.PagesDone} pagina's"))).Append("</p>");
        html.Append("<p><span class=\"score\">").Append(run.Score?.ToString(CultureInfo.InvariantCulture) ?? "–").Append("</span> ")
            .Append(Grade(run.Grade)).Append("</p>");

        html.Append("<h2>").Append(E(T("Categories", "Categorieën"))).Append("</h2><table>");
        foreach (var category in run.CategoryScores)
            html.Append("<tr><td>").Append(E(category.Category)).Append("</td><td>")
                .Append(category.Score is { } s ? s.ToString(CultureInfo.InvariantCulture) : E(T("not measured", "niet gemeten")))
                .Append("</td><td>").Append(Grade(category.Grade)).Append(category.Capped ? " " + E(T("(capped by a critical issue)", "(begrensd door een kritiek probleem)")) : "")
                .Append("</td></tr>");
        html.Append("</table>");

        var issues = run.CheckResults.Where(r => r.Status is "Fail" or "Warn")
            .OrderBy(r => r.Status == "Fail" ? 0 : 1).ThenByDescending(r => checks.TryGetValue(r.CheckId, out var d) ? d.Weight : 0).ThenBy(r => r.CheckId, StringComparer.Ordinal)
            .ToList();
        html.Append("<h2>").Append(E(T("Issues to fix", "Op te lossen"))).Append(" (").Append(issues.Count).Append(")</h2>");
        foreach (var group in issues.GroupBy(r => CategoryOf(r.CheckId)))
        {
            html.Append("<h3>").Append(E(group.Key)).Append("</h3>");
            foreach (var issue in group)
            {
                html.Append("<div class=\"issue\"><span class=\"").Append(issue.Status == "Fail" ? "fail" : "warn").Append("\">")
                    .Append(E(issue.Status == "Fail" ? T("Fail", "Faalt") : T("Warning", "Waarschuwing"))).Append("</span> ")
                    .Append(E(Title(issue.CheckId)));
                if (issue.Affected > 0)
                    html.Append(" – ").Append(E(T($"{issue.Affected} affected", $"{issue.Affected} getroffen")));
                if (Fix(issue.CheckId) is { Length: > 0 } fix)
                    html.Append("<div class=\"fix\">").Append(E(fix)).Append("</div>");
                if (issue.SampleUrls.Count > 0)
                    html.Append("<div class=\"urls\">").Append(string.Join("<br>", issue.SampleUrls.Select(E))).Append("</div>");
                html.Append("</div>");
            }
        }

        var notMeasured = run.CheckResults.Where(r => r.Status is "NotApplicable" or "Error" && r.Note is { Length: > 0 }).ToList();
        if (notMeasured.Count > 0)
        {
            html.Append("<h2>").Append(E(T("Not measured", "Niet gemeten"))).Append("</h2><table>");
            foreach (var item in notMeasured)
                html.Append("<tr><td>").Append(E(Title(item.CheckId))).Append("</td><td>").Append(E(item.Note)).Append("</td></tr>");
            html.Append("</table>");
        }

        var passed = run.CheckResults.Count(r => r.Status == "Pass");
        html.Append("<p class=\"meta\">").Append(E(T($"{passed} checks passed.", $"{passed} controles geslaagd."))).Append(' ')
            .Append(E(T("Generated by SeoTester on ", "Gemaakt door SeoTester op "))).Append(E(generatedAtUtc.ToString("d MMMM yyyy HH:mm 'UTC'", format)))
            .Append(" · ").Append(E(T("scoring version ", "scoreversie "))).Append(E(run.ScoringVersion)).Append("</p>");
        html.Append("</body></html>");
        return html.ToString();

        static string Grade(string? grade) => grade is { Length: > 0 } g
            ? $"<span class=\"grade {WebUtility.HtmlEncode(g)}\">{WebUtility.HtmlEncode(g)}</span>"
            : "<span class=\"grade none\">–</span>";
    }
}
