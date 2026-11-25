# MintPlayer.Spark.Dashboards

Administrator dashboards for [MintPlayer.Spark](https://github.com/MintPlayer/MintPlayer.Spark). A dashboard is a
**live virtual persistent object**: its model type has `"live": true` and no `clrType`, `IDashboardProvider`s fill
its attribute values, and the package pushes it to viewers over `/spark/live`. Nothing else: no widget endpoint, no
authorization path of its own, no push channel of its own.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>();
    spark.AddDashboards(dashboards => dashboards
        .Add("OperatorDashboard", d => d
            .Provider<OperatorFigures>()      // your IDashboardProvider
            .DatabaseStatistics()            // built in: size, documents, collections
            .HostMetrics()                   // built in: host disk, container memory and CPU
            .History("HostDiskUsedPercent"))); // sampled every 60 s into the store
});

public sealed class OperatorDashboardActions(IServiceProvider services) : SparkDashboardActions(services);
```

`App_Data/security.json` binds the package's slot and grants the app's dashboard to it:

```json
"bindings": { "dashboards:administrators": [ "Administrators" ] },
"rights": [ { "key": "operator-dashboard", "resource": "Read/OperatorDashboard", "groupId": "dashboards:administrators" } ]
```

The package works on every Spark store. It ships its own `DatabaseStatistics` dashboard (menu: *Dashboards →
Database*): the size on disk, the document count and a table of the collections, from the store's own statistics
(`ISparkStoreStatistics`; memory has no sizes).

History keeps raw points 7 days and a 5-minute rollup 90 days:

- **RavenDB**: on `SparkDashboards/{name}` documents, one single-valued time series per value; that collection has
  revisions disabled.
- **SQL Server, PostgreSQL, memory**: in the `SparkDashboardSamples` collection, a row per value and slot plus one per
  rollup interval, which the store's expiration sweep retires. Synchronize adds its table to the app's migration.

One instance samples each slot, whichever store: the lease is an `ISparkAtomicValues` value, `dashboards/sampler`.

The Angular side is `@mintplayer/ng-spark/dashboards`: the tile layout (`withDashboards()`) and the
`spark-meter`, `spark-stat` and `spark-dashboard-table` renderers.

Guide: [docs/guide-dashboards.md](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-dashboards.md).
