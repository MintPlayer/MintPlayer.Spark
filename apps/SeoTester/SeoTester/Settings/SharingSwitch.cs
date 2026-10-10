using MintPlayer.Spark.Settings;
using MintPlayer.Spark.Sharing;

namespace SeoTester.Settings;

/// <summary>
/// The administrator's sharing switch (<c>docs/seo_analyzer_PRD.md</c> D11, D31) for every share link and badge:
/// <see cref="SeoSettings.Sharing"/> <c>Off</c> refuses new links and hides existing ones; switched back on, they work
/// again. Whether an unverified site may share under <c>VerifiedOnly</c> is decided per site by the <c>ShareLink</c>
/// action (M8, which brings <c>Site</c>).
/// </summary>
internal sealed class SharingSwitch(ISparkSettings settings) : ISparkShareGate
{
    public async ValueTask<bool> IsEnabledAsync(string typeName, CancellationToken cancellationToken)
        => await settings.GetAsync(SeoSettings.Sharing, cancellationToken) != SeoFeatureScope.Off;
}
