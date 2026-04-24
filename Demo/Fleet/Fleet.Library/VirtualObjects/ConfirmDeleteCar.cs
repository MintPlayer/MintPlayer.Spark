namespace Fleet.VirtualObjects;

/// <summary>
/// Marker class for the <c>ConfirmDeleteCar</c> Virtual PO. Spark's
/// <c>EntityTypeDefinition.ClrType</c> is required, so every schema registration needs a
/// CLR type to resolve against — this class exists only to satisfy that shape. No
/// persistence; instances are scaffolded via <c>manager.NewPersistentObject</c> and live
/// for the duration of one retry-action round-trip in <c>CarActions.OnBeforeDeleteAsync</c>.
/// </summary>
public sealed class ConfirmDeleteCar
{
    /// <summary>Copied from the car being deleted, rendered read-only in the modal.</summary>
    public string? LicensePlate { get; set; }

    /// <summary>User-typed plate text — must match <see cref="LicensePlate"/> to proceed.</summary>
    public string? Confirmation { get; set; }
}
