# Server-side rendering

A Spark app can render its pages on the server, with their data, and hand them to the browser to hydrate. Crawlers
and link previews get the full page; visitors see content before the JavaScript runs, and the browser fetches none of
it a second time. The design, its decisions and their evidence are in [spark_ssr_PRD.md](spark_ssr_PRD.md).

It is opt-in per app. MintPlayer uses it; the demos, CodeCoverage and SeoTester render in the browser (D6).

## How it works

1. ASP.NET Core receives a page request (`/po/song/Songs%2F23/a-sky-full-of-stars`).
2. Spark predicts the requests the page will make (`/spark/po/load`, `/spark/types`, …) and runs them **in-process**,
   through the application's own endpoints, as the visitor. Authorization, `security.json`, row security and
   attribute rights apply exactly as over HTTP, and the response bodies are byte-equal to what the visitor would fetch
   (`SparkPrerenderEquivalenceTests`).
3. Node renders the Angular app (`main.server.mjs`). On the server, `HttpClient` is answered from those responses;
   a request nobody predicted fails at once and is logged as `[spark-ssr] miss: <key>`, and the browser makes it after
   hydration.
4. Angular writes every answered response into the page (`<script id="ng-state">`), and the browser's identical
   requests are served from it.

The page's status is its primary read's (D4): a missing or denied object is a 401 for an anonymous visitor and a 404
for a signed-in one, the same for both cases, so a page never tells whether something exists. A stale slug is a 301 to
the canonical one, only for a readable object. Every rendered response carries `Cache-Control: private, no-store` and
`Vary: Cookie` (D7), and a non-200 one `X-Robots-Tag: noindex`.

## Setting it up

**.NET** (`Program.cs`):

```csharp
builder.Services.AddSpark(builder.Configuration, spark => spark
    .AddPrerendering(prerendering => prerendering.DefaultTimeZone = "Europe/Brussels"));

app.UseWhen(context => !context.Request.Path.StartsWithSegments("/spark"), appBuilder =>
    appBuilder.UseSpaImproved(spa =>
    {
        spa.Options.SourcePath = "ClientApp";
        spa.UseSparkPrerendering(prerendering => prerendering.BootModuleBuilder = app.Environment.IsDevelopment()
            ? SparkBuilderPrerenderingExtensions.WatchBuild("build:ssr:watch")
            : null);
        if (app.Environment.IsDevelopment())
            spa.UseAngularCliServer(npmScript: "start");
    }));
```

**Angular**: the build writes a server bundle (`"server": "src/main.server.ts"`, `"ssr": true`, `"prerender": false`;
`@angular/ssr` is a build-time devDependency only), and:

```ts
// main.server.ts
const bootstrap = (context: BootstrapContext) => bootstrapApplication(App, config, context);
export default createSparkServerRenderer(bootstrap);

// app.config.server.ts
export const config = mergeApplicationConfig(appConfig, { providers: [provideSparkServerRendering()] });

// app.config.ts
provideSparkHydration(),
```

All Angular code is zoneless: a render waits through `PendingTasks`, never zone.js.

**Development.** The dev server (`npm start`) serves the browser build only. The server bundle is a second, watch-mode
build that the host starts through `WatchBuild`: add a `development-ssr` build configuration (the `development` one
with `ssr` left on) and the script

```json
"build:ssr:watch": "node ../../../../tools/ssr-watch.mjs <nx project>"
```

`tools/ssr-watch.mjs` runs that configuration with `--watch` and `CI=1`, which keeps its Angular cache apart from the
dev server's.

## What gets supplied

- **The shell**: culture, translations, program units, the signed-in user.
- **Spark's routes** (`sparkRoutes()`): `po/:type/:id[/:slug]` (the object, the types, rights, actions, lookups and
  AsDetail reference queries), `po/:type` and `query/:id` (the query, rights, actions and the first page of rows).
  A live or virtual-scrolling grid renders its rows after hydration; the live socket never opens on the server (D9).
- **Boards** on their declared `paths` (D14, D15): the board, and each tile's content when the board has at most 7
  tiles. A larger board renders its tiles as links until hydration.
- **Anything else**: an `ISparkPrerenderContributor`, or `prerendering.MapPage(path, supply)` for one page. Supply the
  request *exactly* as the component sends it (method, URL and body); `ISparkPrerenderContext.GetAsync` and
  `PostAsync` run it in-process. Only an endpoint marked `[SparkPrerenderable]` (`WithMetadata(SparkPrerenderableAttribute.Instance)`)
  runs; any other answers 404 and is not supplied.

The server's language is the `spark-lang` cookie, then `Accept-Language`, then the default; its timezone is the
timezone cookie, else `DefaultTimeZone` (D10).

## Search engines and link previews

A type describes its detail page in the model (D12):

```json
"breadcrumb": "{Title}",
"seo": {
  "indexable": true,
  "schemaType": "MusicRecording",
  "description": "Summary",
  "image": "Cover",
  "properties": { "datePublished": "Released" }
}
```

`spark-po-detail` sets the title (the breadcrumb), the description, `robots`, the canonical link (the slugged URL),
Open Graph tags and JSON-LD, on the server and on client navigation alike. Every name is an attribute of the
**presented** object, so a value the visitor may not read is absent. Robots is `index` only for a readable object of
an indexable type; a type without `seo`, an error, a deleted view, and the new and edit forms are `noindex`.

Rich JSON-LD that the mapping cannot express comes from a per-type hook, which receives only the presented object:

```ts
provideSparkSeo({
  structuredData: {
    Song: po => ({ byArtist: artistsOf(po).map(name => ({ '@type': 'MusicGroup', name })) }),
  },
}),
```

## Service worker

`ngsw.json`'s index is `/index.csr.html`, the raw shell a static file serves, so the service worker never caches a
rendered page; returning visitors with the worker installed get the client-rendered shell, as before (D7).
