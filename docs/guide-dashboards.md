# Dashboards: live pages of figures for administrators

Disk usage, document counts, refused requests, queue lengths: figures an administrator watches rather than
edits belong on a dashboard (`MintPlayer.Spark.Dashboards`, alias `dashboards`; npm
`@mintplayer/ng-spark/dashboards`). A dashboard is an ordinary Spark page: a **live virtual persistent object**
whose attribute values your providers fill. The package adds the providers, a notifier that pushes the page over
`/spark/live`, a shared read cache, optional history on RavenDB time series, and a tile layout. It adds no endpoint,
no authorization path and no push channel of its own: rights, the load, the live socket and the menu are Spark's.

Decisions and evidence: [coverage_commit_graph_PRD.md](coverage_commit_graph_PRD.md) §0 D11–D14, D24, D29 and
[spark_dashboards_PRD.md](spark_dashboards_PRD.md). Package reference:
[README](../libs/dashboards/MintPlayer.Spark.Dashboards/README.md).

## Declaring a dashboard

Three pieces: the model type, a one-line actions class and the registration.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>();
    spark.AddDashboards(dashboards => dashboards
        .Add("OperatorDashboard", d => d
            .Provider<OperatorFigures>()    // yours, below
            .DatabaseStatistics()           // built in
            .HostMetrics()                  // built in
            .History("HostDiskUsedPercent")));
});

// Spark finds a virtual type's actions by name; the class name is the dashboard's name + "Actions".
public sealed class OperatorDashboardActions(IServiceProvider services) : SparkDashboardActions(services);
```

`App_Data/Model/OperatorDashboard.json` declares the type **virtual and live**: no `clrType`, `"live": true`, one
attribute per value, each with the renderer that draws it.

```json
{
  "persistentObject": {
    "id": "458035ba-1fc4-4f98-b24c-de1edbf8214c",
    "name": "OperatorDashboard",
    "alias": "operator-dashboard",
    "live": true,
    "groups": [ { "id": "…", "name": "Usage", "order": 1 } ],
    "attributes": [
      { "id": "…", "name": "Users", "dataType": "number", "showedOn": "PersistentObject", "group": "…", "renderer": "spark-stat" },
      { "id": "…", "name": "HostDisk", "dataType": "string", "showedOn": "PersistentObject", "renderer": "spark-meter",
        "rendererOptions": { "format": "bytes" } },
      { "id": "…", "name": "HostDiskUsedPercentHistory", "dataType": "string", "showedOn": "PersistentObject",
        "renderer": "spark-trend", "rendererOptions": { "width": "100%", "height": "48px" } }
    ]
  }
}
```

`UseSpark()` refuses to start when a declared dashboard has no model type, its type has a `clrType` or is not
live, or its actions class is missing; the message names each. The page answers one object id, `main` by default
(`.ObjectId("…")` changes it): the program unit's `objectId`. Any other id is a 404.

## Granting who may open it

The package ships its rights as a [library layer](guide-library-layers.md): `Read/DatabaseStatistics` (its own
dashboard, below) granted to the slot `dashboards:administrators`. Bind the slot in `App_Data/security.json`, or
**startup is refused**, and grant your own dashboards to the same slot:

```json
"bindings": { "dashboards:administrators": [ "Administrators" ] },
"rights": [
  { "key": "operator-dashboard", "resource": "Read/OperatorDashboard", "groupId": "dashboards:administrators" }
]
```

`Read` only: a dashboard is a page, not a list, and nobody edits it. The load runs **after** Spark's `Read` check,
so a provider never runs for a caller without the right. Such a caller gets Spark's one answer for unknown,
forbidden and missing: **404** on the load (an anonymous caller may be refused earlier, as on any page), and a
`gone` frame on a `/spark/live` subscription. Nothing tells them the dashboard exists.

The menu gains *Dashboards → Database* (the shipped dashboard), visible only to the bound group. Remove it with
`{ "id": "dcb89939-15e3-55e2-9231-2d6917dbf2ba", "$remove": true }` in your `programUnits.json`. Your own
dashboards get program units like any page (`"type": "persistentObject"`, `"objectId": "main"`).

## Writing a provider

```csharp
internal sealed class OperatorFigures(IDocumentStore store) : IDashboardProvider
{
    public async Task FillAsync(DashboardValues values, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        values.Set("Users", await session.Query<SparkUser>().CountAsync(cancellationToken));
        values.Set("Disk", DashboardValues.Meter(usedBytes, totalBytes));   // { value, max } for spark-meter
        if (!values.IsSampling)                                             // the sampler needs no tables
            values.Set("Collections", rows);                                // a list or dictionary: JSON
    }
}
```

- **It runs as the system, not as the viewer.** One reading is shared by every viewer for
  `Spark:Dashboards:CacheDuration` (5 s), and the sampler runs it with nobody signed in. Only callers holding `Read`
  see the page, so a value must not depend on who asks. Use a raw `IDocumentStore` session.
- **Each reading gets its own DI scope**; constructor injection works. Several providers may fill one dashboard;
  the last `Set` of a name wins. A name the model does not declare is dropped; so is one the caller may not read
  (an attribute-level right).
- **A failing provider leaves its values empty** (the tile shows a dash) and logs the exception; it never fails the
  page, and its exception text never reaches a value. No mount paths, device names or environment values in a value:
  labels are the model's.

## Built-in providers

| Builder call | Attributes | Source |
|---|---|---|
| `.DatabaseStatistics()` | `DatabaseSize` (bytes on disk), `DocumentCount`, `CollectionCount`, `Collections` (`name`, `documents`, `sizeBytes`, largest first) | RavenDB's detailed statistics, through the application's own store and certificate (D24) |
| `.HostMetrics()` | `HostDisk` (meter: used of total), `HostDiskFree`, `HostDiskUsedPercent`, `Memory` (meter: in use of the limit), `MemoryUsed`, `Cpu` (percent) | `DriveInfo("/")`, cgroup v2 `memory.current` / `memory.max` / `cpu.stat`, `/proc/meminfo` |

Inside a container, `DriveInfo("/")` measures **the host's disk**: overlay2 reports its backing filesystem, and S10
measured identical totals inside the app container and on the host. Memory is the container's `memory.current`
(page cache included) against `memory.max`, or the host's `MemTotal` when the limit is `max` (production, S10); CPU
is the container's `usage_usec` since the previous reading over the cores `cpu.max` allows. Host-wide RAM and CPU are
not shown: a container sees only host totals without its own share. On a development machine without cgroups the
process's own figures stand in, and nothing throws. The same readings are public for your own providers:
`SparkHostMetrics.ReadDisk()`, `SparkHostMetrics.ReadMemory()` and the singleton `SparkCpuUsage`.

The package's own **DatabaseStatistics** dashboard uses the first, with the history of `DatabaseSize` and
`DocumentCount`.

## History

`.History("Name", …)` keeps a time series of each named value (D14, D29):

- **Sampled every `Spark:Dashboards:SampleInterval` (60 s)** into one **single-valued** RavenDB time series per value,
  named after it, on the document `SparkDashboards/{dashboard}` (the declared name, which an application rename of the
  type never changes, D28). Only a number, or a meter's `value`, is written; a
  table or text is skipped with a warning. Six values per entry silently disabled RavenDB's rollup and retention in
  S11, so there is no multi-valued form.
- **One host per slot.** A slot is `floor(utcNow / interval)`; a host appends only after a compare-exchange
  (`spark/dashboards/sampler`, `{NodeId, ExpiresAtUtc, Fence, LastSlot}`) let it claim the slot, and it appends at the
  slot's start, so a second write of a slot would overwrite rather than add.
- **Retention by policy, not pruning:** raw points 7 days (`RawRetention`), a 5-minute rollup (`RollupInterval`) kept
  90 days (`RollupRetention`). The collection is configured at startup with `ConfigureRawTimeSeriesPolicyOperation`
  and `ConfigureTimeSeriesPolicyOperation`, which touch this collection only; other collections' policies are kept.
  If that fails, nothing is sampled: a series without retention grows for ever.
- **Revisions off** for `SparkDashboards`, by read-modify-write of the revisions configuration (other collections
  keep theirs). Time-series appends would otherwise cut revisions (the Community licence caps them, `CLAUDE.md`).
- **Read back** into an attribute named `{Name}History`, when the model declares one: `HistoryPoints` (200) buckets
  over `HistoryWindow` (7 days) of averages, as `[{ "x": epochMs, "y": average }]` for `spark-trend`. One grouped query,
  every value a parameter; the series name is checked against `RqlIdentifier` before it is spliced. Past the raw
  retention, RavenDB answers from the rollup.
- `Spark:Dashboards:Sample = false` stops this process sampling (another host's sampler then takes every slot).

## The page

```ts
// app.config.ts
provideSparkScoreRenderers([...appRenderers, ...sparkDashboardRenderers]),   // spark-trend comes from here
provideSparkDashboards(),
```

`provideSparkDashboards()` registers a [detail layout](#detail-layouts) that draws every dashboard (an object the
server marks with `metadata.dashboard`) as tiles instead of the attribute cards: one section per model group, one tile
per readable attribute, its label above its renderer. `columnSpan` 2 takes two columns, 3 or more the whole row (a
table without a `columnSpan` takes the row too). The page follows the object over `/spark/live` like any live type,
so the tiles change in place every `RefreshInterval` (15 s); the footer says when the figures were read. The action
bar, sub-queries, `extraContentTemplate` (SeoTester puts its abuse log there) and detail panels stay.

| Renderer | Value | `rendererOptions` |
|---|---|---|
| `spark-stat` | a number | `format` (`number`, `bytes`, `percent`, `date`, `text`), `decimals`, `suffix`, `warning`, `danger`, `direction` (`above` default, `below` for "low is bad") |
| `spark-meter` | `{ value, max }` (`DashboardValues.Meter`) or a number against `max` | `format`, `max`, `warning` (75 %), `danger` (90 %), `label` |
| `spark-dashboard-table` | a list of objects, or a dictionary (rows `{ key, value }`) | `columns: [{ key, label, format, translate }]` (`label` and `translate` are translation keys; `format` also `boolean`), `empty` |
| `spark-trend` | `{Name}History` points | see [score renderers](guide-score-renderers.md) |

### Detail layouts

The tile layout is one use of `SPARK_DETAIL_LAYOUTS` (`@mintplayer/ng-spark/panels`): a layout whose
`matches(item, entityType)` is true draws that object's attributes in place of the tabs and cards. Register one with
`provideSparkDetailLayouts({ id, component, matches })`; the component declares
`context = input.required<SparkDetailContext>()`. The first match wins.

## Options

`Spark:Dashboards` in configuration, then `spark.AddDashboards(options => …, dashboards => …)` in code:

| Option | Default | |
|---|---|---|
| `RefreshInterval` | 15 s | how often every dashboard is pushed to its viewers |
| `CacheDuration` | 5 s | how long one reading is shared |
| `SampleInterval` | 60 s | the history slot |
| `RawRetention` / `RollupInterval` / `RollupRetention` | 7 d / 5 min / 90 d | the collection's policies |
| `HistoryWindow` / `HistoryPoints` | 7 d / 200 | what a `{Name}History` attribute shows |
| `Sample` | true | whether this process samples |
