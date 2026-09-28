using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.MailManager.Deliveries;
using MintPlayer.Spark.MailManager.Messaging;

namespace MintPlayer.Spark.MailManager.Bounces;

/// <summary>One recipient's outcome in a bounce report.</summary>
/// <param name="DeliveryId">From the VERP envelope recipient, when the report carried one.</param>
/// <param name="Recipient">The final recipient the report names.</param>
/// <param name="Status">The enhanced status code (<c>5.1.1</c>).</param>
/// <param name="Permanent"><c>Action: failed</c> with a 5.x.x status.</param>
/// <param name="Diagnostic">The remote server's text, if any.</param>
public sealed record SparkMailBounce(string? DeliveryId, string? Recipient, string? Status, bool Permanent, string? Diagnostic);

/// <summary>
/// Turns a raw bounce into outcomes. Replace it for a provider's webhook format. Called only after
/// the framework checked the endpoint's secret — a parser never sees an unauthenticated body.
/// </summary>
public interface ISparkMailBounceParser
{
    /// <summary>The outcomes in <paramref name="body"/>; <paramref name="envelopeRecipient"/> is the address the bounce was delivered to (the VERP address), when the relay passed it.</summary>
    Task<IReadOnlyList<SparkMailBounce>> ParseAsync(Stream body, string? envelopeRecipient, CancellationToken cancellationToken);
}

/// <summary>RFC 3464 delivery status notifications, through MimeKit.</summary>
internal sealed class DsnBounceParser : ISparkMailBounceParser
{
    public async Task<IReadOnlyList<SparkMailBounce>> ParseAsync(Stream body, string? envelopeRecipient, CancellationToken cancellationToken)
    {
        var message = await MimeMessage.LoadAsync(body, cancellationToken);
        var deliveryId = SparkMailVerp.DeliveryId(envelopeRecipient)
            ?? message.To.Mailboxes.Select(m => SparkMailVerp.DeliveryId(m.Address)).FirstOrDefault(id => id is not null)
            ?? SparkMailVerp.DeliveryId(message.Headers["X-Original-To"])
            ?? SparkMailVerp.DeliveryId(message.Headers["Delivered-To"]);

        var outcomes = new List<SparkMailBounce>();
        foreach (var status in message.BodyParts.OfType<MessageDeliveryStatus>())
        {
            // Group 0 is per-message; every later group is one recipient.
            foreach (var group in status.StatusGroups.Skip(1))
            {
                var action = group["Action"]?.Trim();
                var code = group["Status"]?.Trim();
                var recipient = StripType(group["Final-Recipient"] ?? group["Original-Recipient"]);
                var permanent = string.Equals(action, "failed", StringComparison.OrdinalIgnoreCase) && code?.StartsWith('5') == true;
                outcomes.Add(new SparkMailBounce(deliveryId, recipient, code, permanent, group["Diagnostic-Code"]?.Trim()));
            }
        }
        return outcomes;
    }

    /// <summary><c>rfc822; someone@example.org</c> → <c>someone@example.org</c>.</summary>
    private static string? StripType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var semicolon = value.IndexOf(';');
        return (semicolon >= 0 ? value[(semicolon + 1)..] : value).Trim();
    }
}

/// <summary>Applies parsed bounces: marks deliveries and suppresses permanent failures.</summary>
internal sealed partial class SparkMailBounceProcessor
{
    [Inject] private readonly SparkMailDeliveryStore deliveries;
    [Inject] private readonly ISparkMailSuppressions suppressions;
    [Inject] private readonly ILogger<SparkMailBounceProcessor> logger;

    public async Task<int> ApplyAsync(IReadOnlyList<SparkMailBounce> bounces, CancellationToken cancellationToken)
    {
        var suppressed = 0;
        foreach (var bounce in bounces)
        {
            SparkMailDelivery? delivery = bounce.DeliveryId is null ? null : await deliveries.LoadAsync(bounce.DeliveryId, cancellationToken);
            if (delivery is not null)
                await deliveries.RecordAsync(bounce.DeliveryId!, d =>
                {
                    d.Status = SparkMailDeliveryStatus.Bounced;
                    d.Detail = $"{bounce.Status} {bounce.Diagnostic}".Trim();
                }, cancellationToken);

            if (!bounce.Permanent)
                continue;

            // The delivery record is the authority on who the mail went to; a report's Final-Recipient
            // is only trusted when it names the same address (a forwarded bounce may name another).
            var email = delivery?.Email;
            if (email is null)
            {
                logger.LogInformation("Bounce {Status} without a known delivery (id {DeliveryId}); nothing suppressed.", bounce.Status, bounce.DeliveryId);
                continue;
            }
            await suppressions.SuppressAsync(email, SparkMailSuppressionReason.Bounce, stream: null, cancellationToken);
            suppressed++;
        }
        return suppressed;
    }
}

/// <summary><c>/spark/mail</c>.</summary>
internal sealed class SparkMailGroup : IEndpointGroup
{
    public static string Prefix => "/spark/mail";
}

/// <summary>
/// <c>POST /spark/mail/bounces[?recipient=…]</c> with the raw DSN as the body and
/// <c>Authorization: Bearer {Spark:Mail:Bounces:Endpoint:Secret}</c>. Answers 503 unless
/// <c>Spark:Mail:Bounces:Endpoint:Enabled</c> (temporary: the relay keeps the report). 204 when applied —
/// also for a report whose delivery id is unknown, which is logged and suppresses nothing — 401 on a
/// wrong secret, 413 when too large, 429 when flooded, 400 when unparseable. Never 404.
/// </summary>
[MemberOf<SparkMailGroup>]
internal sealed partial class ReceiveBounce : IPostEndpoint
{
    public static string Path => "/bounces";

    // ⚠️ EXPLICITLY exempt: the caller is the mail relay's pipe, which has no browser, no cookie and
    // no antiforgery token. Authenticated by the shared bearer secret instead. Stated, not absent.
    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));

    // Self-contained: a second UseRateLimiter() would halve the app's own budget (Spark's limiter
    // documents why), so the endpoint meters itself. 120 reports a minute is far above any relay.
    private static readonly FixedWindowRateLimiter Limiter = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 120,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    });

    [Inject] private readonly IOptions<SparkMailOptions> options;
    [Inject] private readonly ISparkMailBounceParser parser;
    [Inject] private readonly SparkMailBounceProcessor processor;
    [Inject] private readonly ILogger<ReceiveBounce> logger;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        using var lease = Limiter.AttemptAcquire();
        if (!lease.IsAcquired)
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);

        // Opt-in, but mapped with the rest of the assembly's endpoints. Disabled answers 503, never 404:
        // the relay's pipe DROPS a report on 400/404/413/422 and defers (EX_TEMPFAIL) on everything else,
        // so a 404 here would silently discard every bounce sent while the endpoint is switched off.
        var endpoint = options.Value.Bounces.Endpoint;
        if (!endpoint.Enabled)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        // The secret first, before a single byte of the body is parsed (#460, D9).
        if (!IsAuthorized(httpContext.Request.Headers.Authorization.ToString(), endpoint.Secret))
            return Results.Unauthorized();

        if (httpContext.Request.ContentLength > endpoint.MaxBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await httpContext.Request.Body.ReadAsync(chunk, httpContext.RequestAborted)) > 0)
        {
            if (buffer.Length + read > endpoint.MaxBodyBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;

        IReadOnlyList<SparkMailBounce> bounces;
        try
        {
            bounces = await parser.ParseAsync(buffer, httpContext.Request.Query["recipient"].FirstOrDefault(), httpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is FormatException or ParseException)
        {
            logger.LogWarning(ex, "A bounce report could not be parsed.");
            return Results.BadRequest();
        }

        var suppressed = await processor.ApplyAsync(bounces, httpContext.RequestAborted);
        logger.LogInformation("Bounce report: {Count} outcomes, {Suppressed} addresses suppressed.", bounces.Count, suppressed);
        return Results.NoContent();
    }

    /// <summary><c>Bearer {secret}</c>, compared in constant time.</summary>
    internal static bool IsAuthorized(string header, string? secret)
    {
        if (string.IsNullOrEmpty(secret) || !header.StartsWith("Bearer ", StringComparison.Ordinal))
            return false;
        var presented = Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(secret);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(presented), SHA256.HashData(expected));
    }
}

/// <summary>One-click unsubscribe tokens (RFC 8058): Data Protection, time-limited.</summary>
internal sealed class SparkMailUnsubscribeTokens(IDataProtectionProvider provider, IOptions<SparkMailOptions> options, IConfiguration configuration)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromDays(90);
    private readonly ITimeLimitedDataProtector protector = provider.CreateProtector("MintPlayer.Spark.MailManager.Unsubscribe.v1").ToTimeLimitedDataProtector();

    /// <summary>The public base URL, never the request Host.</summary>
    internal string? BaseUrl => (options.Value.PublicBaseUrl ?? configuration["Spark:Auth:PublicBaseUrl"])?.TrimEnd('/');

    public string? Link(string email, string stream)
        => BaseUrl is { Length: > 0 } baseUrl
            ? $"{baseUrl}/spark/mail/unsubscribe?t={Uri.EscapeDataString(protector.Protect($"{email}\n{stream}", Lifetime))}"
            : null;

    public (string Email, string Stream)? Read(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var value = protector.Unprotect(token);
            var newline = value.IndexOf('\n');
            return newline <= 0 ? null : (value[..newline], value[(newline + 1)..]);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>
/// <c>POST /spark/mail/unsubscribe?t={token}</c> — RFC 8058 one-click: the receiver's mail system
/// POSTs <c>List-Unsubscribe=One-Click</c>. Suppresses the token's address for its stream. 200 for a
/// valid token, 400 otherwise; never says whether the address exists.
/// </summary>
[MemberOf<SparkMailGroup>]
internal sealed partial class Unsubscribe : IPostEndpoint
{
    public static string Path => "/unsubscribe";

    // ⚠️ EXPLICITLY exempt: RFC 8058 posts come from mail providers, with no browser session. The
    // token (Data Protection, 90 days) is the authentication; it can only unsubscribe its own stream.
    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));

    [Inject] private readonly SparkMailUnsubscribeTokens tokens;
    [Inject] private readonly ISparkMailSuppressions suppressions;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (tokens.Read(httpContext.Request.Query["t"].FirstOrDefault()) is not { } target)
            return Results.BadRequest();
        await suppressions.SuppressAsync(target.Email, SparkMailSuppressionReason.Unsubscribe, target.Stream, httpContext.RequestAborted);
        return Results.Text("You are unsubscribed.", "text/plain");
    }
}
