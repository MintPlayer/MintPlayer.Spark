using MintPlayer.Spark.Abstractions;

namespace Fleet.LookupReferences;

public sealed class CarStatus : TransientLookupReference<string>
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;
    public const string InUse = nameof(InUse);
    public const string OnParking = nameof(OnParking);
    public const string InMaintenance = nameof(InMaintenance);
    public const string Stolen = nameof(Stolen);

    private CarStatus() { }

    public static IReadOnlyCollection<CarStatus> Items { get; } =
    [
        new CarStatus
        {
            Key = InUse,
            Description = "Car is currently in use",
            Values = _TS("In use", "En usage", "In gebruik"),
        },
        new CarStatus
        {
            Key = OnParking,
            Description = "Car is parked",
            Values = _TS("On parking", "Au parking", "Op parking"),
        },
        new CarStatus
        {
            Key = InMaintenance,
            Description = "Car is being maintained",
            Values = _TS("In maintenance", "En maintenance", "In onderhoud"),
        },
        new CarStatus
        {
            Key = Stolen,
            Description = "Car has been stolen",
            Values = _TS("Stolen", "Volé", "Gestolen"),
        },
    ];
}
