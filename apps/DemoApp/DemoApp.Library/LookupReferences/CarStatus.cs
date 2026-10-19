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
            Values = _TS(("en", "In use"), ("fr", "En usage"), ("nl", "In gebruik")),
            AllowOnCarNotes = true,
        },
        new CarStatus()
        {
            Key = ECarStatus.OnParking,
            Description = "Car is parked",
            Values = _TS(("en", "In parking lot"), ("fr", "Dans le parking"), ("nl", "Op parking")),
            AllowOnCarNotes = false,
        },
        new CarStatus()
        {
            Key = ECarStatus.InMaintenance,
            Description = "Car is being maintained",
            Values = _TS(("en", "In maintenance"), ("fr", "En maintenance"), ("nl", "In onderhoud")),
            AllowOnCarNotes = true,
        },
        new CarStatus()
        {
            Key = ECarStatus.Stolen,
            Description = "Car is stolen",
            Values = _TS(("en", "Stolen"), ("fr", "Vole"), ("nl", "Gestolen")),
            AllowOnCarNotes = true,
        },
    ];
}
