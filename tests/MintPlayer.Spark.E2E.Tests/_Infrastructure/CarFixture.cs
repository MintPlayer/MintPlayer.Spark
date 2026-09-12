using System.Globalization;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// Shared factory for the Fleet <c>Car</c> PersistentObject payloads used by the
/// <c>Security/*Tests</c>. Collapses ~4 hand-built object initializers with magic
/// strings into one constructor call, and pulls the Guid from the source-generated
/// <see cref="PersistentObjectIds"/> constants (fed by
/// <c>apps/Fleet/Fleet/App_Data/Model/Car.json</c> via <c>&lt;AdditionalFiles&gt;</c>
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
        public const string RegisteredAt = "RegisteredAt";
    }

    /// <summary>
    /// The <c>RegisteredAt</c> every Car is seeded with unless a test overrides it.
    /// </summary>
    /// <remarks>
    /// <c>RegisteredAt</c> is <b>required</b> on Car, so a fixture that omits it produces a 400 and
    /// every Car-creating test fails on setup rather than on what it meant to assert.
    /// <para>
    /// The offset is deliberately not UTC and not the build agent's: a fixture that seeded
    /// <c>+00:00</c> would let an offset-destroying regression pass unnoticed, which is the exact
    /// defect this suite now covers.
    /// </para>
    /// </remarks>
    public static readonly DateTimeOffset DefaultRegisteredAt =
        new(2026, 6, 15, 10, 30, 0, TimeSpan.FromHours(-8));

    /// <summary>
    /// Builds a fresh Car PO suitable for <c>CreatePersistentObjectAsync</c>. Callers
    /// supply the license plate (usually a random one to avoid collisions across runs);
    /// <paramref name="model"/> and <paramref name="year"/> take reasonable defaults
    /// since most Security tests don't care about those values beyond "valid Car".
    /// </summary>
    public static PersistentObject New(
        string licensePlate,
        string model = "M1",
        int year = 2024,
        DateTimeOffset? registeredAt = null)
        => new()
        {
            Name = TypeName,
            ObjectTypeId = TypeId,
            Attributes =
            [
                new PersistentObjectAttribute { Name = AttributeNames.LicensePlate, Value = licensePlate },
                new PersistentObjectAttribute { Name = AttributeNames.Model,        Value = model },
                new PersistentObjectAttribute { Name = AttributeNames.Year,         Value = year },
                // Required on Car. Sent in round-trip format ("o") -- a complete ISO-8601 string with
                // the offset already applied, which is what the browser sends and what the server
                // parses without inferring anything.
                new PersistentObjectAttribute
                {
                    Name = AttributeNames.RegisteredAt,
                    Value = (registeredAt ?? DefaultRegisteredAt).ToString("o", CultureInfo.InvariantCulture),
                },
            ],
        };

    /// <summary>
    /// Generates a unique 8-char uppercase license plate. Useful for create flows where
    /// every test run needs a plate that won't collide with an existing document.
    /// </summary>
    public static string RandomLicensePlate(string prefix = "RO")
        => $"{prefix}{Guid.NewGuid():N}"[..8].ToUpperInvariant();
}
