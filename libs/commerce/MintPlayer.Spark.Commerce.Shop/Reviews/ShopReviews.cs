using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Shop.Catalog;
using MintPlayer.Spark.Commerce.Shop.Contracts;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Commerce.Shop.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Commerce.Shop.Reviews;

/// <summary>
/// Verified-purchase reviews (GC5, PRD §4.10). Only the buyer of a delivered order line reviews it, once, within
/// <c>Reviews:Window</c> of the delivery. While <c>Reviews:PreModeration</c> is on (default), a review waits for one of
/// Moderation's reviewers (<c>Review/Moderation</c>) before it is shown; flags and locks are Moderation's own
/// (<see cref="ShopProductReview"/> is <c>IModeratable</c>).
/// <para><b>The verification statement (UCPD Annex I point 23b, inserted by Directive (EU) 2019/2161 Art. 3(7);
/// unconfirmed against EUR-Lex, D37).</b> The storefront states that and how reviews are verified: the
/// <see cref="SparkShopReviewPage.PreModerated"/> flag and <c>commerceShop.reviews.verification</c> say so. Every published
/// review comes from a delivered order line (<see cref="SparkShopReview.Verified"/> is always true); no review is
/// published or refused because of its rating (point 23c, unconfirmed), and a review written for a benefit says so
/// (<see cref="ShopProductReview.Incentivised"/>).</para>
/// </summary>
public interface ISparkShopReviews
{
    /// <summary>A page of a published product's published reviews with the summary (anonymous).</summary>
    Task<SparkShopReviewPage?> GetAsync(string productId, int skip = 0, int take = 20, CancellationToken cancellationToken = default);

    /// <summary>A review of a delivered line of the caller's own order (signed-in, or a guest through the signed order link).</summary>
    /// <exception cref="ShopException">Reviews closed, not verified, already reviewed, or an invalid text or rating.</exception>
    Task<SparkShopReviewResult> SubmitAsync(SparkShopReviewRequest request, CancellationToken cancellationToken = default);

    /// <summary>The reviews waiting for a moderator (<c>Review/Moderation</c>).</summary>
    Task<IReadOnlyList<SparkShopPendingReview>> GetPendingAsync(int skip = 0, int take = 50, CancellationToken cancellationToken = default);

    /// <summary>Publishes or rejects a pending review (<c>Review/Moderation</c>); a rejection needs one of the allowed reasons.</summary>
    Task<SparkShopReviewResult> DecideAsync(SparkShopReviewDecision decision, CancellationToken cancellationToken = default);
}

internal sealed class ShopReviews(
    IDocumentStore store,
    ISparkCheckout checkout,
    ShopAccess access,
    IOptionsMonitor<SparkCommerceShopOptions> options,
    TimeProvider clock) : ISparkShopReviews
{
    public const int MaxTitle = 200;
    public const int MaxDisplayName = 80;

    public async Task<SparkShopReviewPage?> GetAsync(string productId, int skip = 0, int take = 20, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return null;
        using var session = store.OpenAsyncSession();
        var product = await session.LoadAsync<ShopProduct>(productId, cancellationToken);
        if (product is not { IsPublished: true })
            return null;
        var page = await session.Query<ShopProductReview>()
            .Where(r => r.ProductId == productId && r.Status == EShopReviewStatus.Published)
            .OrderByDescending(r => r.PostedAt)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 50))
            .ToListAsync(cancellationToken);
        var distribution = new int[5];
        for (var rating = 1; rating <= 5; rating++)
        {
            var r = rating;
            distribution[rating - 1] = await session.Query<ShopProductReview>()
                .Where(x => x.ProductId == productId && x.Status == EShopReviewStatus.Published && x.Rating == r)
                .CountAsync(cancellationToken);
        }
        return new SparkShopReviewPage(product.Id!, product.ReviewCount, product.RatingAverage, distribution,
            page.Select(View).ToList(), options.CurrentValue.Reviews.PreModeration);
    }

    internal static SparkShopReview View(ShopProductReview review)
        => new(review.Id!, review.Rating, review.Title, review.Body, review.DisplayName, review.Language, review.Incentivised,
            Verified: true, review.PostedAt, review.VariantId);

    public async Task<SparkShopReviewResult> SubmitAsync(SparkShopReviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = options.CurrentValue.Reviews;
        if (!settings.Enabled)
            throw ShopException.Of(ShopRefusals.ReviewsClosed);
        if (Problem(request, settings.MaxLength) is { } problem)
            throw ShopException.Of(ShopRefusals.InvalidReview, problem);

        // The order must be the caller's own (Checkout decides: the account, or the guest's signed order link, D4).
        var order = await checkout.GetOwnOrderAsync(request.OrderNumber, cancellationToken) ?? throw ShopException.Of(ShopRefusals.OrderNotFound);
        var orderLine = order.Lines.FirstOrDefault(l => l.LineId == request.LineId && ShopItemIds.Handles(l.ItemId))
            ?? throw ShopException.Of(ShopRefusals.NotVerified, request.LineId);

        using var session = store.OpenAsyncSession();
        var shopOrder = await session.LoadAsync<ShopOrder>(ShopDocumentIds.Order(order.Id!, orderLine.SellerOrganizationId), cancellationToken);
        var line = shopOrder?.Lines.FirstOrDefault(l => l.LineId == request.LineId);
        var now = clock.GetUtcNow();
        if (!IsVerified(line, now, settings.Window))
            throw ShopException.Of(ShopRefusals.NotVerified, request.LineId);
        var product = await session.LoadAsync<ShopProduct>(line!.ProductId, cancellationToken);
        if (product is null || !product.ReviewsEnabled)
            throw ShopException.Of(ShopRefusals.ReviewsClosed, line.ProductId);

        var review = new ShopProductReview
        {
            Id = ShopDocumentIds.Review(order.Id!, line.LineId),
            ProductId = product.Id!,
            VariantId = line.VariantId,
            OrganizationId = product.OrganizationId,
            OrderId = order.Id!,
            LineId = line.LineId,
            AuthorId = access.UserId,
            PostedAt = now,
            DisplayName = Clean(request.DisplayName, MaxDisplayName),
            Rating = request.Rating,
            Title = Clean(request.Title, MaxTitle),
            Body = request.Body.Trim(),
            Language = Clean(request.Language, 35),
            Incentivised = request.Incentivised,
            PurchaseDeliveredAt = line.DeliveredAt,
            Status = settings.PreModeration ? EShopReviewStatus.Pending : EShopReviewStatus.Published,
            DecidedAt = settings.PreModeration ? null : now,
        };
        try
        {
            // One review per order line: an atomic natural-id create (X3).
            await session.StoreAsync(review, string.Empty, review.Id, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyException)
        {
            throw ShopException.Of(ShopRefusals.AlreadyReviewed, request.LineId);
        }
        if (review.Status == EShopReviewStatus.Published)
            await RefreshSummaryAsync(review.ProductId, cancellationToken);
        return new SparkShopReviewResult(review.Id, review.Status.ToString());
    }

    /// <summary>
    /// GC5: a line is verified when its units were all delivered (Checkout's delivery report, not the buyer's word) and
    /// the review window since that delivery is still open. Pure.
    /// </summary>
    internal static bool IsVerified(ShopOrderLine? line, DateTimeOffset now, TimeSpan window)
        => line is { Cancelled: false, DeliveredAt: { } delivered } && delivered <= now && now - delivered <= window;

    /// <summary>What is wrong with the text or rating, or null. Pure.</summary>
    internal static string? Problem(SparkShopReviewRequest request, int maxLength)
    {
        if (request.Rating is < 1 or > 5)
            return "rating";
        if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Trim().Length > maxLength)
            return "body";
        if (request.Title is { } title && title.Trim().Length > MaxTitle)
            return "title";
        if (request.DisplayName is { } name && name.Trim().Length > MaxDisplayName)
            return "displayName";
        return null;
    }

    private static string? Clean(string? value, int max)
        => value?.Trim() is { Length: > 0 } trimmed ? trimmed[..Math.Min(trimmed.Length, max)] : null;

    public async Task<IReadOnlyList<SparkShopPendingReview>> GetPendingAsync(int skip = 0, int take = 50, CancellationToken cancellationToken = default)
    {
        if (!await access.MayDecideReviewsAsync(cancellationToken))
            throw ShopException.Of(ShopRefusals.Forbidden);
        using var session = store.OpenAsyncSession();
        var pending = await session.Query<ShopProductReview>()
            .Include(r => r.ProductId)
            .Where(r => r.Status == EShopReviewStatus.Pending)
            .OrderBy(r => r.PostedAt)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(cancellationToken);
        var products = await session.LoadAsync<ShopProduct>(pending.Select(r => r.ProductId).Distinct(StringComparer.Ordinal), cancellationToken);
        return pending.Select(r => new SparkShopPendingReview(r.Id!, r.ProductId, products.GetValueOrDefault(r.ProductId)?.Name,
            r.Rating, r.Title, r.Body, r.Incentivised, r.PostedAt)).ToList();
    }

    public async Task<SparkShopReviewResult> DecideAsync(SparkShopReviewDecision decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (!await access.MayDecideReviewsAsync(cancellationToken))
            throw ShopException.Of(ShopRefusals.Forbidden);
        EShopReviewRejection? reason = null;
        if (!decision.Publish)
        {
            // Only the listed reasons, never the rating or a negative tone (UCPD Art. 7(2) and Annex I point 23c; unconfirmed, D37).
            if (!Enum.TryParse<EShopReviewRejection>(decision.Reason, ignoreCase: false, out var parsed) || !Enum.IsDefined(parsed))
                throw ShopException.Of(ShopRefusals.ReasonRequired, decision.Reason);
            reason = parsed;
        }
        using var session = store.OpenAsyncSession();
        var review = await session.LoadAsync<ShopProductReview>(decision.ReviewId, cancellationToken)
            ?? throw ShopException.Of(ShopRefusals.InvalidReview, decision.ReviewId);
        var wasPublished = review.Status == EShopReviewStatus.Published;
        review.Status = decision.Publish ? EShopReviewStatus.Published : EShopReviewStatus.Rejected;
        review.RejectionReason = reason;
        review.DecidedAt = clock.GetUtcNow();
        review.DecidedBy = access.UserId;
        await session.SaveChangesAsync(cancellationToken);
        if (wasPublished != (review.Status == EShopReviewStatus.Published))
            await RefreshSummaryAsync(review.ProductId, cancellationToken);
        return new SparkShopReviewResult(review.Id!, review.Status.ToString());
    }

    /// <summary>Recomputes a product's review count and average from its published reviews (the catalogue's rating sort reads them).</summary>
    internal async Task RefreshSummaryAsync(string productId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var ratings = await session.Query<ShopProductReview>()
            .Customize(c => c.WaitForNonStaleResults())
            .Where(r => r.ProductId == productId && r.Status == EShopReviewStatus.Published)
            .Select(r => r.Rating)
            .Take(100_000)
            .ToListAsync(cancellationToken);
        if (await session.LoadAsync<ShopProduct>(productId, cancellationToken) is not { } product)
            return;
        (product.ReviewCount, product.RatingAverage) = Summary(ratings);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The count and the average rounded to one decimal (half away from zero). Pure.</summary>
    internal static (int Count, decimal? Average) Summary(IReadOnlyCollection<int> ratings)
        => ratings.Count == 0 ? (0, null) : (ratings.Count, Math.Round((decimal)ratings.Sum() / ratings.Count, 1, MidpointRounding.AwayFromZero));
}

/// <summary>GDPR (Art. 15, 20): the buyer's reviews in the personal-data export.</summary>
internal sealed class ShopReviewPersonalData<TUser>(IDocumentStore store) : ISparkPersonalDataContributor<TUser>
    where TUser : SparkUser
{
    public string Name => "shopReviews";

    public async Task<object?> GetPersonalDataAsync(TUser user, CancellationToken cancellationToken)
    {
        if (user.Id is not { Length: > 0 } userId)
            return null;
        using var session = store.OpenAsyncSession();
        var reviews = await session.Query<ShopProductReview>().Where(r => r.AuthorId == userId).Take(10_000).ToListAsync(cancellationToken);
        return reviews.Count == 0 ? null : reviews.Select(r => new
        {
            r.ProductId,
            r.OrderId,
            r.Rating,
            r.Title,
            r.Body,
            r.DisplayName,
            r.Language,
            r.Incentivised,
            r.PostedAt,
            Status = r.Status.ToString(),
        }).ToList();
    }
}

/// <summary>
/// GDPR (Art. 17): the buyer's reviews are deleted with the account (their text and display name are the buyer's
/// personal data), and the products' summaries are recomputed.
/// </summary>
internal sealed class ShopReviewAccountDeletion<TUser>(IDocumentStore store, ShopReviews reviews) : ISparkAccountDeletionHandler<TUser>
    where TUser : SparkUser
{
    public async Task OnDeletingAccountAsync(TUser user, CancellationToken cancellationToken)
    {
        if (user.Id is not { Length: > 0 } userId)
            return;
        List<string> productIds;
        using (var session = store.OpenAsyncSession())
        {
            var own = await session.Query<ShopProductReview>().Where(r => r.AuthorId == userId).Take(10_000).ToListAsync(cancellationToken);
            productIds = own.Where(r => r.Status == EShopReviewStatus.Published).Select(r => r.ProductId).Distinct(StringComparer.Ordinal).ToList();
            foreach (var review in own)
                session.Delete(review);
            await session.SaveChangesAsync(cancellationToken);
        }
        foreach (var productId in productIds)
            await reviews.RefreshSummaryAsync(productId, cancellationToken);
    }
}
