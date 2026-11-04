# Site Search

One search box for the whole application: it finds objects of every searchable type, suggests them while the visitor
types, lands on the one object a term names, and lists the rest on a results page. Browsers can add the site as a
search engine. This is not the search box of a query list; that one is in [Full-Text Search](guide-search.md).

Design and evidence: `docs/spark_search_PRD.md`.

## Turning it on

Server, three packages, each optional on top of the first:

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddSearch();                                               // MintPlayer.Spark.Search
    spark.AddOpenSearchDescription(o => o.ShortName = "MintPlayer"); // MintPlayer.Spark.OpenSearchDescription
});
```

Opt a type in from its model JSON. Nothing is searchable until a type says so:

```json
"search": { "enabled": true, "label": "{Title} - {Artist}", "fields": ["Lyrics"], "fuzzy": true }
```

| Key | Default | |
|---|---|---|
| `enabled` | `false` | The type takes part. |
| `label` | the `breadcrumb` | What a suggestion shows, filled from the visitor's own read of the row. |
| `fields` | none | Searched **in addition to** the label's attributes, never instead of them. |
| `fuzzy` | `SparkSearchOptions.Fuzzy` (on) | Tolerate a typo (`bohemain` finds *Bohemian Rhapsody*). |

Only string and string-collection properties are searched; a number or a reference in `fields` is left out.

Client (`@mintplayer/ng-spark/search`):

```ts
// app.config.ts
provideSpark(withSearch(), withOpenSearchDescription({ title: 'MintPlayer' }))
// app.routes.ts
export const routes: Routes = [ /* ... */ ...sparkSearchRoutes() ];
```

```html
<spark-site-search />   <!-- the box, e.g. in the top bar -->
```

`withOpenSearchDescription` adds `<link rel="search">` and brings `withSearch()` with it.

## The index lives in the application

The default provider is a RavenDB index, `Spark_Search` (`Spark/Search`), which a source generator writes **into the
application** from the model's `search` blocks: one map per type, Lucene (fuzzy needs it; the rest of the database
stays Corax). A library gets none; neither does an app without the search package. Change a `search` block and the
index changes with the next build, deployed side by side like any other index.

## What a visitor gets back

Every hit is **re-read through the type's list query as the visitor**. Query rights, row policies and attribute
redaction apply exactly as on the list page, so a row the visitor may not see is never a hit, never a suggestion, and
never counted. A provider (the index, or an engine) only proposes candidates.

Labels are unambiguous among what the visitor sees. Two visible songs called *Queen* are *Queen (Song 1)* and
*Queen (Song 2)*, numbered by id; a hidden third one does not make them 1 and 3. A visible song whose only namesake is
hidden is just *Queen*: the suffix would tell the visitor the other exists.

| Endpoint | Answers |
|---|---|
| `GET /spark/search?q=&take=` | Hits grouped by type on the results page; at most `MaxResults` (50). |
| `GET /spark/search/suggest?q=` | Suggestions, the last word as a prefix; at most `MaxSuggestions` (8). |
| `GET /spark/search/resolve?q=` | The one object a text names (a picked label, or a term only one visible object matches), else 404. |

All three are anonymous and rate limited per client address (`RateLimit` 120 per `RateLimitWindow`, one minute);
below `MinTermLength` (2) a term returns nothing. Terms are reduced to letters and digits before any provider sees
them, so no wildcard or operator from the visitor reaches a query.

Submitting the box opens the object the term names (`withSearch({ singleHit: 'item' })`, the default) or the results
page; `singleHit: 'searchPage'` always shows the results page. The results page is `noindex` and server-rendered: with
`AddPrerendering`, `/search?q=` arrives with its results (see [Server-side rendering](guide-ssr.md)).

## A search engine: OpenSearch

`MintPlayer.Spark.Search.OpenSearch` hands searches to an OpenSearch engine, for search-as-you-type over large data:

```csharp
spark.AddMessaging();
spark.AddSearch(s => s.UseOpenSearch(o => o.Uri = new Uri("http://search:9200")));
```

The engine is filled from `Spark_Search` every `SyncInterval` (5 s); hard deletes arrive as Spark Messaging messages.
While it is unreachable, the RavenDB index answers for 30 s at a time, with the same labels. Run the engine on an
internal network only: Spark talks to it without credentials. Details:
[the package README](../libs/search/MintPlayer.Spark.Search.OpenSearch/README.md).

## Writing a provider

Implement `ISparkSearchProvider` and register it with `s.UseProvider<TProvider>(services => ...)`:

- **Propose, never decide.** Return candidates (type name and id), best first. The core drops what the visitor may not
  read, so a provider needs no security and must not try to add any.
- **Ids in any casing.** RavenDB ids are case-insensitive and the core matches them so; an index projection answers
  `songs/1` for `Songs/1`, and that is fine.
- **Over-fetch is the core's job.** It asks for more than it shows, because some candidates disappear in the re-read.
- **Values as parameters.** A provider that queries RavenDB passes the term as a query argument, never spliced into
  RQL.
- `FindByLabelAsync` returns every object whose text has all the label's words; the core compares the rendered
  labels itself.

## Browsers: the OpenSearch description

`AddOpenSearchDescription` serves `/opensearch.xml` (short name at most 16 characters), browser suggestions at
`/spark/opensearch/suggest`, and `/spark/opensearch/search`, which redirects to the one object a term names or to the
results page. All anonymous, on the same rate limit. Options:
[the package README](../libs/search/MintPlayer.Spark.OpenSearchDescription/README.md).

## Testing

`MintPlayer.Spark.Tests/Search` runs the core against RavenDB with a provider that proposes everything, and deploys the
generator's real output (a snapshot of `SearchIndexGeneratorTests`). The OpenSearch tests need an engine and skip
without `SPARK_OPENSEARCH_URI`:

```powershell
docker run -d -p 9200:9200 -e discovery.type=single-node -e DISABLE_SECURITY_PLUGIN=true -e DISABLE_INSTALL_DEMO_CONFIG=true opensearchproject/opensearch:3.9.0
$env:SPARK_OPENSEARCH_URI = 'http://localhost:9200'
```

See also: [Full-Text Search](guide-search.md) · [Row security](guide-row-security.md) · [Server-side rendering](guide-ssr.md)
