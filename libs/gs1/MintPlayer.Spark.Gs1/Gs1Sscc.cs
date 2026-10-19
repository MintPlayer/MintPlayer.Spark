using System.Globalization;
using MintPlayer.Spark.Gs1.Core;

namespace MintPlayer.Spark.Gs1;

/// <summary>Serial Shipping Container Codes (SSCC, AI 00).</summary>
public static class Gs1Sscc
{
    /// <summary>
    /// The 18-digit SSCC for a serial reference: the extension digit, the GS1 Company Prefix, the serial reference
    /// left-padded with zeros, and the check digit.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The extension digit is not 0–9, the company prefix is not 7–10 digits, or the serial reference is negative or
    /// does not fit in the 16 − prefix-length digits left.
    /// </exception>
    public static string Create(int extensionDigit, string companyPrefix, long serialReference)
    {
        ArgumentNullException.ThrowIfNull(companyPrefix);
        ArgumentOutOfRangeException.ThrowIfNegative(serialReference);
        var sscc = Gs1Core.gs1Sscc(
            extensionDigit.ToString(CultureInfo.InvariantCulture),
            companyPrefix,
            serialReference.ToString(CultureInfo.InvariantCulture));
        return sscc.Length == 18
            ? sscc
            : throw new ArgumentException("The extension digit, company prefix or serial reference is out of range for an SSCC.");
    }
}
