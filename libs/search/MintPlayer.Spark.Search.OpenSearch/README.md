# MintPlayer.Spark.Search.OpenSearch

An OpenSearch engine for [MintPlayer.Spark.Search](../MintPlayer.Spark.Search/README.md).

```csharp
spark.AddMessaging();
spark.AddSearch(search => search.UseOpenSearch(o => o.Uri = new Uri("http://search:9200")));
```

- Search-as-you-type over a `search_as_you_type` field, every word required, typos tolerated
  (`fuzziness: AUTO`) for the types with fuzzy search.
- Kept in sync from the app's generated `Spark_Search` RavenDB index every `SyncInterval` (5 s). Soft deletes are
  copied like any change; hard deletes reach the engine as durable Spark Messaging messages, so
  `spark.AddMessaging()` is required.
- A new or wiped engine index is filled from the start. `ISparkSearchReindex.ReindexAsync()` rebuilds it.
- While the engine is unreachable, the RavenDB index answers (a 30 s circuit breaker), with the same labels.
- The engine only proposes candidates. Every hit is re-read through Spark as the caller, so rights and row
  security apply as on the list page.

- Document ids are stored lower-cased in the engine, so a hard delete (stored casing) hits the row the sync copied
  from the index (lower-cased).

Run the engine on an internal network only: Spark talks to it without credentials.

The tests (`MintPlayer.Spark.Tests/Search/OpenSearchEngineTests`) run against a real engine and skip unless
`SPARK_OPENSEARCH_URI` names one, e.g. `opensearchproject/opensearch:3.9.0` with `DISABLE_SECURITY_PLUGIN=true`.

Design: `docs/spark_search_PRD.md` D6–D8, D13.
