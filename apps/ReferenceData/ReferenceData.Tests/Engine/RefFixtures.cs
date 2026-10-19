using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Sources;
using MintPlayer.Spark.ReferenceData.Sources.Belgium;
using MintPlayer.Spark.ReferenceData.Sources.Global;
using MintPlayer.Spark.ReferenceData.Sources.Holidays;
using MintPlayer.Spark.ReferenceData.Sources.Tedb;

namespace ReferenceData.Tests.Engine;

/// <summary>The adapters as apps/ReferenceData registers them, over their real sample fixtures (RS8).</summary>
internal static class RefFixtures
{
    public static IReadOnlyList<IReferenceDataSource> Sources() =>
    [
        new BelgiumRefnisSource(), new BelgiumNis6Source(), new BelgiumPostalStreetsSource(),
        new GeoNamesSource(), new WikidataSource(), new CldrSource(), new TzDataSource(),
        new TedbSource(), new StatuteHolidaysSource(),
    ];

    public static IReadOnlyList<IReferenceDataMapper> Mappers() =>
        [new GlobalMapper(), new BelgiumMapper(), new TedbMapper(), new StatuteHolidaysMapper()];

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
