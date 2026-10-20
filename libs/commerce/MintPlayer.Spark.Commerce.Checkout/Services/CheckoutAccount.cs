using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Checkout.Contracts;
using MintPlayer.Spark.Commerce.Checkout.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Checkout.Services;

/// <summary>
/// The customer account area's order side (GB10, §4.14): the signed-in buyer's order history, and attaching guest orders
/// (D4: the guest orders placed with the account's <b>confirmed</b> e-mail address move onto the account). An unconfirmed
/// address attaches nothing: anyone can type someone else's address into a guest checkout, so the confirmation is the
/// proof of ownership.
/// </summary>
internal sealed class CheckoutAccount(IDocumentStore store, ICheckoutBuyer buyer, OrderLifecycle lifecycle, TimeProvider clock)
{
    /// <summary>The most guest orders one request lists or attaches.</summary>
    internal const int MaxGuestOrders = 100;

    /// <summary>The largest page of the history.</summary>
    internal const int MaxPage = 50;

    /// <summary>The signed-in buyer's orders, newest first; null when the caller is not signed in.</summary>
    public async Task<SparkAccountOrderPage?> OrdersAsync(int skip, int take, CancellationToken cancellationToken)
    {
        if (buyer.UserId is not { } userId)
            return null;
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, MaxPage);
        using var session = store.OpenAsyncSession();
        var orders = await session.Query<CheckoutOrder>()
            .Statistics(out var stats)
            .Where(o => o.BuyerUserId == userId)
            .OrderByDescending(o => o.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
        return new SparkAccountOrderPage(orders.Select(Summary).ToList(), (int)stats.TotalResults, skip, take);
    }

    internal static SparkAccountOrder Summary(CheckoutOrder order) => new(
        order.Number, order.State.ToString(), order.Total, order.AmountRefunded, order.Lines.Count,
        order.Lines.Take(3).Select(l => l.Title).ToList(), order.CreatedAt, order.ConfirmedAt);

    /// <summary>The guest orders the account may attach; null when the caller is not signed in.</summary>
    public async Task<SparkGuestOrdersInfo?> GuestOrdersAsync(CancellationToken cancellationToken)
    {
        if (buyer.UserId is not { } userId)
            return null;
        using var session = store.OpenAsyncSession();
        if (await ConfirmedEmailAsync(session, userId, cancellationToken) is not { } email)
            return new SparkGuestOrdersInfo(false, []);
        var orders = await GuestOrdersOfAsync(session, email, cancellationToken);
        return new SparkGuestOrdersInfo(true, orders.Select(o => o.Number).ToList());
    }

    /// <summary>
    /// Moves every guest order with the account's confirmed address onto the account, one order at a time (each under
    /// the order's own optimistic concurrency, like every order write). Raises <see cref="OrderEventKind.AttachedToAccount"/>
    /// per order so the packages that keep their own buyer link (Tickets) follow.
    /// </summary>
    /// <exception cref="CheckoutException"><see cref="CheckoutRefusals.EmailNotConfirmed"/>.</exception>
    public async Task<SparkGuestOrdersAttached?> AttachGuestOrdersAsync(CancellationToken cancellationToken)
    {
        if (buyer.UserId is not { } userId)
            return null;
        string email;
        List<CheckoutOrder> candidates;
        using (var session = store.OpenAsyncSession())
        {
            email = await ConfirmedEmailAsync(session, userId, cancellationToken)
                ?? throw CheckoutException.Of(CheckoutRefusals.EmailNotConfirmed);
            candidates = await GuestOrdersOfAsync(session, email, cancellationToken);
        }

        var attached = new List<string>();
        foreach (var candidate in candidates)
        {
            var moved = false;
            await lifecycle.UpdateAsync(candidate.Id!, (session, order, raised) =>
            {
                // Re-checked on the loaded order: it may have been attached elsewhere since the query.
                if (!IsGuestOrderOf(order, email))
                    return Task.CompletedTask;
                order.BuyerUserId = userId;
                order.Log.Add(new CheckoutOrderLogEntry { At = clock.GetUtcNow(), Event = "attached to account", Detail = userId });
                raised.Add(new OrderEvent(order, OrderEventKind.AttachedToAccount, null, order.VisitorId));
                moved = true;
                return Task.CompletedTask;
            }, cancellationToken);
            if (moved)
                attached.Add(candidate.Number);
        }
        return new SparkGuestOrdersAttached(attached);
    }

    /// <summary>A guest order placed with <paramref name="email"/> (ordinal, case-insensitive) and no account yet.</summary>
    internal static bool IsGuestOrderOf(CheckoutOrder order, string email)
        => order.BuyerUserId is null && order.BuyerEmail is { Length: > 0 } buyerEmail
            && string.Equals(buyerEmail.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> ConfirmedEmailAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string userId, CancellationToken cancellationToken)
    {
        var user = await session.LoadAsync<SparkUser>(userId, cancellationToken);
        return user is { Email: { Length: > 0 } email, EmailConfirmed: true } ? email : null;
    }

    private static async Task<List<CheckoutOrder>> GuestOrdersOfAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string email, CancellationToken cancellationToken)
    {
        // A parameterised equality (RavenDB compares strings case-insensitively), re-checked in memory.
        var orders = await session.Query<CheckoutOrder>()
            .Where(o => o.BuyerUserId == null && o.BuyerEmail == email)
            .OrderByDescending(o => o.CreatedAt)
            .Take(MaxGuestOrders)
            .ToListAsync(cancellationToken);
        return orders.Where(o => IsGuestOrderOf(o, email)).ToList();
    }
}
