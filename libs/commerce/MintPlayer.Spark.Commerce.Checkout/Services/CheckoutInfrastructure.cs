using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Commerce.Checkout.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Checkout.Services;

/// <summary>Who is buying: the signed-in account, else the guest's visitor (X1). Never an authenticated principal for a guest.</summary>
internal interface ICheckoutBuyer
{
    string? UserId { get; }

    string? VisitorId { get; }

    /// <summary>The visitor id, issuing the strictly necessary cookie when the request has none (actions only).</summary>
    ValueTask<string?> EnsureVisitorAsync();

    /// <summary>The caller's address (dispute evidence, GB1); null outside a request.</summary>
    string? IpAddress { get; }
}

internal sealed class RequestCheckoutBuyer(IServiceProvider services, IHttpContextAccessor http) : ICheckoutBuyer
{
    private ISparkCurrentUser? User => services.GetService<ISparkCurrentUser>();

    private ISparkVisitor? Visitor => services.GetService<ISparkVisitor>();

    public string? UserId => User is { IsAuthenticated: true, Id: { Length: > 0 } id } ? id : null;

    public string? VisitorId => Visitor?.VisitorId;

    public async ValueTask<string?> EnsureVisitorAsync()
    {
        if (UserId is not null)
            return null;
        return Visitor is { } visitor ? await visitor.EnsureAsync() : null;
    }

    public string? IpAddress => http.HttpContext?.Connection.RemoteIpAddress?.ToString();
}

/// <summary>
/// Reads and writes baskets. Every write is a cluster-wide session (S18): the visitor merge moves baskets inside the
/// merge's cluster-wide transaction, and such a transaction refuses a document an ordinary session wrote.
/// </summary>
internal sealed class BasketStore(IDocumentStore store, ICheckoutBuyer buyer, TimeProvider clock)
{
    public string? CurrentId => buyer.UserId is { } user ? CheckoutBasket.IdForUser(user)
        : buyer.VisitorId is { } visitor ? CheckoutBasket.IdForVisitor(visitor) : null;

    public async Task<CheckoutBasket?> LoadCurrentAsync(CancellationToken cancellationToken)
    {
        if (CurrentId is not { } id)
            return null;
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<CheckoutBasket>(id, cancellationToken);
    }

    /// <summary>
    /// Loads (or starts) the current buyer's basket, lets <paramref name="change"/> modify it, and stores it in one
    /// cluster-wide transaction. A guest gets the visitor cookie here (an action).
    /// </summary>
    public async Task<CheckoutBasket> UpdateCurrentAsync(Func<CheckoutBasket, Task> change, CancellationToken cancellationToken)
    {
        var userId = buyer.UserId;
        var visitorId = userId is null ? await buyer.EnsureVisitorAsync() : null;
        if (userId is null && visitorId is null)
            throw CheckoutException.Of(CheckoutRefusals.Forbidden);
        var id = userId is not null ? CheckoutBasket.IdForUser(userId) : CheckoutBasket.IdForVisitor(visitorId!);
        return await UpdateAsync(id, basket =>
        {
            basket.UserId = userId;
            basket.VisitorId = visitorId;
            return change(basket);
        }, cancellationToken);
    }

    public async Task<CheckoutBasket> UpdateAsync(string id, Func<CheckoutBasket, Task> change, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions { TransactionMode = TransactionMode.ClusterWide });
        var basket = await session.LoadAsync<CheckoutBasket>(id, cancellationToken);
        if (basket is null)
        {
            basket = new CheckoutBasket { Id = id };
            await session.StoreAsync(basket, id, cancellationToken);
        }
        await change(basket);
        basket.UpdatedAt = clock.GetUtcNow();
        basket.ReminderSentAt = null;
        await session.SaveChangesAsync(cancellationToken);
        return basket;
    }

    /// <summary>A write that is not the buyer's activity (the reminder stamp, an order clearing the basket): <c>UpdatedAt</c> is kept.</summary>
    public async Task TouchAsync(string id, Action<CheckoutBasket> change, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions { TransactionMode = TransactionMode.ClusterWide });
        var basket = await session.LoadAsync<CheckoutBasket>(id, cancellationToken);
        if (basket is null)
            return;
        change(basket);
        await session.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Resolves items through the registered <see cref="ICheckoutCapacitySource"/>s.</summary>
internal sealed class CheckoutItems(IEnumerable<ICheckoutCapacitySource> sources)
{
    public async Task<CheckoutItem> ResolveAsync(CheckoutItemRequest request, string? lineId, CancellationToken cancellationToken)
    {
        if (request.Quantity < 1 || request.Quantity > 10_000)
            throw CheckoutException.Of(CheckoutRefusals.InvalidQuantity, lineId);
        var source = sources.FirstOrDefault(s => s.Handles(request.ItemId))
            ?? throw CheckoutException.Of(CheckoutRefusals.ItemNotFound, lineId, request.ItemId);
        return await source.ResolveAsync(request, cancellationToken)
            ?? throw CheckoutException.Of(CheckoutRefusals.ItemNotFound, lineId, request.ItemId);
    }

    public Task<CheckoutItem> ResolveAsync(CheckoutBasketLine line, string? parentItemId, CancellationToken cancellationToken)
        => ResolveAsync(new CheckoutItemRequest(line.ItemId, line.Quantity, line.Units.Count > 0 ? line.Units : null,
            line.Picks.Count > 0 ? line.Picks : null, line.Options, parentItemId), line.LineId, cancellationToken);
}

/// <summary>
/// The admission check (§4.6, §4.12) over the registered gates: every one of an item's
/// <see cref="CheckoutItem.AdmissionScopes"/> must admit (a queued presale phase needs the waiting-room token and the
/// presale code); a scope no gate owns, or one without a token, is refused (fail closed).
/// </summary>
internal sealed class CheckoutAdmission(IEnumerable<ICheckoutAdmissionGate> gates, ICheckoutBuyer buyer)
{
    /// <param name="tokens">The tokens the buyer presented, by scope.</param>
    public async Task RequireAsync(CheckoutItem item, IReadOnlyDictionary<string, string>? tokens, int quantity, bool consume, string? lineId, CancellationToken cancellationToken)
    {
        var scopes = item.AdmissionScopes.Where(s => s is { Length: > 0 }).Distinct(StringComparer.Ordinal).ToList();
        if (scopes.Count == 0)
            return;
        var checks = new List<(ICheckoutAdmissionGate Gate, AdmissionCheck Check)>(scopes.Count);
        foreach (var scope in scopes)
        {
            var gate = gates.FirstOrDefault(g => g.Handles(scope));
            var token = tokens is not null && tokens.TryGetValue(scope, out var presented) ? presented : null;
            if (gate is null || token is not { Length: > 0 })
                throw CheckoutException.Of(CheckoutRefusals.AdmissionRequired, lineId, scope);
            checks.Add((gate, new AdmissionCheck(scope, token, buyer.UserId, buyer.VisitorId, item.ItemId, quantity, Consume: false)));
        }

        // Every scope first without consuming: a refused second scope must not spend the first one's token.
        foreach (var (gate, check) in checks)
            await DecideAsync(gate, check, lineId, cancellationToken);
        if (!consume)
            return;
        foreach (var (gate, check) in checks)
            await DecideAsync(gate, check with { Consume = true }, lineId, cancellationToken);
    }

    /// <summary>Merges the tokens of a later request over the ones a line was added with (a later one wins per scope).</summary>
    public static IReadOnlyDictionary<string, string> Merge(IReadOnlyDictionary<string, string>? stored, IReadOnlyDictionary<string, string>? presented)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in new[] { stored, presented })
            if (source is not null)
                foreach (var (scope, token) in source)
                    if (token is { Length: > 0 })
                        merged[scope] = token;
        return merged;
    }

    private static async Task DecideAsync(ICheckoutAdmissionGate gate, AdmissionCheck check, string? lineId, CancellationToken cancellationToken)
    {
        var decision = await gate.CheckAsync(check, cancellationToken);
        if (!decision.Admitted)
            throw CheckoutException.Of(CheckoutRefusals.AdmissionRequired, lineId, decision.Reason ?? check.Scope);
    }
}

/// <summary>Tells every <see cref="IOrderEventRecipient"/>; a failing recipient is logged and noted on the order's log by the caller's next save.</summary>
internal sealed class OrderEvents(IEnumerable<IOrderEventRecipient> recipients, ILogger<OrderEvents> logger)
{
    public async Task PublishAsync(OrderEvent orderEvent, CancellationToken cancellationToken)
    {
        foreach (var recipient in recipients)
        {
            try
            {
                await recipient.OnOrderEventAsync(orderEvent, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "{Recipient} failed on {Kind} of order {OrderId}.", recipient.GetType().Name, orderEvent.Kind, orderEvent.Order.Id);
            }
        }
    }
}
