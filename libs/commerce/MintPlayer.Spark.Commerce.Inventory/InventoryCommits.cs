using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Inventory;

/// <summary>The operation an <see cref="InventoryCommit"/> came from.</summary>
public static class InventoryOperations
{
    public const string Define = "define";
    public const string Claim = "claim";
    public const string Adjust = "adjust";
    public const string Convert = "convert";
    public const string Release = "release";
    public const string Return = "return";
    public const string Restock = "restock";
    public const string Exchange = "exchange";
    public const string Sweep = "sweep";
}

/// <summary>
/// What one committed engine transaction changed: the quotas whose availability moved and the numbered units (seats)
/// whose state moved (free, held, sold). Raised once per commit, after it, never for a refused or conflicting attempt.
/// </summary>
/// <param name="Operation">One of <see cref="InventoryOperations"/>.</param>
/// <param name="ClaimIds">The claims the transaction wrote (two for an exchange); empty for definitions and sweeps.</param>
/// <param name="Quotas">Quotas whose availability changed.</param>
/// <param name="Units">Units whose state changed: taken, given back, or turned from held into sold.</param>
public sealed record InventoryCommit(string Operation, IReadOnlyList<string> ClaimIds, IReadOnlyCollection<string> Quotas, IReadOnlyCollection<string> Units);

/// <summary>
/// Told after every engine commit what it changed (<see cref="InventoryCommit"/>): Ticketing.Venues pushes the seat
/// states of a snapshot whose units moved, a host may invalidate its own caches. Discovered
/// (<c>[SparkDiscoverable]</c>) in an application; a library registers its own (scoped).
/// </summary>
/// <remarks>
/// Called inline, after the commit and before the operation returns to its caller, on the instance that committed:
/// keep it fast (mark something dirty, schedule work) and never block on I/O you can defer. An exception is logged and
/// swallowed: the commit stands. A crash right after the commit loses the call, so a listener that must not miss a
/// change also reconciles on its own (the inventory topics do the same, X2).
/// </remarks>
[SparkDiscoverable]
public interface IInventoryCommitted
{
    Task OnCommittedAsync(InventoryCommit commit, CancellationToken cancellationToken);
}

/// <summary>
/// An exchange (seat change, upgrade, swap of a ticket type) of a converted claim, in ONE cluster-wide transaction:
/// <see cref="Return"/> gives part of <see cref="ClaimId"/> back, <see cref="Target"/> claims the replacement and the
/// replacement is converted (sold) at once. Either all three happen or nothing does (I2), so a buyer never ends with
/// both the old and the new seats, or with neither. Idempotent by the return id and the target's claim id.
/// </summary>
/// <param name="ClaimId">The converted claim the units come from.</param>
/// <param name="Return">What goes back (reason usually <see cref="ReturnReason.Other"/>; the order line id as return id).</param>
/// <param name="Target">The replacement claim; its <c>ExpiresAt</c> is ignored (it is sold in the same transaction). A
/// seat it names explicitly (units or picks) that <paramref name="Return"/> gives back is handed over as it is: a ticket
/// type upgrade keeps its seat. Seats given back are not offered to the target's chain fill (best available) in the
/// same exchange; they become holes once the target is seated.</param>
/// <param name="LateLimits">Limit keys counted at conversion only (as <see cref="ISparkInventory.ConvertAsync"/>).</param>
public sealed record InventoryExchange(string ClaimId, InventoryReturn Return, ClaimRequest Target, IReadOnlyList<LimitLine>? LateLimits = null);
