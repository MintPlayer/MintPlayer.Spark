using MintPlayer.Spark.Abstractions;

namespace DemoApp.Library.LookupReferences;

public enum ECarStatus
{
    InUse,
    OnParking,
    InMaintenance,
    Stolen
}

public sealed class CarStatus : TransientLookupReference<ECarStatus>
{
    private CarStatus() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    // Extra properties beyond Key/Values
    public bool AllowOnCarNotes { get; init; }

    public static IReadOnlyCollection<CarStatus> Items { get; } =
    [
        new CarStatus()
        {
            Key = ECarStatus.InUse,
            Description = "Car is in use",
            Values = _TS("In use", "En usage", "In gebruik"),
            AllowOnCarNotes = true,
        },
        new CarStatus()
        {
            Key = ECarStatus.OnParking,
            Description = "Car is parked",
            Values = _TS("In parking lot", "Dans le parking", "Op parking"),
            AllowOnCarNotes = false,
        },
        new CarStatus()
        {
            Key = ECarStatus.InMaintenance,
            Description = "Car is being maintained",
            Values = _TS("In maintenance", "En maintenance", "In onderhoud"),
            AllowOnCarNotes = true,
        },
        new CarStatus()
        {
            Key = ECarStatus.Stolen,
            Description = "Car is stolen",
            Values = _TS("Stolen", "Vole", "Gestolen"),
            AllowOnCarNotes = true,
        },
    ];
}
