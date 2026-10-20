namespace MintPlayer.Spark.Printing.Rendering.Barcodes;

/// <summary>EAN-13 (ISO/IEC 15420; GS1 General Specifications §5.2): 95 modules, the first digit set by the left half's parity.</summary>
public static class Ean13
{
    private static readonly string[] L = ["0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011"];
    private static readonly string[] Parity = ["LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL"];

    /// <summary>The check digit of 12 digits (weights 1 and 3 from the left).</summary>
    public static int CheckDigit(string twelveDigits)
    {
        if (twelveDigits is not { Length: 12 } || !twelveDigits.All(char.IsAsciiDigit))
            throw new ArgumentException("EAN-13 data is 12 digits.", nameof(twelveDigits));
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (twelveDigits[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10;
    }

    /// <summary>The 13 digits: 12 get their check digit; 13 are checked.</summary>
    /// <exception cref="ArgumentException">Not 12 or 13 digits, or a wrong check digit.</exception>
    public static string Complete(string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);
        digits = digits.Trim();
        if (digits.Length == 12)
            return digits + CheckDigit(digits);
        if (digits.Length == 13 && digits.All(char.IsAsciiDigit) && CheckDigit(digits[..12]) == digits[12] - '0')
            return digits;
        throw new ArgumentException($"'{digits}' is not an EAN-13 (12 digits, or 13 with a valid check digit).", nameof(digits));
    }

    /// <summary>The 95 modules (true = bar), quiet zones not included.</summary>
    public static bool[] Modules(string digits)
    {
        var code = Complete(digits);
        var parity = Parity[code[0] - '0'];
        var bits = new System.Text.StringBuilder("101");
        for (var i = 1; i <= 6; i++)
        {
            var l = L[code[i] - '0'];
            bits.Append(parity[i - 1] == 'L' ? l : G(l));
        }
        bits.Append("01010");
        for (var i = 7; i <= 12; i++)
            bits.Append(R(L[code[i] - '0']));
        bits.Append("101");
        return bits.ToString().Select(b => b == '1').ToArray();
    }

    private static string R(string l) => new(l.Select(b => b == '1' ? '0' : '1').ToArray());

    private static string G(string l) => new(R(l).Reverse().ToArray());
}
