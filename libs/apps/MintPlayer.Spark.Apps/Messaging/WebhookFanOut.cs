using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Apps.Catalog;
using MintPlayer.Spark.Apps.Models;
using MintPlayer.Spark.Apps.Services;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Webhooks;
using Raven.Client.Documents;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Apps.Messaging;

/// <summary>
/// Turns a <see cref="WebhookEventRaised"/> into one <see cref="WebhookDelivery"/> per destination of its owner: each
/// subscribed, enabled direct endpoint, and the webhook of each App installed on the owner whose granted permissions
/// cover the event (D30). Delivery ids are derived from the event, so a retried fan-out finds the deliveries it already
/// made and makes none twice.
/// </summary>
[Register(typeof(IRecipient<WebhookEventRaised>), Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)]
internal sealed partial class WebhookFanOut : IRecipient<WebhookEventRaised>
{
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly WebhookScheduler scheduler;
    [Inject] private readonly WebhookEventCatalog catalog;
    [Inject] private readonly IOptions<SparkAppsOptions> options;

    public async Task HandleAsync(WebhookEventRaised message, CancellationToken cancellationToken = default)
    {
        var owner = new WebhookOwner(message.OwnerKind, message.OwnerKey);
        for (var attempt = 1; ; attempt++)
        {
            using var session = store.OpenAsyncSession();
            // A chain changed by another event in between is read again; so is a delivery made by a concurrent run.
            session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;

            var registry = await session.LoadAsync<WebhookOwnerRegistry>(WebhookOwnerRegistry.IdFor(owner).Value, cancellationToken);
            if (registry is not { HasDestinations: true })
                return;

            var targets = new List<(WebhookEndpoint Endpoint, string Payload)>();
            var endpoints = await session.LoadAsync<WebhookEndpoint>(registry.EndpointIds, cancellationToken);
            targets.AddRange(endpoints.Values
                .Where(e => e is not null && e.Enabled && e.Subscribes(message.EventType))
                .Select(e => (e!, message.Payload)));
            targets.AddRange(await InstallationTargetsAsync(session, registry, message, cancellationToken));
            if (targets.Count == 0)
                return;

            var ids = targets.Select(t => WebhookDelivery.IdFor(t.Endpoint.PublicId, message.CreatedUtc, message.MessageId).Value).ToList();
            var existing = await session.LoadAsync<WebhookDelivery>(ids, cancellationToken);
            for (var i = 0; i < targets.Count; i++)
            {
                if (existing.TryGetValue(ids[i], out var made) && made is not null)
                    continue;
                await scheduler.ScheduleAsync(session, New(ids[i], message, targets[i].Endpoint, targets[i].Payload), bypassChain: false, cancellationToken);
            }

            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (ConcurrencyException) when (attempt < 10)
            {
            }
        }
    }

    /// <summary>
    /// The webhooks of the Apps installed on the owner that may see this event: not suspended, the App subscribes to it,
    /// and the installation's granted permissions (not the App's requested ones) cover the event's permission. Each
    /// payload names the installation, so the App knows which token to ask for.
    /// </summary>
    private async Task<IEnumerable<(WebhookEndpoint, string)>> InstallationTargetsAsync(
        Raven.Client.Documents.Session.IAsyncDocumentSession session, WebhookOwnerRegistry registry, WebhookEventRaised message, CancellationToken cancellationToken)
    {
        if (registry.InstallationIds.Count == 0 || catalog.Find(message.EventType) is not { } descriptor)
            return [];
        var installations = (await session.LoadAsync<AppInstallation>(registry.InstallationIds, cancellationToken)).Values
            .Where(i => i is not null && !i.Suspended && (descriptor.Permission is not { } needed || SparkAppManager.Covers(i.Permissions, needed)))
            .ToList();
        if (installations.Count == 0)
            return [];
        var apps = await session.LoadAsync<SparkApp>(installations.Select(i => SparkApp.IdFor(i!.AppSlug).Value).Distinct(), cancellationToken);
        var subscribed = installations
            .Select(i => (Installation: i!, App: apps.GetValueOrDefault(SparkApp.IdFor(i!.AppSlug).Value)))
            .Where(x => x.App is { WebhookEndpointId: not null } && x.App.Events.Contains(message.EventType, StringComparer.Ordinal))
            .ToList();
        if (subscribed.Count == 0)
            return [];
        var hooks = await session.LoadAsync<WebhookEndpoint>(subscribed.Select(x => WebhookEndpoint.IdFor(x.App!.WebhookEndpointId!).Value), cancellationToken);
        return subscribed
            .Select(x => (Hook: hooks.GetValueOrDefault(WebhookEndpoint.IdFor(x.App!.WebhookEndpointId!).Value), x.Installation))
            .Where(x => x.Hook is { Enabled: true })
            .Select(x => (x.Hook!, WithInstallation(message.Payload, x.Installation)))
            .ToList();
    }

    /// <summary>The envelope with <c>"installation": { "id": "in_…" }</c> beside <c>data</c>, as GitHub does.</summary>
    internal static string WithInstallation(string envelope, AppInstallation installation)
    {
        var node = JsonNode.Parse(envelope)!.AsObject();
        node["installation"] = new JsonObject { ["id"] = installation.PublicId };
        return node.ToJsonString(WebhookJson.Options);
    }

    private WebhookDelivery New(string id, WebhookEventRaised message, WebhookEndpoint endpoint, string payload) => new()
    {
        Id = Id<WebhookDelivery>.FromTrusted(id),
        MessageId = message.MessageId,
        EventType = message.EventType,
        EndpointId = endpoint.PublicId,
        Payload = payload,
        Status = WebhookDeliveryStatus.Pending,
        CreatedUtc = message.CreatedUtc,
        ExpiresAtUtc = message.CreatedUtc + options.Value.DeliveryRetention,
    };
}
