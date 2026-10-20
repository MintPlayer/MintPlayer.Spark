using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Cron;

namespace MintPlayer.Spark.Commerce.Checkout;

/// <summary>M9's registrations, called once by <see cref="SparkCommerceCheckoutExtensions.AddCommerceCheckout"/>.</summary>
internal static class CheckoutRefundRegistration
{
    public static void AddCheckoutRefunds(this ISparkBuilder builder, SparkCommerceCheckoutOptions registrationTime)
    {
        var services = builder.Services;
        services.TryAddScoped<OrderLinks>();
        services.TryAddScoped<RefundEngine>();
        services.TryAddScoped<RefundMails>();
        services.TryAddScoped<CheckoutHolidays>();
        services.TryAddScoped<RefundService>();
        services.TryAddScoped<ISparkCheckoutRefunds>(sp => sp.GetRequiredService<RefundService>());
        services.TryAddScoped<ISparkCheckoutUndelivered>(sp => sp.GetRequiredService<RefundService>());
        services.TryAddScoped<MassRefunds>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOrderEventRecipient, RefundTermsRecipient>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IPaymentEventRecipient, RefundPaymentRecipient>());

        builder.AddCron(cron => cron
            .AddJob<MassRefundJob>(registrationTime.Refunds.MassRefunds.Schedule, "Checkout.MassRefunds")
            .AddJob<RefundSweepJob>(registrationTime.Refunds.SweepSchedule, "Checkout.RefundSweep"));
    }
}

/// <summary>Optional integrations of Checkout's refund periods.</summary>
public static class CheckoutHolidayExtensions
{
    /// <summary>
    /// Moves a withdrawal period's last day off the public holidays of <c>Spark:Commerce:Checkout:Refunds:HolidayCountry</c>
    /// (Reg. 1182/71 Art. 3(4)), from the reference data's holiday calendar (GC11). The calendar is the reference-data
    /// client's sync (<c>PublicHolidays</c>); without it, or without a country, only weekends move it.
    /// </summary>
    public static ISparkBuilder UseCheckoutReferenceDataHolidays(this ISparkBuilder builder)
    {
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<ICheckoutHolidayCalendar, ReferenceDataCheckoutHolidayCalendar>());
        return builder;
    }
}
