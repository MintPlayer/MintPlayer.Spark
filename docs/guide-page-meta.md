# Page meta tags: link previews and indexing for SPA routes

A Spark app is a single-page app: every route is answered with the same `index.html`, and its title and tags are set
by Angular after the scripts run. A crawler or a link-preview bot (WhatsApp, Slack, Facebook, LinkedIn, X) mostly does
not run those scripts, so a shared link to an event or a product shows the app's generic title and no image.

Spark fixes this on the server, without SSR or Node: for a public route a provider registers, the .NET host rewrites
`index.html` with that page's title, description, OpenGraph and Twitter card and canonical link. It is part of core
and does nothing until a provider is registered.

Decisions and evidence: [commerce_PRD.md](commerce_PRD.md) X6 and D27; spike S19.

## Registering a provider

```csharp
public sealed class EventPageMeta(IAsyncDocumentSession session) : ISparkPageMetaProvider   // MintPlayer.Spark.PageMeta
{
    public IEnumerable<string> RoutePatterns => ["/events/{slug}"];

    public async Task<SparkPageMeta?> GetAsync(SparkPageMetaContext context, CancellationToken cancellationToken)
    {
        var slug = (string)context.RouteValues["slug"]!;
        var ev = await session.Query<Event>().Where(e => e.Slug == slug && e.IsPublished).FirstOrDefaultAsync(cancellationToken);
        return ev is null
            ? null                                                    // unknown or not public: index.html untouched
            : new SparkPageMeta(ev.Name, ev.Summary, ev.PosterUrl);   // a relative image URL is resolved against the origin
    }
}

builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddPageMeta(o => o.PublicOrigin = "https://tickets.example");   // or Spark:PageMeta:PublicOrigin
    spark.AddPageMetaProvider<EventPageMeta>();
});
```

- **Patterns** are ASP.NET route templates (`/shop/products/{id}`, `/events/{slug}`), compiled once per provider type.
  The first matching pattern of the first registered provider is asked. Paths under `/spark`, `/connect` and
  `/.well-known` are never matched.
- **`null`** serves `index.html` untouched. There is no soft 404: the SPA shows its own not-found page.
- **`SparkPageMeta`**: `Title`, `Description`, `Image`, `Type` (`og:type`, default `website`), `Canonical` (default:
  the public origin plus the request path; also `og:url`) and `TwitterCard` (default `summary_large_image`).
- **Every value is HTML-encoded** by the framework. Return seller text as it is; never encode it yourself.

## What the page gets

The template's first `<title>`, its `<meta name="description">`, every `og:*` and `twitter:*` meta and the canonical
link are removed, and these are inserted right after `<head>`, each marked `data-spark-meta`:

```html
<title data-spark-meta>Rock Werchter – summer festival tickets</title>
<meta name="description" content="…" data-spark-meta>
<meta property="og:title" content="…" data-spark-meta>
<meta property="og:description" content="…" data-spark-meta>
<meta property="og:type" content="website" data-spark-meta>
<meta property="og:image" content="https://tickets.example/media/poster.png" data-spark-meta>
<meta property="og:url" content="https://tickets.example/events/rock-werchter" data-spark-meta>
<meta name="twitter:card" content="summary_large_image" data-spark-meta>
<link rel="canonical" href="https://tickets.example/events/rock-werchter" data-spark-meta>
```

The response is sent with `Cache-Control: no-cache` and without the template's `ETag`, `Last-Modified` and
`Content-Length` (a new length is set). Conditional and range headers are removed from the request, so a browser that
cached the bare template gets the page, not a `304`. SeoTester's page checks (`seo.title-present`, `seo.title-length`,
`seo.meta-description`, `seo.canonical`, `social.open-graph`, `social.twitter-card`) pass on such a page
(`SeoTester.Tests/Checks/SparkPageMetaSeoTests.cs`), given a title of 30–60 and a description of 70–160 characters.

## How it works

The middleware is registered as an `IStartupFilter`, like the service-worker cache headers, so it wraps the whole
pipeline: the static-file middleware and the SPA fallback run outside `UseSpark()`. It acts only on a `GET` or `HEAD`
whose `Accept` names `text/html` or `*/*` (or is absent: several preview bots send `*/*`) and whose path matches a
pattern. The provider is asked first; then the response body is buffered, and only a `200 text/html` answer without a
`Content-Encoding` is rewritten. Anything else is copied out unchanged.

- **Production**: `UseSpaImproved` rewrites the path to `index.html` and the static-file middleware sends it with
  `SendFileAsync`, which lands in the buffer too.
- **Development**: `UseAngularCliServer` proxies to the Angular dev server. The middleware removes `Accept-Encoding`
  from the request on a matched route, so the dev server answers in plain text.

## Rules for a provider

- **The origin comes from configuration, never from `Host`.** `og:url`, the canonical link and relative images use
  `Spark:PageMeta:PublicOrigin`, else `Spark:Auth:PublicBaseUrl`, else (Development only) the request's origin. With
  none of them, those tags are left out: a card cached by a crawler must not carry a host the caller chose.
- **Return public fields only.** The middleware runs before authentication, so the caller is anonymous here even when
  signed in, and the page is the same for everyone. Query only published objects, and project only what a stranger
  may see.
- **Keep it fast.** The provider runs on every navigation to a matching route, before the page is sent. A point load
  or one indexed query; no remote calls.
- The tags carry no script, so a Content Security Policy is not affected.

## The service worker (PWA)

An installed app's Angular service worker (ngsw) answers navigations from its cached `/index.html`. That file is
fetched by its exact path, matches no pattern and is cached untouched, which is correct: the cache must not hold one
event's tags for every page. The consequence is that a user of the installed app never sees the server's tags.
Crawlers and preview bots don't run service workers, so previews are unaffected. For the tab title and in-app
consistency, set the same title and tags in the Angular page through `Title` and `Meta`.
