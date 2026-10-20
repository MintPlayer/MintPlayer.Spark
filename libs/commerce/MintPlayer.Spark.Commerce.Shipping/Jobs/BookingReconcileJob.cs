using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cron;

namespace MintPlayer.Spark.Commerce.Shipping.Jobs;

/// <summary>
/// Looks up bookings whose carrier answer was lost (<see cref="Models.PendingBookingState.InDoubt"/>) at carriers that
/// can be asked (<see cref="IShippingBookingLookup"/>): a booking the carrier holds gets its labels stored, one it does
/// not hold becomes safe to book again. Never books, so it can never pay for a label twice.
/// </summary>
internal sealed partial class BookingReconcileJob : ISparkCronJob
{
    /// <summary>The shipped default; <c>Spark:Commerce:Shipping:BookingReconcileSchedule</c> overrides it.</summary>
    public static string CronSchedule => "*/10 * * * *";

    [Inject] private readonly ISparkShipping shipping;

    public Task RunAsync(CancellationToken cancellationToken) => shipping.ReconcilePendingBookingsAsync(cancellationToken);
}
