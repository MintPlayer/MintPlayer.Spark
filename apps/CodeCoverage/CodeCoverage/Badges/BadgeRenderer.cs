using System.Globalization;
using System.Security;

namespace CodeCoverage.Badges;

/// <summary>
/// Renders a shields.io-style flat SVG badge. Text width is approximated
/// (Verdana ≈ 6.6px/char at font-size 11) — good enough for badge text.
/// <para>
/// The label comes from a CLOSED SET of the constants below and is never
/// caller-supplied. That is the control, not the escaping in
/// <see cref="Render"/>: no branch name, PR number or query value reaches the
/// SVG, so there is nothing to inject. The escaping is defence in depth for a
/// future caller who forgets.
/// </para>
/// </summary>
public static class BadgeRenderer
{
    private const string LabelCoverage = "coverage";

    /// <summary>
    /// Used when the number comes from a commit whose assembly is Partial — a
    /// subset's total. Saying so is the whole point: the repository-level badge
    /// is promoted only from a Complete assembly, so an unlabelled partial
    /// number would silently disagree with it for the same branch.
    /// </summary>
    private const string LabelCoveragePartial = "coverage (partial)";

    /// <param name="partial">
    /// Render the partial label. Set when the resolved commit's assembly is
    /// Partial, never on the repository-level badge.
    /// </param>
    public static string Coverage(double? percent, bool partial = false)
    {
        var value = percent is null ? "unknown" : percent.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        var color = percent switch
        {
            null => "#9f9f9f",
            >= 90 => "#4c1",
            >= 80 => "#97ca00",
            >= 70 => "#a4a61d",
            >= 60 => "#dfb317",
            >= 50 => "#fe7d37",
            _ => "#e05d44",
        };
        // "unknown" is never partial — there is no number to qualify.
        var label = partial && percent is not null ? LabelCoveragePartial : LabelCoverage;
        return Render(label, value, color);
    }

    private static string Render(string label, string value, string color)
    {
        var labelWidth = TextWidth(label);
        var valueWidth = TextWidth(value);
        var totalWidth = labelWidth + valueWidth;

        // After the widths: they must measure the VISIBLE text, not the escaped
        // form, or an escaped character would widen the badge it renders inside.
        label = SecurityElement.Escape(label);
        value = SecurityElement.Escape(value);

        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{totalWidth}" height="20" role="img" aria-label="{label}: {value}">
              <title>{label}: {value}</title>
              <linearGradient id="s" x2="0" y2="100%"><stop offset="0" stop-color="#bbb" stop-opacity=".1"/><stop offset="1" stop-opacity=".1"/></linearGradient>
              <clipPath id="r"><rect width="{totalWidth}" height="20" rx="3" fill="#fff"/></clipPath>
              <g clip-path="url(#r)">
                <rect width="{labelWidth}" height="20" fill="#555"/>
                <rect x="{labelWidth}" width="{valueWidth}" height="20" fill="{color}"/>
                <rect width="{totalWidth}" height="20" fill="url(#s)"/>
              </g>
              <g fill="#fff" text-anchor="middle" font-family="Verdana,Geneva,DejaVu Sans,sans-serif" font-size="11">
                <text x="{labelWidth / 2.0}" y="15" fill="#010101" fill-opacity=".3">{label}</text>
                <text x="{labelWidth / 2.0}" y="14">{label}</text>
                <text x="{labelWidth + valueWidth / 2.0}" y="15" fill="#010101" fill-opacity=".3">{value}</text>
                <text x="{labelWidth + valueWidth / 2.0}" y="14">{value}</text>
              </g>
            </svg>
            """;
    }

    private static int TextWidth(string text) => (int)Math.Ceiling(text.Length * 6.6) + 10;
}
