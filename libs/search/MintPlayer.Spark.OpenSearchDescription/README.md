# MintPlayer.Spark.OpenSearchDescription

Makes a Spark application a search engine a browser can add ("Add search engine", typing in the address bar), through
[MintPlayer.AspNetCore.OpenSearch](https://www.nuget.org/packages/MintPlayer.AspNetCore.OpenSearch). Suggestions and
searches go through [MintPlayer.Spark.Search](../MintPlayer.Spark.Search/README.md), so they show only what the
visitor may read.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddSearch();                                          // optional: added when missing
    spark.AddOpenSearchDescription(o => o.ShortName = "MintPlayer");
});
```

```ts
provideSpark(withOpenSearchDescription({ title: 'MintPlayer' }))   // adds <link rel="search">
```

| Endpoint | Answers |
|---|---|
| `GET /opensearch.xml` | The description document (`application/opensearchdescription+xml`), absolute URLs. |
| `GET /spark/opensearch/suggest?q=` | `[q, [labels]]`: Spark search's unambiguous labels. |
| `GET /spark/opensearch/search?q=` | 302 to the one object the term names (a picked label, or a term only one visible object matches), else to `/search?q=`. |

## Options

| Option | Default | |
|---|---|---|
| `ShortName` | entry assembly name | At most 16 characters; longer is cut. |
| `Description` | `Search {ShortName}` | |
| `ImageUrl` / `ImageType` / `ImageSize` | `/favicon.ico`, `image/x-icon`, 16 | `ImageUrl = null` advertises no icon. |
| `SingleHit` | `Item` | `SearchPage` always lands on the search page. |
| `Path`, `SuggestPath`, `SearchPath` | as above | |

The search page is `SparkSearchOptions.SearchPagePath` (`/search`). Suggestions and searches count against the same
per-address budget as `/spark/search/*`; over it, suggestions are empty and a search lands on the search page.

`AddOpenSearch` registers MVC with an XML output formatter and sets `RespectBrowserAcceptHeader`. The formatter only
writes the description document, so every other response still negotiates to JSON.
