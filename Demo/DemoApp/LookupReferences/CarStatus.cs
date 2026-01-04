using MintPlayer.Spark.Abstractions;

namespace DemoApp.LookupReferences;

public sealed class CarStatus : TransientLookupReference
{
    public const string InUse = nameof(InUse);
    public const string OnParking = nameof(OnParking);
    public const string InMaintenance = nameof(InMaintenance);
    public const string Stolen = nameof(Stolen);

    private CarStatus() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    // Extra properties beyond Key/Values
    public bool AllowOnCarNotes { get; init; }

    public static IReadOnlyCollection<CarStatus> Items { get; } =
    [
        new CarStatus()
        {
            Key = InUse,
            Description = "Car is in use",
            Values = _TS("In use", "En usage", "In gebruik"),
            AllowOnCarNotes = true,
        },
        new CarStatus()
        {
            Key = OnParking,
            Description = "Car is parked",
            Values = _TS("In parking lot", "Dans le parking", "Op parking"),
            AllowOnCarNotes = false,
        },
        new CarStatus()
        {
            Key = InMaintenance,
            Description = "Car is being maintained",
            Values = _TS("In maintenance", "En maintenance", "In onderhoud"),
            AllowOnCarNotes = true,
        },
        new CarStatus()
        {
            Key = Stolen,
            Description = "Car is stolen",
            Values = _TS("Stolen", "Vole", "Gestolen"),
            AllowOnCarNotes = true,
        },
    ];
}
