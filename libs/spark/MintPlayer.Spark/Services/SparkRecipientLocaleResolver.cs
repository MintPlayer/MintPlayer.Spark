using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.DateTimes;
using MintPlayer.Spark.Configuration;

namespace MintPlayer.Spark.Services;

/// <summary>
/// <see cref="ISparkRecipientLocale"/> (X14): the account's stored preference through
/// <see cref="ISparkRecipientLocaleSource"/> (registered by Authorization), completed with
/// <see cref="SparkTimeZoneOptions.DefaultRecipientTimeZone"/> and the application's default language.
/// </summary>
[Register(typeof(ISparkRecipientLocale), ServiceLifetime.Scoped)]
internal partial class SparkRecipientLocaleResolver : ISparkRecipientLocale
{
    [Inject] private readonly IRequestTimeZoneResolver timeZones;
    [Inject] private readonly IRequestCultureResolver cultures;
    [Inject] private readonly ICultureLoader cultureLoader;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly IOptions<SparkTimeZoneOptions> options;
    [Inject] private readonly ISparkRecipientLocaleSource? source;

    public SparkRecipientLocale FromCurrentRequest()
    {
        if (httpContextAccessor.HttpContext is null)
            return Defaults();

        return new SparkRecipientLocale(IanaId(timeZones.GetViewerTimeZone()), cultures.GetCurrentCulture());
    }

    public async Task<SparkRecipientLocale> ForUserAsync(string? userId, CancellationToken cancellationToken = default)
    {
        var defaults = Defaults();
        if (source is null || string.IsNullOrEmpty(userId))
            return defaults;

        var preference = await source.GetAsync(userId, cancellationToken);
        return Complete(preference, defaults);
    }

    public async Task<SparkRecipientLocale> CaptureForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        var current = FromCurrentRequest();
        if (source is null)
            return current;

        var preference = await source.GetAsync(userId, cancellationToken);
        if (preference is null)
            return current;

        if (string.IsNullOrEmpty(preference.TimeZoneId) || string.IsNullOrEmpty(preference.Culture))
            await source.SaveIfUnsetAsync(userId, current, cancellationToken);

        return Complete(preference, current);
    }

    private static SparkRecipientLocale Complete(SparkRecipientLocalePreference? preference, SparkRecipientLocale fallback) => new(
        SparkTimeZones.IsKnown(preference?.TimeZoneId) ? preference!.TimeZoneId!.Trim() : fallback.TimeZoneId,
        string.IsNullOrWhiteSpace(preference?.Culture) ? fallback.Culture : preference!.Culture!.Trim());

    private SparkRecipientLocale Defaults()
    {
        var zone = options.Value.DefaultRecipientTimeZone;
        return new SparkRecipientLocale(SparkTimeZones.IsKnown(zone) ? zone!.Trim() : "UTC", cultureLoader.GetCulture().DefaultLanguage);
    }

    /// <summary>The IANA id of a resolved zone; a zone found through its Windows id is converted back.</summary>
    internal static string IanaId(TimeZoneInfo zone)
    {
        if (zone == TimeZoneInfo.Utc)
            return "UTC";
        if (zone.HasIanaId)
            return zone.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : "UTC";
    }
}
