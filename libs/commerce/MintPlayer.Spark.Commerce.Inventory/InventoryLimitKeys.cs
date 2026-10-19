using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MintPlayer.Spark.Commerce.Inventory;

/// <summary>
/// The identifiers a buyer is known by (D16, D29). Every one that is present gets its own limit key, so a buyer who
/// signs out, clears cookies or uses another account still meets the same limit through the e-mail, address or card.
/// </summary>
/// <param name="AccountId">The signed-in user's id.</param>
/// <param name="VisitorId">The core visitor identity (X1), for guests.</param>
/// <param name="Email">The e-mail address, normalised by <see cref="InventoryLimitKeys.NormalizeEmail"/>.</param>
/// <param name="Address">The delivery or billing address as one line, normalised by <see cref="InventoryLimitKeys.NormalizeAddress"/>.</param>
/// <param name="CardFingerprint">The payment provider's card fingerprint; known only at payment, so it usually goes to <c>ConvertAsync</c>'s late limits.</param>
public sealed record BuyerIdentifiers(
    string? AccountId = null,
    string? VisitorId = null,
    string? Email = null,
    string? Address = null,
    string? CardFingerprint = null);

/// <summary>
/// Builds per-buyer limit keys (D16): <c>{scope}/{kind}/{hash}</c>, one per linked identifier. The scope is the sale
/// or the whole event. Identifiers are normalised, then hashed (SHA-256), so a compare-exchange key never holds an
/// e-mail address or a street in clear text.
/// </summary>
/// <remarks>
/// A hash of an e-mail address is pseudonymous, not anonymous: anyone who can list compare-exchange keys and guesses
/// an address can confirm it. Keep the database private as for any personal data (§4.15).
/// </remarks>
public static class InventoryLimitKeys
{
    public const string Account = "account";
    public const string Visitor = "visitor";
    public const string Email = "email";
    public const string Address = "address";
    public const string Card = "card";

    /// <summary>The key of one identifier.</summary>
    public static string Key(string scope, string kind, string normalizedValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentException.ThrowIfNullOrEmpty(kind);
        if (scope.Any(char.IsControl) || kind.Any(char.IsControl))
            throw new ArgumentException("A limit scope or kind holds no control characters.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(kind + ":" + normalizedValue)));
        return $"{scope}/{kind}/{hash}";
    }

    /// <summary>
    /// One <see cref="LimitLine"/> per identifier that is present: at most <paramref name="max"/> units in
    /// <paramref name="scope"/>, this claim taking <paramref name="n"/>.
    /// </summary>
    public static IReadOnlyList<LimitLine> For(string scope, int max, int n, BuyerIdentifiers buyer)
    {
        var lines = new List<LimitLine>(5);
        void Add(string kind, string? normalized)
        {
            if (!string.IsNullOrEmpty(normalized))
                lines.Add(new LimitLine(Key(scope, kind, normalized), max, n));
        }
        Add(Account, buyer.AccountId?.Trim());
        Add(Visitor, buyer.VisitorId?.Trim());
        Add(Email, buyer.Email is { } email ? NormalizeEmail(email) : null);
        Add(Address, buyer.Address is { } address ? NormalizeAddress(address) : null);
        Add(Card, buyer.CardFingerprint?.Trim());
        return lines;
    }

    /// <summary>
    /// Lower case, trimmed, without a <c>+tag</c> in the local part; Gmail's dots and its <c>googlemail.com</c> alias
    /// are folded too, since Gmail ignores them. Other providers' dots are significant and kept.
    /// </summary>
    public static string NormalizeEmail(string email)
    {
        var value = email.Trim().ToLowerInvariant();
        var at = value.LastIndexOf('@');
        if (at <= 0 || at == value.Length - 1)
            return value;
        var local = value[..at];
        var domain = value[(at + 1)..].TrimEnd('.');
        var plus = local.IndexOf('+');
        if (plus > 0)
            local = local[..plus];
        if (domain is "gmail.com" or "googlemail.com")
        {
            local = local.Replace(".", "", StringComparison.Ordinal);
            domain = "gmail.com";
        }
        return local + "@" + domain;
    }

    /// <summary>
    /// Upper case without diacritics; every run of characters that are not letters or digits becomes one space.
    /// "Rue de l'Église 5, 1000 Bruxelles" and "RUE DE L EGLISE 5 1000 BRUXELLES" give the same key.
    /// </summary>
    public static string NormalizeAddress(string address)
    {
        var decomposed = address.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0)
                    builder.Append(' ');
                pendingSpace = false;
                builder.Append(char.ToUpperInvariant(c));
            }
            else
            {
                pendingSpace = true;
            }
        }
        return builder.ToString();
    }
}
