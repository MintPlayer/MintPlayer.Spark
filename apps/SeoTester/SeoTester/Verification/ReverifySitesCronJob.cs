using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cron;

namespace SeoTester.Verification;

/// <summary>
/// The periodic re-check of verified sites (<c>docs/seo_analyzer_PRD.md</c> F3): hourly, the sites whose last check is
/// older than <c>SeoTester:Verification:RecheckInterval</c> (a day), up to 200 a pass. Cluster-safe through Spark cron's
/// claim. The work is <see cref="SiteVerification.RecheckDueAsync"/>.
/// </summary>
public partial class ReverifySitesCronJob : ISparkCronJob
{
    [Inject] private readonly SiteVerification verification;

    public static string CronSchedule => "23 * * * *";

    public Task RunAsync(CancellationToken cancellationToken) => verification.RecheckDueAsync(batch: 200, cancellationToken);
}
