# MintPlayer.Spark.SitemapXml

`sitemap.xml` for a [MintPlayer.Spark](https://github.com/MintPlayer/MintPlayer.Spark) application, built on
[MintPlayer.AspNetCore.SitemapXml](https://www.nuget.org/packages/MintPlayer.AspNetCore.SitemapXml)'s sitemap types
and stylesheet.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    // ...
    spark.AddSitemap();
});
```

`MapSpark()` maps it with the other modules' endpoints:

- `/sitemap.xml`: a sitemap index, one sitemap per 1000 readable rows of each indexable type's list;
- `/sitemap/{type}/{page}.xml`: the detail page of each object on that page of the list, by the canonical URL the
  page writes itself (`/po/{type}/{id}/{slug}`);
- `/sitemap.xsl`: the package's stylesheet, so a browser shows the sitemap as a table (`StylesheetUrl = null` turns it off).

A type is listed when its model says `"seo": { "indexable": true }`. With `"indexableWhen": "IsPublic"`, an object is
listed only when that boolean attribute is true in its query row. The lists run as an anonymous visitor, whoever asks,
so row security and rights decide what appears exactly as they do for a crawler.

Name it in the app's `robots.txt`:

```text
Sitemap: https://example.com/sitemap.xml
```
