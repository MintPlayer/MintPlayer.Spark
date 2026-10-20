using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Warehouse.Barcodes;
using MintPlayer.Spark.Warehouse.Items;
using MintPlayer.Spark.Warehouse.Locations;
using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Owners;
using MintPlayer.Spark.Warehouse.Stock;
using MintPlayer.Spark.Warehouse.Work;
using NSubstitute;

namespace MintPlayer.Spark.Warehouse.Tests;

/// <summary>Records what the warehouse tells replication (raw writes marked, row scopes changed).</summary>
internal sealed class RecordingReplication : IReplicaPublisher, IReplicaRowScopeChanges
{
    public List<(Type Type, string Id)> Marked { get; } = [];
    public List<string> ScopeChanges { get; } = [];

    public Task MarkChangedAsync(object session, Type sourceType, string id, CancellationToken cancellationToken = default)
    {
        lock (Marked)
            Marked.Add((sourceType, id));
        return Task.CompletedTask;
    }

    public Task ScopeChangedAsync(object session, string consumerModule, CancellationToken cancellationToken = default)
    {
        lock (ScopeChanges)
            ScopeChanges.Add(consumerModule);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A RavenDB test database with the warehouse's declared indexes, and a DI container holding the core's services the way
/// <c>AddWarehouse()</c> registers them (without the Spark host): the store, a fixed clock, the options, a recording
/// replication, a signed-in user and their organization memberships.
/// </summary>
public abstract class WarehouseTestBase : SparkTestDriver
{
    internal const string Site = "warehouse-sites/S1";
    internal const string OwnerA = "organizations/owner-a";
    internal const string OwnerB = "organizations/owner-b";

    internal readonly RecordingReplication Replication = new();
    internal readonly SparkWarehouseOptions Options = new();
    internal string? UserId = "users/staff-1";
    internal readonly Dictionary<string, OrganizationRole> Memberships = [];
    internal bool IsStaff = true;

    private ServiceProvider? _services;

    protected override IEnumerable<Assembly> IndexAssemblies => [typeof(Warehouse_Stock).Assembly];

    public override async Task DisposeAsync()
    {
        if (_services is not null)
            await _services.DisposeAsync();
        await base.DisposeAsync();
    }

    internal IServiceProvider Services => _services ??= Build();

    internal T Get<T>() where T : notnull => Services.CreateScope().ServiceProvider.GetRequiredService<T>();

    internal IWarehouseStock StockEngine => Get<IWarehouseStock>();

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(Store);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOptions<SparkWarehouseOptions>>(Microsoft.Extensions.Options.Options.Create(Options));
        services.AddSingleton<IReplicaPublisher>(Replication);
        services.AddSingleton<IReplicaRowScopeChanges>(Replication);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());

        var user = Substitute.For<ISparkCurrentUser>();
        user.Id.Returns(_ => UserId);
        user.IsAuthenticated.Returns(_ => UserId is not null);
        services.AddSingleton(user);
        var organizations = Substitute.For<ISparkOrganizations>();
        organizations.GetMembershipsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyDictionary<string, OrganizationRole>>(new Dictionary<string, OrganizationRole>(Memberships)));
        services.AddSingleton(organizations);
        // security.json as the warehouse library ships it: the staff slot holds AllOrganizations/{Type} (IsStaff = in the slot).
        var permissions = Substitute.For<IPermissionService>();
        permissions.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(IsStaff && call.ArgAt<string>(0) == OrganizationRights.AllOrganizations));
        services.AddSingleton(permissions);

        services.AddSingleton<IAllocationRule, FifoAllocationRule>();
        services.AddSingleton<IAllocationRule, FefoAllocationRule>();
        services.AddSingleton<IAllocationRule, ClosestLocationAllocationRule>();
        services.AddScoped<IWarehouseStock, WarehouseStock>();
        services.AddScoped<IStockLevelPublisher, StockLevelPublisher>();
        services.AddScoped<IRecallTrace, RecallTrace>();
        services.AddScoped<IWarehouseOwnerLinks, WarehouseOwnerLinks>();
        services.AddScoped<IWarehouseOwnerScope, WarehouseOwnerScope>();
        services.AddScoped<IReplicaRowFilter, WarehouseOwnerLinkRowFilter>();
        services.AddScoped<IWarehouseItems, WarehouseItems>();
        services.AddScoped<IWarehouseLocations, WarehouseLocations>();
        services.AddScoped<IBarcodeResolver, BarcodeResolver>();
        services.AddScoped<IBarcodeResolutionStep, Gs1BarcodeStep>();
        services.AddScoped<IBarcodeResolutionStep, OwnCodesBarcodeStep>();
        services.AddScoped<IBarcodeResolutionStep, RegistryBarcodeStep>();
        services.AddScoped<IWarehouseTasks, WarehouseTasks>();
        services.AddScoped<IWarehouseReplenishment, WarehouseReplenishment>();
        AddServices(services);
        return services.BuildServiceProvider();
    }

    /// <summary>A package's tests add its services here (WM4+: Inbound, Outbound, Packing, Counting), after the core's.</summary>
    protected virtual void AddServices(IServiceCollection services)
    {
    }

    // --- seeding -------------------------------------------------------------------------------------------------

    internal async Task SeedSiteAsync()
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new WarehouseSite { Id = Site, Code = "S1", Name = "Test site", TimeZone = "Europe/Brussels" }, Site);
        await session.StoreAsync(new WarehouseZone { Id = WarehouseZone.IdFor(Site, "Q"), SiteId = Site, Code = "Q", Kind = WarehouseZoneKind.Quarantine }, WarehouseZone.IdFor(Site, "Q"));
        await session.StoreAsync(new ReasonCode { Id = ReasonCode.IdFor("COUNT"), Code = "COUNT", Name = "Count variance", Kind = ReasonCodeKind.Adjustment }, ReasonCode.IdFor("COUNT"));
        await session.SaveChangesAsync();
    }

    internal async Task<string> LocationAsync(string code, WarehouseLocationType type = WarehouseLocationType.Storage, long walk = 0, string? zoneId = null,
        string? fixedItemId = null, decimal? min = null, decimal? max = null)
    {
        var id = WarehouseLocation.IdFor(Site, code);
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new WarehouseLocation
        {
            Id = id, SiteId = Site, Code = code, Barcode = code, Type = type, WalkSequence = walk, ZoneId = zoneId,
            FixedItemId = fixedItemId, Min = min, Max = max, Path = $"/{code}/",
        }, id);
        await session.SaveChangesAsync();
        return id;
    }

    internal async Task<string> ItemAsync(string owner, string sku, ItemTracking tracking = ItemTracking.None, int shelfLifeDays = 0)
    {
        var result = await Get<IWarehouseItems>().CreateAsync(new WarehouseItem { OrganizationId = owner, Sku = sku, Name = sku, Tracking = tracking, MinimumShelfLifeDays = shelfLifeDays });
        result.Succeeded.Should().BeTrue(result.Refusal?.ToString() ?? "");
        return result.ItemId!;
    }

    internal static StockOp Op(string id) => new(id, "users/staff-1", "device-1");

    internal async Task<T?> LoadAsync<T>(string id) where T : class
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<T>(id);
    }
}
