# Slug URLs for every object, and the language in the URL (#602, #604)

**Packages:** `MintPlayer.Spark.Abstractions`, `MintPlayer.Spark`, `MintPlayer.Spark.SitemapXml` and the bundles → a
minor of each; npm `@mintplayer/ng-spark` → a minor. The versioning engine computes the numbers from the tags. The majors
don't move, because the packages still target .NET 11 and Angular 22, so the breaks below ship as minors.

Every object's page now has one canonical URL. The server builds it, sends it as `href`, and 301s to it before Angular
renders. An app can move a type to a URL of its own, or map a natural-key URL to an object. A page's language is part of its
URL (`?lang=nl`), stated in its head tags and sitemap, and switched by a picker made of real links, without a reload.

Guide: [Object URLs and languages in the URL](guide-object-urls.md). Decisions and evidence:
[spark_generic_slugs_PRD.md](spark_generic_slugs_PRD.md), [spark_language_urls_PRD.md](spark_language_urls_PRD.md).

---

## ⚠️ Breaking changes

1. **`sparkRoutes({ slugs })` is gone.** Slugs are on for every app and every type. A type turns them off with
   `"slug": false` in its model JSON. `sparkSlug()` and `data.sparkSlug` are removed: link to the object's `href`, or use
   `sparkDetailLink(type, id)` from `@mintplayer/ng-spark/models`.
2. **Server-rendered `/po/{type}/{id}` URLs answer 301** to `/po/{type}/{id}/{slug}` for a readable object. A missing or
   denied object keeps its 401/404, with no `Location`.
3. **`<spark-language-selector>` is a `<details>` menu of links**, not a `bs-select`. Styling that targeted its
   `bs-select` no longer applies.
4. **Every app should add `withLanguageUrls()`** to `provideSpark(...)` and `withSparkLanguage()` to
   `provideHttpClient(...)`. Without the first, links lose `?lang` after a switch. Without the second, text the server
   resolves stays in the browser's language.
5. **`SparkSitemapReader` reads `ICultureLoader`.** This matters only for code that constructs it by hand, as tests do.

## New

- `href` on `PersistentObject`, save responses and `QueryResultItem`. The grid, references, create and edit link to it.
- A per-type URL template, `"route": "songs/{id}/{slug}"`, with the client's `sparkObjectRoute({ type, path })`.
- `ISparkEntityRedirect` + `spark.AddEntityRedirect<T>()`: natural-key URLs answered with a 301 during the server render,
  with the read path's uniform refusal.
- `?lang` comes first in the server's and the client's language resolution, and is remembered in the cookie.
- Head tags: hreflang alternates plus `x-default` on every page, a canonical and `og:url` in the language served,
  `og:locale` for region codes, and JSON-LD `inLanguage`.
- The sitemap has one entry per language with `xhtml:link` alternates when the app has more than one language.

## Apps

- Every app: `withLanguageUrls()`; `withSparkLanguage()` in DemoApp, Fleet, HR, QnA and SparkId (the others had it).
- CodeCoverage: its vanity URLs are registered as entity redirects. They have no effect until the app renders on the
  server, so the client guards stay. Its own language select is replaced by `<spark-language-selector>`.
- MintPlayer: `sparkRoutes({ notFound: true })` (the `slugs` option is gone).
