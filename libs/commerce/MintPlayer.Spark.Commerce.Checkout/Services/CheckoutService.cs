using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Consent;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Checkout.Contracts;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Payments;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Checkout.Services;

internal sealed class CheckoutService(
    IServiceProvider services,
    IDocumentStore store,
    BasketStore baskets,
    ICheckoutBuyer buyer,
    CheckoutItems items,
    CheckoutAdmission admission,
    CheckoutPricing pricing,
    CheckoutInsurance insurance,
    OrderLifecycle lifecycle,
    OrderEvents events,
    CheckoutMails mails,
    ISparkInventory inventory,
    ISparkPayments payments,
    ISparkSellers sellers,
    ISparkLegalDocuments legal,
    ICommerceAudit audit,
    IOptionsMonitor<SparkCommerceOptions> commerce,
    IOptionsMonitor<SparkCommerceCheckoutOptions> options,
    IOptionsMonitor<SparkCommerceInventoryOptions> inventoryOptions,
    TimeProvider clock,
    IEnumerable<ICheckoutPrePaymentCheck> prePaymentChecks) : ISparkCheckout
{
    /// <summary>The pay button's translation key (CRD Art. 8(2): "order with obligation to pay" or an equally unambiguous text).</summary>
    public const string OrderButtonKey = "commerceCheckout.orderWithObligationToPay";

    private DateTimeOffset Now => clock.GetUtcNow();

    private CommerceChannelOptions Channel(string name, string? lineId)
    {
        try { return commerce.CurrentValue.GetChannel(name); }
        catch (KeyNotFoundException) { throw CheckoutException.Of(CheckoutRefusals.UnknownChannel, lineId, name); }
    }

    private BuyerIdentifiers Identifiers(string? email = null) => new(AccountId: buyer.UserId, VisitorId: buyer.UserId is null ? buyer.VisitorId : null, Email: email);

    /// <summary>The identifiers a line's limits count against: the buyer's, plus the guest visitor a merged line was chosen under (D16).</summary>
    private BuyerIdentifiers LineIdentifiers(CheckoutBasketLine? line, string? email = null)
        => Identifiers(email) is var own && own.VisitorId is null && line?.LimitVisitorId is { Length: > 0 } visitor ? own with { VisitorId = visitor } : own;

    // ---- basket ---------------------------------------------------------------------------------------------------

    public async Task<SparkBasket> GetBasketAsync(CancellationToken cancellationToken = default)
        => await ViewAsync(await baskets.LoadCurrentAsync(cancellationToken), cancellationToken);

    public async Task<SparkBasket> AddLineAsync(SparkBasketAddRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var basket = await baskets.UpdateCurrentAsync(async basket =>
        {
            if (basket.PendingOrderId is not null)
                throw CheckoutException.Of(CheckoutRefusals.PendingOrder);

            var parent = request.ParentLineId is { } parentId ? basket.Lines.FirstOrDefault(l => l.LineId == parentId) : null;
            var item = await items.ResolveAsync(new CheckoutItemRequest(request.ItemId, request.Quantity, request.Units,
                Options: request.Options, ParentItemId: parent?.ItemId), null, cancellationToken);
            var channel = Channel(item.Channel, null);
            await CheckFitsAsync(basket, item, channel, parent, null, cancellationToken);

            // The same item again (no seats, no options, no parent) raises the quantity of its line.
            var same = item.HasNumberedUnits || parent is not null ? null : basket.Lines.FirstOrDefault(l => l.ItemId == item.ItemId
                && l.ParentLineId is null && l.Units.Count == 0 && l.Picks.Count == 0
                && l.Options.Count == (request.Options?.Count ?? 0) && (request.Options ?? new Dictionary<string, string>()).All(o => l.Options.TryGetValue(o.Key, out var v) && v == o.Value));
            if (same is not null)
            {
                await ChangeQuantityAsync(basket, same, same.Quantity + request.Quantity, cancellationToken);
                return;
            }

            var line = new CheckoutBasketLine
            {
                LineId = Guid.NewGuid().ToString("N"),
                ItemId = item.ItemId,
                Channel = item.Channel,
                SellerOrganizationId = item.OrganizationId,
                Quantity = request.Quantity,
                Units = request.Units?.ToList() ?? [],
                Picks = item.Capacity.Picks.ToList(),
                Options = request.Options is null ? new(StringComparer.Ordinal) : new(request.Options, StringComparer.Ordinal),
                ParentLineId = parent?.LineId,
                // Only the tokens of the item's own scopes are kept.
                AdmissionTokens = new(CheckoutAdmission.Merge(null, request.AdmissionTokens)
                    .Where(t => item.AdmissionScopes.Contains(t.Key, StringComparer.Ordinal)), StringComparer.Ordinal),
                AddedAt = Now,
            };
            if (channel.Allocation == AllocationMode.HoldFromSelection && !item.Capacity.IsEmpty)
            {
                await admission.RequireAsync(item, line.AdmissionTokens, request.Quantity, consume: true, line.LineId, cancellationToken);
                await HoldAsync(basket, line, item, channel, cancellationToken);
            }
            else
            {
                // Early feedback only: the admission is used when the line is claimed (at "Pay" or at payment).
                await admission.RequireAsync(item, line.AdmissionTokens, request.Quantity, consume: false, line.LineId, cancellationToken);
            }
            basket.Currency ??= item.Price.Currency;
            basket.Lines.Add(line);
        }, cancellationToken);
        return await ViewAsync(basket, cancellationToken);
    }

    public async Task<SparkBasket> SetQuantityAsync(string lineId, int quantity, CancellationToken cancellationToken = default)
    {
        var basket = await baskets.UpdateCurrentAsync(async basket =>
        {
            if (basket.PendingOrderId is not null)
                throw CheckoutException.Of(CheckoutRefusals.PendingOrder);
            var line = basket.Lines.FirstOrDefault(l => l.LineId == lineId) ?? throw CheckoutException.Of(CheckoutRefusals.LineNotFound, lineId);
            if (quantity <= 0)
            {
                foreach (var removed in basket.Lines.Where(l => l == line || l.ParentLineId == line.LineId).ToList())
                {
                    if (removed.ClaimId is { } claimId)
                        await inventory.ReleaseAsync(claimId, ReturnReason.Cancelled, cancellationToken: cancellationToken);
                    basket.Lines.Remove(removed);
                }
                if (basket.Lines.Count == 0)
                    basket.Currency = null;
                return;
            }
            await ChangeQuantityAsync(basket, line, quantity, cancellationToken);
        }, cancellationToken);
        return await ViewAsync(basket, cancellationToken);
    }

    public async Task<SparkBasket> SetChoicesAsync(IReadOnlyDictionary<string, string?> choices, string? email, CancellationToken cancellationToken = default)
    {
        var basket = await baskets.UpdateCurrentAsync(basket =>
        {
            foreach (var (key, value) in choices)
            {
                if (string.IsNullOrWhiteSpace(value))
                    basket.Choices.Remove(key);
                else
                    basket.Choices[key] = value.Trim();
            }
            if (email is not null)
                basket.Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
            return Task.CompletedTask;
        }, cancellationToken);
        return await ViewAsync(basket, cancellationToken);
    }

    private async Task ChangeQuantityAsync(CheckoutBasket basket, CheckoutBasketLine line, int quantity, CancellationToken cancellationToken)
    {
        var parentItem = line.ParentLineId is { } p ? basket.Lines.FirstOrDefault(l => l.LineId == p)?.ItemId : null;
        var previous = line.Quantity;
        line.Quantity = quantity;
        var item = await items.ResolveAsync(line, parentItem, cancellationToken);
        var channel = Channel(item.Channel, line.LineId);
        if (line.ClaimId is { } claimId)
        {
            // GA10/D2: a quantity change never restarts a live hold. The line keeps its deadline and its first HeldSince,
            // so Hold:Max bounds the total hold time however often the quantity changes; extra units are claimed under
            // the same deadline. Only a lapsed hold is taken afresh.
            var (expiresAt, heldSince) = HoldAfterQuantityChange(line, Now, channel.Hold.Clamp(item.HoldTime));
            var result = await inventory.AdjustAsync(OrderClaims.Request(claimId, basket.Id!, expiresAt, CheckoutCapacitySnapshot.From(item.Capacity),
                OrderClaims.Limits(item, channel, quantity, LineIdentifiers(line))), cancellationToken);
            if (!result.Succeeded)
            {
                line.Quantity = previous;
                throw OrderClaims.Refusal(result, line.LineId);
            }
            line.HeldUntil = expiresAt;
            line.HeldSince = heldSince;
            line.HeldUnits = result.Units?.ToList() ?? [];
        }
        else if (channel.Allocation == AllocationMode.HoldFromSelection && !item.Capacity.IsEmpty)
        {
            await admission.RequireAsync(item, line.AdmissionTokens, quantity, consume: true, line.LineId, cancellationToken);
            await HoldAsync(basket, line, item, channel, cancellationToken);
        }
    }

    /// <summary>
    /// The deadline and hold start after a quantity change (pure, for the unit test): a live hold keeps both (its
    /// first <c>HeldSince</c>, falling back to <c>AddedAt</c> for lines held before the field existed); a lapsed or
    /// missing one starts now for <paramref name="holdTime"/>.
    /// </summary>
    internal static (DateTimeOffset ExpiresAt, DateTimeOffset HeldSince) HoldAfterQuantityChange(CheckoutBasketLine line, DateTimeOffset now, TimeSpan holdTime)
        => line.HeldUntil is { } until && until > now
            ? (until, line.HeldSince ?? line.AddedAt)
            : (now + holdTime, now);

    private async Task HoldAsync(CheckoutBasket basket, CheckoutBasketLine line, CheckoutItem item, CommerceChannelOptions channel, CancellationToken cancellationToken)
    {
        var claimId = OrderClaims.ClaimIdFor(line.LineId);
        var expiresAt = Now + channel.Hold.Clamp(item.HoldTime);
        var result = await inventory.TryClaimAsync(OrderClaims.Request(claimId, basket.Id!, expiresAt, CheckoutCapacitySnapshot.From(item.Capacity),
            OrderClaims.Limits(item, channel, line.Quantity, LineIdentifiers(line))), cancellationToken);
        if (!result.Succeeded)
            throw OrderClaims.Refusal(result, line.LineId);
        line.ClaimId = claimId;
        line.HeldUntil = expiresAt;
        line.HeldSince = Now;
        line.HeldUnits = result.Units?.ToList() ?? [];
    }

    /// <summary>
    /// GA10 (WCAG 2.2.1 "Extend"): one "More time" moves every live hold of the basket on by <c>Hold:Extension</c>, each
    /// capped at its channel's <c>Hold:Max</c> from when it was taken. A lapsed hold is not revived here (re-adding or
    /// "Pay" re-claims it); a line already at its cap stays as it is. All or nothing per line: a claim the engine refuses
    /// to extend keeps its old deadline and the refusal is answered.
    /// </summary>
    public async Task<SparkBasket> ExtendHoldAsync(CancellationToken cancellationToken = default)
    {
        var basket = await baskets.UpdateCurrentAsync(async basket =>
        {
            if (basket.PendingOrderId is not null)
                throw CheckoutException.Of(CheckoutRefusals.PendingOrder);
            var now = Now;
            var extended = false;
            foreach (var line in basket.Lines)
            {
                if (line.ClaimId is not { } claimId || line.HeldUntil is not { } until || until <= now)
                    continue;
                var channel = Channel(line.Channel, line.LineId);
                if (channel.Hold.Extend(line.HeldSince ?? line.AddedAt, until) is not { } next)
                    continue;
                var result = await inventory.ExtendAsync(claimId, next, cancellationToken);
                if (!result.Succeeded)
                    throw OrderClaims.Refusal(result, line.LineId);
                line.HeldUntil = next;
                extended = true;
            }
            if (!extended)
                throw CheckoutException.Of(CheckoutRefusals.HoldNotExtendable);
        }, cancellationToken);
        return await ViewAsync(basket, cancellationToken);
    }

    /// <summary>The deadline the earliest live hold would get from one "More time"; null when none can move (GA10).</summary>
    private DateTimeOffset? NextExtension(CheckoutBasket basket, DateTimeOffset now)
    {
        DateTimeOffset? earliest = null;
        foreach (var line in basket.Lines)
        {
            if (line.ClaimId is null || line.HeldUntil is not { } until || until <= now)
                continue;
            CommerceChannelOptions channel;
            try { channel = commerce.CurrentValue.GetChannel(line.Channel); }
            catch (KeyNotFoundException) { continue; }
            if (channel.Hold.Extend(line.HeldSince ?? line.AddedAt, until) is { } next && (earliest is null || next < earliest))
                earliest = next;
        }
        return earliest;
    }

    /// <summary>
    /// D2/D40/D41: channel purity unless <c>Basket:MixChannels</c>; one seller unless mixed-seller orders are enabled
    /// (ConnectedSellers + <c>MixSellers</c> + <c>AcknowledgeSplitMerchantOfRecord</c>); one currency; D4 sign-in; the
    /// seller gate (D21, D43); GB2 add-ons.
    /// </summary>
    private async Task CheckFitsAsync(CheckoutBasket basket, CheckoutItem item, CommerceChannelOptions channel, CheckoutBasketLine? parent, string? lineId, CancellationToken cancellationToken)
    {
        var settings = commerce.CurrentValue;
        var others = basket.Lines.Where(l => l.LineId != lineId).ToList();
        if (!settings.Basket.MixChannels && others.Any(l => !string.Equals(l.Channel, item.Channel, StringComparison.OrdinalIgnoreCase)))
            throw CheckoutException.Of(CheckoutRefusals.ChannelMismatch, lineId, item.Channel);
        if (others.Any(l => l.SellerOrganizationId != item.OrganizationId) && !MixedSellersAllowed(settings))
            throw CheckoutException.Of(CheckoutRefusals.SellerMismatch, lineId);
        if (basket.Currency is { } currency && others.Count > 0 && currency != item.Price.Currency)
            throw CheckoutException.Of(CheckoutRefusals.CurrencyMismatch, lineId);
        if (channel.RequireSignIn && buyer.UserId is null)
            throw CheckoutException.Of(CheckoutRefusals.SignInRequired, lineId, item.Channel);
        if (item.RequiresParentItemIds.Count > 0 && (parent is null || !item.RequiresParentItemIds.Contains(parent.ItemId)))
            throw CheckoutException.Of(CheckoutRefusals.ParentRequired, lineId, item.ItemId);
        var gate = await sellers.CanSellAsync(item.OrganizationId, item.Channel, cancellationToken);
        if (!gate.Allowed)
            throw CheckoutException.Of(CheckoutRefusals.SellerCannotSell, lineId, gate.Reasons.FirstOrDefault());
    }

    /// <summary>D41: mixed-seller orders need the host's opt-in; in Platform mode the platform is the only merchant anyway.</summary>
    internal static bool MixedSellersAllowed(SparkCommerceOptions settings)
        => settings.Merchant == MerchantMode.Platform
            || (settings.Basket.MixSellers && settings.Basket.AcknowledgeSplitMerchantOfRecord);

    private static bool NeedsSplit(SparkCommerceOptions settings, IEnumerable<string?> lineSellers)
        => settings.Merchant == MerchantMode.ConnectedSellers && lineSellers.Distinct().Count() > 1;

    private async Task<SparkBasket> ViewAsync(CheckoutBasket? basket, CancellationToken cancellationToken)
    {
        var choices = new Dictionary<string, string>(basket?.Choices ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (basket is null || basket.Lines.Count == 0)
            return new SparkBasket([], null, null, null, false, choices, null, null);

        var now = Now;
        var grace = inventoryOptions.CurrentValue.Grace;
        var resolved = new Dictionary<string, CheckoutItem>();
        foreach (var line in basket.Lines)
        {
            try { resolved[line.LineId] = await items.ResolveAsync(line, ParentItem(basket, line), cancellationToken); }
            catch (CheckoutException) { /* no longer on sale: shown without a price */ }
        }
        CheckoutPricingContext? priced = null;
        if (resolved.Count == basket.Lines.Count)
        {
            try
            {
                priced = await pricing.PriceAsync(basket.Lines.Select(l => new PricingInput(l.LineId, resolved[l.LineId], l.Quantity)).ToList(),
                    basket.Choices, buyer.UserId, placing: false, cancellationToken, basket.Email, buyer.VisitorId);
            }
            catch (CheckoutException) { priced = null; }
        }

        string? pendingNumber = null;
        if (basket.PendingOrderId is { } pending)
        {
            using var session = store.OpenAsyncSession();
            pendingNumber = (await session.LoadAsync<CheckoutOrder>(pending, cancellationToken))?.Number;
        }

        var lines = basket.Lines.Select(l =>
        {
            resolved.TryGetValue(l.LineId, out var item);
            var p = priced?.Lines.FirstOrDefault(x => x.LineId == l.LineId);
            return new SparkBasketLine(l.LineId, l.ItemId, item?.Title, l.Channel, l.SellerOrganizationId, l.Quantity,
                l.HeldUnits.Count > 0 ? l.HeldUnits : l.Units, l.HeldUntil, l.HeldUntil is { } held && held + grace <= now,
                item?.MinimumAge, l.ParentLineId, p?.Price.UnitTotal, p?.Total);
        }).ToList();
        var earliest = basket.Lines.Where(l => l.HeldUntil is { } h && h + grace > now).Select(l => l.HeldUntil).Min();
        return new SparkBasket(lines, basket.Currency, priced?.Total, earliest,
            basket.Lines.Select(l => l.Channel).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1, choices, pendingNumber,
            basket.PendingOrderId is null ? NextExtension(basket, now) : null);
    }

    private static string? ParentItem(CheckoutBasket basket, CheckoutBasketLine line)
        => line.ParentLineId is { } p ? basket.Lines.FirstOrDefault(l => l.LineId == p)?.ItemId : null;

    // ---- the last step (GA2) ------------------------------------------------------------------------------------

    private sealed record Prepared(CheckoutBasket Basket, List<(CheckoutBasketLine Line, CheckoutItem Item, CommerceChannelOptions Channel)> Lines, CheckoutPricingContext Priced);

    private async Task<Prepared> PrepareAsync(CheckoutBasket? basket, bool placing, CancellationToken cancellationToken, string? guestEmail = null)
    {
        if (basket is null || basket.Lines.Count == 0)
            throw CheckoutException.Of(CheckoutRefusals.BasketEmpty);
        var lines = new List<(CheckoutBasketLine, CheckoutItem, CommerceChannelOptions)>();
        foreach (var line in basket.Lines)
        {
            var item = await items.ResolveAsync(line, ParentItem(basket, line), cancellationToken);
            var channel = Channel(item.Channel, line.LineId);
            var parent = line.ParentLineId is { } p ? basket.Lines.FirstOrDefault(l => l.LineId == p) : null;
            await CheckFitsAsync(basket, item, channel, parent, line.LineId, cancellationToken);
            lines.Add((line, item, channel));
        }
        var priced = await pricing.PriceAsync(lines.Select(l => new PricingInput(l.Item1.LineId, l.Item2, l.Item1.Quantity)).ToList(),
            basket.Choices, buyer.UserId, placing, cancellationToken, guestEmail ?? basket.Email, buyer.VisitorId);
        return new Prepared(basket, lines, priced);
    }

    /// <summary>D3/D40: the methods every held line allows, capped by the shortest payment window; split-capable for mixed sellers (D41).</summary>
    private (IReadOnlyList<PaymentMethodInfo> Methods, bool Held, TimeSpan Window) Methods(Prepared prepared)
    {
        var held = prepared.Lines.Where(l => l.Channel.Allocation != AllocationMode.OnPayment).ToList();
        var window = (held.Count > 0 ? held : prepared.Lines).Min(l => l.Channel.MaxPaymentWindow);
        var excluded = prepared.Lines.SelectMany(l => options.CurrentValue.Channel(l.Item.Channel).ExcludedMethods).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var query = new PaymentMethodQuery
        {
            Held = held.Count > 0,
            MaxPaymentWindow = held.Count > 0 ? window : null,
            RequireSplit = NeedsSplit(commerce.CurrentValue, prepared.Lines.Select(l => l.Item.OrganizationId)),
            Excluded = excluded,
        };
        return (payments.GetOfferableMethods(query), held.Count > 0, window);
    }

    /// <summary>GA2: the required and linked documents in force for every seller of the basket (the platform's for its own lines).</summary>
    private async Task<(List<(LegalDocumentRef Ref, bool MustAccept)> Documents, List<string> Missing)> DocumentsAsync(Prepared prepared, CancellationToken cancellationToken)
    {
        var required = options.CurrentValue.RequiredAcceptance;
        var linked = options.CurrentValue.LinkedDocuments;
        var result = new List<(LegalDocumentRef, bool)>();
        var missing = new List<string>();
        foreach (var seller in prepared.Lines.Select(l => l.Item.OrganizationId).Distinct())
        {
            var inForce = await legal.GetInForceAsync(seller, Now, cancellationToken);
            foreach (var kind in required.Where(k => !inForce.Any(d => d.Kind == k)))
                missing.Add(kind);
            foreach (var document in inForce.Where(d => required.Contains(d.Kind) || linked.Contains(d.Kind)))
                if (!result.Any(r => r.Item1.Id == document.Id))
                    result.Add((document, required.Contains(document.Kind)));
        }
        return (result, missing);
    }

    public async Task<SparkCheckoutSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var basket = await baskets.LoadCurrentAsync(cancellationToken);
        if (basket?.PendingOrderId is not null)
            throw CheckoutException.Of(CheckoutRefusals.PendingOrder);
        var prepared = await PrepareAsync(basket, placing: false, cancellationToken);
        var (methods, held, window) = Methods(prepared);
        var (documents, _) = await DocumentsAsync(prepared, cancellationToken);

        using var session = store.OpenAsyncSession();
        var loaded = documents.Count == 0 ? new Dictionary<string, Commerce.Models.LegalDocument>()
            : await session.LoadAsync<Commerce.Models.LegalDocument>(documents.Select(d => d.Ref.Id), cancellationToken);

        var donation = options.CurrentValue.Donation;
        SparkDonationOffer? donationOffer = donation.Enabled
            ? new SparkDonationOffer(donation.Beneficiary, donation.FixedAmounts.TryGetValue(prepared.Priced.Currency, out var amounts) ? amounts : [], donation.RoundUp)
            : null;
        SparkInsuranceOffer? insuranceOffer = null;
        if (await insurance.QuoteAsync(prepared.Priced, cancellationToken) is { } quote)
            insuranceOffer = new SparkInsuranceOffer(quote.Provider, quote.Premium, quote.InsuredAmount, quote.TermsUrl, quote.Description);

        var grace = inventoryOptions.CurrentValue.Grace;
        return new SparkCheckoutSummary(
            prepared.Priced.Lines.Select(ToView).ToList(),
            prepared.Priced.Extras.Select(e => new SparkCheckoutAmount(e.Code, e.Kind.ToString(), e.Amount, e.LabelKey)).ToList(),
            prepared.Priced.Total,
            prepared.Lines.Select(l => l.Item.OrganizationId).Distinct().ToList(),
            documents.Select(d => new SparkCheckoutDocument(d.Ref.Id, d.Ref.Kind, d.Ref.OrganizationId, d.Ref.Version, d.Ref.EffectiveFrom,
                loaded.TryGetValue(d.Ref.Id, out var doc) ? doc?.Title : null, doc?.HasPdf ?? false, d.MustAccept)).ToList(),
            prepared.Lines.Max(l => l.Item.MinimumAge),
            prepared.Lines.Any(l => l.Channel.RequireSignIn),
            buyer.UserId is not null,
            methods.Select(m => new SparkCheckoutMethod(m.Id, m.Provider)).ToList(),
            held ? (int)Math.Ceiling(window.TotalMinutes) : null,
            prepared.Basket.Lines.Where(l => l.HeldUntil is { } h && h + grace > Now).Select(l => l.HeldUntil).Min(),
            OrderButtonKey,
            donationOffer,
            insuranceOffer,
            services.GetService<ISparkConsent>() is not null,
            buyer.UserId is null);
    }

    private static SparkCheckoutLine ToView(PricedLine line) => new(
        line.LineId, line.Item.ItemId, line.Item.Title, line.Item.Description, line.Item.Channel, line.Item.OrganizationId, line.Quantity,
        line.Item.Capacity.Units, line.Item.Price, line.Net ?? line.Price.Net, line.Vat ?? line.Price.Vat,
        line.Price.MandatoryFees.Select(f => new SparkCheckoutAmount(f.Code, f.Kind.ToString(), f.Amount, f.LabelKey)).ToList(),
        line.Adjustments.Select(a => new SparkCheckoutAmount(a.Code, a.Source, a.Amount, a.LabelKey)).ToList(),
        line.Total, line.Item.MinimumAge, line.Item.ServiceDate);

    // ---- placing the order ----------------------------------------------------------------------------------------

    public async Task<SparkPlaceOrderResult> PlaceOrderAsync(SparkPlaceOrderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var basket = await baskets.LoadCurrentAsync(cancellationToken);
        if (basket?.PendingOrderId is not null)
            throw CheckoutException.Of(CheckoutRefusals.PendingOrder);
        // A guest's address is known before pricing, so once-per-buyer codes hold per address (the order stores the same).
        var prepared = await PrepareAsync(basket, placing: true, cancellationToken, request.Email?.Trim() is { Length: > 0 } guestEmail ? guestEmail : null);
        var now = Now;
        var settings = commerce.CurrentValue;

        // The buyer.
        string? email = request.Email?.Trim() is { Length: > 0 } given ? given : basket!.Email;
        string? name = request.Name?.Trim() is { Length: > 0 } n ? n : null;
        if (buyer.UserId is { } userId)
        {
            using var users = store.OpenAsyncSession();
            if (await users.LoadAsync<SparkUser>(userId, cancellationToken) is { } user)
            {
                email ??= user.Email;
                name ??= user.UserName;
            }
        }
        if (email is null || !email.Contains('@'))
            throw CheckoutException.Of(CheckoutRefusals.EmailRequired);

        // GA2: every required document in force accepted, by id (a new version shown later must be accepted again).
        var (documents, missing) = await DocumentsAsync(prepared, cancellationToken);
        if (missing.Count > 0)
            throw CheckoutException.Of(CheckoutRefusals.TermsMissing, subject: missing[0]);
        var accepted = request.AcceptedDocumentIds?.ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var (document, _) in documents.Where(d => d.MustAccept))
        {
            if (!accepted.Contains(document.Id))
                throw CheckoutException.Of(accepted.Count > 0 ? CheckoutRefusals.TermsChanged : CheckoutRefusals.TermsNotAccepted, subject: document.Kind);
        }

        // GB12: the age attestation.
        var minimumAge = prepared.Lines.Max(l => l.Item.MinimumAge);
        if (minimumAge is { } age && (request.AttestedMinimumAge is not { } attested || attested < age))
            throw CheckoutException.Of(CheckoutRefusals.AgeAttestationRequired, subject: age.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // The method (D3, D40; never by country, GA11).
        var total = prepared.Priced.Total;
        PaymentMethodInfo? method = null;
        var (methods, held, window) = Methods(prepared);
        if (!total.IsZero)
        {
            if (methods.Count == 0)
                throw CheckoutException.Of(CheckoutRefusals.NoMethod);
            method = request.Method is { Length: > 0 } wanted
                ? methods.FirstOrDefault(m => string.Equals(m.Id, wanted, StringComparison.OrdinalIgnoreCase)) ?? throw CheckoutException.Of(CheckoutRefusals.MethodNotOffered, subject: wanted)
                : methods[0];
        }

        var order = NewOrder(prepared, email, name, now);
        order.MarketingOptIn = request.MarketingOptIn;
        order.Acceptances = documents.Where(d => d.MustAccept || accepted.Contains(d.Ref.Id)).Select(d => new CheckoutOrderAcceptance
        {
            DocumentId = d.Ref.Id, Kind = d.Ref.Kind, OrganizationId = d.Ref.OrganizationId, Version = d.Ref.Version,
            EffectiveFrom = d.Ref.EffectiveFrom, AcceptedAt = now,
        }).ToList();
        if (minimumAge is { } min)
            order.AgeAttestation = new CheckoutAgeAttestation { MinimumAge = min, AttestedAt = now, AttestedBy = buyer.UserId };

        // Claims per mode (D1). A refusal undoes what this placement claimed; basket holds stay the basket's.
        var placedClaims = new List<string>();
        CheckoutPrePaymentContext? prePayment = null;
        try
        {
            foreach (var (line, item, channel) in prepared.Lines)
            {
                var orderLine = order.Lines.First(l => l.LineId == line.LineId);
                if (item.Capacity.IsEmpty || channel.Allocation == AllocationMode.OnPayment)
                {
                    // D1: OnPayment claims nothing here, but a line that is visibly gone is refused before paying.
                    if (channel.Allocation == AllocationMode.OnPayment && !item.Capacity.IsEmpty
                        && !await SoftAvailability.LooksAvailableAsync(inventory, orderLine.Capacity, cancellationToken))
                        throw CheckoutException.Of(CheckoutRefusals.SoldOut, line.LineId);
                    if (channel.Allocation == AllocationMode.OnPayment && item.AdmissionScopes.Count > 0)
                        await admission.RequireAsync(item, TokensFor(line, request), line.Quantity, consume: true, line.LineId, cancellationToken);
                    continue;
                }
                var expiresAt = now + channel.Hold.Clamp(item.HoldTime);
                var target = OrderClaims.Request(OrderClaims.ClaimIdFor(line.LineId), order.Id!, expiresAt, orderLine.Capacity, orderLine.Limits);
                ClaimResult result;
                if (channel.Allocation == AllocationMode.HoldFromSelection && line.ClaimId is not null)
                {
                    // Re-assert the hold with every identifier now known (the e-mail address, D16); a lapsed one re-claims.
                    result = await inventory.AdjustAsync(target, cancellationToken);
                }
                else
                {
                    await admission.RequireAsync(item, TokensFor(line, request), line.Quantity, consume: true, line.LineId, cancellationToken);
                    result = await inventory.TryClaimAsync(target, cancellationToken);
                    if (result.Succeeded)
                        placedClaims.Add(target.ClaimId);
                }
                if (!result.Succeeded)
                    throw OrderClaims.Refusal(result, line.LineId);
                orderLine.ClaimId = target.ClaimId;
                orderLine.Units = result.Units?.ToList() ?? orderLine.Units;
            }

            // Pre-payment checks (required attendee questions, GB3, …): a refusal releases this placement's claims below.
            prePayment = new CheckoutPrePaymentContext
            {
                Order = order,
                Data = request.Data ?? new Dictionary<string, string>(),
                Language = request.Language,
            };
            foreach (var check in prePaymentChecks.OrderBy(c => c.Order))
                await check.CheckAsync(prePayment, cancellationToken);

            if (method is not null)
            {
                var grace = options.CurrentValue.PaymentGrace;
                var deadline = now + PaymentMethodCatalog.PaymentPendingWindow(method, window, grace);
                order.PaymentPendingUntil = deadline;
                foreach (var claimId in order.Lines.Where(l => l.ClaimId is not null).Select(l => l.ClaimId!))
                {
                    var extended = await inventory.ExtendAsync(claimId, deadline, cancellationToken);
                    if (!extended.Succeeded)
                        throw OrderClaims.Refusal(extended, order.Lines.First(l => l.ClaimId == claimId).LineId);
                }

                var manual = method.SupportsAuthorization && (
                    prepared.Lines.Any(l => options.CurrentValue.Channel(l.Item.Channel).AuthorizeThenCapture)
                    || (method.Fingerprint == FingerprintTiming.BeforeConfirm && order.Lines.Any(l => l.LimitMax is not null)));
                var split = NeedsSplit(settings, prepared.Lines.Select(l => l.Item.OrganizationId));
                order.MixedSellers = split;
                ProviderPaymentInfo created;
                try
                {
                    created = await CreatePaymentAsync(order, method, manual, split, deadline, email, cancellationToken);
                }
                catch (SparkValidationException ex)
                {
                    throw CheckoutException.Of(CheckoutRefusals.SellerCannotSell, subject: ex.Message);
                }
                order.Payment = created.Record;
                order.Payments.Add(created.Record);
            }
        }
        catch
        {
            foreach (var claimId in placedClaims)
                await inventory.ReleaseAsync(claimId, ReturnReason.Cancelled, cancellationToken: CancellationToken.None);
            throw;
        }

        var raised = new List<OrderEvent>();
        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(order, order.Id, cancellationToken);
            foreach (var (document, documentId) in prePayment?.Documents ?? [])
                await session.StoreAsync(document, documentId, cancellationToken);
            await audit.RecordAsync(session, new CommerceAuditEntry
            {
                Action = CheckoutAuditActions.OrderPlaced, SubjectId = order.Id!, OrganizationId = order.Sellers.FirstOrDefault(),
                After = order.Total, ActorId = buyer.UserId, At = now,
            }, cancellationToken);
            raised.Add(new OrderEvent(order, OrderEventKind.Placed, null));
            if (method is null)
                await lifecycle.ConfirmAsync(session, order, payment: null, raised, cancellationToken); // zero total (D6)
            await session.SaveChangesAsync(cancellationToken);
        }

        await baskets.TouchAsync(prepared.Basket.Id!, b =>
        {
            b.Email = email;
            if (order.State == OrderState.PaymentPending)
                b.PendingOrderId = order.Id;
        }, cancellationToken);

        // GA9: the soft opt-in, recorded as a consent record (ePrivacy Art. 13(2); ⚠️ confirm against EUR-Lex, D37).
        if (request.MarketingOptIn && services.GetService<ISparkConsent>() is { } consent)
        {
            var subject = buyer.UserId is { } account ? SparkConsentSubject.Account(account) : SparkConsentSubject.Email(email);
            await consent.RecordAsync(subject, new SparkConsentChoice(new Dictionary<string, bool> { [SparkConsentPurposes.MarketingMail] = true }, SparkConsentSources.Checkout), cancellationToken);
        }

        foreach (var e in raised)
            await events.PublishAsync(e, cancellationToken);

        return new SparkPlaceOrderResult(order.Number, order.State.ToString(), order.Payment?.CheckoutUrl ?? mails.OrderPath(order));
    }

    /// <summary>The line's admission tokens, with the ones presented at placement winning per scope.</summary>
    private static IReadOnlyDictionary<string, string> TokensFor(CheckoutBasketLine line, SparkPlaceOrderRequest request)
        => CheckoutAdmission.Merge(line.AdmissionTokens, request.AdmissionTokens);

    private CheckoutOrder NewOrder(Prepared prepared, string email, string? name, DateTimeOffset now)
    {
        var id = "CheckoutOrders/" + Guid.NewGuid().ToString("N");
        var order = new CheckoutOrder
        {
            Id = id,
            Number = NewNumber(),
            State = OrderState.PaymentPending,
            Origin = OrderOrigin.Online,
            BuyerUserId = buyer.UserId,
            VisitorId = buyer.UserId is null ? buyer.VisitorId : null,
            BuyerEmail = email,
            BuyerName = name,
            PurchaseIp = buyer.IpAddress,
            Currency = prepared.Priced.Currency,
            Sellers = prepared.Lines.Select(l => l.Item.OrganizationId).Where(s => s is not null).Select(s => s!).Distinct().ToList(),
            Total = prepared.Priced.Total,
            CreatedAt = now,
            UpdatedAt = now,
        };
        foreach (var priced in prepared.Priced.Lines)
        {
            var (line, item, channel) = prepared.Lines.First(l => l.Line.LineId == priced.LineId);
            order.Lines.Add(ToOrderLine(priced, item, channel, line, LineIdentifiers(line, email)));
        }
        order.Extras = prepared.Priced.Extras.Select(ToOrderExtra).ToList();
        order.Log.Add(new CheckoutOrderLogEntry { At = now, Event = "placed", ActorId = buyer.UserId, Detail = buyer.VisitorId is { } v ? "visitor " + v : null });
        return order;
    }

    internal static CheckoutOrderLine ToOrderLine(PricedLine priced, CheckoutItem item, CommerceChannelOptions channel, CheckoutBasketLine? basketLine, BuyerIdentifiers identifiers)
        => new()
        {
            LineId = priced.LineId,
            ItemId = item.ItemId,
            Title = item.Title,
            Channel = item.Channel,
            Allocation = channel.Allocation,
            SellerOrganizationId = item.OrganizationId,
            Quantity = priced.Quantity,
            UnitPrice = item.Price,
            Net = priced.Net ?? priced.Price.Net,
            Vat = priced.Vat ?? priced.Price.Vat,
            VatRateBasisPoints = item.VatRateBasisPoints,
            VatCategory = item.VatCategory,
            Fees = priced.Price.MandatoryFees.Select(f => new CheckoutOrderFee { Code = f.Code, Kind = f.Kind.ToString(), Amount = f.Amount, LabelKey = f.LabelKey, Mandatory = true }).ToList(),
            Adjustments = priced.Adjustments.Select(a => new CheckoutOrderFee { Code = a.Code, Kind = a.Source, Amount = a.Amount, LabelKey = a.LabelKey }).ToList(),
            Total = priced.Total,
            // S21: the line's share of the order discount, stored so Net + Vat = Total − fees + OrderDiscount holds on the order.
            OrderDiscount = priced.OrderDiscount ?? Money.Zero(priced.Total.Currency),
            ClaimId = basketLine?.ClaimId,
            Units = basketLine?.HeldUnits.Count > 0 ? [.. basketLine.HeldUnits] : [.. item.Capacity.Units],
            Capacity = CheckoutCapacitySnapshot.From(item.Capacity),
            Limits = OrderClaims.Limits(item, channel, priced.Quantity, identifiers).ToList(),
            LimitScope = (item.PerBuyerLimit ?? channel.PerBuyerLimit) is null ? null : OrderClaims.LimitScope(item),
            LimitMax = item.PerBuyerLimit ?? channel.PerBuyerLimit,
            BasketHoldUntil = basketLine?.HeldUntil,
            ParentLineId = basketLine?.ParentLineId,
            MinimumAge = item.MinimumAge,
            IdCheckRequired = item.MinimumAge is not null,
            IsPhysicalGoods = item.IsPhysicalGoods,
            ServiceDate = item.ServiceDate,
        };

    internal static CheckoutOrderExtra ToOrderExtra(PricedExtra e) => new()
    {
        Code = e.Code, Kind = e.Kind, Amount = e.Amount, LabelKey = e.LabelKey, Source = e.Source, VatCategory = e.VatCategory, Reference = e.Reference,
    };

    internal sealed record ProviderPaymentInfo(CheckoutOrderPayment Record);

    private async Task<ProviderPaymentInfo> CreatePaymentAsync(CheckoutOrder order, PaymentMethodInfo method, bool manual, bool split, DateTimeOffset deadline, string email, CancellationToken cancellationToken)
    {
        var sellerIds = order.Lines.Select(l => l.SellerOrganizationId).Distinct().ToList();
        var splits = split
            ? order.Lines.Where(l => l.SellerOrganizationId is not null).GroupBy(l => l.SellerOrganizationId!)
                .Select(g => new PaymentSplitRequest(g.Key, Money.Sum(g.Select(l => l.Charged()), order.Currency))).ToList()
            : [];
        var attempt = order.Payments.Count;
        var payment = await payments.CreatePaymentAsync(new PaymentCreation
        {
            Reference = order.Id!,
            Attempt = attempt,
            // D41: a mixed-seller order is the platform's (merchant of record), split to the sellers.
            SellerOrganizationId = split ? null : sellerIds.SingleOrDefault(),
            Provider = method.Provider,
            Amount = order.Total,
            Description = "Order " + order.Number,
            Method = method.Id,
            AllowedMethods = [method.Id],
            Capture = manual ? PaymentCaptureMode.Manual : PaymentCaptureMode.Automatic,
            ExpiresAt = deadline,
            RedirectUrl = mails.OrderPath(order),
            CustomerEmail = email,
            Splits = splits,
        }, cancellationToken);
        return new ProviderPaymentInfo(new CheckoutOrderPayment
        {
            PaymentId = payment.Id, Provider = payment.Provider, Method = method.Id, Attempt = attempt, Status = payment.Status.ToString(),
            Amount = order.Total, ManualCapture = manual, CheckoutUrl = payment.CheckoutUrl, CreatedAt = Now,
        });
    }

    internal static string NewNumber()
    {
        // Crockford base32, 10 characters (50 bits): short to read out on the phone, unguessable enough not to enumerate.
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[10];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[bytes[i] & 31];
        return new string(chars);
    }

    // ---- the buyer's own order -----------------------------------------------------------------------------------

    public async Task<CheckoutOrder?> GetOwnOrderAsync(string number, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(number))
            return null;
        var userId = buyer.UserId;
        var visitorId = buyer.VisitorId;
        if (userId is null && visitorId is null)
            return null;
        using var session = store.OpenAsyncSession();
        var order = await Raven.Client.Documents.LinqExtensions.FirstOrDefaultAsync(
            session.Query<CheckoutOrder>().Where(o => o.Number == number), cancellationToken);
        if (order is null)
            return null;
        return (userId is not null && order.BuyerUserId == userId) || (userId is null && visitorId is not null && order.VisitorId == visitorId) ? order : null;
    }

    public async Task<CheckoutOrder?> CancelPaymentAsync(string number, CancellationToken cancellationToken = default)
    {
        var order = await GetOwnOrderAsync(number, cancellationToken);
        if (order is null)
            return null;
        if (order.State != OrderState.PaymentPending)
            throw CheckoutException.Of(CheckoutRefusals.InvalidState, subject: order.State.ToString());
        return await lifecycle.UpdateAsync(order.Id!, (session, o, raised)
            => lifecycle.EndUnpaidAsync(o, OrderState.Cancelled, "cancelled by the buyer", raised, cancelProvider: true, cancellationToken), cancellationToken);
    }

    public async Task MarkShippedAsync(string orderId, string lineId, DateTimeOffset shippedAt, CancellationToken cancellationToken = default)
        => await lifecycle.UpdateAsync(orderId, (session, order, raised) =>
        {
            var line = order.Lines.FirstOrDefault(l => l.LineId == lineId) ?? throw CheckoutException.Of(CheckoutRefusals.LineNotFound, lineId);
            if (line.ShippedAt is null)
            {
                line.ShippedAt = shippedAt;
                order.Log.Add(new CheckoutOrderLogEntry { At = Now, Event = "shipped", Detail = lineId });
                raised.Add(new OrderEvent(order, OrderEventKind.LineShipped, null, lineId));
            }
            return Task.CompletedTask;
        }, cancellationToken);
}
