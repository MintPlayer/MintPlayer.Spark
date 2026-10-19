namespace MintPlayer.Spark.Commerce.Models;

/// <summary>Where a seller's registration stands (D21).</summary>
public enum SellerRegistrationStatus
{
    /// <summary>Data is being filled in; not submitted.</summary>
    Draft,

    /// <summary>Submitted; a platform approver decides (ApprovalRequired) or the trader check is pending (Open).</summary>
    Pending,

    /// <summary>Approved: the seller may sell once the other gates pass (KYC, legal documents).</summary>
    Approved,

    /// <summary>Rejected, with a reason.</summary>
    Rejected,

    /// <summary>Suspended by the platform; a statement of reasons was issued (DSA Art. 17, P2B Art. 4).</summary>
    Suspended,
}

/// <summary>
/// The seller profile of one organization (D21, §4.19): a satellite document of the Organizations organization, id
/// <c>SellerProfiles/{organizationId}</c>. A plain RavenDB document served by <c>/spark/commerce/sellers/*</c>, never by the
/// generic persistent-object pipeline: it holds trader-traceability and DAC7 data that only the seller's admins and
/// the platform may read, and an identity document (attachment <see cref="IdentityDocumentAttachment"/>) that only
/// holders of <c>ViewTraderData/Commerce</c> may download, every download audited.
/// </summary>
public sealed class SellerProfile
{
    /// <summary>The name of the identity-document attachment.</summary>
    public const string IdentityDocumentAttachment = "identity-document";

    /// <summary>The profile id for <paramref name="organizationId"/>.</summary>
    public static string IdFor(string organizationId) => "SellerProfiles/" + organizationId;

    /// <summary>The document id.</summary>
    public string? Id { get; set; }

    /// <summary>The seller organization.</summary>
    public string OrganizationId { get; set; } = "";

    /// <summary>The registration status.</summary>
    public SellerRegistrationStatus Status { get; set; } = SellerRegistrationStatus.Draft;

    /// <summary>The pending registration request (Approvals), if any.</summary>
    public string? RegistrationRequestId { get; set; }

    /// <summary>Why the registration was rejected or the seller suspended.</summary>
    public string? StatusReason { get; set; }

    /// <summary>When the profile was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it was submitted.</summary>
    public DateTimeOffset? SubmittedAt { get; set; }

    /// <summary>When it was approved.</summary>
    public DateTimeOffset? ApprovedAt { get; set; }

    /// <summary>Trader or not (CRD Art. 6a(1)(b)), shown on every listing.</summary>
    public TraderStatus TraderStatus { get; set; } = TraderStatus.Undeclared;

    /// <summary>The trader data (DSA Art. 30(1)).</summary>
    public TraderData Trader { get; set; } = new();

    /// <summary>The DAC7 data (Directive (EU) 2021/514, Annex V).</summary>
    public Dac7Data Dac7 { get; set; } = new();

    /// <summary>The best-effort verification of the trader data (DSA Art. 30(2)).</summary>
    public TraderVerification? Verification { get; set; }

    /// <summary>The cached KYC state reported by the provider (D21); refreshed on read.</summary>
    public KycStatus Kyc { get; set; } = KycStatus.NotStarted;

    /// <summary>Whether payouts are blocked for missing DAC7 data (GA6).</summary>
    public bool PayoutsBlocked { get; set; }

    /// <summary>The DPA version accepted at registration (GB14).</summary>
    public string? AcceptedDpaId { get; set; }

    /// <summary>The platform-terms version accepted (P2B Art. 3).</summary>
    public string? AcceptedPlatformTermsId { get; set; }

    /// <summary>When the relationship with the platform ended (the organization was deleted or the seller left). Trader
    /// data is deleted 6 months later (DSA Art. 30(3)).</summary>
    public DateTimeOffset? RelationshipEndedAt { get; set; }

    /// <summary>When the trader data was erased by the retention sweep.</summary>
    public DateTimeOffset? TraderDataErasedAt { get; set; }
}

/// <summary>The trader-traceability data of DSA Art. 30(1)(a)–(e).</summary>
public sealed class TraderData
{
    /// <summary>The trader's (legal) name. (a)</summary>
    public string? Name { get; set; }

    /// <summary>The geographic address. (a)</summary>
    public string? Address { get; set; }

    /// <summary>The ISO 3166-1 alpha-2 country of the address.</summary>
    public string? Country { get; set; }

    /// <summary>The telephone number. (a)</summary>
    public string? Phone { get; set; }

    /// <summary>The e-mail address. (a)</summary>
    public string? Email { get; set; }

    /// <summary>Whether a copy of an identification document was uploaded (the attachment). (b)</summary>
    public bool HasIdentityDocument { get; set; }

    /// <summary>The payment account details (IBAN or the provider's account id). (c)</summary>
    public string? PaymentAccount { get; set; }

    /// <summary>The trade register and the registration number, where the trader is registered. (d)</summary>
    public string? TradeRegister { get; set; }

    /// <summary>The registration number in <see cref="TradeRegister"/>. (d)</summary>
    public string? RegistrationNumber { get; set; }

    /// <summary>The VAT identification number (also checked against VIES when an adapter exists).</summary>
    public string? VatId { get; set; }

    /// <summary>The self-certification that the trader only offers products that comply with Union law. (e)</summary>
    public bool SelfCertification { get; set; }

    /// <summary>When the self-certification was given.</summary>
    public DateTimeOffset? SelfCertifiedAt { get; set; }

    /// <summary>The missing mandatory fields (translation keys) for a trader.</summary>
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) missing.Add("commerce.trader.name");
        if (string.IsNullOrWhiteSpace(Address)) missing.Add("commerce.trader.address");
        if (string.IsNullOrWhiteSpace(Phone)) missing.Add("commerce.trader.phone");
        if (string.IsNullOrWhiteSpace(Email)) missing.Add("commerce.trader.email");
        if (!HasIdentityDocument) missing.Add("commerce.trader.identityDocument");
        if (string.IsNullOrWhiteSpace(PaymentAccount)) missing.Add("commerce.trader.paymentAccount");
        if (!SelfCertification) missing.Add("commerce.trader.selfCertification");
        return missing;
    }
}

/// <summary>DAC7 seller data (Directive (EU) 2021/514, Annex V, Section II; confirm per member state, D37).</summary>
public sealed class Dac7Data
{
    /// <summary>Whether the seller is an entity (true) or an individual.</summary>
    public bool IsEntity { get; set; }

    /// <summary>The tax identification number.</summary>
    public string? Tin { get; set; }

    /// <summary>The ISO country that issued <see cref="Tin"/>.</summary>
    public string? TinCountry { get; set; }

    /// <summary>The VAT identification number, if any.</summary>
    public string? VatId { get; set; }

    /// <summary>The date of birth (individuals only).</summary>
    public DateOnly? BirthDate { get; set; }

    /// <summary>The primary address.</summary>
    public string? PrimaryAddress { get; set; }

    /// <summary>The business registration number (entities).</summary>
    public string? BusinessRegistrationNumber { get; set; }

    /// <summary>The financial account identifier (IBAN) the consideration is paid to.</summary>
    public string? Iban { get; set; }

    /// <summary>The name of the holder of <see cref="Iban"/>.</summary>
    public string? AccountHolder { get; set; }

    /// <summary>When the data was first requested (the reminder clock starts here).</summary>
    public DateTimeOffset? RequestedAt { get; set; }

    /// <summary>The reminders sent so far.</summary>
    public int RemindersSent { get; set; }

    /// <summary>When the last reminder was sent.</summary>
    public DateTimeOffset? LastReminderAt { get; set; }

    /// <summary>The missing mandatory fields (translation keys).</summary>
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Tin)) missing.Add("commerce.dac7.tin");
        if (string.IsNullOrWhiteSpace(TinCountry)) missing.Add("commerce.dac7.tinCountry");
        if (string.IsNullOrWhiteSpace(PrimaryAddress)) missing.Add("commerce.dac7.primaryAddress");
        if (string.IsNullOrWhiteSpace(Iban)) missing.Add("commerce.dac7.iban");
        if (string.IsNullOrWhiteSpace(AccountHolder)) missing.Add("commerce.dac7.accountHolder");
        if (IsEntity && string.IsNullOrWhiteSpace(BusinessRegistrationNumber)) missing.Add("commerce.dac7.businessRegistrationNumber");
        if (!IsEntity && BirthDate is null) missing.Add("commerce.dac7.birthDate");
        return missing;
    }
}

/// <summary>A best-effort verification of the trader data (DSA Art. 30(2)).</summary>
public sealed class TraderVerification
{
    /// <summary>How: <c>registry</c> (an adapter), <c>vies</c>, or <c>manual</c> (through Approvals).</summary>
    public string Method { get; set; } = "manual";

    /// <summary>Whether it passed.</summary>
    public bool Passed { get; set; }

    /// <summary>Who decided (manual) or the adapter name.</summary>
    public string? By { get; set; }

    /// <summary>When.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>Notes, such as the reason of a failure.</summary>
    public string? Notes { get; set; }

    /// <summary>The Approvals request that recorded a manual check.</summary>
    public string? RequestId { get; set; }
}
