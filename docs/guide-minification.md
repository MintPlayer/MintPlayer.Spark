# HTML minification

`MintPlayer.Spark.Minification` minifies a Spark app's **server-rendered pages** with
[WebMarkupMin](https://github.com/Taritsyn/WebMarkupMin) (Apache-2.0). The design, its decisions and the measurements
behind them are in [html_minification_PRD.md](html_minification_PRD.md). MintPlayer uses it.

## Setting it up

```csharp
builder.Services.AddSpark(builder.Configuration, spark => spark
    .AddPrerendering()
    .AddMinification(o => o.ExcludedPaths.Add("/legal")));

app.UseResponseCompression();          // outside: compresses the minified page
// ... static files, UseRouting, UseSpark, UseEndpoints ...
app.UseSpaImproved(spa =>
{
    spa.Options.SourcePath = "ClientApp";
    spa.UseSparkPrerendering();        // places the minification, just outside the renderer
});
```

There is no `Use...` call of its own: `UseSparkPrerendering()` places it.

## Options

| Option | Default | |
|---|---|---|
| `Enabled` | `true` | `false` serves the pages as rendered and logs that at start. |
| `ExcludedPaths` | empty | Path prefixes served as rendered, matched per segment and case-insensitively. For a page that misbehaves minified. |
| `Whitespace` | `Safe` | `Safe`, `Medium` or `Aggressive`, WebMarkupMin's modes of the same name. ⚠️ `Aggressive` breaks hydration, see below. |
| `RemoveComments` | `false` | ⚠️ Breaks hydration, see below. |

Configuration under `Spark:Minification` is applied after the code, so it wins:

```json
{ "Spark": { "Minification": { "Enabled": false } } }
```

switches it off on a server without a deploy.

Everything else is fixed. It minifies the inline and embedded CSS (on mintplayer.com that is the whole saving: about
5 % raw and 3 % after Brotli; whitespace adds almost nothing), keeps attribute quotes, and leaves inline scripts,
the transfer state (`ng-state`), JSON-LD and attributes as they are. It runs for GET requests with status 200, 401,
403 or 404 (Spark's SSR error pages are full pages), also in Development.

## Why it needs server-side rendering

Without SSR the server only ever sends the small static `index.html` shell (7 KB on mintplayer.com, 0.7 % less after
Brotli when minified); the page is built in the browser. So `AddMinification()` registers into the `BeforePrerendering` middleware stage, which only
`UseSparkPrerendering()` applies. If the app never calls it, the host **refuses to start** with a message that says
so, rather than silently doing nothing.

## Where it sits

```
UseResponseCompression      compresses what comes out
  UseStaticFiles            /index.csr.html, scripts, styles: never minified
  UseSpark / endpoints      /spark/* JSON: never reaches it
  SPA branch
    minification            BeforePrerendering stage
      prerenderer           renders the page into the response
```

- **Compression outside.** WebMarkupMin throws when the response is already compressed, and minifying before
  compressing is the point. It never compresses itself.
- **Outside the renderer, not inside.** The SpaServices prerenderer swaps the response body, lets the rest of the
  pipeline write the `index.html` template into it, and then writes the rendered page. Middleware inside it sees
  only the template; that is what the legacy MintPlayer site minified.
- **`/index.csr.html` is never minified,** also when it is not served as a static file: the Angular service worker
  checks its hash against `ngsw.json`. `/index.html` and the dev server's paths (`/@vite`, `/@fs`, `/ng-cli-ws`,
  `/sockjs-node`) are excluded too.

## Hydration

Angular's server output has comments that are not decoration: they are hydration markers (`<!--container-->`, …). **`RemoveComments = true` removes them, and Angular then fails to hydrate (NG0500)** and
re-renders the page. Leave it off unless the app does not hydrate.

Angular also keeps a whitespace-only text node that has no line break (`<b>a</b> <i>b</i>` on one template line) and
expects to find it. **`Whitespace = Aggressive` removes those spaces, and hydration fails on every page** (`TypeError:
Cannot set properties of null (setting 'nodeValue')`, then the browser fetches the page's data again). `Medium`
hydrated mintplayer.com's pages too, but removes such a space beside a block element. `Safe` keeps every one, and
on mintplayer.com the three differ by less than 0.1 %: the saving is in the CSS, which every mode minifies fully.

Whitespace minification does not see CSS. Text kept on separate lines only by `white-space: pre` or `pre-wrap`
collapses onto one line. ng-spark therefore renders a `MultiLineString` attribute as lines joined by `<br>` (the
`lines` pipe in `@mintplayer/ng-spark/pipes`), and a custom template should do the same rather than rely on
`pre-wrap`. A page that still misbehaves goes into `ExcludedPaths`.

See also: [Server-side rendering](guide-ssr.md) · [package README](../libs/minification/MintPlayer.Spark.Minification/README.md)
