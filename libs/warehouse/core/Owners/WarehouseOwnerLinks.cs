using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Rendering;
using MintPlayer.Spark.Warehouse.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Warehouse.Owners;

/// <summary>Owner links (Q-W23): resolve a module's owner id to the warehouse's organization, and back.</summary>
public interface IWarehouseOwnerLinks
{
    /// <summary>The active link for (module, external owner id), or null: an order of an unlinked owner is <c>Rejected(OwnerNotServed)</c>.</summary>
    Task<WarehouseOwnerLink?> ResolveAsync(string sourceModule, string externalOwnerId, CancellationToken cancellationToken = default);

    /// <summary>Every active link of <paramref name="sourceModule"/>; no index (an id-prefix load).</summary>
    Task<IReadOnlyList<WarehouseOwnerLink>> ForModuleAsync(string sourceModule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a link in <paramref name="session"/> and tells replication that the module's row scope changed
    /// in the same commit (its stock levels, statuses, shipments and return receipts are re-filtered).
    /// </summary>
    Task<WarehouseOwnerLink> LinkAsync(IAsyncDocumentSession session, string sourceModule, string externalOwnerId, string organizationId,
        string? defaultSiteId = null, bool automatic = false, CancellationToken cancellationToken = default);

    /// <summary>Deactivates a link; the module stops receiving that owner's rows (and loses the ones it holds at the next reconcile).</summary>
    Task UnlinkAsync(IAsyncDocumentSession session, string sourceModule, string externalOwnerId, CancellationToken cancellationToken = default);
}

internal sealed partial class WarehouseOwnerLinks : IWarehouseOwnerLinks
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IReplicaRowScopeChanges scopeChanges;

    public async Task<WarehouseOwnerLink?> ResolveAsync(string sourceModule, string externalOwnerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceModule) || string.IsNullOrWhiteSpace(externalOwnerId))
            return null;
        using var session = documentStore.OpenAsyncSession();
        var link = await session.LoadAsync<WarehouseOwnerLink>(WarehouseOwnerLink.IdFor(sourceModule, externalOwnerId), cancellationToken);
        return link is { Active: true } ? link : null;
    }

    public async Task<IReadOnlyList<WarehouseOwnerLink>> ForModuleAsync(string sourceModule, CancellationToken cancellationToken = default)
    {
        using var session = documentStore.OpenAsyncSession();
        var prefix = $"{WarehouseOwnerLink.IdPrefix}{WarehouseIds.Module(sourceModule)}/";
        var links = new List<WarehouseOwnerLink>();
        var start = 0;
        while (true)
        {
            var page = (await session.Advanced.LoadStartingWithAsync<WarehouseOwnerLink>(prefix, start: start, pageSize: 1024, token: cancellationToken)).ToList();
            links.AddRange(page.Where(l => l.Active));
            if (page.Count < 1024)
                return links;
            start += page.Count;
        }
    }

    public async Task<WarehouseOwnerLink> LinkAsync(IAsyncDocumentSession session, string sourceModule, string externalOwnerId, string organizationId,
        string? defaultSiteId = null, bool automatic = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalOwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        var id = WarehouseOwnerLink.IdFor(sourceModule, externalOwnerId);

        // The row filter keys on OwnerId alone (the contracts carry no module), so an external owner id may be linked to
        // one module only: otherwise shop B, linking "A" too, would receive shop A's rows. A guard document per external
        // id, created with a must-not-exist change vector, makes two concurrent links of the same id conflict.
        var guardId = OwnerIdGuard.IdFor(externalOwnerId);
        var guard = await session.LoadAsync<OwnerIdGuard>(guardId, cancellationToken);
        var module = WarehouseIds.Module(sourceModule);
        if (guard is null)
            await session.StoreAsync(new OwnerIdGuard { Id = guardId, SourceModule = module }, string.Empty, guardId, cancellationToken);
        else if (!string.Equals(guard.SourceModule, module, StringComparison.OrdinalIgnoreCase))
        {
            var held = await session.LoadAsync<WarehouseOwnerLink>(WarehouseOwnerLink.IdFor(guard.SourceModule, externalOwnerId), cancellationToken);
            if (held is { Active: true })
                throw new InvalidOperationException(
                    $"The owner id '{externalOwnerId}' is already linked to module '{guard.SourceModule}'. Owner ids must be unique across the modules a warehouse serves; let '{module}' qualify its owner ids.");
            guard.SourceModule = module;
        }

        var link = await session.LoadAsync<WarehouseOwnerLink>(id, cancellationToken) ?? new WarehouseOwnerLink { Id = id };
        link.SourceModule = WarehouseIds.Module(sourceModule);
        link.ExternalOwnerId = externalOwnerId.Trim();
        link.OrganizationId = organizationId;
        link.DefaultSiteId = defaultSiteId;
        link.Automatic = automatic;
        link.Active = true;
        await session.StoreAsync(link, id, cancellationToken);
        await scopeChanges.ScopeChangedAsync(session, link.SourceModule, cancellationToken);
        return link;
    }

    public async Task UnlinkAsync(IAsyncDocumentSession session, string sourceModule, string externalOwnerId, CancellationToken cancellationToken = default)
    {
        var link = await session.LoadAsync<WarehouseOwnerLink>(WarehouseOwnerLink.IdFor(sourceModule, externalOwnerId), cancellationToken);
        if (link is null || !link.Active)
            return;
        link.Active = false;
        await scopeChanges.ScopeChangedAsync(session, link.SourceModule, cancellationToken);
    }
}

/// <summary>Which module an external owner id is linked to: <c>warehouse-owner-ids/{externalOwnerId}</c> (unique across modules).</summary>
public sealed class OwnerIdGuard
{
    public static string IdFor(string externalOwnerId) => "warehouse-owner-ids/" + Uri.EscapeDataString(externalOwnerId.Trim());

    public string? Id { get; set; }

    public string SourceModule { get; set; } = "";
}

/// <summary>
/// Replication's per-consumer row filter, bound to the owner links (Q-W23; PRD §4.2.3 "Row filter", §4.3): the four
/// contracts the warehouse publishes reach a shop module only for the owner ids linked to it. A module without a link
/// receives none of them. Every other collection is not restricted here.
/// </summary>
internal sealed partial class WarehouseOwnerLinkRowFilter : IReplicaRowFilter
{
    /// <summary>The warehouse-owned contracts, each carrying the owner as its producer named it in <c>OwnerId</c>.</summary>
    internal static readonly IReadOnlySet<string> FilteredCollections = new HashSet<string>(StringComparer.Ordinal)
    {
        FulfilmentCollections.OrderStatuses,
        FulfilmentCollections.Shipments,
        FulfilmentCollections.StockLevels,
        FulfilmentCollections.ReturnReceipts,
    };

    internal const string OwnerField = nameof(FulfilmentStockLevel.OwnerId);

    [Inject] private readonly IWarehouseOwnerLinks links;

    public async Task<ReplicaRowCondition?> GetConditionAsync(string consumerModule, string sourceCollection, CancellationToken cancellationToken)
    {
        if (!FilteredCollections.Contains(sourceCollection))
            return null;

        IReadOnlyList<WarehouseOwnerLink> linked;
        try
        {
            linked = await links.ForModuleAsync(consumerModule, cancellationToken);
        }
        catch (ArgumentException)
        {
            // A module name the link ids cannot hold has no link: nothing is delivered.
            linked = [];
        }
        return new ReplicaRowCondition(OwnerField, linked.Select(l => l.ExternalOwnerId));
    }
}
