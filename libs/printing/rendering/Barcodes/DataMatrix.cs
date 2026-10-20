namespace MintPlayer.Spark.Printing.Rendering.Barcodes;

/// <summary>
/// Data Matrix ECC 200 (ISO/IEC 16022), square symbols 10×10 to 48×48 (one Reed–Solomon block, up to 174 data codewords),
/// ASCII encodation with digit pairs, and GS1 DataMatrix (FNC1 first and as the separator). Pure: codewords and modules.
/// </summary>
/// <remarks>
/// The algorithms are the standard's: ASCII encodation (§5.2.3), the pad codeword's 253-state randomisation (§5.2.3),
/// Reed–Solomon over GF(2⁸) with the field polynomial x⁸+x⁵+x³+x²+1 (301) and generator roots 2¹…2ᵏ (Annex E), and the
/// "utah" module placement with its four corner cases (Annex F). Larger symbols and the other encodations (C40, Text,
/// Base 256) are not built: labels carry short codes.
/// </remarks>
public static class DataMatrix
{
    /// <summary>The FNC1 codeword (GS1: first, and as the separator after a variable-length element).</summary>
    public const int Fnc1 = 232;

    private const int Pad = 129;

    /// <summary>(symbol size, data region size, regions per side, data codewords, error codewords).</summary>
    internal static readonly (int Size, int Region, int Regions, int Data, int Ecc)[] Sizes =
    [
        (10, 8, 1, 3, 5), (12, 10, 1, 5, 7), (14, 12, 1, 8, 10), (16, 14, 1, 12, 12), (18, 16, 1, 18, 14),
        (20, 18, 1, 22, 18), (22, 20, 1, 30, 20), (24, 22, 1, 36, 24), (26, 24, 1, 44, 28),
        (32, 14, 2, 62, 36), (36, 16, 2, 86, 42), (40, 18, 2, 114, 48), (44, 20, 2, 144, 56), (48, 22, 2, 174, 68),
    ];

    /// <summary>The ASCII-encoded data codewords of <paramref name="text"/> (no padding).</summary>
    public static List<int> EncodeAscii(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EncodeAscii(text.Select(c => (int)c).ToList());
    }

    /// <summary>GS1 DataMatrix data codewords from element strings, FNC1 first and after each variable-length element but the last.</summary>
    public static List<int> EncodeGs1(IReadOnlyList<KeyValuePair<string, string>> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        if (elements.Count == 0)
            throw new ArgumentException("GS1 DataMatrix needs at least one element string.", nameof(elements));
        var tokens = new List<int> { -1 };
        for (var i = 0; i < elements.Count; i++)
        {
            tokens.AddRange((elements[i].Key + elements[i].Value).Select(c => (int)c));
            if (i < elements.Count - 1 && !Gs1FixedLength.IsPredefined(elements[i].Key))
                tokens.Add(-1);
        }
        return EncodeAscii(tokens);
    }

    /// <summary>ASCII encodation of characters (0–255) and FNC1 (-1).</summary>
    internal static List<int> EncodeAscii(IReadOnlyList<int> tokens)
    {
        var codewords = new List<int>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t == -1)
                codewords.Add(Fnc1);
            else if (t is >= '0' and <= '9' && i + 1 < tokens.Count && tokens[i + 1] is >= '0' and <= '9')
            {
                codewords.Add(130 + (t - '0') * 10 + (tokens[i + 1] - '0'));
                i++;
            }
            else if (t is >= 0 and <= 127)
                codewords.Add(t + 1);
            else if (t is >= 128 and <= 255)
            {
                codewords.Add(235);
                codewords.Add(t - 128 + 1);
            }
            else
                throw new ArgumentException($"Data Matrix ASCII encodation takes bytes 0–255; U+{t:X4} is not one.", nameof(tokens));
        }
        return codewords;
    }

    /// <summary>The smallest square symbol that holds <paramref name="dataCodewords"/>, padded, with its error codewords appended.</summary>
    public static (int Size, int[] Codewords) Complete(IReadOnlyList<int> dataCodewords)
    {
        var size = Sizes.FirstOrDefault(s => s.Data >= dataCodewords.Count);
        if (size.Size == 0)
            throw new ArgumentException($"{dataCodewords.Count} codewords do not fit a 48×48 symbol (174).", nameof(dataCodewords));
        var data = dataCodewords.ToList();
        if (data.Count < size.Data)
            data.Add(Pad);
        while (data.Count < size.Data)
        {
            var position = data.Count + 1;
            var pseudo = (149 * position) % 253 + 1;
            var value = Pad + pseudo;
            data.Add(value <= 254 ? value : value - 254);
        }
        return (size.Size, [.. data, .. ReedSolomon(data, size.Ecc)]);
    }

    /// <summary>The error codewords of <paramref name="data"/>: the remainder of data(x)·xᵏ by ∏(x + 2ⁱ), i = 1…k.</summary>
    public static int[] ReedSolomon(IReadOnlyList<int> data, int eccCount)
    {
        var generator = new int[] { 1 };
        for (var i = 1; i <= eccCount; i++)
        {
            var next = new int[generator.Length + 1];
            var root = Gf.Exp[i];
            for (var j = 0; j < generator.Length; j++)
            {
                next[j] ^= generator[j];
                next[j + 1] ^= Gf.Multiply(generator[j], root);
            }
            generator = next;
        }

        var remainder = new int[eccCount];
        foreach (var d in data)
        {
            var factor = d ^ remainder[0];
            Array.Copy(remainder, 1, remainder, 0, eccCount - 1);
            remainder[eccCount - 1] = 0;
            for (var j = 0; j < eccCount; j++)
                remainder[j] ^= Gf.Multiply(generator[j + 1], factor);
        }
        return remainder;
    }

    /// <summary>The symbol's modules (true = dark), finder and clock patterns included, quiet zone not.</summary>
    public static bool[,] Modules(string text) => Modules(EncodeAscii(text));

    /// <inheritdoc cref="Modules(string)"/>
    public static bool[,] Modules(IReadOnlyList<int> dataCodewords)
    {
        var (size, codewords) = Complete(dataCodewords);
        var spec = Sizes.First(s => s.Size == size);
        var n = spec.Region * spec.Regions;
        var placement = Place(n, n);

        var symbol = new bool[size, size];
        for (var ry = 0; ry < spec.Regions; ry++)
        for (var rx = 0; rx < spec.Regions; rx++)
        {
            var top = ry * (spec.Region + 2);
            var left = rx * (spec.Region + 2);
            var last = spec.Region + 1;
            for (var k = 0; k <= last; k++)
            {
                symbol[top + k, left] = true;                       // the solid left edge
                symbol[top + last, left + k] = true;                // the solid bottom edge
                symbol[top, left + k] = k % 2 == 0;                 // the alternating top edge
                symbol[top + k, left + last] = k % 2 == 1;          // the alternating right edge
            }
        }

        for (var row = 0; row < n; row++)
        for (var col = 0; col < n; col++)
        {
            var value = placement[row * n + col];
            bool dark;
            if (value == Corner)
                dark = true;
            else if (value == 0)
                dark = false;   // the two light modules of the unfilled corner's fixed pattern
            else
            {
                var chr = value / 10;
                var bit = value % 10;
                dark = (codewords[chr - 1] & (1 << (8 - bit))) != 0;
            }
            var symbolRow = row / spec.Region * (spec.Region + 2) + 1 + row % spec.Region;
            var symbolCol = col / spec.Region * (spec.Region + 2) + 1 + col % spec.Region;
            symbol[symbolRow, symbolCol] = dark;
        }
        return symbol;
    }

    private const int Corner = 1;

    /// <summary>Annex F: which codeword (×10) and bit (1 = most significant) each module of the mapping matrix carries.</summary>
    internal static int[] Place(int nrow, int ncol)
    {
        var array = new int[nrow * ncol];

        void Module(int row, int col, int chr, int bit)
        {
            if (row < 0)
            {
                row += nrow;
                col += 4 - (nrow + 4) % 8;
            }
            if (col < 0)
            {
                col += ncol;
                row += 4 - (ncol + 4) % 8;
            }
            array[row * ncol + col] = 10 * chr + bit;
        }

        void Utah(int row, int col, int chr)
        {
            Module(row - 2, col - 2, chr, 1);
            Module(row - 2, col - 1, chr, 2);
            Module(row - 1, col - 2, chr, 3);
            Module(row - 1, col - 1, chr, 4);
            Module(row - 1, col, chr, 5);
            Module(row, col - 2, chr, 6);
            Module(row, col - 1, chr, 7);
            Module(row, col, chr, 8);
        }

        void Corner1(int chr)
        {
            Module(nrow - 1, 0, chr, 1); Module(nrow - 1, 1, chr, 2); Module(nrow - 1, 2, chr, 3);
            Module(0, ncol - 2, chr, 4); Module(0, ncol - 1, chr, 5);
            Module(1, ncol - 1, chr, 6); Module(2, ncol - 1, chr, 7); Module(3, ncol - 1, chr, 8);
        }

        void Corner2(int chr)
        {
            Module(nrow - 3, 0, chr, 1); Module(nrow - 2, 0, chr, 2); Module(nrow - 1, 0, chr, 3);
            Module(0, ncol - 4, chr, 4); Module(0, ncol - 3, chr, 5); Module(0, ncol - 2, chr, 6); Module(0, ncol - 1, chr, 7);
            Module(1, ncol - 1, chr, 8);
        }

        void Corner3(int chr)
        {
            Module(nrow - 3, 0, chr, 1); Module(nrow - 2, 0, chr, 2); Module(nrow - 1, 0, chr, 3);
            Module(0, ncol - 2, chr, 4); Module(0, ncol - 1, chr, 5);
            Module(1, ncol - 1, chr, 6); Module(2, ncol - 1, chr, 7); Module(3, ncol - 1, chr, 8);
        }

        void Corner4(int chr)
        {
            Module(nrow - 1, 0, chr, 1); Module(nrow - 1, ncol - 1, chr, 2);
            Module(0, ncol - 3, chr, 3); Module(0, ncol - 2, chr, 4); Module(0, ncol - 1, chr, 5);
            Module(1, ncol - 3, chr, 6); Module(1, ncol - 2, chr, 7); Module(1, ncol - 1, chr, 8);
        }

        var c = 1;
        var r = 4;
        var k = 0;
        do
        {
            if (r == nrow && k == 0) Corner1(c++);
            if (r == nrow - 2 && k == 0 && ncol % 4 != 0) Corner2(c++);
            if (r == nrow - 2 && k == 0 && ncol % 8 == 4) Corner3(c++);
            if (r == nrow + 4 && k == 2 && ncol % 8 == 0) Corner4(c++);
            do
            {
                if (r < nrow && k >= 0 && array[r * ncol + k] == 0) Utah(r, k, c++);
                r -= 2;
                k += 2;
            } while (r >= 0 && k < ncol);
            r += 1;
            k += 3;
            do
            {
                if (r >= 0 && k < ncol && array[r * ncol + k] == 0) Utah(r, k, c++);
                r += 2;
                k -= 2;
            } while (r < nrow && k >= 0);
            r += 3;
            k += 1;
        } while (r < nrow || k < ncol);

        if (array[nrow * ncol - 1] == 0)
            array[nrow * ncol - 1] = array[nrow * ncol - ncol - 2] = Corner;
        return array;
    }

    /// <summary>GF(2⁸) with the Data Matrix field polynomial 301.</summary>
    internal static class Gf
    {
        public static readonly int[] Exp = new int[512];
        public static readonly int[] Log = new int[256];

        static Gf()
        {
            var x = 1;
            for (var i = 0; i < 255; i++)
            {
                Exp[i] = x;
                Log[x] = i;
                x <<= 1;
                if (x >= 256)
                    x ^= 301;
            }
            for (var i = 255; i < 512; i++)
                Exp[i] = Exp[i - 255];
        }

        public static int Multiply(int a, int b) => a == 0 || b == 0 ? 0 : Exp[Log[a] + Log[b]];
    }
}
