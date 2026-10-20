using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Shipping.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Shipping.Services;

/// <summary>The one implementation of <see cref="ISparkShipping"/>.</summary>
internal sealed partial class SparkShipping(
    IDocumentStore store,
    ShippingCarrierRegistry registry,
    IEnumerable<IShippingRateProvider> rateProviders,
    DispatchSchedule schedule,
    IOptionsMonitor<SparkCommerceShippingOptions> options,
    IMemoryCache cache,
    IServiceProvider services,
    TimeProvider time,
    ILogger<SparkShipping> logger) : ISparkShipping
{
    /// <summary>How long a tracking lookup waits for the index; a failure bound, never a delay.</summary>
    private static readonly TimeSpan StaleWait = TimeSpan.FromSeconds(5);

    public IReadOnlyList<IShippingCarrier> Carriers => registry.All;

    // ---- Rates -------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ShippingRate>> GetRatesAsync(ShippingRateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = options.CurrentValue;
        var shipTo = ShippingAddressRules.Normalize(request.ShipTo);
        var shipFrom = request.ShipFrom is { } from ? ShippingAddressRules.Normalize(from) : settings.Sender;
        var dangerous = DangerousGoodsRules.Normalize(request.DangerousGoods);
        var customs = shipFrom is null ? CustomsReason.None : CustomsRules.Requirement(shipFrom, shipTo, settings.Customs);

        var offered = new List<ShippingRate>();
        foreach (var provider in rateProviders)
        {
            foreach (var rate in await provider.GetRatesAsync(request with { ShipTo = shipTo }, cancellationToken))
            {
                if (await CheckRateAsync(rate, shipTo, shipFrom, request, dangerous, customs, settings, cancellationToken) is { } checkedRate)
                    offered.Add(checkedRate);
            }
        }
        return offered.OrderBy(r => r.Order).ThenBy(r => r.Price.Amount).ToList();
    }

    private async Task<ShippingRate?> CheckRateAsync(ShippingRate rate, ShippingAddress shipTo, ShippingAddress? shipFrom, ShippingRateRequest request,
        IReadOnlyList<DangerousGoodsItem> dangerous, CustomsReason customs, SparkCommerceShippingOptions settings, CancellationToken cancellationToken)
    {
        var zone = rate.Zone ?? ShippingZones.Resolve(settings.Zones, shipTo.Country);
        var method = settings.Methods.GetValueOrDefault(rate.Method);
        DeliveryPromise? promise = null;
        if (method?.TransitDays is { } transit)
            promise = await schedule.PromiseAsync(transit, shipTo, request.At, cancellationToken);

        if (rate.Kind == ShippingMethodKind.Pickup)
            return rate with { Zone = zone, Customs = CustomsReason.None, Promise = promise };

        // A carrier rate must name a registered carrier and a service in its catalog that takes this parcel.
        if (registry.Find(rate.Carrier) is not { } carrier || rate.ServiceCode is not { Length: > 0 } code)
            return null;
        var service = carrier.Services.FirstOrDefault(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase));
        if (service is null || service.Return)
            return null;
        if (CatalogProblems(carrier, service, shipFrom, shipTo, [request.WeightGrams], dangerous).Count > 0)
            return null;
        if (customs != CustomsReason.None && !carrier.Capabilities.CustomsDocuments)
            return null;

        var incoterm = customs == CustomsReason.None ? (Incoterm?)null
            : zone is { } z && settings.Zones.TryGetValue(z, out var zoneOptions) ? zoneOptions.Incoterm : Incoterm.Dap;
        return rate with
        {
            Carrier = carrier.Name,
            Zone = zone,
            ServicePoint = service.ServicePoint,
            Customs = customs,
            Incoterm = incoterm,
            Promise = rate.Promise ?? promise,
        };
    }

    /// <summary>Why <paramref name="service"/> of <paramref name="carrier"/> cannot carry these parcels; empty when it can.</summary>
    internal static List<string> CatalogProblems(IShippingCarrier carrier, CarrierService service, ShippingAddress? shipFrom, ShippingAddress shipTo,
        IEnumerable<int> parcelWeights, IReadOnlyList<DangerousGoodsItem> dangerous)
    {
        var problems = new List<string>();
        if (service.Countries.Count > 0 && !service.Countries.Contains(shipTo.Country, StringComparer.OrdinalIgnoreCase))
            problems.Add($"{carrier.Name} service '{service.Code}' does not deliver to {shipTo.Country}.");
        if (shipFrom is not null && service.FromCountries.Count > 0 && !service.FromCountries.Contains(shipFrom.Country, StringComparer.OrdinalIgnoreCase))
            problems.Add($"{carrier.Name} service '{service.Code}' does not ship from {shipFrom.Country}.");
        foreach (var weight in parcelWeights)
        {
            if (weight < service.MinWeightGrams || (service.MaxWeightGrams > 0 && weight > service.MaxWeightGrams))
                problems.Add($"{carrier.Name} service '{service.Code}' does not take a parcel of {weight} g.");
        }
        // GC9: a service that does not accept a class is never offered and never booked.
        foreach (var item in dangerous.Where(i => !service.AcceptedDangerousGoods.Contains(i.Class)))
            problems.Add($"{carrier.Name} service '{service.Code}' does not accept {item.Class}{(item.UnNumber is { } un ? $" ({un})" : string.Empty)}.");
        if (service.ServicePoint && !carrier.Capabilities.ServicePoints)
            problems.Add($"{carrier.Name} cannot book service-point delivery.");
        return problems;
    }

    // ---- Service points ----------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ServicePoint>> SearchServicePointsAsync(string? carrier, ServicePointQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var resolved = registry.Get(carrier);
        if (registry.ServicePoints(resolved.Name) is not { } provider)
            return [];
        var normalized = query with
        {
            Country = query.Country.Trim().ToUpperInvariant(),
            PostalCode = query.PostalCode.Trim().ToUpperInvariant(),
            Limit = Math.Clamp(query.Limit, 1, 50),
        };
        if (!ShippingAddressRules.IsCountryCode(normalized.Country) || normalized.PostalCode.Length is 0 or > 16)
            return [];

        var key = string.Join('|', "spark-shipping-points", resolved.Name, normalized.Account, normalized.Country, normalized.PostalCode,
            normalized.City, normalized.Street, normalized.HouseNumber, normalized.Kind, normalized.Limit);
        if (cache.TryGetValue(key, out IReadOnlyList<ServicePoint>? cached) && cached is not null)
            return cached;
        var found = await provider.SearchAsync(normalized, cancellationToken);
        var result = (normalized.Kind is { } kind ? found.Where(p => p.Kind == kind) : found).Take(normalized.Limit).ToList();
        cache.Set(key, (IReadOnlyList<ServicePoint>)result, options.CurrentValue.ServicePointCacheDuration);
        return result;
    }

    // ---- Booking -----------------------------------------------------------------------------------------------

    public async Task<Shipment> CreateShipmentAsync(CreateShipmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = options.CurrentValue;
        var problems = new List<string>();

        var (carrierName, serviceCode) = ResolveService(request, settings, problems);
        IShippingCarrier? carrier = null;
        try { carrier = registry.Get(carrierName); }
        catch (InvalidOperationException ex) { problems.Add(ex.Message); }

        var shipFrom = request.ShipFrom ?? settings.Sender;
        problems.AddRange(ShippingAddressRules.Validate(shipFrom, "ship-from"));
        problems.AddRange(ShippingAddressRules.Validate(request.ShipTo, "ship-to"));
        if (request.Parcels is not { Count: > 0 })
            problems.Add("A shipment needs at least one parcel.");
        if (request.Reference is not { Length: > 0 })
            problems.Add("A shipment needs a reference.");
        if (request.IdempotencyKey is { } idempotency && !IdempotencyKeyPattern().IsMatch(idempotency))
            problems.Add("The idempotency key is 1-120 letters, digits, '-', '_' or '/'.");
        if (problems.Count > 0)
            throw new ShippingRequestException(problems);

        var from = ShippingAddressRules.Normalize(shipFrom!);
        var to = ShippingAddressRules.Normalize(request.ShipTo);
        await ValidateAddressAsync(to, "ship-to", problems, cancellationToken);

        var service = carrier!.Services.FirstOrDefault(s => string.Equals(s.Code, serviceCode, StringComparison.OrdinalIgnoreCase));
        var dangerous = DangerousGoodsRules.Normalize(request.DangerousGoods);
        if (service is null)
            problems.Add($"{carrier.Name} has no service '{serviceCode}'.");
        else
        {
            problems.AddRange(CatalogProblems(carrier, service, from, to, request.Parcels.Select(p => p.WeightGrams), dangerous));
            if (service.ServicePoint && request.ServicePoint is null)
                problems.Add($"{carrier.Name} service '{service.Code}' delivers to a service point: choose one.");
            if (!service.ServicePoint && request.ServicePoint is not null)
                problems.Add($"{carrier.Name} service '{service.Code}' does not deliver to a service point.");
        }

        var customs = BuildCustoms(carrier, service, from, to, request.CustomsItems, request.CustomsContentType, request.InvoiceNumber, settings, problems);
        if (problems.Count > 0)
            throw new ShippingRequestException(problems);

        var shipment = new Shipment
        {
            Id = request.IdempotencyKey is { } key ? "Shipments/" + key : null,
            Reference = request.Reference,
            Owner = request.Owner,
            Direction = ShipmentDirection.Outbound,
            Carrier = carrier.Name,
            ServiceCode = service!.Code,
            Method = request.Method,
            Account = request.Account,
            ShipFrom = from,
            ShipTo = to,
            ServicePoint = request.ServicePoint is { } point
                ? new ShipmentServicePoint { Id = point.Id, Name = point.Name, Kind = point.Kind, Address = ShippingAddressRules.Normalize(point.Address) }
                : null,
            Parcels = request.Parcels.Select((p, i) => NewParcel(p, i + 1)).ToList(),
            Customs = customs,
            DangerousGoods = dangerous,
            CreatedBy = request.CreatedBy,
        };
        if (request.Method is { } methodKey && settings.Methods.GetValueOrDefault(methodKey)?.TransitDays is { } transit)
        {
            var promise = await schedule.PromiseAsync(transit, to, null, cancellationToken);
            shipment.PromisedFrom = promise.From;
            shipment.PromisedBy = promise.By;
        }

        var (booked, fresh) = await BookAsync(shipment, carrier, request.LabelFormat ?? settings.LabelFormat, isReturn: false, cancellationToken);
        if (fresh)
        {
            foreach (var recipient in services.GetServices<IShipmentEventRecipient>())
                await recipient.OnShipmentLabelledAsync(booked, cancellationToken);
        }
        return booked;
    }

    public async Task<Shipment> AddParcelAsync(string shipmentId, ParcelRequest parcel, string? userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parcel);
        Shipment shipment;
        using (var session = store.OpenAsyncSession())
            shipment = await session.LoadAsync<Shipment>(shipmentId, cancellationToken) ?? throw new ShippingRequestException([$"No shipment '{shipmentId}'."]);

        var carrier = registry.Get(shipment.Carrier);
        var service = carrier.Services.FirstOrDefault(s => string.Equals(s.Code, shipment.ServiceCode, StringComparison.OrdinalIgnoreCase));
        var problems = new List<string>();
        if (service is null)
            problems.Add($"{carrier.Name} no longer offers service '{shipment.ServiceCode}'.");
        else
            problems.AddRange(CatalogProblems(carrier, service, shipment.ShipFrom, shipment.ShipTo, [parcel.WeightGrams], shipment.DangerousGoods));
        if (shipment.Final || shipment.Status >= TrackingStatus.InTransit)
            problems.Add("The shipment has left: book a new one.");
        // The declaration covers the parcels booked with it; another parcel needs its own declaration.
        if (shipment.Customs is not null)
            problems.Add("A shipment with a customs declaration cannot take another parcel: book a new shipment.");
        if (problems.Count > 0)
            throw new ShippingRequestException(problems);

        if (shipment.HasPendingBooking)
            throw new ShippingRequestException(["The shipment has a booking in progress: wait for it, or release it."]);

        // The parcel and its pending booking are stored before the carrier is called (carrier idempotency).
        int number;
        using (var session = store.OpenAsyncSession())
        {
            session.Advanced.UseOptimisticConcurrency = true;
            var current = await session.LoadAsync<Shipment>(shipmentId, cancellationToken);
            number = current.Parcels.Count == 0 ? 1 : current.Parcels.Max(p => p.Number) + 1;
            current.Parcels.Add(NewParcel(parcel, number));
            current.PendingBookings.Add(NewBooking($"{current.Id}/{number}", [number], options.CurrentValue.LabelFormat));
            current.HasPendingBooking = true;
            current.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
            await session.SaveChangesAsync(cancellationToken);
        }
        try
        {
            await ProcessBookingsAsync(shipmentId, carrier, lookupOnly: false, cancellationToken);
        }
        catch (CarrierException ex) when (IsRefusal(ex))
        {
            // The carrier booked nothing: the shipment is as it was.
            using var session = store.OpenAsyncSession();
            session.Advanced.UseOptimisticConcurrency = true;
            var current = await session.LoadAsync<Shipment>(shipmentId, cancellationToken);
            current.Parcels.RemoveAll(p => p.Number == number);
            current.PendingBookings.RemoveAll(b => b.Parcels.Contains(number));
            current.HasPendingBooking = current.PendingBookings.Count > 0;
            Recompute(current);
            await session.SaveChangesAsync(cancellationToken);
            throw;
        }
        logger.LogInformation("Parcel {Number} added to shipment {Shipment} by {User}.", number, shipmentId, userId);
        using (var session = store.OpenAsyncSession())
            return await session.LoadAsync<Shipment>(shipmentId, cancellationToken);
    }

    public async Task<Shipment> CreateReturnLabelAsync(ReturnLabelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = options.CurrentValue;
        Shipment outbound;
        using (var session = store.OpenAsyncSession())
            outbound = await session.LoadAsync<Shipment>(request.ShipmentId, cancellationToken) ?? throw new ShippingRequestException([$"No shipment '{request.ShipmentId}'."]);
        if (outbound.Direction != ShipmentDirection.Outbound)
            throw new ShippingRequestException(["A return label is made for an outbound shipment."]);

        var carrier = registry.Get(outbound.Carrier);
        var problems = new List<string>();
        if (!carrier.Capabilities.ReturnLabels)
            problems.Add($"{carrier.Name} cannot create return labels.");
        if (request.IdempotencyKey is { } idempotency && !IdempotencyKeyPattern().IsMatch(idempotency))
            problems.Add("The idempotency key is 1-120 letters, digits, '-', '_' or '/'.");
        var from = outbound.ShipTo;
        var to = settings.ReturnAddress ?? settings.Sender ?? outbound.ShipFrom;
        var service = request.ServiceCode is { Length: > 0 } code
            ? carrier.Services.FirstOrDefault(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase))
            : carrier.Services.FirstOrDefault(s => s.Return && (s.FromCountries.Count == 0 || s.FromCountries.Contains(from.Country, StringComparer.OrdinalIgnoreCase)))
              ?? carrier.Services.FirstOrDefault(s => string.Equals(s.Code, outbound.ServiceCode, StringComparison.OrdinalIgnoreCase) && !s.ServicePoint);
        var parcels = request.Parcels is { Count: > 0 } asked
            ? asked
            : [new ParcelRequest(outbound.Parcels.FirstOrDefault(p => !p.Voided)?.WeightGrams ?? 1000)];
        if (service is null)
            problems.Add($"{carrier.Name} has no return service for {from.Country}.");
        else
        {
            // The return service's "countries" are where it collects from: the buyer's.
            var reversed = service with { Countries = service.FromCountries, FromCountries = service.Countries };
            problems.AddRange(CatalogProblems(carrier, reversed, to, from, parcels.Select(p => p.WeightGrams), outbound.DangerousGoods));
        }
        CustomsDeclaration? customs = null;
        if (outbound.Customs is { } declared)
        {
            customs = new CustomsDeclaration
            {
                Document = declared.Document,
                Incoterm = Incoterm.Dap,
                ContentType = CustomsContentType.ReturnedGoods,
                InvoiceNumber = declared.InvoiceNumber,
                Items = declared.Items,
            };
        }
        if (problems.Count > 0)
            throw new ShippingRequestException(problems);

        var shipment = new Shipment
        {
            Id = request.IdempotencyKey is { } key ? "Shipments/" + key : null,
            Reference = outbound.Reference,
            Owner = outbound.Owner,
            Direction = ShipmentDirection.Return,
            ReturnOf = outbound.Id,
            ReturnReason = request.Reason,
            Carrier = carrier.Name,
            ServiceCode = service!.Code,
            Account = outbound.Account,
            ShipFrom = from,
            ShipTo = ShippingAddressRules.Normalize(to),
            Parcels = parcels.Select((p, i) => NewParcel(p, i + 1)).ToList(),
            Customs = customs,
            DangerousGoods = outbound.DangerousGoods,
            CreatedBy = request.CreatedBy,
        };
        // A return label is mailed to the buyer as a PDF, whatever the station prints.
        var (booked, fresh) = await BookAsync(shipment, carrier, LabelFormat.Pdf, isReturn: true, cancellationToken);
        if (fresh)
        {
            foreach (var recipient in services.GetServices<IShipmentEventRecipient>())
                await recipient.OnReturnLabelCreatedAsync(booked, cancellationToken);
        }
        return booked;
    }

    public async Task<bool> VoidAsync(string shipmentId, int? parcelNumber, string? userId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        var shipment = await session.LoadAsync<Shipment>(shipmentId, cancellationToken);
        if (shipment is null)
            return false;
        var carrier = registry.Get(shipment.Carrier);
        if (!carrier.Capabilities.VoidLabel)
            return false;

        var all = true;
        var asked = shipment.Parcels.Where(p => !p.Voided && (parcelNumber is null || p.Number == parcelNumber)).ToList();
        if (carrier.Capabilities.VoidsWholeShipment)
        {
            // The carrier cancels a whole carrier shipment (one booking): void every live parcel of it with one call,
            // and never one parcel of several, which would silently cancel its siblings.
            foreach (var group in asked.GroupBy(p => p.CarrierShipmentId ?? shipment.CarrierShipmentId))
            {
                var booked = shipment.Parcels.Where(p => !p.Voided && (p.CarrierShipmentId ?? shipment.CarrierShipmentId) == group.Key).ToList();
                var first = booked.FirstOrDefault(p => p.CarrierParcelId is not null);
                if (booked.Count != group.Count() || first is null || booked.Any(p => p.Status >= TrackingStatus.InTransit)
                    || !await carrier.VoidAsync(Reference(shipment, first), cancellationToken))
                {
                    all = false;
                    continue;
                }
                foreach (var parcel in booked)
                {
                    parcel.Voided = true;
                    parcel.Status = TrackingStatus.Cancelled;
                }
            }
        }
        else
        {
            foreach (var parcel in asked)
            {
                if (parcel.CarrierParcelId is null || parcel.Status >= TrackingStatus.InTransit
                    || !await carrier.VoidAsync(Reference(shipment, parcel), cancellationToken))
                {
                    all = false;
                    continue;
                }
                parcel.Voided = true;
                parcel.Status = TrackingStatus.Cancelled;
            }
        }
        var previous = shipment.Status;
        Recompute(shipment);
        shipment.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Shipment {Shipment}: labels voided by {User} (all asked: {All}).", shipmentId, userId, all);
        if (shipment.Status != previous)
            await NotifyTrackingAsync(shipment, previous, false, cancellationToken);
        return all;
    }

    public async Task<CarrierDocument?> GetLabelAsync(string shipmentId, int parcelNumber, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var shipment = await session.LoadAsync<Shipment>(shipmentId, cancellationToken);
        var parcel = shipment?.Parcels.FirstOrDefault(p => p.Number == parcelNumber);
        if (parcel?.LabelAttachment is not { } name)
            return null;
        return await ReadAttachmentAsync(session, shipmentId, name, parcel.LabelMediaType, parcel.LabelDpi, cancellationToken);
    }

    public async Task<CarrierDocument?> GetCustomsDocumentAsync(string shipmentId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var shipment = await session.LoadAsync<Shipment>(shipmentId, cancellationToken);
        if (shipment?.Customs?.DocumentAttachment is not { } name)
            return null;
        return await ReadAttachmentAsync(session, shipmentId, name, null, null, cancellationToken);
    }

    private static async Task<CarrierDocument?> ReadAttachmentAsync(IAsyncDocumentSession session, string id, string name, string? mediaType, int? dpi, CancellationToken cancellationToken)
    {
        using var attachment = await session.Advanced.Attachments.GetAsync(id, name, cancellationToken);
        if (attachment is null)
            return null;
        using var buffer = new MemoryStream();
        await attachment.Stream.CopyToAsync(buffer, cancellationToken);
        return new CarrierDocument(buffer.ToArray(), mediaType ?? attachment.Details.ContentType ?? CarrierDocument.Pdf, dpi);
    }

    // ---- Tracking ----------------------------------------------------------------------------------------------

    public async Task<int> RecordTrackingAsync(string carrier, IReadOnlyList<CarrierTrackingEvent> events, CancellationToken cancellationToken)
    {
        var added = 0;
        foreach (var group in events.Where(e => e.TrackingNumber is { Length: > 0 }).GroupBy(e => e.TrackingNumber, StringComparer.Ordinal))
        {
            Shipment? shipment;
            TrackingStatus previous;
            bool proofOfDispatch;
            using (var session = store.OpenAsyncSession())
            {
                session.Advanced.UseOptimisticConcurrency = true;
                var number = group.Key;
                // Values go through the LINQ provider's query parameters, never into the RQL text.
                // A carrier's first scan can arrive seconds after the label: wait (bounded) for the index to see it.
                shipment = await session.Query<Shipment>()
                    .Customize(x => x.WaitForNonStaleResults(StaleWait))
                    .Where(s => s.Carrier == carrier && s.Parcels.Any(p => p.TrackingNumber == number))
                    .FirstOrDefaultAsync(cancellationToken);
                if (shipment is null)
                {
                    logger.LogDebug("{Carrier} tracking for a parcel we did not label was dropped.", carrier);
                    continue;
                }
                previous = shipment.Status;
                var wasDispatched = shipment.Events.Any(e => e.Status is >= TrackingStatus.InTransit and not TrackingStatus.Cancelled);
                var now = time.GetUtcNow().UtcDateTime;
                var known = shipment.Events.Select(e => e.EventId).ToHashSet(StringComparer.Ordinal);
                var fresh = group.Where(e => known.Add(e.EventId)).ToList();
                if (fresh.Count == 0)
                    continue;
                shipment.Events.AddRange(fresh.Select(e => new TrackingEventRecord
                {
                    EventId = e.EventId,
                    TrackingNumber = e.TrackingNumber,
                    Status = e.Status,
                    CarrierStatus = e.CarrierStatus,
                    Description = e.Description,
                    OccurredAtUtc = e.OccurredAt.UtcDateTime,
                    ReceivedAtUtc = now,
                }));
                // Unordered at-least-once deliveries: order by event time; the latest event sets the parcel's status.
                shipment.Events = shipment.Events.OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.ReceivedAtUtc).ToList();
                var parcel = shipment.Parcels.First(p => p.TrackingNumber == number);
                if (!parcel.Voided)
                    parcel.Status = shipment.Events.Last(e => e.TrackingNumber == number).Status;
                Recompute(shipment);
                shipment.UpdatedAtUtc = now;
                await session.SaveChangesAsync(cancellationToken);
                added += fresh.Count;
                proofOfDispatch = shipment.Direction == ShipmentDirection.Return && !wasDispatched
                    && shipment.Events.Any(e => e.Status is >= TrackingStatus.InTransit and not TrackingStatus.Cancelled);
            }
            if (shipment.Status != previous || proofOfDispatch)
                await NotifyTrackingAsync(shipment, previous, proofOfDispatch, cancellationToken);
        }
        return added;
    }

    public async Task PollTrackingAsync(CancellationToken cancellationToken)
    {
        var pulled = registry.All.Where(c => c.Capabilities.TrackingPull && !c.Capabilities.TrackingWebhooks).ToList();
        if (pulled.Count == 0)
            return;
        var since = time.GetUtcNow().UtcDateTime - options.CurrentValue.TrackingPollMaxAge;
        foreach (var carrier in pulled)
        {
            var name = carrier.Name;
            List<Shipment> moving;
            using (var session = store.OpenAsyncSession())
            {
                moving = await session.Query<Shipment>()
                    .Where(s => s.Carrier == name && !s.Final && s.CreatedAtUtc >= since)
                    .Take(500)
                    .ToListAsync(cancellationToken);
            }
            foreach (var shipment in moving)
            {
                foreach (var parcel in shipment.Parcels.Where(p => !p.Voided && p.CarrierParcelId is not null && !p.Status.IsFinal()))
                {
                    try
                    {
                        var events = await carrier.GetTrackingAsync(Reference(shipment, parcel), cancellationToken);
                        if (events.Count > 0)
                            await RecordTrackingAsync(name, events, cancellationToken);
                    }
                    catch (Exception ex) when (ex is CarrierException or HttpRequestException)
                    {
                        // One parcel's failure never stops the sweep; the next run tries again.
                        logger.LogWarning(ex, "{Carrier} tracking poll failed for shipment {Shipment}.", name, shipment.Id);
                    }
                }
            }
        }
    }

    // ---- Internals ---------------------------------------------------------------------------------------------

    private static (string? Carrier, string? Service) ResolveService(CreateShipmentRequest request, SparkCommerceShippingOptions settings, List<string> problems)
    {
        if (request.Method is not { Length: > 0 } key)
        {
            if (request.ServiceCode is not { Length: > 0 })
                problems.Add("Name a configured method, or a carrier service.");
            return (request.Carrier, request.ServiceCode);
        }
        if (!settings.Methods.TryGetValue(key, out var method))
        {
            problems.Add($"No shipping method '{key}' is configured.");
            return (null, null);
        }
        if (method.Kind == ShippingMethodKind.Pickup)
        {
            problems.Add($"Method '{key}' is pickup: nothing is shipped.");
            return (null, null);
        }
        return (request.Carrier ?? method.Carrier ?? settings.Carrier, request.ServiceCode ?? method.Service);
    }

    private async Task ValidateAddressAsync(ShippingAddress address, string role, List<string> problems, CancellationToken cancellationToken)
    {
        foreach (var validator in services.GetServices<IShippingAddressValidator>())
            problems.AddRange(await validator.ValidateAsync(address, role, cancellationToken));
    }

    private static CustomsDeclaration? BuildCustoms(IShippingCarrier? carrier, CarrierService? service, ShippingAddress from, ShippingAddress to,
        IReadOnlyList<CustomsItem>? items, CustomsContentType? contentType, string? invoiceNumber, SparkCommerceShippingOptions settings, List<string> problems)
    {
        var reason = CustomsRules.Requirement(from, to, settings.Customs);
        if (reason == CustomsReason.None)
            return null;
        if (carrier is not null && !carrier.Capabilities.CustomsDocuments)
            problems.Add($"{carrier.Name} cannot create customs documents, which a shipment to {to.Country} {to.PostalCode} needs.");
        if (items is not { Count: > 0 })
        {
            problems.Add($"A shipment to {to.Country} {to.PostalCode} needs customs lines (HS code, origin, value, weight).");
            return null;
        }
        var zone = ShippingZones.Resolve(settings.Zones, to.Country);
        var declaration = new CustomsDeclaration
        {
            Incoterm = zone is { } z && settings.Zones.TryGetValue(z, out var zoneOptions) ? zoneOptions.Incoterm : Incoterm.Dap,
            ContentType = contentType ?? settings.Customs.DefaultContentType,
            InvoiceNumber = invoiceNumber,
            Items = items.Select(i => new CustomsItem
            {
                Description = i.Description?.Trim() ?? string.Empty,
                Quantity = i.Quantity,
                WeightGrams = i.WeightGrams,
                Value = i.Value,
                HsCode = (i.HsCode ?? string.Empty).Replace(".", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal),
                OriginCountry = (i.OriginCountry ?? string.Empty).Trim().ToUpperInvariant(),
                Sku = i.Sku,
            }).ToList(),
        };
        problems.AddRange(CustomsRules.Validate(declaration));
        if (declaration.ContentType == CustomsContentType.CommercialGoods && string.IsNullOrWhiteSpace(invoiceNumber))
            problems.Add("Commercial goods leaving the EU customs territory need an invoice number.");
        if (problems.Count == 0)
        {
            var currency = declaration.Items[0].Value.Currency;
            declaration.Document = CustomsRules.DocumentFor(service?.CustomsStyle ?? CustomsDocumentStyle.PostalParcel, declaration.TotalValue(currency), settings.Customs);
        }
        return declaration;
    }

    private static ShipmentParcel NewParcel(ParcelRequest parcel, int number) => new()
    {
        Number = number,
        WeightGrams = parcel.WeightGrams,
        LengthMm = parcel.LengthMm,
        WidthMm = parcel.WidthMm,
        HeightMm = parcel.HeightMm,
        Lines = parcel.Lines.ToList(),
    };

    /// <summary>
    /// Books a shipment idempotently towards the carrier. The shipment is stored first with one pending booking per
    /// carrier call (parcels go in one call when the carrier takes several, otherwise one per parcel); each booking is
    /// marked in doubt <i>before</i> its call, so an answer lost to a timeout or a crash is never booked blindly again:
    /// the next attempt looks it up at the carrier (<see cref="IShippingBookingLookup"/>). With an idempotency key, a
    /// labelled shipment of that key is answered as it is, and a pending one is resumed as it was first asked.
    /// </summary>
    private async Task<(Shipment Shipment, bool Fresh)> BookAsync(Shipment shipment, IShippingCarrier carrier, LabelFormat format, bool isReturn, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        string id;
        using (var session = store.OpenAsyncSession())
        {
            if (shipment.Id is { } key && await session.LoadAsync<Shipment>(key, cancellationToken) is { } existing)
            {
                if (!existing.HasPendingBooking)
                    return (existing, false);
                // An earlier attempt left bookings pending: resume them (the first request's content wins).
                logger.LogWarning("Shipment {Shipment} has pending bookings from an earlier attempt; resuming them.", key);
                id = key;
            }
            else
            {
                shipment.CreatedAtUtc = now;
                shipment.UpdatedAtUtc = now;
                var batches = shipment.Parcels.Count > 1 && !carrier.Capabilities.MultiParcel
                    ? shipment.Parcels.Select(p => (IReadOnlyList<ShipmentParcel>)[p]).ToList()
                    : [shipment.Parcels];
                // Ids are needed for the keys: a shipment without an idempotency key gets its id from the store first.
                if (shipment.Id is null)
                {
                    await session.StoreAsync(shipment, cancellationToken);
                    shipment.PendingBookings = batches.Select(b => NewBooking(BookingKey(shipment, b), b.Select(p => p.Number).ToList(), format)).ToList();
                    shipment.HasPendingBooking = true;
                }
                else
                {
                    shipment.PendingBookings = batches.Select(b => NewBooking(BookingKey(shipment, b), b.Select(p => p.Number).ToList(), format)).ToList();
                    shipment.HasPendingBooking = true;
                    // An empty change vector: the key's document must not exist yet, so two concurrent first calls book once.
                    await session.StoreAsync(shipment, string.Empty, shipment.Id, cancellationToken);
                }
                await session.SaveChangesAsync(cancellationToken);
                id = shipment.Id!;
            }
        }

        var labelled = await ProcessBookingsAsync(id, carrier, lookupOnly: false, cancellationToken);
        return (labelled.Shipment, labelled.FirstLabels);
    }

    /// <summary>
    /// Works through a shipment's pending bookings. An in-doubt booking is looked up first: found, its labels are
    /// stored; not found, it is booked (unless <paramref name="lookupOnly"/>, or it is younger than
    /// <see cref="SparkCommerceShippingOptions.BookingReconcileAfter"/> and may still be in flight); not answerable,
    /// <see cref="ShipmentBookingInDoubtException"/>. A booking is saved as in doubt before the carrier is called; a
    /// refusal (4xx) is saved as refused and rethrown, and the next attempt sends a fresh reference.
    /// </summary>
    private async Task<(Shipment Shipment, bool FirstLabels, int Resolved)> ProcessBookingsAsync(string shipmentId, IShippingCarrier carrier, bool lookupOnly, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        var shipment = await session.LoadAsync<Shipment>(shipmentId, cancellationToken)
            ?? throw new ShippingRequestException([$"No shipment '{shipmentId}'."]);
        var hadLabels = shipment.Parcels.Any(p => p.LabelAttachment is not null);
        var settleBefore = time.GetUtcNow().UtcDateTime - options.CurrentValue.BookingReconcileAfter;
        var resolved = 0;

        foreach (var booking in shipment.PendingBookings.ToList())
        {
            var request = ToCarrierRequest(shipment, booking);
            CarrierShipmentResult? result = null;
            if (booking.State == PendingBookingState.InDoubt)
            {
                var lookup = carrier is IShippingBookingLookup finder
                    ? await finder.FindBookingAsync(request, cancellationToken)
                    : CarrierBookingLookup.Unsupported;
                switch (lookup.Status)
                {
                    case CarrierBookingLookupStatus.Found:
                        logger.LogInformation("Shipment {Shipment}: {Carrier} already held booking {Reference}; its labels are stored, nothing is booked again.",
                            shipmentId, carrier.Name, booking.CarrierReference);
                        result = lookup.Result;
                        break;
                    case CarrierBookingLookupStatus.NotFound when booking.LastAttemptAtUtc is { } last && last <= settleBefore:
                        booking.State = PendingBookingState.NotAtCarrier;
                        resolved++;
                        await session.SaveChangesAsync(cancellationToken);
                        break;
                    case CarrierBookingLookupStatus.NotFound:
                        // Not found yet, but the earlier call may still be in flight: booking now could book twice.
                        if (lookupOnly)
                            continue;
                        throw new ShippingRequestException([$"Shipment {shipmentId}: an earlier {carrier.Name} booking is still in progress; try again in a few minutes."]);
                    default:
                        if (lookupOnly)
                            continue;
                        throw new ShipmentBookingInDoubtException(shipmentId, carrier.Name);
                }
            }

            if (result is null)
            {
                if (lookupOnly)
                    continue;
                // Recorded before the call: a lost answer leaves the booking in doubt, never forgotten.
                booking.State = PendingBookingState.InDoubt;
                booking.Attempts++;
                booking.LastAttemptAtUtc = time.GetUtcNow().UtcDateTime;
                booking.Error = null;
                shipment.UpdatedAtUtc = booking.LastAttemptAtUtc.Value;
                await session.SaveChangesAsync(cancellationToken);
                try
                {
                    result = await carrier.CreateShipmentAsync(request, cancellationToken);
                }
                catch (CarrierException ex) when (IsRefusal(ex))
                {
                    booking.State = PendingBookingState.Refused;
                    booking.Generation++;
                    booking.Error = ex.Message;
                    await session.SaveChangesAsync(CancellationToken.None);
                    throw;
                }
            }
            else
            {
                resolved++;
            }

            var targets = booking.Parcels.Select(n => shipment.Parcels.First(p => p.Number == n)).ToList();
            ApplyResult(session, shipment, targets, result!);
            shipment.CarrierShipmentId ??= result!.CarrierShipmentId;
            shipment.PendingBookings.Remove(booking);
            shipment.HasPendingBooking = shipment.PendingBookings.Count > 0;
            Recompute(shipment);
            shipment.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
            await session.SaveChangesAsync(cancellationToken);
        }
        return (shipment, !hadLabels && !shipment.HasPendingBooking && shipment.Parcels.Any(p => p.LabelAttachment is not null), resolved);
    }

    public async Task<int> ReconcilePendingBookingsAsync(CancellationToken cancellationToken)
    {
        var settleBefore = time.GetUtcNow().UtcDateTime - options.CurrentValue.BookingReconcileAfter;
        List<Shipment> pending;
        using (var session = store.OpenAsyncSession())
        {
            pending = await session.Query<Shipment>()
                .Where(s => s.HasPendingBooking)
                .Take(200)
                .ToListAsync(cancellationToken);
        }
        var resolved = 0;
        foreach (var shipment in pending)
        {
            if (!shipment.PendingBookings.Any(b => b.State == PendingBookingState.InDoubt && b.LastAttemptAtUtc <= settleBefore))
                continue;
            if (registry.Find(shipment.Carrier) is not { } carrier)
                continue;
            try
            {
                var outcome = await ProcessBookingsAsync(shipment.Id!, carrier, lookupOnly: true, cancellationToken);
                resolved += outcome.Resolved;
                if (outcome.FirstLabels)
                    await NotifyLabelledAsync(outcome.Shipment, cancellationToken);
            }
            catch (Exception ex) when (ex is CarrierException or HttpRequestException or Raven.Client.Exceptions.ConcurrencyException)
            {
                // One shipment's failure never stops the sweep; the next run tries again.
                logger.LogWarning(ex, "Booking reconciliation failed for shipment {Shipment}.", shipment.Id);
            }
        }
        return resolved;
    }

    public async Task<bool> ReleasePendingBookingAsync(string shipmentId, string? userId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        var shipment = await session.LoadAsync<Shipment>(shipmentId, cancellationToken);
        var inDoubt = shipment?.PendingBookings.Where(b => b.State == PendingBookingState.InDoubt).ToList() ?? [];
        if (inDoubt.Count == 0)
            return false;
        foreach (var booking in inDoubt)
            booking.State = PendingBookingState.NotAtCarrier;
        shipment!.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        await session.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Shipment {Shipment}: {Count} in-doubt booking(s) released by {User}; the next attempt books again.", shipmentId, inDoubt.Count, userId);
        return true;
    }

    private async Task NotifyLabelledAsync(Shipment shipment, CancellationToken cancellationToken)
    {
        foreach (var recipient in services.GetServices<IShipmentEventRecipient>())
        {
            if (shipment.Direction == ShipmentDirection.Return)
                await recipient.OnReturnLabelCreatedAsync(shipment, cancellationToken);
            else
                await recipient.OnShipmentLabelledAsync(shipment, cancellationToken);
        }
    }

    /// <summary>
    /// A carrier's 4xx refusal booked nothing (except a timeout, conflict, too-early or rate limit, whose outcome is not
    /// certain). Any other failure (5xx, a network error, an unreadable answer) leaves the booking in doubt.
    /// </summary>
    internal static bool IsRefusal(CarrierException exception)
        => exception.StatusCode is >= 400 and < 500 and not (408 or 409 or 425 or 429);

    private PendingBooking NewBooking(string key, List<int> parcels, LabelFormat format) => new()
    {
        Key = key,
        Parcels = parcels,
        LabelFormat = format,
        State = PendingBookingState.New,
        CreatedAtUtc = time.GetUtcNow().UtcDateTime,
    };

    /// <summary>
    /// Unique per booking: a booking of part of the shipment (one parcel at a time, or an added parcel) gets the first
    /// parcel's number, so carriers that key on our reference (bpost's order reference, Sendcloud's external reference)
    /// never see it twice.
    /// </summary>
    private static string BookingKey(Shipment shipment, IReadOnlyList<ShipmentParcel> parcels)
        => parcels.Count == shipment.Parcels.Count ? shipment.Id! : $"{shipment.Id}/{parcels[0].Number}";

    private static CarrierParcelReference Reference(Shipment shipment, ShipmentParcel parcel)
        => new(parcel.CarrierParcelId!, parcel.TrackingNumber ?? string.Empty, shipment.Account, shipment.ShipTo.PostalCode, shipment.ShipTo.Country,
            parcel.CarrierShipmentId ?? shipment.CarrierShipmentId);

    private static void ApplyResult(IAsyncDocumentSession session, Shipment shipment, IReadOnlyList<ShipmentParcel> parcels, CarrierShipmentResult result)
    {
        if (result.Parcels.Count != parcels.Count)
            throw new CarrierException(shipment.Carrier, $"answered {result.Parcels.Count} labels for {parcels.Count} parcels.");
        for (var i = 0; i < parcels.Count; i++)
        {
            var parcel = parcels[i];
            var labelled = result.Parcels[i];
            parcel.CarrierParcelId = labelled.CarrierParcelId;
            parcel.CarrierShipmentId = result.CarrierShipmentId;
            parcel.TrackingNumber = labelled.TrackingNumber;
            parcel.TrackingUrl = labelled.TrackingUrl;
            parcel.LabelMediaType = labelled.Label.MediaType;
            parcel.LabelDpi = labelled.Label.Dpi;
            parcel.LabelAttachment = $"label-{parcel.Number}{Extension(labelled.Label.MediaType)}";
            parcel.Status = TrackingStatus.Labelled;
            session.Advanced.Attachments.Store(shipment, parcel.LabelAttachment, new MemoryStream(labelled.Label.Content), labelled.Label.MediaType);
        }
        if (result.CustomsDocument is { } document && shipment.Customs is { } customs && customs.DocumentAttachment is null)
        {
            customs.DocumentAttachment = "customs" + Extension(document.MediaType);
            session.Advanced.Attachments.Store(shipment, customs.DocumentAttachment, new MemoryStream(document.Content), document.MediaType);
        }
    }

    private static string Extension(string mediaType) => mediaType switch
    {
        CarrierDocument.Zpl => ".zpl",
        CarrierDocument.Png => ".png",
        _ => ".pdf",
    };

    private static CarrierShipmentRequest ToCarrierRequest(Shipment shipment, PendingBooking booking) => new()
    {
        Reference = shipment.Reference,
        ShipmentId = booking.CarrierReference,
        ServiceCode = shipment.ServiceCode,
        ShipFrom = shipment.ShipFrom,
        ShipTo = shipment.ShipTo,
        ServicePointId = shipment.ServicePoint?.Id,
        ServicePoint = shipment.ServicePoint,
        Parcels = booking.Parcels
            .Select(n => shipment.Parcels.First(p => p.Number == n))
            .Select(p => new ParcelSpec(p.WeightGrams, p.LengthMm, p.WidthMm, p.HeightMm))
            .ToList(),
        IsReturn = shipment.Direction == ShipmentDirection.Return,
        Customs = shipment.Customs,
        DangerousGoods = shipment.DangerousGoods,
        LabelFormat = booking.LabelFormat,
        Account = shipment.Account,
    };

    /// <summary>The shipment's status is the least advanced live parcel's; all voided is Cancelled.</summary>
    private static void Recompute(Shipment shipment)
    {
        var live = shipment.Parcels.Where(p => !p.Voided).ToList();
        shipment.Status = live.Count == 0 ? TrackingStatus.Cancelled : live.Min(p => p.Status);
        shipment.Final = live.Count == 0 || live.All(p => p.Status.IsFinal());
    }

    private async Task NotifyTrackingAsync(Shipment shipment, TrackingStatus previous, bool proofOfDispatch, CancellationToken cancellationToken)
    {
        var notice = new ShipmentTrackingNotice(shipment, previous, proofOfDispatch);
        foreach (var recipient in services.GetServices<IShipmentEventRecipient>())
            await recipient.OnTrackingChangedAsync(notice, cancellationToken);
    }

    [GeneratedRegex("^[A-Za-z0-9_/-]{1,120}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdempotencyKeyPattern();
}
