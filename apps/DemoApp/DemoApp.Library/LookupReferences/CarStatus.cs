using MintPlayer.Spark.Abstractions;

namespace DemoApp.Library.LookupReferences;

/// <summary>Where a car is. The lookup <see cref="CarStatus"/> is generated; its labels are <c>lookup.ECarStatus.*</c> in translations.json.</summary>
[SparkLookup]
public enum ECarStatus
{
    /// <summary>Car is in use.</summary>
    InUse,
    /// <summary>Car is parked.</summary>
    OnParking,
    /// <summary>Car is being maintained.</summary>
    InMaintenance,
    /// <summary>Car is stolen.</summary>
    Stolen
}

public sealed partial class CarStatus
{
    // Extra properties beyond Key/Values
    public bool AllowOnCarNotes { get; private set; }

    static partial void Initialize(CarStatus item)
        => item.AllowOnCarNotes = item.Key != ECarStatus.OnParking;
}
