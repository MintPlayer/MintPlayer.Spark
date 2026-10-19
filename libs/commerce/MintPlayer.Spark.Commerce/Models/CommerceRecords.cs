using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Models;

/// <summary>
/// One entry of the price history (GA3). A plain RavenDB document, never a persistent object: it is written by
/// <see cref="ISparkPriceHistory"/> and never edited. Id <c>CommercePriceChanges/{itemId}/{ticks}</c>.
/// </summary>
public sealed class CommercePriceChange
{
    /// <summary>The document id.</summary>
    public string? Id { get; set; }

    /// <summary>The sellable item.</summary>
    public string ItemId { get; set; } = "";

    /// <summary>The seller.</summary>
    public string? OrganizationId { get; set; }

    /// <summary>The item's channel.</summary>
    public string Channel { get; set; } = "";

    /// <summary>When the price took effect.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>The final unit price before; null for the first.</summary>
    public Money? Before { get; set; }

    /// <summary>The final unit price after.</summary>
    public Money After { get; set; } = null!;

    /// <summary>The stored unit price after, before fees.</summary>
    public Money UnitPrice { get; set; } = null!;

    /// <summary>Why it changed.</summary>
    public string Reason { get; set; } = "";

    /// <summary>For an announced reduction: the Omnibus prior price.</summary>
    public Money? PriorPrice { get; set; }

    /// <summary>Whether the storefront announces this change as a reduction.</summary>
    public bool IsAnnouncedReduction { get; set; }

    /// <summary>The user that made the change.</summary>
    public string? ChangedBy { get; set; }

    internal PriceHistoryEntry ToEntry() => new(ItemId, At, Before, After, UnitPrice, Reason, PriorPrice, ChangedBy);
}

/// <summary>
/// One money audit entry (default 8, §4.15). Plain document, append-only, id <c>CommerceAudit/{subject}/{ticks}-{n}</c>.
/// </summary>
public sealed class CommerceAuditRecord
{
    /// <summary>The document id.</summary>
    public string? Id { get; set; }

    /// <summary>What happened.</summary>
    public string Action { get; set; } = "";

    /// <summary>The document the change is about.</summary>
    public string SubjectId { get; set; } = "";

    /// <summary>The seller involved.</summary>
    public string? OrganizationId { get; set; }

    /// <summary>The amount before.</summary>
    public Money? Before { get; set; }

    /// <summary>The amount after or moved.</summary>
    public Money? After { get; set; }

    /// <summary>The reason.</summary>
    public string? Reason { get; set; }

    /// <summary>The acting user; null for system jobs.</summary>
    public string? ActorId { get; set; }

    /// <summary>When.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>Extra facts.</summary>
    public Dictionary<string, string>? Data { get; set; }
}
