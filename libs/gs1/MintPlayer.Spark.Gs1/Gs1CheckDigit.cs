using MintPlayer.Spark.Gs1.Core;

namespace MintPlayer.Spark.Gs1;

/// <summary>
/// GS1 mod-10 check digits (GTIN-8/12/13/14, SSCC, GLN). The logic lives once, in <c>pg/checkdigit.pg</c>, and is
/// transpiled to C# at build time and to TypeScript for ng-spark.
/// </summary>
public static class Gs1CheckDigit
{
    /// <summary>The check digit for <paramref name="digits"/>: the data digits of a GS1 key, without its check digit.</summary>
    /// <exception cref="ArgumentException"><paramref name="digits"/> is empty or holds anything but ASCII digits.</exception>
    public static int Compute(string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);
        var digit = Gs1Core.gs1CheckDigit(digits);
        return digit >= 0 ? digit : throw new ArgumentException("Expected one or more ASCII digits.", nameof(digits));
    }

    /// <summary>True when the last digit of <paramref name="code"/> is the GS1 check digit of the digits before it.</summary>
    public static bool IsValid(string? code) => code is not null && Gs1Core.gs1HasValidCheckDigit(code);
}
