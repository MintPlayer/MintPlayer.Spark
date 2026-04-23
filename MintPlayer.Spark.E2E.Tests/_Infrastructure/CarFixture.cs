using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// Shared factory for the Fleet <c>Car</c> PersistentObject payloads used by the
/// <c>Security/*Tests</c>. Collapses ~4 hand-built object initializers with magic
/// strings into one constructor call, and pulls the Guid from the source-generated
/// <see cref="PersistentObjectIds"/> constants (fed by
/// <c>Demo/Fleet/Fleet/App_Data/Model/Car.json</c> via <c>&lt;AdditionalFiles&gt;</c>
/// in the csproj), so schema drift in the Fleet model file flows through to the
/// tests at compile time rather than failing mysteriously on the wire.
/// </summary>
internal static class CarFixture
{
    public const string TypeName = "Car";

    /// <summary>Canonical <c>ObjectTypeId</c> for Fleet's Car. Resolved at compile time.</summary>
    public static readonly Guid TypeId = Guid.Parse(PersistentObjectIds.Default.Car);

    /// <summary>Attribute names exposed on Car. Keep in sync with Car.json.</summary>
    public static class AttributeNames
    {
        public const string LicensePlate = "LicensePlate";
        public const string Model = "Model";
        public const string Year = "Year";
    }

    /// <summary>
    /// Builds a fresh Car PO suitable for <c>CreatePersistentObjectAsync</c>. Callers
    /// supply the license plate (usually a random one to avoid collisions across runs);
    /// <paramref name="model"/> and <paramref name="year"/> take reasonable defaults
    /// since most Security tests don't care about those values beyond "valid Car".
    /// </summary>
    public static PersistentObject New(string licensePlate, string model = "M1", int year = 2024)
        => new()
        {
            Name = TypeName,
            ObjectTypeId = TypeId,
            Attributes =
            [
                new PersistentObjectAttribute { Name = AttributeNames.LicensePlate, Value = licensePlate },
                new PersistentObjectAttribute { Name = AttributeNames.Model,        Value = model },
                new PersistentObjectAttribute { Name = AttributeNames.Year,         Value = year },
            ],
        };

    /// <summary>
    /// Generates a unique 8-char uppercase license plate. Useful for create flows where
    /// every test run needs a plate that won't collide with an existing document.
    /// </summary>
    public static string RandomLicensePlate(string prefix = "RO")
        => $"{prefix}{Guid.NewGuid():N}"[..8].ToUpperInvariant();
}
