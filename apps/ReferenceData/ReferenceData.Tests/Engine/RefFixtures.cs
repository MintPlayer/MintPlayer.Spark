using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Sources;
using MintPlayer.Spark.ReferenceData.Sources.Belgium;
using MintPlayer.Spark.ReferenceData.Sources.Belgium.Best;
using MintPlayer.Spark.ReferenceData.Sources.Belgium.Flemish;
using MintPlayer.Spark.ReferenceData.Sources.Global;
using MintPlayer.Spark.ReferenceData.Sources.Holidays;
using MintPlayer.Spark.ReferenceData.Sources.Tedb;
using MintPlayer.Spark.ReferenceData.Sources.France;
using MintPlayer.Spark.ReferenceData.Sources.Luxembourg;
using MintPlayer.Spark.ReferenceData.Sources.Netherlands;

namespace ReferenceData.Tests.Engine;

/// <summary>The adapters of the basic seed set's countries (BE, NL, FR, LU, DE via the baseline), over their real sample fixtures (RS8).</summary>
internal static class RefFixtures
{
    public static IReadOnlyList<IReferenceDataSource> Sources() =>
    [
        new BelgiumRefnisSource(), new BelgiumNis6Source(), new BelgiumPostalStreetsSource(),
        new BelgiumBestSource(), new FlemishDiffSource(), new FlemishApiSource(),
        new GeoNamesSource(), new WikidataSource(), new CldrSource(), new TzDataSource(),
        new GnsSource(), new UnLocodeSource(), new IanaSubtagSource(),
        new NetherlandsCbsGemeentenSource(), new NetherlandsHerindelingenSource(), new NetherlandsBagSource(),
        new FranceCogSource(), new FranceBanSource(), new FranceLaPosteSource(),
        new LuxembourgCaclrSource(),
        new TedbSource(), new StatuteHolidaysSource(),
    ];

    public static IReadOnlyList<IReferenceDataMapper> Mappers() =>
        [new GlobalMapper(), new GlobalNamesMapper(), new BelgiumMapper(), new NetherlandsMapper(), new FranceMapper(), new LuxembourgMapper(), new TedbMapper(), new StatuteHolidaysMapper()];

    public static RefDatasetBuilder Builder() => new(Sources(), Mappers());

    private static readonly Lazy<Task<RefDataBuild>> Shared = new(() => Builder().BuildFixturesAsync(CancellationToken.None));

    /// <summary>The basic seed set's build (shared: the build is deterministic and read-only in these tests).</summary>
    public static Task<RefDataBuild> BuildAsync() => Shared.Value;

    /// <summary>The as-of loader over a build.</summary>
    public static Func<string, Task<RefPlace?>> Loader(RefDataBuild build) => id => Task.FromResult(build.Places.GetValueOrDefault(id));

    public static DateOnly D(string text) => DateOnly.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The ancestors of <paramref name="place"/> on <paramref name="day"/>.</summary>
    public static IReadOnlyList<string> AncestorsOn(RefPlace place, string day)
        => RefAsOf.SegmentAt(place, D(day))?.Ancestors ?? [];

    /// <summary>The parent of <paramref name="place"/> on <paramref name="day"/>.</summary>
    public static string? ParentOn(RefPlace place, string day) => RefAsOf.PeriodAt(place, D(day))?.Parent;
}
