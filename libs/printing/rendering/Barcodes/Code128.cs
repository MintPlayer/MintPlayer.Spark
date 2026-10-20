namespace MintPlayer.Spark.Printing.Rendering.Barcodes;

/// <summary>
/// Code 128 and GS1-128 (ISO/IEC 15417; GS1 General Specifications §5.4): subsets B and C, chosen per run of digits, with
/// FNC1 for GS1. Pure: the symbol values (start, data, check, stop) and the module pattern. ZPL prints the same symbol
/// values through invocation codes (<c>ZplCanvas</c>), so a label shows the same bars on both outputs.
/// </summary>
public static class Code128
{
    public const int StartA = 103;
    public const int StartB = 104;
    public const int StartC = 105;
    public const int CodeC = 99;
    public const int CodeB = 100;
    public const int Fnc1 = 102;
    public const int Stop = 106;

    /// <summary>
    /// Bar and space widths (in modules) of every symbol value, ISO/IEC 15417 Table 1. Each sums to 11; the stop (106) has
    /// a final 2-module bar and sums to 13.
    /// </summary>
    internal static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    ];

    /// <summary>A FNC1 in the input of <see cref="Encode(IReadOnlyList{int})"/>.</summary>
    public const int Fnc1Token = -1;

    /// <summary>The symbol values of <paramref name="text"/> (printable ASCII), check and stop included.</summary>
    public static IReadOnlyList<int> Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Encode(text.Select(c => (int)c).ToList());
    }

    /// <summary>
    /// GS1-128 from element strings (AI, value) in order: FNC1 first, then each element, with a FNC1 separator after every
    /// variable-length element that is not the last (GS1 General Specifications §5.4.7.5). Check digits are the caller's
    /// (MintPlayer.Spark.Gs1).
    /// </summary>
    public static IReadOnlyList<int> EncodeGs1(IReadOnlyList<KeyValuePair<string, string>> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        if (elements.Count == 0)
            throw new ArgumentException("GS1-128 needs at least one element string.", nameof(elements));
        var tokens = new List<int> { Fnc1Token };
        for (var i = 0; i < elements.Count; i++)
        {
            var (ai, value) = (elements[i].Key, elements[i].Value);
            if (ai.Length is < 2 or > 4 || !ai.All(char.IsAsciiDigit))
                throw new ArgumentException($"'{ai}' is not an application identifier.", nameof(elements));
            tokens.AddRange((ai + value).Select(c => (int)c));
            if (i < elements.Count - 1 && !Gs1FixedLength.IsPredefined(ai))
                tokens.Add(Fnc1Token);
        }
        return Encode(tokens);
    }

    /// <summary>The symbol values of <paramref name="tokens"/> (characters, or <see cref="Fnc1Token"/>), check and stop included.</summary>
    public static IReadOnlyList<int> Encode(IReadOnlyList<int> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Count == 0)
            throw new ArgumentException("Nothing to encode.", nameof(tokens));
        foreach (var t in tokens)
            if (t != Fnc1Token && t is < 32 or > 127)
                throw new ArgumentException($"Code 128 here encodes printable ASCII (32–127) and FNC1; U+{t:X4} is not.", nameof(tokens));

        var values = new List<int>();
        var i = 0;
        var firstChar = tokens[0] == Fnc1Token ? 1 : 0;
        var leadingRun = DigitRun(tokens, firstChar);
        var remaining = tokens.Count - firstChar;
        var c = leadingRun >= 4 && leadingRun % 2 == 0 || leadingRun == 2 && remaining == 2;
        values.Add(c ? StartC : StartB);

        while (i < tokens.Count)
        {
            var token = tokens[i];
            if (token == Fnc1Token)
            {
                values.Add(Fnc1);
                i++;
                continue;
            }
            if (c)
            {
                if (DigitRun(tokens, i) >= 2)
                {
                    values.Add((tokens[i] - '0') * 10 + (tokens[i + 1] - '0'));
                    i += 2;
                    continue;
                }
                values.Add(CodeB);
                c = false;
                continue;
            }
            var run = DigitRun(tokens, i);
            if (run >= 4)
            {
                if (run % 2 == 1)
                {
                    // An odd run: one digit in B first, then the rest as pairs.
                    values.Add(token - 32);
                    i++;
                }
                values.Add(CodeC);
                c = true;
                continue;
            }
            values.Add(token - 32);
            i++;
        }

        values.Add(Checksum(values));
        values.Add(Stop);
        return values;
    }

    /// <summary>The modulo-103 check value over a start code and data values (the check and stop not included).</summary>
    public static int Checksum(IReadOnlyList<int> startAndData)
    {
        var sum = startAndData[0];
        for (var i = 1; i < startAndData.Count; i++)
            sum += startAndData[i] * i;
        return sum % 103;
    }

    /// <summary>The modules (true = bar), quiet zones not included.</summary>
    public static bool[] Modules(IReadOnlyList<int> values)
    {
        var modules = new List<bool>();
        foreach (var value in values)
        {
            var pattern = Patterns[value];
            for (var p = 0; p < pattern.Length; p++)
                modules.AddRange(Enumerable.Repeat(p % 2 == 0, pattern[p] - '0'));
        }
        return modules.ToArray();
    }

    private static int DigitRun(IReadOnlyList<int> tokens, int from)
    {
        var n = 0;
        while (from + n < tokens.Count && tokens[from + n] is >= '0' and <= '9')
            n++;
        return n;
    }
}

/// <summary>
/// The AIs whose length is predefined, so no FNC1 follows them in a GS1-128 or GS1 DataMatrix symbol (GS1 General
/// Specifications Figure 5.10.1-2: the two-digit prefixes 00–04, 11–20 (except 10), 31–36, 41).
/// </summary>
public static class Gs1FixedLength
{
    private static readonly HashSet<string> Prefixes =
    [
        "00", "01", "02", "03", "04", "11", "12", "13", "14", "15", "16", "17", "18", "19", "20",
        "31", "32", "33", "34", "35", "36", "41",
    ];

    public static bool IsPredefined(string ai) => ai.Length >= 2 && Prefixes.Contains(ai[..2]);
}
