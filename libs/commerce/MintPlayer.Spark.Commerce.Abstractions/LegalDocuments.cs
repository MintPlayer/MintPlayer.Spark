namespace MintPlayer.Spark.Commerce;

/// <summary>
/// The kinds of versioned legal document (GA2, §4.20). Stored on every <c>LegalDocument</c> and on every acceptance,
/// so the values are permanent. A host may add its own kinds (any string).
/// </summary>
public static class LegalDocumentKinds
{
    /// <summary>Terms of sale (general terms and conditions of the seller).</summary>
    public const string TermsOfSale = "terms-of-sale";

    /// <summary>Privacy policy (GDPR Art. 13).</summary>
    public const string PrivacyPolicy = "privacy-policy";

    /// <summary>Withdrawal information and the model withdrawal form (CRD Art. 6(1)(h), Annex I(A) and I(B)).</summary>
    public const string WithdrawalInformation = "withdrawal-information";

    /// <summary>Platform terms for sellers (P2B Reg. (EU) 2019/1150 Art. 3): ranking parameters, suspension grounds,
    /// complaint handling.</summary>
    public const string PlatformTerms = "platform-terms";

    /// <summary>The data-processing agreement sellers accept (GDPR Art. 28(3), GB14).</summary>
    public const string DataProcessingAgreement = "dpa";

    /// <summary>The accessibility statement (Directive (EU) 2019/882 Annex V, GA10).</summary>
    public const string AccessibilityStatement = "accessibility-statement";

    /// <summary>The cookie policy (ePrivacy Directive 2002/58/EC Art. 5(3)).</summary>
    public const string CookiePolicy = "cookie-policy";

    /// <summary>The built-in kinds, in display order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [TermsOfSale, PrivacyPolicy, WithdrawalInformation, PlatformTerms, DataProcessingAgreement, AccessibilityStatement, CookiePolicy];
}

/// <summary>A published version of a legal document, as an order or an account stores it (GA2).</summary>
/// <param name="Id">The <c>LegalDocument</c> document id (one per version).</param>
/// <param name="Kind">A <see cref="LegalDocumentKinds"/> value.</param>
/// <param name="OrganizationId">The seller that owns it; null for the platform.</param>
/// <param name="Version">The version number, from 1.</param>
/// <param name="EffectiveFrom">When it took effect.</param>
public sealed record LegalDocumentRef(string Id, string Kind, string? OrganizationId, int Version, DateTimeOffset EffectiveFrom);

/// <summary>
/// The legal documents in force (GA2, §4.20). Checkout stores <see cref="GetInForceAsync"/>'s answer on the order and
/// attaches the documents to the confirmation mail (CRD Art. 8(7), durable medium).
/// </summary>
public interface ISparkLegalDocuments
{
    /// <summary>The latest published version of each kind in force at <paramref name="at"/> for a seller (its own
    /// documents first, then the platform's of the same kind). Null <paramref name="organizationId"/> asks the
    /// platform's.</summary>
    Task<IReadOnlyList<LegalDocumentRef>> GetInForceAsync(string? organizationId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>The kinds of <paramref name="required"/> that have no published version in force for the seller now.</summary>
    Task<IReadOnlyList<string>> GetMissingAsync(string? organizationId, IEnumerable<string> required, CancellationToken cancellationToken = default);

    /// <summary>Records that <paramref name="subjectId"/> (a user or an organization) accepted
    /// <paramref name="document"/> (account-level acceptance: platform terms, DPA). Saved in the caller's session.</summary>
    Task RecordAcceptanceAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string subjectId, LegalDocumentRef document, string source, CancellationToken cancellationToken = default);
}
