using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Commerce.Shipping;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Commerce.Shipping.Rates;
using MintPlayer.Spark.Commerce.Shipping.Services;
using MintPlayer.Spark.Commerce.Shipping.Test;
using MintPlayer.Spark.Commerce.Tests.Payments;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Tests.Shipping;

/// <summary>
/// Carrier booking idempotency (#521 item 11): a booking is recorded as pending before the carrier is called, and a
/// booking whose answer was lost is looked up at the carrier (by the reference it was sent) before it is booked again,
/// by the next attempt or by <c>Shipping.BookingReconcile</c>. A carrier that cannot be asked waits for staff.
/// </summary>
public class ShippingBookingIdempotencyTests : SparkTestDriver
{
    private readonly ManualTimeProvider clock = new(DateTimeOffset.Parse("2026-10-21T08:00:00Z"));

    /// <summary>What the flaky carrier does with the next calls.</summary>
    private sealed class Plan
    {
        /// <summary>Book at the carrier, then lose the answer (a timeout after the carrier booked).</summary>
        public int LoseAnswers { get; set; }

        /// <summary>Refuse without booking (a 400).</summary>
        public int Refusals { get; set; }

        public bool CanLookUp { get; set; } = true;

        public bool VoidsWholeShipment { get; set; }

        public int Lookups { get; set; }

        public List<CarrierParcelReference> Voids { get; } = [];
    }

    /// <summary>The test carrier behind a network that loses answers; looks up only when the plan says so.</summary>
    private sealed class FlakyCarrier(TestCarrier inner, Plan plan) : IShippingCarrier, IShippingBookingLookup
    {
        public string Name => inner.Name;

        public CarrierCapabilities Capabilities => inner.Capabilities with { VoidsWholeShipment = plan.VoidsWholeShipment };

        public IReadOnlyList<CarrierService> Services => inner.Services;

        public async Task<CarrierShipmentResult> CreateShipmentAsync(CarrierShipmentRequest request, CancellationToken cancellationToken)
        {
            if (plan.Refusals > 0)
            {
                plan.Refusals--;
                throw new CarrierException(Name, "the address is invalid.", 400);
            }
            var result = await inner.CreateShipmentAsync(request, cancellationToken);
            if (plan.LoseAnswers > 0)
            {
                plan.LoseAnswers--;
                throw new HttpRequestException("The response ended prematurely.");
            }
            return result;
        }

        public Task<bool> VoidAsync(CarrierParcelReference parcel, CancellationToken cancellationToken)
        {
            plan.Voids.Add(parcel);
            return inner.VoidAsync(parcel, cancellationToken);
        }

        public Task<IReadOnlyList<CarrierTrackingEvent>> GetTrackingAsync(CarrierParcelReference parcel, CancellationToken cancellationToken)
            => inner.GetTrackingAsync(parcel, cancellationToken);

        public Task<IReadOnlyList<CarrierTrackingEvent>> ParseWebhookAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
            => inner.ParseWebhookAsync(delivery, cancellationToken);

        public Task<CarrierBookingLookup> FindBookingAsync(CarrierShipmentRequest request, CancellationToken cancellationToken)
        {
            if (!plan.CanLookUp)
                return Task.FromResult(CarrierBookingLookup.Unsupported);
            plan.Lookups++;
            return inner.FindBookingAsync(request, cancellationToken);
        }
    }

    private sealed class LabelledRecipient : IShipmentEventRecipient
    {
        public List<Shipment> Labelled { get; } = [];

        public Task OnShipmentLabelledAsync(Shipment shipment, CancellationToken cancellationToken) { Labelled.Add(shipment); return Task.CompletedTask; }
    }

    private sealed record Host(IServiceProvider Services, TestCarrierStore Carrier, Plan Plan, LabelledRecipient Recipient)
    {
        public ISparkShipping Shipping => Services.CreateScope().ServiceProvider.GetRequiredService<ISparkShipping>();
    }

    private static readonly ShippingAddress Shop = new() { Name = "Shop", Street = "Rue de la Loi", HouseNumber = "16", PostalCode = "1000", City = "Brussels", Country = "BE" };

    private Host Build(Plan? plan = null)
    {
        plan ??= new Plan();
        var settings = new SparkCommerceShippingOptions
        {
            Carrier = TestCarrier.CarrierName,
            Sender = Shop,
            Zones = new(StringComparer.OrdinalIgnoreCase) { ["eu"] = new() { Countries = ["BE", "NL", "LU", "FR", "DE"] } },
            Methods = new(StringComparer.OrdinalIgnoreCase) { ["standard"] = new() { Service = "test-standard", Rates = new() { ["eu"] = 4.95m }, Order = 1 } },
            Dispatch = new() { TimeZone = "Europe/Brussels" },
        };
        var store = new TestCarrierStore();
        var recipient = new LabelledRecipient();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton(Store);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IOptionsMonitor<SparkCommerceShippingOptions>>(new StaticOptionsMonitor<SparkCommerceShippingOptions>(settings));
        services.AddSingleton<IOptionsMonitor<TestShippingOptions>>(new StaticOptionsMonitor<TestShippingOptions>(new TestShippingOptions()));
        services.AddSingleton(store);
        services.AddSingleton(plan);
        services.AddScoped<TestCarrier>();
        services.AddScoped<IShippingCarrier>(sp => new FlakyCarrier(sp.GetRequiredService<TestCarrier>(), plan));
        services.AddScoped<IShippingRateProvider, ConfiguredShippingRateProvider>();
        services.AddScoped<ShippingCarrierRegistry>();
        services.AddScoped<DispatchSchedule>();
        services.AddScoped<ISparkShipping, SparkShipping>();
        services.AddSingleton<IShipmentEventRecipient>(recipient);
        return new Host(services.BuildServiceProvider(), store, plan, recipient);
    }

    private static CreateShipmentRequest Request(int parcels = 1) => new()
    {
        Reference = "orders/7",
        Method = "standard",
        IdempotencyKey = "orders/7/outbound",
        ShipTo = new ShippingAddress { Name = "Buyer", Street = "Damrak", HouseNumber = "1", PostalCode = "1012AB", City = "Amsterdam", Country = "NL" },
        Parcels = Enumerable.Range(1, parcels).Select(i => new ParcelRequest(800 * i)).ToList(),
    };

    private async Task<Shipment> LoadAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<Shipment>(id);
    }

    [Fact]
    public async Task A_lost_carrier_answer_is_looked_up_on_retry_instead_of_booked_twice()
    {
        var host = Build(new Plan { LoseAnswers = 1 });

        var first = async () => await host.Shipping.CreateShipmentAsync(Request(), default);
        await first.Should().ThrowAsync<HttpRequestException>();
        var pending = await LoadAsync("Shipments/orders/7/outbound");
        pending.Status.Should().Be(TrackingStatus.Pending);
        pending.HasPendingBooking.Should().BeTrue();
        pending.PendingBookings.Single().State.Should().Be(PendingBookingState.InDoubt);

        var shipment = await host.Shipping.CreateShipmentAsync(Request(), default);

        host.Carrier.Requests.Should().HaveCount(1, "the carrier already held the booking: the retry must find it, not book again");
        host.Plan.Lookups.Should().Be(1);
        shipment.Status.Should().Be(TrackingStatus.Labelled);
        shipment.HasPendingBooking.Should().BeFalse();
        shipment.PendingBookings.Should().BeEmpty();
        shipment.Parcels.Single().TrackingNumber.Should().Be(host.Carrier.Parcels.Values.Single().TrackingNumber);
        (await host.Shipping.GetLabelAsync(shipment.Id!, 1, default)).Should().NotBeNull();
        host.Recipient.Labelled.Should().ContainSingle();
    }

    [Fact]
    public async Task The_reconciliation_stores_the_labels_of_a_lost_answer_without_booking()
    {
        var host = Build(new Plan { LoseAnswers = 1 });
        var first = async () => await host.Shipping.CreateShipmentAsync(Request(parcels: 2), default);
        await first.Should().ThrowAsync<HttpRequestException>();

        // Too young: it may still be in flight.
        (await host.Shipping.ReconcilePendingBookingsAsync(default)).Should().Be(0);
        clock.Now = clock.Now.AddMinutes(11);
        WaitForIndexing(Store);
        (await host.Shipping.ReconcilePendingBookingsAsync(default)).Should().Be(1);

        var shipment = await LoadAsync("Shipments/orders/7/outbound");
        shipment.Status.Should().Be(TrackingStatus.Labelled);
        shipment.Parcels.Should().OnlyContain(p => p.TrackingNumber != null && p.LabelAttachment != null);
        shipment.HasPendingBooking.Should().BeFalse();
        host.Carrier.Requests.Should().HaveCount(1);
        host.Recipient.Labelled.Should().ContainSingle();

        // A later retry answers the labelled shipment as it is.
        var again = await host.Shipping.CreateShipmentAsync(Request(parcels: 2), default);
        again.Parcels.Select(p => p.TrackingNumber).Should().Equal(shipment.Parcels.Select(p => p.TrackingNumber));
        host.Carrier.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_lookup_that_finds_nothing_makes_the_retry_book()
    {
        var host = Build(new Plan { LoseAnswers = 1 });
        var lost = async () => await host.Shipping.CreateShipmentAsync(Request(), default);
        await lost.Should().ThrowAsync<HttpRequestException>();
        host.Carrier.Requests.Should().HaveCount(1);
        // The call never reached the carrier's books (here: the carrier forgot it): the lookup finds nothing, so the retry books.
        host.Carrier.Requests.Clear();
        host.Carrier.Bookings.Clear();
        host.Carrier.Parcels.Clear();

        // Right away, "not found" may mean "still in flight": the retry refuses to book blind.
        var tooSoon = async () => await host.Shipping.CreateShipmentAsync(Request(), default);
        await tooSoon.Should().ThrowAsync<ShippingRequestException>();
        host.Carrier.Requests.Should().BeEmpty();

        clock.Now = clock.Now.AddMinutes(11);
        var retried = await host.Shipping.CreateShipmentAsync(Request(), default);
        retried.Status.Should().Be(TrackingStatus.Labelled);
        host.Carrier.Requests.Should().ContainSingle();
        host.Plan.Lookups.Should().Be(2);
    }

    [Fact]
    public async Task A_carrier_that_cannot_look_up_waits_for_staff_before_booking_again()
    {
        var host = Build(new Plan { LoseAnswers = 1, CanLookUp = false });
        var first = async () => await host.Shipping.CreateShipmentAsync(Request(), default);
        await first.Should().ThrowAsync<HttpRequestException>();

        var retry = async () => await host.Shipping.CreateShipmentAsync(Request(), default);
        await retry.Should().ThrowAsync<ShipmentBookingInDoubtException>();
        host.Carrier.Requests.Should().HaveCount(1);
        (await host.Shipping.ReconcilePendingBookingsAsync(default)).Should().Be(0);

        // Staff checked the carrier's panel (and voided the stray label there).
        (await host.Shipping.ReleasePendingBookingAsync("Shipments/orders/7/outbound", "users/staff", default)).Should().BeTrue();
        var shipment = await host.Shipping.CreateShipmentAsync(Request(), default);
        shipment.Status.Should().Be(TrackingStatus.Labelled);
        host.Carrier.Requests.Should().HaveCount(2);
        (await host.Shipping.ReleasePendingBookingAsync(shipment.Id!, "users/staff", default)).Should().BeFalse();
    }

    [Fact]
    public async Task A_refused_booking_is_retried_under_a_fresh_carrier_reference()
    {
        var host = Build(new Plan { Refusals = 1 });
        var first = async () => await host.Shipping.CreateShipmentAsync(Request(), default);
        await first.Should().ThrowAsync<CarrierException>();
        var refused = await LoadAsync("Shipments/orders/7/outbound");
        refused.PendingBookings.Single().State.Should().Be(PendingBookingState.Refused);
        refused.PendingBookings.Single().Error.Should().Contain("address is invalid");

        var shipment = await host.Shipping.CreateShipmentAsync(Request(), default);
        shipment.Status.Should().Be(TrackingStatus.Labelled);
        host.Plan.Lookups.Should().Be(0, "a refusal booked nothing: there is nothing to look up");
        host.Carrier.Requests.Single().ShipmentId.Should().Be("Shipments/orders/7/outbound-r1");
    }

    [Fact]
    public async Task An_added_parcel_whose_answer_was_lost_is_looked_up_too()
    {
        var host = Build();
        var shipment = await host.Shipping.CreateShipmentAsync(Request(), default);
        host.Plan.LoseAnswers = 1;
        var add = async () => await host.Shipping.AddParcelAsync(shipment.Id!, new ParcelRequest(500), "users/packer", default);
        await add.Should().ThrowAsync<HttpRequestException>();
        host.Carrier.Requests.Should().HaveCount(2);

        clock.Now = clock.Now.AddMinutes(11);
        WaitForIndexing(Store);
        (await host.Shipping.ReconcilePendingBookingsAsync(default)).Should().Be(1);
        var stored = await LoadAsync(shipment.Id!);
        stored.Parcels.Should().HaveCount(2);
        stored.Parcels.Should().OnlyContain(p => p.Status == TrackingStatus.Labelled);
        stored.Status.Should().Be(TrackingStatus.Labelled);
        host.Carrier.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_refused_added_parcel_leaves_the_shipment_as_it_was()
    {
        var host = Build();
        var shipment = await host.Shipping.CreateShipmentAsync(Request(), default);
        host.Plan.Refusals = 1;
        var add = async () => await host.Shipping.AddParcelAsync(shipment.Id!, new ParcelRequest(500), "users/packer", default);
        await add.Should().ThrowAsync<CarrierException>();

        var stored = await LoadAsync(shipment.Id!);
        stored.Parcels.Should().ContainSingle();
        stored.Status.Should().Be(TrackingStatus.Labelled);
        stored.HasPendingBooking.Should().BeFalse();
    }

    [Fact]
    public async Task A_carrier_that_cancels_whole_shipments_voids_all_parcels_with_one_call_and_never_one_of_several()
    {
        var host = Build(new Plan { VoidsWholeShipment = true });
        var shipment = await host.Shipping.CreateShipmentAsync(Request(parcels: 2), default);

        (await host.Shipping.VoidAsync(shipment.Id!, 1, "users/staff", default)).Should().BeFalse();
        host.Plan.Voids.Should().BeEmpty();
        (await LoadAsync(shipment.Id!)).Parcels.Should().OnlyContain(p => !p.Voided);

        (await host.Shipping.VoidAsync(shipment.Id!, null, "users/staff", default)).Should().BeTrue();
        host.Plan.Voids.Should().ContainSingle();
        var voided = await LoadAsync(shipment.Id!);
        voided.Parcels.Should().OnlyContain(p => p.Voided && p.Status == TrackingStatus.Cancelled);
        voided.Status.Should().Be(TrackingStatus.Cancelled);
    }
}
