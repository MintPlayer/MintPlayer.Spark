using MintPlayer.Spark.Abstractions;

namespace Fleet.Entities;

/// <summary>
/// One maintenance visit on a vehicle's service history — the sample for the server-side row
/// lifecycle (#386).
/// </summary>
/// <remarks>
/// It earns its place by being the shape that needs both halves of the feature. A new entry wants a
/// server-set date and an odometer reading carried down from the vehicle, which is
/// <c>OnNewAsync</c>; and an entry that has been invoiced must not simply vanish from the grid,
/// which is <c>OnDeleteRowAsync</c>.
/// <para>
/// <see cref="ValueObjectAttribute"/> gives it the generated row key that makes any of this
/// possible: without one, "which row is being removed" is not a question the server can answer, and
/// a save cannot tell an edit from a delete plus a create.
/// </para>
/// </remarks>
[ValueObject]
public partial class ServiceEntry
{
    /// <summary>What was done at this visit.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Date the work was carried out. Defaulted to today by <c>ServiceEntryActions.OnNewAsync</c>.</summary>
    public DateOnly PerformedOn { get; set; }

    /// <summary>Odometer reading at the visit, in kilometres.</summary>
    public int Odometer { get; set; }

    /// <summary>Amount charged, in euro.</summary>
    public decimal Cost { get; set; }

    /// <summary>
    /// Whether the visit has been billed. Once it has, the entry is an accounting record and
    /// <c>ServiceEntryActions.OnDeleteRowAsync</c> refuses to let it be removed.
    /// </summary>
    public bool IsInvoiced { get; set; }
}
