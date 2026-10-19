using MintPlayer.Spark.Abstractions;

namespace Fleet.LookupReferences;

/// <summary>
/// Where a car is. Stored as a string; the lookup <see cref="CarStatus"/> (with its <c>const string</c> keys) is
/// generated, and its labels are <c>lookup.ECarStatus.*</c> in translations.json.
/// </summary>
[SparkLookup(StringKeys = true)]
public enum ECarStatus
{
    /// <summary>Car is currently in use.</summary>
    InUse,
    /// <summary>Car is parked.</summary>
    OnParking,
    /// <summary>Car is being maintained.</summary>
    InMaintenance,
    /// <summary>Car has been stolen.</summary>
    Stolen,
}
