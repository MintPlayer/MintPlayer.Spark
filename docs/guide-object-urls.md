# Object URLs and languages in the URL

How Spark names an object's page (#602), the language of a page (#604) and a section of a page (#721). They apply to every
app: there is nothing to turn on beyond what is listed under each heading. Plans and decisions:
[spark_generic_slugs_PRD.md](spark_generic_slugs_PRD.md), [spark_language_urls_PRD.md](spark_language_urls_PRD.md),
[section_fragment_links_PRD.md](section_fragment_links_PRD.md).

## The canonical URL: `href`

The server builds every object's page URL in one place (`ISparkEntityLinks`) and sends it as `href` on a loaded object,
on a save response and on every query row. The client links to `href`, so a grid row, a reference and a new object's
redirect all land on the canonical URL directly. The sitemap, site search and the page's canonical link use the same
value.

By default the URL is `/po/{type}/{id}/{slug}`: `{type}` is the alias (else the id) and `{slug}` is the breadcrumb, lower
case, accents removed, every run of other characters one `-`. A server render answers a URL whose slug is missing or
stale with a **301** to the canonical one, query string kept. It answers only for an object the visitor may read; a missing
or denied one gets the page's normal 401/404, with no `Location`.

## Three tiers of pages

**(a) The default.** `sparkRoutes()` mounts `po/:type/:id` and `po/:type/:id/:slug`. Nothing to do.

A type whose breadcrumb holds text that should not end up in URLs, history and logs turns the slug off in its model:

```json
{ "name": "Patient", "slug": false }
```

Its `href` is `/po/patient/{id}`, and a URL with a slug 301s to it.

**(b) A type at its own URL.** Give the type a template in its model, and declare the matching client route before
`sparkRoutes()`:

```json
{ "name": "Song", "alias": "song", "route": "songs/{id}/{slug}" }
```

```ts
export const routes: Routes = [
  ...sparkObjectRoute({ type: 'song', path: 'songs/:id/:slug' }),
  ...sparkRoutes({ notFound: true }),
];
```

The placeholders are `{type}`, `{id}` (required) and `{slug}` (optional, last segment only). Links, the sitemap, search
and the server's 301s follow the template. `sparkObjectRoute` mounts the path with and without its slug and stores the type
in the route's data (`sparkType`), so Spark's detail page works on it unchanged. Pass `loadComponent` for a page of your own:
it reads `id` from the route and the type from `data.sparkType`. `sparkObjectRoute` also takes the default path
(`po/song/:id/:slug`) when only the component should change.

**(c) A URL by natural key.** For a URL that names an object by something other than its id, e.g. CodeCoverage's
`/github/r/{owner}/{repo}`, implement `ISparkEntityRedirect` and register it:

```csharp
internal sealed partial class RepositoryRedirect : ISparkEntityRedirect
{
    [Inject] private readonly IRepositoryScope scope;

    public string Pattern => "{provider}/r/{owner}/{repo}";

    public async Task<SparkEntityTarget?> ResolveAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct)
        => await scope.ResolveAsync(values["provider"], values["owner"], values["repo"], ct) is { Id: { } id }
            ? new SparkEntityTarget("repository", id)
            : null;
}

spark.AddEntityRedirect<RepositoryRedirect>();
```

A server render answers a matching request with a 301 to the object's `href`. Spark loads the object through the normal
read path, so a `null` answer, a missing object and an object the visitor may not read all get the same refusal. The
lookup needs no authorization of its own and never reveals whether a private object exists. The hook runs in the
prerender step only: an app without server rendering keeps a client guard for these URLs, as CodeCoverage does.

## The language in the URL

The **default language's pages have no `?lang`; every other language is `?lang=xx`**. A page's language is the URL's
`?lang`, then the `spark-lang` cookie, then `Accept-Language`, then the default, on the server render and in the browser
alike. An unknown `?lang` is ignored. Following a `?lang` link also writes the cookie.

Each app adds two providers:

```ts
provideSpark(withLanguageUrls(), /* … */),
provideHttpClient(withInterceptors([/* … */]), withSparkLanguage()),
```

- `withLanguageUrls()` (from `@mintplayer/ng-spark/services`) installs a `UrlSerializer` that adds the current
  `?lang` to every router URL (`routerLink` hrefs, `navigate()`, the address bar) when the URL names none and the
  language is not the default. A `lang` the URL sets is never changed.
- `withSparkLanguage()` sends the chosen language as `Accept-Language`, so text the server resolves follows it.

Switching languages never reloads the page. `<spark-language-selector>` (the shell's default topbar item) is a
`<details>` menu of `routerLink`s to the current page in each language, so it also works without JavaScript, and crawlers
reach every language. Labels and translated values follow the switch at once. Text the server already resolved for a view
on screen (a dashboard, a notification) changes on its next request.

**Head tags and sitemap.** With `withSeo`, every page gets one `<link rel="alternate" hreflang>` per language plus
`x-default` (the bare URL), also with one language. The canonical link and `og:url` name the language served. JSON-LD gets
`inLanguage`. `og:locale` is written only for a code with a region (`nl-BE` → `nl_BE`). With more than one language, the
sitemap lists each object once per language, each entry linking all of them as `xhtml:link` alternates.

## Section links: `#fragment`

A URL's `#fragment` can name a section of an object page: a group card, a tab or a sub-query card, e.g.
`/po/company/companies-1#company-people`. Every app renders these ids, and opening a section that sits on another tab
activates that tab. The ids and their rules are in [Attribute grouping](guide-attribute-grouping.md#section-ids-and-fragment-links).

**Scrolling to the section is opt-in** (CodeCoverage and HR have it). It takes two lines, because Angular's router is the
one that asks for the anchor:

```ts
provideRouter(routes, withInMemoryScrolling({ anchorScrolling: 'enabled' })),
provideSpark(/* … */, withFragmentScrolling()),
```

- `withInMemoryScrolling({ anchorScrolling: 'enabled' })` is Angular's own anchor scrolling: after a navigation to a URL
  with a fragment, the router asks the `ViewportScroller` for the anchor.
- `withFragmentScrolling()` (from `@mintplayer/ng-spark/fragment-scrolling`) replaces Angular's scroller with Spark's.
  Angular's scrolls the window at once. On an object page, though, the sections render only after the object has loaded,
  and the shell scrolls `<main id="spark-main">`, not the window. Spark's scroller:
  - waits for an anchor that is not on the page yet, up to 10 s;
  - opens the tab that holds it (a bubbling `spark-reveal` event);
  - scrolls the anchor to the top of `#spark-main` (else the window), below the sticky action bar, smoothly unless the
    visitor prefers reduced motion. An app with another fixed header passes its height to `ViewportScroller.setOffset`;
  - keeps it in place while the page settles (a card above it loading its rows), until it has not moved for 1 s, for 5 s
    at most, or until the visitor scrolls, touches, clicks or types;
  - outlines it for about 1.5 s (the class `spark-section-revealed`) and gives it focus without scrolling.
- The next navigation cancels a pending scroll. Back and forward restore the position the router remembers.
- **The first page load scrolls too.** The router skips its scroll while a server-rendered page hydrates, so once the app
  is stable Spark asks for the URL's anchor itself, unless the router already did.
- **Scrolling never rewrites the URL**: the address names the section that was linked, not the one scrolled to.
- **In development, a missing `anchorScrolling` is reported.** When a link with a fragment never reaches the scroller, the
  console warns and names `withInMemoryScrolling({ anchorScrolling: 'enabled' })`. Back and forward don't count.

A redirect or guard that rebuilds the URL must carry `fragment` along, or the section is lost on the way; CodeCoverage's
`/` → home redirect and its vanity-URL guards do.
