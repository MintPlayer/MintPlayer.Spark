# MintPlayer.Spark.Minification

HTML minification of a Spark app's server-rendered pages, through [WebMarkupMin](https://github.com/Taritsyn/WebMarkupMin)
(Apache-2.0).

```csharp
builder.Services.AddSpark(builder.Configuration, spark => spark
    .AddPrerendering()
    .AddMinification());

app.UseResponseCompression();          // outside: compresses the minified page
// ... static files, UseRouting, UseSpark, UseEndpoints ...
app.UseSpaImproved(spa =>
{
    spa.Options.SourcePath = "ClientApp";
    spa.UseSparkPrerendering();        // minification runs here, just outside the renderer
});
```

- **Server-rendered apps only.** Without `UseSparkPrerendering()` the host refuses to start.
- **Options:** `Enabled` (also `Spark:Minification:Enabled` in configuration, which wins), `ExcludedPaths`,
  `Whitespace` (`Safe` | `Medium` | `Aggressive`, default `Safe`; `Aggressive` breaks Angular hydration) and
  `RemoveComments` (default `false`: turning it on breaks Angular hydration).
- It minifies the rendered page, not the `index.html` template, and leaves the transfer state, JSON-LD, inline scripts
  and attribute quotes as they are.

The guide is [docs/guide-minification.md](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-minification.md).
