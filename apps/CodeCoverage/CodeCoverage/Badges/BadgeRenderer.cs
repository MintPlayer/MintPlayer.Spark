using System.Globalization;

namespace CodeCoverage.Badges;

/// <summary>
/// Renders a shields.io-style flat SVG badge. Text width is approximated
/// (Verdana ≈ 6.6px/char at font-size 11) — good enough for badge text.
/// </summary>
public static class BadgeRenderer
{
    public static string Coverage(double? percent)
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
        return Render("coverage", value, color);
    }

    private static string Render(string label, string value, string color)
    {
        var labelWidth = TextWidth(label);
        var valueWidth = TextWidth(value);
        var totalWidth = labelWidth + valueWidth;

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
