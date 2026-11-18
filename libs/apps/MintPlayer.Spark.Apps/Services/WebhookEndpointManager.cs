using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Apps.Catalog;
using MintPlayer.Spark.Apps.Models;
using MintPlayer.Webhooks;
using Raven.Client.Documents;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Apps.Services;

public sealed record WebhookEndpointView(
    string Id, string OwnerKind, string OwnerKey, string Url, string? Description, IReadOnlyList<string> Events,
    bool Enabled, string? DisabledReason, DateTime CreatedUtc, DateTime? LastSuccessUtc, DateTime? FailingSinceUtc,
    DateTime? PreviousSecretExpiresUtc);

/// <summary>A new or rotated secret. Shown once: the site keeps it encrypted and never returns it again.</summary>
public sealed record WebhookEndpointSecret(WebhookEndpointView Endpoint, string Secret);

public sealed record WebhookDeliveryView(
    string Id, string MessageId, string EventType, WebhookDeliveryStatus Status, DateTime CreatedUtc, DateTime? CompletedUtc,
    IReadOnlyList<WebhookDeliveryAttempt> Attempts, string? RedeliveryOf, string Payload);

public sealed record WebhookEndpointInput(string? OwnerKind, string? OwnerKey, string? Url, string? Description, string[]? Events);

public sealed record WebhookEndpointChange(string? Id, string? Url, string? Description, string[]? Events);

public enum WebhookOutcome
{
    Ok,
    NotFound,
    Invalid,
    Conflict,
}

public sealed record WebhookResult<T>(WebhookOutcome Outcome, T? Value = default, string? Error = null)
{
    public static WebhookResult<T> Ok(T value) => new(WebhookOutcome.Ok, value);
    public static WebhookResult<T> NotFound() => new(WebhookOutcome.NotFound);
    public static WebhookResult<T> Invalid(string error) => new(WebhookOutcome.Invalid, Error: error);
    public static WebhookResult<T> Conflict(string error) => new(WebhookOutcome.Conflict, Error: error);
}

/// <summary>
/// Direct webhooks, as their owners manage them. Every call checks that the caller manages the owner
/// (<see cref="IWebhookOwnerProvider"/>); someone else's endpoint answers <see cref="WebhookOutcome.NotFound"/>, the
/// same as a missing one. Ids from requests are matched against their exact shape before any load.
/// </summary>
[Register(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)]
internal sealed partial class WebhookEndpointManager
{
    private static readonly Regex EndpointIdShape = new("^ep_[0-9a-f]{24}$", RegexOptions.CultureInvariant);
    private static readonly Regex DeliveryIdShape = new("^ep_[0-9a-f]{24}/[0-9a-f]{16}_msg_[0-9a-f]{32}(_r[0-9a-f]{8})?$", RegexOptions.CultureInvariant);

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly WebhookOwnerResolver owners;
    [Inject] private readonly WebhookEventCatalog catalog;
    [Inject] private readonly WebhookSecretProtector secrets;
    [Inject] private readonly WebhookScheduler scheduler;
    [Inject] private readonly IOptions<SparkAppsOptions> options;
    [Inject] private readonly TimeProvider time;

    public Task<IReadOnlyList<WebhookOwnerDescription>> ListOwnersAsync(string userId, CancellationToken cancellationToken)
        => owners.ListManageableAsync(userId, cancellationToken);

    public async Task<WebhookResult<IReadOnlyList<WebhookEndpointView>>> ListAsync(string? ownerKind, string? ownerKey, string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(ownerKind) || string.IsNullOrEmpty(ownerKey))
            return WebhookResult<IReadOnlyList<WebhookEndpointView>>.Invalid("ownerKind and ownerKey are required.");
        var owner = new WebhookOwner(ownerKind, ownerKey);
        if (!await owners.CanManageAsync(owner, userId, cancellationToken))
            return WebhookResult<IReadOnlyList<WebhookEndpointView>>.NotFound();

        using var session = store.OpenAsyncSession();
        var registry = await session.LoadAsync<WebhookOwnerRegistry>(WebhookOwnerRegistry.IdFor(owner), cancellationToken);
        if (registry is null)
            return WebhookResult<IReadOnlyList<WebhookEndpointView>>.Ok([]);
        var endpoints = await session.LoadAsync<WebhookEndpoint>(registry.EndpointIds, cancellationToken);
        return WebhookResult<IReadOnlyList<WebhookEndpointView>>.Ok(endpoints.Values.Where(e => e is not null).Select(e => View(e!)).OrderBy(e => e.CreatedUtc).ToList());
    }

    public async Task<WebhookResult<WebhookEndpointSecret>> CreateAsync(WebhookEndpointInput input, string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(input.OwnerKind) || string.IsNullOrEmpty(input.OwnerKey))
            return WebhookResult<WebhookEndpointSecret>.Invalid("ownerKind and ownerKey are required.");
        var owner = new WebhookOwner(input.OwnerKind, input.OwnerKey);
        if (!await owners.CanManageAsync(owner, userId, cancellationToken))
            return WebhookResult<WebhookEndpointSecret>.NotFound();
        if (ValidateUrl(input.Url) is { } urlError)
            return WebhookResult<WebhookEndpointSecret>.Invalid(urlError);
        if (ValidateEvents(input.Events) is { } eventsError)
            return WebhookResult<WebhookEndpointSecret>.Invalid(eventsError);
        if (input.Description is { Length: > 200 })
            return WebhookResult<WebhookEndpointSecret>.Invalid("The description is at most 200 characters.");

        using var session = store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
        var registryId = WebhookOwnerRegistry.IdFor(owner);
        var registry = await session.LoadAsync<WebhookOwnerRegistry>(registryId, cancellationToken);
        if (registry is null)
        {
            registry = new WebhookOwnerRegistry { Id = registryId, OwnerKind = owner.Kind, OwnerKey = owner.Key };
            await session.StoreAsync(registry, registryId, cancellationToken);
        }
        if (registry.EndpointIds.Count >= options.Value.MaxEndpointsPerOwner)
            return WebhookResult<WebhookEndpointSecret>.Invalid($"An owner has at most {options.Value.MaxEndpointsPerOwner} webhooks.");

        var secret = WebhookSecret.Generate();
        var endpoint = new WebhookEndpoint
        {
            Id = WebhookEndpoint.IdPrefix + "ep_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)),
            OwnerKind = owner.Kind,
            OwnerKey = owner.Key,
            Url = input.Url!.Trim(),
            Description = input.Description?.Trim(),
            Events = input.Events!.Distinct(StringComparer.Ordinal).ToList(),
            ProtectedSecret = secrets.Protect(secret),
            CreatedUtc = time.GetUtcNow().UtcDateTime,
        };
        await session.StoreAsync(endpoint, endpoint.Id, cancellationToken);
        registry.EndpointIds.Add(endpoint.Id);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyException)
        {
            return WebhookResult<WebhookEndpointSecret>.Conflict("The owner's webhooks changed meanwhile; try again.");
        }
        return WebhookResult<WebhookEndpointSecret>.Ok(new(View(endpoint), secret.ToString()));
    }

    public Task<WebhookResult<WebhookEndpointView>> UpdateAsync(WebhookEndpointChange change, string userId, CancellationToken cancellationToken)
        => WithEndpointAsync(change.Id, userId, async (session, endpoint) =>
        {
            if (endpoint.AppSlug is not null)
                return WebhookResult<WebhookEndpointView>.Invalid(AppOwned(endpoint));
            if (change.Url is not null)
            {
                if (ValidateUrl(change.Url) is { } urlError)
                    return WebhookResult<WebhookEndpointView>.Invalid(urlError);
                endpoint.Url = change.Url.Trim();
            }
            if (change.Events is not null)
            {
                if (ValidateEvents(change.Events) is { } eventsError)
                    return WebhookResult<WebhookEndpointView>.Invalid(eventsError);
                endpoint.Events = change.Events.Distinct(StringComparer.Ordinal).ToList();
            }
            if (change.Description is not null)
            {
                if (change.Description.Length > 200)
                    return WebhookResult<WebhookEndpointView>.Invalid("The description is at most 200 characters.");
                endpoint.Description = change.Description.Trim();
            }
            await session.SaveChangesAsync(cancellationToken);
            return WebhookResult<WebhookEndpointView>.Ok(View(endpoint));
        }, cancellationToken);

    public Task<WebhookResult<bool>> DeleteAsync(string? id, string userId, CancellationToken cancellationToken)
        => WithEndpointAsync(id, userId, async (session, endpoint) =>
        {
            if (endpoint.AppSlug is not null)
                return WebhookResult<bool>.Invalid(AppOwned(endpoint));
            var registry = await session.LoadAsync<WebhookOwnerRegistry>(WebhookOwnerRegistry.IdFor(endpoint.Owner), cancellationToken);
            registry?.EndpointIds.Remove(endpoint.Id!);
            session.Delete(endpoint);
            if (await session.LoadAsync<WebhookChain>(WebhookChain.IdFor(endpoint.PublicId), cancellationToken) is { } chain)
                session.Delete(chain);
            await session.SaveChangesAsync(cancellationToken);
            return WebhookResult<bool>.Ok(true);
        }, cancellationToken);

    /// <summary>A new secret; the old one keeps signing beside it for <see cref="SparkAppsOptions.SecretRotationOverlap"/>.</summary>
    public Task<WebhookResult<WebhookEndpointSecret>> RotateSecretAsync(string? id, string userId, CancellationToken cancellationToken)
        => WithEndpointAsync(id, userId, async (session, endpoint) =>
        {
            var secret = WebhookSecret.Generate();
            endpoint.PreviousProtectedSecret = endpoint.ProtectedSecret;
            endpoint.PreviousSecretExpiresUtc = time.GetUtcNow().UtcDateTime + options.Value.SecretRotationOverlap;
            endpoint.ProtectedSecret = secrets.Protect(secret);
            await session.SaveChangesAsync(cancellationToken);
            return WebhookResult<WebhookEndpointSecret>.Ok(new(View(endpoint), secret.ToString()));
        }, cancellationToken);

    /// <summary>Re-enables a disabled endpoint, clears its failure streak and resumes a halted ordered chain.</summary>
    public async Task<WebhookResult<WebhookEndpointView>> EnableAsync(string? id, string userId, CancellationToken cancellationToken)
    {
        var result = await WithEndpointAsync(id, userId, async (session, endpoint) =>
        {
            endpoint.Enabled = true;
            endpoint.DisabledReason = null;
            endpoint.ConsecutiveFailures = 0;
            endpoint.FailingSinceUtc = null;
            endpoint.LastFailureUtc = null;
            await session.SaveChangesAsync(cancellationToken);
            return WebhookResult<WebhookEndpointView>.Ok(View(endpoint));
        }, cancellationToken);
        if (result.Outcome == WebhookOutcome.Ok)
            await scheduler.ResumeAsync(result.Value!.Id, cancellationToken);
        return result;
    }

    /// <summary>Sends a <c>ping</c> event to this endpoint alone, whatever it subscribes to. Returns the delivery's id.</summary>
    public Task<WebhookResult<string>> PingAsync(string? id, string userId, CancellationToken cancellationToken)
        => WithEndpointAsync(id, userId, async (session, endpoint) =>
        {
            if (!endpoint.Enabled)
                return WebhookResult<string>.Invalid("The endpoint is disabled; enable it first.");
            var deliveryId = await SchedulePingAsync(session, endpoint, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
            return WebhookResult<string>.Ok(deliveryId);
        }, cancellationToken);

    /// <summary>A <c>ping</c> to <paramref name="endpoint"/> in <paramref name="session"/>, outside any ordered chain; returns the delivery id.</summary>
    internal async Task<string> SchedulePingAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, WebhookEndpoint endpoint, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var messageId = WebhookPublisher.NewMessageId(now);
        var data = new JsonObject { ["endpointId"] = endpoint.PublicId };
        if (endpoint.AppSlug is not null)
            data["app"] = endpoint.AppSlug;
        var delivery = new WebhookDelivery
        {
            Id = WebhookDelivery.IdFor(endpoint.PublicId, now.UtcDateTime, messageId),
            MessageId = messageId,
            EventType = WebhookEventCatalog.PingEvent,
            EndpointId = endpoint.PublicId,
            Payload = WebhookPublisher.Envelope(WebhookEventCatalog.PingEvent, now, data, options.Value.MaxPayloadBytes),
            CreatedUtc = now.UtcDateTime,
            ExpiresAtUtc = now.UtcDateTime + options.Value.DeliveryRetention,
        };
        await scheduler.ScheduleAsync(session, delivery, bypassChain: true, cancellationToken);
        return delivery.PublicId;
    }

    private static string AppOwned(WebhookEndpoint endpoint) => $"This webhook belongs to the App '{endpoint.AppSlug}'; change it there.";

    /// <summary>The endpoint's delivery log, newest first, <paramref name="pageSize"/> at a time after <paramref name="after"/>.</summary>
    public Task<WebhookResult<IReadOnlyList<WebhookDeliveryView>>> ListDeliveriesAsync(string? id, string? after, int pageSize, string userId, CancellationToken cancellationToken)
        => WithEndpointAsync(id, userId, async (session, endpoint) =>
        {
            if (after is not null && !DeliveryIdShape.IsMatch(after))
                return WebhookResult<IReadOnlyList<WebhookDeliveryView>>.Invalid("after is not a delivery id.");
            var page = await session.Advanced.LoadStartingWithAsync<WebhookDelivery>(
                WebhookDelivery.PrefixFor(endpoint.PublicId),
                pageSize: Math.Clamp(pageSize, 1, 100),
                startAfter: after is null ? null : WebhookDelivery.IdPrefix + after,
                token: cancellationToken);
            return WebhookResult<IReadOnlyList<WebhookDeliveryView>>.Ok(page.Select(View).ToList());
        }, cancellationToken);

    /// <summary>
    /// Sends a logged delivery again: a new log entry with the same payload and the same <c>webhook-id</c>, so a receiver
    /// that processed the original recognises it. It bypasses an ordered chain.
    /// </summary>
    public async Task<WebhookResult<string>> RedeliverAsync(string? deliveryId, string userId, CancellationToken cancellationToken)
    {
        if (deliveryId is null || !DeliveryIdShape.IsMatch(deliveryId))
            return WebhookResult<string>.NotFound();
        return await WithEndpointAsync(deliveryId[..deliveryId.IndexOf('/')], userId, async (session, endpoint) =>
        {
            if (await session.LoadAsync<WebhookDelivery>(WebhookDelivery.IdPrefix + deliveryId, cancellationToken) is not { } original)
                return WebhookResult<string>.NotFound();
            if (!endpoint.Enabled)
                return WebhookResult<string>.Invalid("The endpoint is disabled; enable it first.");
            var now = time.GetUtcNow().UtcDateTime;
            var copy = new WebhookDelivery
            {
                Id = WebhookDelivery.IdFor(endpoint.PublicId, now, original.MessageId, "_r" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))),
                MessageId = original.MessageId,
                EventType = original.EventType,
                EndpointId = endpoint.PublicId,
                Payload = original.Payload,
                CreatedUtc = now,
                ExpiresAtUtc = now + options.Value.DeliveryRetention,
                RedeliveryOf = original.PublicId,
            };
            await scheduler.ScheduleAsync(session, copy, bypassChain: true, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
            return WebhookResult<string>.Ok(copy.PublicId);
        }, cancellationToken);
    }

    private async Task<WebhookResult<T>> WithEndpointAsync<T>(string? id, string userId, Func<Raven.Client.Documents.Session.IAsyncDocumentSession, WebhookEndpoint, Task<WebhookResult<T>>> action, CancellationToken cancellationToken)
    {
        if (id is null || !EndpointIdShape.IsMatch(id))
            return WebhookResult<T>.NotFound();
        using var session = store.OpenAsyncSession();
        // The delivery engine patches the same document (failure streak, auto-disable): an edit made on a stale copy
        // must not undo that.
        session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
        var endpoint = await session.LoadAsync<WebhookEndpoint>(WebhookEndpoint.IdPrefix + id, cancellationToken);
        if (endpoint is null || !await owners.CanManageAsync(endpoint.Owner, userId, cancellationToken))
            return WebhookResult<T>.NotFound();
        try
        {
            return await action(session, endpoint);
        }
        catch (ConcurrencyException)
        {
            return WebhookResult<T>.Conflict("The webhook changed meanwhile; reload and try again.");
        }
    }

    internal string? ValidateUrl(string? url)
    {
        // A path is relative whatever the parse says: on Linux, Uri.TryCreate("/in", UriKind.Absolute) succeeds as file:///in.
        if (string.IsNullOrWhiteSpace(url) || url.TrimStart() is ['/' or '\\', ..] || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || url.Length > 2048)
            return "The URL must be an absolute URL.";
        if (uri.Scheme != Uri.UriSchemeHttps && !(options.Value.AllowInsecureUrls && uri.Scheme == Uri.UriSchemeHttp))
            return "The URL must use https.";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "The URL must not contain credentials.";
        return null;
    }

    private string? ValidateEvents(string[]? events)
    {
        if (events is null || events.Length == 0)
            return "Pick at least one event (or * for all).";
        var unknown = events.Where(e => e != "*" && !catalog.IsKnown(e)).ToList();
        return unknown.Count > 0 ? "Unknown events: " + string.Join(", ", unknown) : null;
    }

    private static WebhookEndpointView View(WebhookEndpoint e) => new(
        e.PublicId, e.OwnerKind, e.OwnerKey, e.Url, e.Description, e.Events, e.Enabled, e.DisabledReason, e.CreatedUtc,
        e.LastSuccessUtc, e.FailingSinceUtc, e.PreviousSecretExpiresUtc);

    private static WebhookDeliveryView View(WebhookDelivery d) => new(
        d.PublicId, d.MessageId, d.EventType, d.Status, d.CreatedUtc, d.CompletedUtc, d.Attempts, d.RedeliveryOf, d.Payload);
}
