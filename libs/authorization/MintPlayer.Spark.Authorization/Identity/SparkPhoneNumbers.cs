using System.Text;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// E.164 normalization for phone verification (<c>docs/commerce_PRD.md</c> X13): <c>+</c>, the country calling
/// code and the subscriber number, digits only, 15 digits at most (ITU-T E.164 §6).
/// </summary>
/// <remarks>
/// <para>
/// Accepted input: <c>+32 470 12 34 56</c>, <c>0032470123456</c>, and, when a default country calling code is
/// configured, a national number with a leading trunk <c>0</c> (<c>0470/12.34.56</c>). Spaces, hyphens, dots,
/// slashes and parentheses are dropped; any other character refuses the number.
/// </para>
/// <para>
/// ⚠️ This is a syntax check, not a numbering-plan check: it does not know which ranges a country assigns, and
/// the trunk-<c>0</c> rule is wrong for the few countries that keep the <c>0</c> internationally (Italy's fixed
/// lines). Whether a number is real is proven by the code arriving, which is the point of verifying it.
/// </para>
/// </remarks>
public static class SparkPhoneNumbers
{
    /// <summary>The fewest digits accepted after the <c>+</c>. Shorter numbers are service or emergency numbers.</summary>
    internal const int MinDigits = 7;

    /// <summary>E.164's maximum: country calling code and subscriber number together.</summary>
    internal const int MaxDigits = 15;

    /// <summary>
    /// <paramref name="input"/> in E.164 form, read in <paramref name="defaultCountryCallingCode"/> when it is
    /// national; false when it is not a phone number.
    /// </summary>
    public static bool TryNormalize(string? input, string? defaultCountryCallingCode, out string e164)
    {
        e164 = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var digits = new StringBuilder(input.Length);
        var plus = false;
        foreach (var c in input.Trim())
        {
            if (c is ' ' or '-' or '.' or '/' or '(' or ')' or ' ')
                continue;
            if (c == '+' && digits.Length == 0 && !plus)
            {
                plus = true;
                continue;
            }
            if (c is < '0' or > '9')
                return false;
            digits.Append(c);
        }

        var number = digits.ToString();
        if (plus)
        {
            // "+" then digits: international already.
        }
        else if (number.StartsWith("00", StringComparison.Ordinal))
        {
            number = number[2..];
        }
        else if (number.StartsWith('0') && IsCountryCallingCode(defaultCountryCallingCode))
        {
            number = defaultCountryCallingCode + number[1..];
        }
        else
        {
            // A national number without a trunk 0, or no default country: which country is a guess, and a
            // wrong guess sends the code to a stranger.
            return false;
        }

        if (number.Length is < MinDigits or > MaxDigits || number[0] == '0')
            return false;

        e164 = "+" + number;
        return true;
    }

    /// <summary>Whether <paramref name="e164"/> starts with one of <paramref name="countryCallingCodes"/> (every country when empty).</summary>
    /// <remarks>Country calling codes are prefix-free (ITU-T E.164 Annex), so a prefix match is exact.</remarks>
    public static bool IsInCountries(string e164, IReadOnlyCollection<string> countryCallingCodes)
        => countryCallingCodes.Count == 0
            || countryCallingCodes.Any(cc => IsCountryCallingCode(cc) && e164.StartsWith("+" + cc, StringComparison.Ordinal));

    /// <summary>One to three digits, not starting with 0.</summary>
    internal static bool IsCountryCallingCode(string? code)
        => code is { Length: >= 1 and <= 3 } && code[0] != '0' && code.All(char.IsAsciiDigit);
}
