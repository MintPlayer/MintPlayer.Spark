using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Commerce.Payments.Services;
using MintPlayer.Spark.Cron;

namespace MintPlayer.Spark.Commerce.Payments.Jobs;

/// <summary>Imports and reconciles settlements (GB4). Idempotent: lines have natural ids, the cursor only moves forward.</summary>
internal sealed partial class SettlementImportJob : ISparkCronJob
{
    /// <summary>The shipped default; <c>Spark:Commerce:Payments:Settlements:ImportSchedule</c> overrides it.</summary>
    public static string CronSchedule => "17 * * * *";

    [Inject] private readonly SettlementReconciler reconciler;

    public Task RunAsync(CancellationToken cancellationToken) => reconciler.ImportAllAsync(cancellationToken);
}

/// <summary>Re-reads linked seller accounts that are not verified yet (Mollie sends no onboarding webhook; D21).</summary>
internal sealed partial class KycRefreshJob : ISparkCronJob
{
    /// <summary>The shipped default; <c>Spark:Commerce:Payments:Connect:KycRefreshSchedule</c> overrides it.</summary>
    public static string CronSchedule => "*/15 * * * *";

    [Inject] private readonly SellerOnboarding onboarding;

    public Task RunAsync(CancellationToken cancellationToken) => onboarding.RefreshPendingAsync(cancellationToken);
}

/// <summary>Closes as lost the chargebacks a provider never decides, past <c>Disputes:ChargebackFinalAfter</c> (GB1).</summary>
internal sealed partial class ChargebackFinalityJob : ISparkCronJob
{
    public static string CronSchedule => "41 3 * * *";

    [Inject] private readonly SparkPayments payments;

    public Task RunAsync(CancellationToken cancellationToken) => payments.CloseStaleChargebacksAsync(cancellationToken);
}
