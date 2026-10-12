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
            .History("HostDiskUsedPercent"))); // sampled every 60 s into a RavenDB time series
});

public sealed class OperatorDashboardActions(IServiceProvider services) : SparkDashboardActions(services);
```

`App_Data/security.json` binds the package's slot and grants the app's dashboard to it:

```json
"bindings": { "dashboards:administrators": [ "Administrators" ] },
"rights": [ { "key": "operator-dashboard", "resource": "Read/OperatorDashboard", "groupId": "dashboards:administrators" } ]
```

The package ships its own `DatabaseStatistics` dashboard (menu: *Dashboards → Database*). History lives on
`SparkDashboards/{name}` documents, one single-valued time series per value, raw points kept 7 days and a 5-minute
rollup 90 days; that collection has revisions disabled.

The Angular side is `@mintplayer/ng-spark/dashboards`: the tile layout (`provideSparkDashboards()`) and the
`spark-meter`, `spark-stat` and `spark-dashboard-table` renderers.

Guide: [docs/guide-dashboards.md](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-dashboards.md).
