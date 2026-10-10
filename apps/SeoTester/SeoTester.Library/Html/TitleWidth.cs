namespace SeoTester.Html;

/// <summary>
/// The width of a search-result title without a browser (spike S9): Google sets desktop titles in Arial at 20 px and
/// cuts them at about 600 px. Arial is metric-compatible with Helvetica, so the advance widths are the Helvetica AFM
/// widths (units per 1000 em) of the standard 14 PDF fonts. A character outside the table counts as the average Latin
/// lower-case width (accents are stripped first, so é measures as e).
/// </summary>
public static class TitleWidth
{
    /// <summary>Google's desktop title font size.</summary>
    public const double FontSizePx = 20;

    /// <summary>About where Google cuts a desktop title.</summary>
    public const double TruncationPx = 600;

    private const int Fallback = 556;

    // ASCII 32..126, Helvetica/Arial advance widths in 1/1000 em.
    private static readonly int[] Ascii =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, // space ! " # $ % & ' ( ) * + , - . /
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556,                               // 0-9
        278, 278, 584, 584, 584, 556, 1015,                                             // : ; < = > ? @
        667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833,                // A-M
        722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611,                // N-Z
        278, 278, 278, 469, 556, 333,                                                   // [ \ ] ^ _ `
        556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833,                // a-m
        556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500,                // n-z
        334, 260, 334, 584,                                                             // { | } ~
    ];

    /// <summary>The width of <paramref name="text"/> in CSS pixels at <see cref="FontSizePx"/>.</summary>
    public static double Pixels(string text)
    {
        var units = 0L;
        foreach (var c in text.Normalize(System.Text.NormalizationForm.FormD))
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            units += c is >= ' ' and <= '~' ? Ascii[c - ' '] : Fallback;
        }
        return units * FontSizePx / 1000.0;
    }
}
