namespace MintPlayer.Spark.Abstractions.DateTimes;

/// <summary>
/// The label of a time zone in a language, for the second rendering of a date ("20:00 Brussel") and for
/// zone pickers (commerce PRD D36). One core service; the client twin is ng-spark's <c>SparkTimeZoneNames</c>.
/// </summary>
/// <remarks>
/// The fallback chain on the server (spike S23):
/// <list type="number">
/// <item>the registered <see cref="ISparkTimeZoneNameProvider"/>: the reference-data application's translated
///   CLDR exemplar city, when the host wires its client;</item>
/// <item>the city derived from the IANA id, in English (<c>America/Argentina/Buenos_Aires</c> → <c>Buenos Aires</c>).</item>
/// </list>
/// The platform-localized generic name the browser uses as its step 2 is deliberately absent here:
/// .NET's <c>TimeZoneInfo</c> names are not localized per culture (Windows: the OS language; Linux: the
/// culture of the process's first use, then fixed), so a job formatting mails for several cultures would
/// emit the wrong language (S23). Results are cached per (zone, language) for the life of the process.
/// </remarks>
public interface ISparkTimeZoneNames
{
    /// <summary>The label of <paramref name="ianaZoneId"/> in <paramref name="language"/> (<c>nl</c>, <c>fr-BE</c>). Never empty.</summary>
    ValueTask<string> GetLabelAsync(string ianaZoneId, string language, CancellationToken cancellationToken = default);
}

/// <summary>
/// Step 1 of <see cref="ISparkTimeZoneNames"/>: translated zone names from a data source. The reference-data
/// client package registers one (commerce plan M19); register at most one.
/// </summary>
public interface ISparkTimeZoneNameProvider
{
    /// <summary>The zone's city name in <paramref name="language"/>, or <see langword="null"/> when the source has none.</summary>
    ValueTask<string?> GetCityNameAsync(string ianaZoneId, string language, CancellationToken cancellationToken);
}
