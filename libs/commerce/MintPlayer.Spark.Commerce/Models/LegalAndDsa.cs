using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Models;

/// <summary>
/// One version of a legal document (GA2, §4.20). Publishing a new version creates a new document; a published one is
/// never edited (the service refuses it), so an order's stored acceptance always points at the text the buyer saw.
/// Id <c>LegalDocuments/{guid}</c>; the optional PDF rendering is the attachment <see cref="PdfAttachment"/> (Q-O9).
/// </summary>
public sealed class LegalDocument
{
    /// <summary>The PDF attachment's name.</summary>
    public const string PdfAttachment = "document.pdf";

    /// <summary>The document id.</summary>
    public string? Id { get; set; }

    /// <summary>A <see cref="LegalDocumentKinds"/> value.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The owning seller; null for the platform.</summary>
    public string? OrganizationId { get; set; }

    /// <summary>The version number, from 1, per (kind, owner).</summary>
    public int Version { get; set; }

    /// <summary>The title, per language (X17).</summary>
    public TranslatedString? Title { get; set; }

    /// <summary>The text, per language (X17). Markdown.</summary>
    public TranslatedString? Body { get; set; }

    /// <summary>When it takes effect. May lie in the future (notice periods, P2B Art. 3(2): at least 15 days for
    /// platform terms changes).</summary>
    public DateTimeOffset EffectiveFrom { get; set; }

    /// <summary>Null while a draft; set once when published, after which the document is frozen.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>Who published it.</summary>
    public string? PublishedBy { get; set; }

    /// <summary>Whether a PDF rendering is attached.</summary>
    public bool HasPdf { get; set; }

    internal LegalDocumentRef ToRef() => new(Id!, Kind, OrganizationId, Version, EffectiveFrom);
}

/// <summary>
/// That a subject (a user or an organization) accepted a legal-document version: account-level acceptance (platform
/// terms, DPA). Order-level acceptance is stored on the order by Checkout. Id <c>LegalAcceptances/{subject}/{document}</c>.
/// </summary>
public sealed class LegalAcceptance
{
    /// <summary>The document id.</summary>
    public string? Id { get; set; }

    /// <summary>The user or organization id.</summary>
    public string SubjectId { get; set; } = "";

    /// <summary>The accepted document version.</summary>
    public string DocumentId { get; set; } = "";

    /// <summary>Its kind.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Its version.</summary>
    public int Version { get; set; }

    /// <summary>The owner of the document; null for the platform.</summary>
    public string? DocumentOrganizationId { get; set; }

    /// <summary>When.</summary>
    public DateTimeOffset AcceptedAt { get; set; }

    /// <summary>Where (<c>seller-registration</c>, <c>checkout</c>, <c>settings</c>).</summary>
    public string Source { get; set; } = "";

    /// <summary>The user who clicked accept.</summary>
    public string? AcceptedBy { get; set; }
}

/// <summary>The status of a DSA notice (Art. 16).</summary>
public enum DsaNoticeStatus
{
    /// <summary>Received, not decided.</summary>
    Open,

    /// <summary>Decided: a restriction was imposed; a statement of reasons went to the seller (Art. 17).</summary>
    ActionTaken,

    /// <summary>Decided: no action.</summary>
    NoAction,
}

/// <summary>
/// A notice of allegedly illegal content (DSA Art. 16) about a listing, an event or a review. Anyone may file one,
/// signed in or as a visitor (X1). Id <c>DsaNotices/{guid}</c>. When the reporter is signed in and the target is
/// moderatable, the notice is also flagged in Moderation so reviewers see it in their queue.
/// </summary>
public sealed class DsaNotice
{
    /// <summary>The document id.</summary>
    public string? Id { get; set; }

    /// <summary>The kind of target (<c>listing</c>, <c>event</c>, <c>review</c>).</summary>
    public string TargetKind { get; set; } = "";

    /// <summary>The target document id.</summary>
    public string TargetId { get; set; } = "";

    /// <summary>The seller responsible for the target, when known.</summary>
    public string? OrganizationId { get; set; }

    /// <summary>The reason category (<see cref="DsaReasons"/>).</summary>
    public string Reason { get; set; } = "";

    /// <summary>A sufficiently substantiated explanation of why the content is illegal (Art. 16(2)(a)).</summary>
    public string Explanation { get; set; } = "";

    /// <summary>The exact electronic location (URL) (Art. 16(2)(b)).</summary>
    public string? Location { get; set; }

    /// <summary>The reporter's name, unless an Art. 16(2)(c) exception applies (optional).</summary>
    public string? ContactName { get; set; }

    /// <summary>The reporter's e-mail, for the confirmation and the decision (Art. 16(4)–(5)) (optional).</summary>
    public string? ContactEmail { get; set; }

    /// <summary>The good-faith statement (Art. 16(2)(d)). Required.</summary>
    public bool GoodFaith { get; set; }

    /// <summary>The reporter's identity key (<c>user:…</c>, <c>visitor:…</c>), for rate limits and their own list.</summary>
    public string ReporterKey { get; set; } = "";

    /// <summary>The signed-in reporter, if any.</summary>
    public string? ReporterUserId { get; set; }

    /// <summary>When it was received.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>The status.</summary>
    public DsaNoticeStatus Status { get; set; } = DsaNoticeStatus.Open;

    /// <summary>When it was decided.</summary>
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>Who decided.</summary>
    public string? DecidedBy { get; set; }

    /// <summary>Whether the decision used automated means (Art. 16(6), 17(3)(c)).</summary>
    public bool AutomatedDecision { get; set; }

    /// <summary>The statement of reasons issued, if any.</summary>
    public string? StatementId { get; set; }
}

/// <summary>Reason categories of a notice (aligned with the DSA Transparency Database's statement categories).</summary>
public static class DsaReasons
{
    /// <summary>Illegal or harmful speech.</summary>
    public const string IllegalSpeech = "illegal-speech";
    /// <summary>Consumer information infringements.</summary>
    public const string ConsumerInformation = "consumer-information";
    /// <summary>Intellectual property infringements.</summary>
    public const string IntellectualProperty = "intellectual-property";
    /// <summary>Unsafe or non-compliant products.</summary>
    public const string UnsafeProducts = "unsafe-products";
    /// <summary>Scams and fraud.</summary>
    public const string ScamsAndFraud = "scams-and-fraud";
    /// <summary>Data protection and privacy violations.</summary>
    public const string DataProtection = "data-protection";
    /// <summary>Protection of minors.</summary>
    public const string ProtectionOfMinors = "protection-of-minors";
    /// <summary>Violation of the platform's terms.</summary>
    public const string TermsViolation = "terms-violation";
    /// <summary>Anything else.</summary>
    public const string Other = "other";

    /// <summary>All categories.</summary>
    public static IReadOnlyList<string> All { get; } =
        [IllegalSpeech, ConsumerInformation, IntellectualProperty, UnsafeProducts, ScamsAndFraud, DataProtection, ProtectionOfMinors, TermsViolation, Other];
}

/// <summary>The restriction a decision imposes (DSA Art. 17(1)).</summary>
public enum DsaRestriction
{
    /// <summary>The content is removed.</summary>
    Removal,
    /// <summary>Access to the content is disabled.</summary>
    DisableAccess,
    /// <summary>The content is demoted in rankings.</summary>
    Demotion,
    /// <summary>Sales are suspended (the seller's listings).</summary>
    SuspendSales,
    /// <summary>The seller's account is suspended or terminated.</summary>
    SuspendAccount,
    /// <summary>Monetary payments are suspended or restricted.</summary>
    RestrictPayments,
}

/// <summary>Whether a decision rests on law or on the platform's terms (Art. 17(3)(d)–(e)).</summary>
public enum DsaGround
{
    /// <summary>The content is illegal: the legal ground is cited.</summary>
    Illegal,
    /// <summary>The content breaks the terms: the contractual ground is cited.</summary>
    Contractual,
}

/// <summary>
/// A statement of reasons (DSA Art. 17(3)): sent to the affected seller with every restriction. Never edited.
/// Id <c>DsaStatements/{guid}</c>. Counted in the transparency export (Art. 15, 24).
/// </summary>
public sealed class DsaStatementOfReasons
{
    /// <summary>The document id.</summary>
    public string? Id { get; set; }

    /// <summary>The notice it answers; null for an own-initiative decision.</summary>
    public string? NoticeId { get; set; }

    /// <summary>The affected seller.</summary>
    public string? OrganizationId { get; set; }

    /// <summary>The target kind.</summary>
    public string TargetKind { get; set; } = "";

    /// <summary>The target id.</summary>
    public string TargetId { get; set; } = "";

    /// <summary>The restriction and its territorial scope and duration (Art. 17(3)(a)).</summary>
    public DsaRestriction Restriction { get; set; }

    /// <summary>The end of a temporary restriction; null for an indefinite one.</summary>
    public DateTimeOffset? RestrictionEndsAt { get; set; }

    /// <summary>The territorial scope (ISO countries); empty for everywhere.</summary>
    public List<string> Territories { get; set; } = [];

    /// <summary>The facts and circumstances relied on (Art. 17(3)(b)).</summary>
    public string Facts { get; set; } = "";

    /// <summary>Whether a notice triggered it (Art. 17(3)(b)).</summary>
    public bool BasedOnNotice { get; set; }

    /// <summary>Whether automated means detected or decided it (Art. 17(3)(c)).</summary>
    public bool Automated { get; set; }

    /// <summary>Law or terms (Art. 17(3)(d)–(e)).</summary>
    public DsaGround Ground { get; set; }

    /// <summary>The legal provision or the terms clause relied on, with an explanation.</summary>
    public string GroundExplanation { get; set; } = "";

    /// <summary>The redress available (Art. 17(3)(f)): internal complaint (Art. 20), out-of-court settlement (Art.
    /// 21) and judicial redress. Fixed text keys, always all three.</summary>
    public List<string> Redress { get; set; } = ["commerce.dsa.redress.internalComplaint", "commerce.dsa.redress.outOfCourt", "commerce.dsa.redress.judicial"];

    /// <summary>When it was issued.</summary>
    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>Who issued it.</summary>
    public string? IssuedBy { get; set; }
}
