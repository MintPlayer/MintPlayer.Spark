using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Promotions.Models;
using MintPlayer.Spark.MailManager;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Promotions.Services;

/// <summary>
/// Promotions' mails through MailManager (optional: without it nothing is sent and a warning is logged). All three are
/// service mails the recipient asked for (an offer from a list they joined, a back-in-stock notice they subscribed to,
/// the confirmation of that subscription), so they go on the transactional stream.
/// </summary>
internal sealed class PromotionMails(
    IServiceProvider services,
    IDocumentStore store,
    IOptionsMonitor<SparkCommercePromotionsOptions> options,
    ILogger<PromotionMails> logger)
{
    private ISparkMailer? Mailer => services.GetService<ISparkMailer>();

    public string? PublicBaseUrl => SparkPublicBaseUrl.Resolve(null,
        services.GetService<IConfiguration>(), services.GetService<IHostEnvironment>(), services.GetService<IHttpContextAccessor>()?.HttpContext);

    public string Absolute(string path) => PublicBaseUrl is { } origin ? origin.TrimEnd('/') + path : path;

    /// <summary>The waiting-list offer: a personal code valid until <paramref name="expiresAt"/>.</summary>
    public async Task<bool> SendOfferAsync(PromotionWaitingListEntry entry, PromotionWaitingList list, string code, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        if (Mailer is not { } mailer)
        {
            logger.LogWarning("No MailManager: the waiting-list offer {Code} is not mailed.", code);
            return false;
        }
        var (email, name, culture) = await AccountAsync(entry.UserId, cancellationToken);
        if (email is null)
            return false;
        var path = options.CurrentValue.WaitingList.OfferPagePath.Replace("{code}", Uri.EscapeDataString(code), StringComparison.Ordinal);
        await mailer.SendAsync(new SparkMailRequest
        {
            Template = options.CurrentValue.WaitingList.OfferMailTemplate,
            To = email,
            ToName = name,
            Culture = culture,
            Data = new
            {
                buyer_name = name,
                title = list.Title?.GetValue(Language(culture)),
                quantity = entry.Quantity,
                code,
                expires_at = expiresAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture),
                offer_url = Absolute(path),
            },
            // The code is a personal purchase right: encrypt it in the queued message.
            Sensitive = true,
            DeduplicationKey = $"promotions:offer:{entry.Id}:{code}",
        }, cancellationToken);
        return true;
    }

    /// <summary>GB11: the item is available again (first payer wins: nothing is reserved).</summary>
    public async Task<bool> SendBackInStockAsync(PromotionStockSubscription subscription, string? title, string unsubscribeToken, CancellationToken cancellationToken)
    {
        if (Mailer is not { } mailer)
        {
            logger.LogWarning("No MailManager: the back-in-stock notice for {ItemId} is not mailed.", subscription.ItemId);
            return false;
        }
        var bis = options.CurrentValue.BackInStock;
        await mailer.SendAsync(new SparkMailRequest
        {
            Template = bis.NotificationMailTemplate,
            To = subscription.Email,
            Culture = subscription.Culture,
            Data = new
            {
                title,
                item_url = Absolute(bis.ItemPagePath.Replace("{itemId}", Uri.EscapeDataString(subscription.ItemId), StringComparison.Ordinal)),
                unsubscribe_url = Absolute(TokenPath(bis.UnsubscribePagePath, subscription.Id!, unsubscribeToken)),
            },
            Sensitive = true,
            DeduplicationKey = $"promotions:back-in-stock:{subscription.Id}:{subscription.CreatedAt.ToUnixTimeSeconds()}",
        }, cancellationToken);
        return true;
    }

    /// <summary>GB11: the double opt-in of a guest's address.</summary>
    public async Task<bool> SendConfirmationAsync(PromotionStockSubscription subscription, string? title, string token, CancellationToken cancellationToken)
    {
        if (Mailer is not { } mailer)
        {
            logger.LogWarning("No MailManager: the back-in-stock confirmation for {ItemId} is not mailed.", subscription.ItemId);
            return false;
        }
        var bis = options.CurrentValue.BackInStock;
        await mailer.SendAsync(new SparkMailRequest
        {
            Template = bis.ConfirmationMailTemplate,
            To = subscription.Email,
            Culture = subscription.Culture,
            Data = new
            {
                title,
                confirm_url = Absolute(TokenPath(bis.ConfirmPagePath, subscription.Id!, token)),
            },
            Sensitive = true,
            DeduplicationKey = $"promotions:back-in-stock-confirm:{subscription.Id}:{subscription.CreatedAt.ToUnixTimeSeconds()}",
        }, cancellationToken);
        return true;
    }

    /// <summary>An account's verified address, name and culture; a null address when the account has no confirmed one.</summary>
    public async Task<(string? Email, string? Name, string? Culture)> AccountAsync(string userId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var user = await session.LoadAsync<SparkUser>(userId, cancellationToken);
        return user is { Email: { Length: > 0 } email, EmailConfirmed: true }
            ? (email, user.UserName, user.PreferredCulture)
            : (null, user?.UserName, user?.PreferredCulture);
    }

    private static string TokenPath(string template, string id, string token)
        => template.Replace("{id}", Uri.EscapeDataString(id), StringComparison.Ordinal).Replace("{token}", Uri.EscapeDataString(token), StringComparison.Ordinal);

    internal static string Language(string? culture)
        => culture is { Length: >= 2 } c ? c[..2].ToLowerInvariant() : TranslatedString.DefaultLanguage;
}
