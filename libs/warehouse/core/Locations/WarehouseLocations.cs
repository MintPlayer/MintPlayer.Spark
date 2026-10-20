using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Gs1;
using MintPlayer.Spark.Warehouse.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Warehouse.Locations;

/// <summary>
/// Sites, zones and locations (PRD §4.5, "Locations"): location ids from the site and the normalised code, check digits for
/// hand-keyed codes, the materialized path from the parent (WX9 grouping), and deactivation instead of deletion once a
/// location has ledger history.
/// </summary>
public interface IWarehouseLocations
{
    /// <summary>Fills a new location's id, barcode, check digits and path, and stores it; refuses a code the site already has.</summary>
    Task<WarehouseLocation> CreateAsync(WarehouseLocation location, CancellationToken cancellationToken = default);

    /// <summary>Whether <paramref name="keyed"/> are the check digits of <paramref name="code"/> (asked when a code is typed, §4.5).</summary>
    bool CheckDigitsMatch(string code, string? keyed);

    /// <summary>Deactivates a location (it stays, with its history). A location without history may be deleted instead.</summary>
    Task<bool> DeactivateAsync(string locationId, CancellationToken cancellationToken = default);
}

internal sealed partial class WarehouseLocations : IWarehouseLocations
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IOptions<SparkWarehouseOptions> options;

    public async Task<WarehouseLocation> CreateAsync(WarehouseLocation location, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        var code = WarehouseIds.Normalize(location.Code);
        using var session = documentStore.OpenAsyncSession();
        var site = await session.LoadAsync<WarehouseSite>(location.SiteId, cancellationToken)
            ?? throw new ArgumentException($"Unknown site '{location.SiteId}'.", nameof(location));

        location.Code = code;
        location.Id = WarehouseLocation.IdFor(site.Id!, code);
        if (string.IsNullOrWhiteSpace(location.Barcode))
            location.Barcode = code;
        location.CheckDigits = ComputeCheckDigits(code);

        var parent = location.ParentId is null ? null : await session.LoadAsync<WarehouseLocation>(location.ParentId, cancellationToken);
        location.Path = MaterializedPath.Child(parent?.Path ?? "/", code);
        if (location.ZoneId is { } zoneId
            && (await session.LoadAsync<WarehouseZone>(zoneId, cancellationToken) is not { } zone || zone.SiteId != site.Id))
            throw new ArgumentException($"Zone '{location.ZoneId}' is not a zone of site '{site.Code}'.", nameof(location));

        // Must not exist: a code is unique per site.
        await session.StoreAsync(location, string.Empty, location.Id, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return location;
    }

    public bool CheckDigitsMatch(string code, string? keyed)
        => LocationCheckDigits.IsValid(WarehouseIds.Normalize(code), keyed, options.Value.Locations.CheckDigitSecret, options.Value.Locations.CheckDigitLength);

    public async Task<bool> DeactivateAsync(string locationId, CancellationToken cancellationToken = default)
    {
        using var session = documentStore.OpenAsyncSession();
        var location = await session.LoadAsync<WarehouseLocation>(locationId, cancellationToken);
        if (location is null)
            return false;
        location.Active = false;
        await session.SaveChangesAsync(cancellationToken);
        return true;
    }

    private string ComputeCheckDigits(string code)
        => LocationCheckDigits.Compute(code, options.Value.Locations.CheckDigitSecret, options.Value.Locations.CheckDigitLength);
}

/// <summary>Materialized-path helpers (WX9): <c>/A/A-01/A-01-03/</c>, so a subtree is a prefix.</summary>
public static class MaterializedPath
{
    public static string Child(string parentPath, string code)
    {
        var parent = string.IsNullOrEmpty(parentPath) ? "/" : parentPath.EndsWith('/') ? parentPath : parentPath + "/";
        return parent + code + "/";
    }

    public static bool IsWithin(string path, string ancestorPath) => path.StartsWith(ancestorPath, StringComparison.OrdinalIgnoreCase);

    public static int Depth(string path) => path.Count(c => c == '/') - 1;
}
