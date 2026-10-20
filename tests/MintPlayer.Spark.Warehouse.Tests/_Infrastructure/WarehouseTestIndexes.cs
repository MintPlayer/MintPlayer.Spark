using System.Collections;
using System.Linq.Expressions;
using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Warehouse.Indexes;
using MintPlayer.Spark.Warehouse.Models;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Warehouse.Tests;

// The warehouse's declared indexes (X19) for tests: this project does not run the application's source generator, so each
// is built here from the library's own map expressions, under the name the generator gives it (Warehouse_Stock →
// Warehouse/Stock). The maps are the library's, not copies, so a change to one is tested.

internal static class MapOf
{
    public static Expression<Func<IEnumerable<TSource>, IEnumerable>> Untyped<TSource, TResult>(Expression<Func<IEnumerable<TSource>, IEnumerable<TResult>>> map)
        => Expression.Lambda<Func<IEnumerable<TSource>, IEnumerable>>(map.Body, map.Parameters);
}

public sealed class Warehouse_Stock : AbstractMultiMapIndexCreationTask<WarehouseStockEntry>
{
    public Warehouse_Stock()
    {
        AddMap(MapOf.Untyped(WarehouseIndexes.Balances));
        AddMap(MapOf.Untyped(WarehouseIndexes.Locations));
    }
}

public sealed class Warehouse_LedgerTotals : AbstractMultiMapIndexCreationTask<WarehouseLedgerEntry>
{
    public Warehouse_LedgerTotals()
    {
        AddMap(MapOf.Untyped(WarehouseIndexes.Ledger));
        StoreAllFields(FieldStorage.Yes);
    }
}

public sealed class Warehouse_Work : AbstractMultiMapIndexCreationTask<WarehouseWorkEntry>
{
    public Warehouse_Work()
    {
        AddMap(MapOf.Untyped(WarehouseIndexes.Tasks));
    }
}

public sealed class Warehouse_Catalog : AbstractMultiMapIndexCreationTask<WarehouseCatalogEntry>
{
    public Warehouse_Catalog()
    {
        AddMap(MapOf.Untyped<WarehouseItem, WarehouseCatalogEntry>(WarehouseIndexes.Items));
        AddMap(MapOf.Untyped<FulfilmentCatalogItem, WarehouseCatalogEntry>(WarehouseIndexes.CatalogItems));
        AddMap(MapOf.Untyped<BarcodeRegistration, WarehouseCatalogEntry>(WarehouseIndexes.Barcodes));
    }
}
