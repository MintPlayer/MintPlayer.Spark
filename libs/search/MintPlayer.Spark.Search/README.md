# MintPlayer.Spark.Search

Search across the entity types of a MintPlayer.Spark application (#238). Guide:
[Site Search](../../../docs/guide-site-search.md).

```csharp
builder.Services.AddSpark(builder.Configuration, spark => spark
    .AddSearch());
```

Opt a type in from its model JSON:

```json
"search": { "enabled": true, "label": "{Title} - {Artist}", "fields": ["Lyrics"], "fuzzy": true }
```

- `label` defaults to the type's `breadcrumb`. Two visible objects with the same label get the type's label, and a
  number among the namesakes of one type, so every suggestion names exactly one object.
- `fields` are searched besides the label's attributes. Only string and string-collection properties count.
- Every hit is re-read through the type's list query as the caller, so query rights, row policies and redaction
  apply exactly as on the list page. A provider only proposes candidates.
- By default the app's generated `Spark_Search` RavenDB index answers. The generator writes it into the application
  (never a library) when the app references this package. An engine package (`MintPlayer.Spark.Search.OpenSearch`)
  can take over; the RavenDB index stays the fallback while the engine is unreachable.
- With `AddPrerendering`, the SPA's results page (`/search?q=`) is server-rendered with its results.

Endpoints (anonymous, rate limited per address): `GET /spark/search?q=&take=`, `GET /spark/search/suggest?q=`,
`GET /spark/search/resolve?q=`.

## Options

`spark.AddSearch(s => ...)`:

| Option | Default | |
|---|---|---|
| `Fuzzy` | `true` | Typos tolerated; a type's `search.fuzzy` overrides it. |
| `MaxSuggestions` | 8 | |
| `MaxResults` | 50 | |
| `MinTermLength` | 2 | A shorter term returns nothing. |
| `RateLimit` / `RateLimitWindow` | 120 / 1 min | Per client address, across the three endpoints. |
| `SearchPagePath` | `/search` | The SPA's results page. |
| `UseProvider<T>(services)` | | Another `ISparkSearchProvider`; the RavenDB one stays the fallback. |

## The provider contract

An `ISparkSearchProvider` **proposes** candidates (type name, id), best first; it never decides what a caller sees.

- The core re-reads every candidate as the caller and drops the rest, without a trace: a dropped row does not count
  toward a label's number, so a hit never reveals that a hidden namesake exists. A provider therefore needs no
  security, and its index may hold rows the caller cannot read.
- The core asks for more than it shows, because the re-read removes some.
- Ids may come back in any casing (RavenDB ids are case-insensitive; an index projection answers lower-cased).
- `FindByLabelAsync` returns every object whose text contains all the label's words; the core compares the rendered
  labels.
- Terms arrive reduced to letters and digits. A provider that queries RavenDB still passes them as query parameters,
  never spliced into RQL.

Design: `docs/spark_search_PRD.md`.
