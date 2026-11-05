# HTML minification (WebMarkupMin) for apps/MintPlayer — PRD and plan (#563)

Status: **Decisions locked 2026-11-05** (grilling session, §5 "Grilling"). Spikes not started. §4's app-level sketch and
§5's D1/D3/D5 are **superseded by G1-G6**: the feature ships as a Spark library, `spark.AddMinification(...)`. Issue #563 has only the title
"WebMarkupMin" and no body. This document reconstructs the intent from the legacy site, explains the odd placement
of the legacy middleware, and gates the work behind a value spike (SP1). If SP1 shows no worthwhile gain, the issue
closes with this document as the evidence.

## 1. Goal

Server-rendered HTML from `apps/MintPlayer` (mintplayer.com) is minified the way the legacy site minified it, without
breaking:

- Angular hydration,
- the service worker,
- the headers and status codes that SSR sets,
- response compression,
- the dev server.

Non-goals:
- Minifying JS and CSS. Angular's production build already does that.
- Minifying JSON APIs and static files.
- AMP. AMP was dropped in mintplayer_website_PRD D11, and `/amp/song/{id}` is a 301
  (`apps/MintPlayer/MintPlayer/LegacyRedirects.cs:50-53`).

## 2. What exists today (evidence)

### 2.1 Legacy MintPlayer (MintPlayer-Archive/MintPlayer, local clone `C:\Repos\MintPlayer`)

**Registration** (`MintPlayer.Web/Startup.cs`):
- `:150`: `AddWebMarkupMin().AddHttpCompression().AddHtmlMinification()`, from the package `WebMarkupMin.AspNetCore3`
  (`:33`).
- `:52`: `AddResponseCompression` is registered as well, so the site carried **two** compression stacks.

**Options**:
- `WebMarkupMinOptions` (`:311-317`) enables minification and compression in Development.
- It sets `DisablePoweredByHttpHeaders` to `true` and then back to `false`. The second assignment wins, so the site
  sent `X-HTML-Minification-Powered-By`. This looks like a slip.

**`HtmlMinificationOptions`** (`:318-328`):
- whitespace: `Aggressive`
- `RemoveEmptyAttributes`
- `RemoveRedundantAttributes`
- `RemoveHttpProtocolFromAttributes`
- `MinifyInlineJsCode`, `MinifyEmbeddedJsCode` and `MinifyEmbeddedJsonData`
- `RemoveHtmlComments` stayed at its default.

**Two middleware registrations**:
- **`:375-381`**: `UseWhen(path starts with "/amp", b => b.UseWebMarkupMin())`. The AMP pages were MVC Razor views
  under `/amp`, outside the SPA branch, so they needed their own registration. This one is obsolete (AMP is gone).
- **`:479`**: `app2.UseWebMarkupMin()` *inside* the `UseSpaImproved(spa => …)` callback, after
  `spa.UseSpaPrerendering(...)` (`:468-477`) and before `UseAngularCliServer` (`:481-484`). This is "the awkward
  place".

**History** (git, legacy clone):
- The `:479` call has been in that spot since the first commit, 001d247 (2020-05-17, "Committed source code to
  GitHub"). At first it was commented out.
- d149ecd (2021-12-18, "Added htmlminification services") uncommented it in place and added `.AddHtmlMinification()`.
- 42d9b04 (2021-12-09) is only a folder rename.
- No commit message, issue or PR gives a reason for the position.

### 2.2 `MintPlayer.AspNetCore.SpaServices` (local clone `C:\Repos\MintPlayer.AspNetCore.SpaServices`)

**`UseSpaImproved`** (`SpaApplicationBuilderExtensions.cs`) first runs `configuration.Invoke(spaBuilder)`, then
`SpaDefaultPageMiddleware.Attach(spaBuilder)`. `DefaultSpaBuilder` wraps the same `IApplicationBuilder`, so `app2` and
`spa.ApplicationBuilder` are one builder. Whatever the callback registers runs **before** the default-page
middleware, in the order the callback registers it.

**`UseSpaPrerendering`** (`SpaPrerenderingExtensions.cs`) registers one `Use(...)` middleware. Per request it:
1. strips the `If-*` conditional headers and `Accept-Encoding`;
2. swaps `Response.Body` for a `MemoryStream`;
3. awaits `next()` to capture **the template** (`index.html`);
4. restores the original body;
5. rejects the template if it is not a 200 HTML response with identity encoding;
6. renders in Node and writes the **SSR output to the original body**.

**The fork's own verification** (`docs/PLAN-Prerendering-Response-Headers.md`, commit 51cb55f, PR #83):
- `:164`: the demo moved `UseWebMarkupMin` "to the top level before `UseSpaImproved`".
- `:214-223`: verified end to end, in Production, over real Kestrel and HTTPS with a real Node prerender. The response
  is 23249 bytes on zero newlines, with `X-HTML-Minification-Powered-By: WebMarkupMin`. The doc says this means "the
  **SSR output** is minified, not just the template as in the old inside-the-SPA-callback position".

### 2.3 WebMarkupMin today (Taritsyn/WebMarkupMin; research 2026-11-05, re-verify in SP4)

**Package**:
- `WebMarkupMin.AspNetCoreLatest` 2.23.0 (2026-10-26) targets net8, net9 and net10. The repo was pushed on 2026-11-04,
  so it is maintained.
- There is no net11 build yet. It should load forward, but that is unverified (SP4).

**Middleware behaviour** (`src/WebMarkupMin.AspNetCoreLatest/BodyWrapperStreamBase.cs`):
- It swaps `IHttpResponseBodyFeature` for a wrapper.
- It decides on the first write, using status code, method, media type and `IncludedPages`/`ExcludedPages`.
- It buffers the **whole body** in a `MemoryStream` (~:214) and minifies at the end, as a string (~:335-390).
- On a minification error it leaves the content untouched.
- It rewrites or removes `Content-Length` and clears `Content-MD5`. It does nothing with ETag.

**Ordering constraint (fact)**: it **throws `InvalidOperationException`** when the response already carries a
`Content-Encoding`. So no compression may sit between WebMarkupMin and the HTML producer, and compression must be on
the *outside*.

**Angular hydration (fact)**: the Angular docs (https://angular.dev/errors/NG0500) say stripping the comment nodes
that SSR emits causes hydration errors, and they name CDN "optimizations" that strip them. WebMarkupMin removes HTML
comments by default.

### 2.4 Spark / apps/MintPlayer today

**Pipeline** (`apps/MintPlayer/MintPlayer/Program.cs`):

| Line | Step |
|---|---|
| `:113-114` | `UseHsts`, outside Development only |
| `:115-127` | `OnStarting` nosniff / Permissions-Policy |
| **`:128`** | **`UseResponseCompression`** (Brotli + gzip, `EnableForHttps`, registered at `:93-99`) |
| `:130-131` | `UseHttpsRedirection` |
| `:132` | `UseStaticFiles` |
| **`:133-143`** | **`UseSpaStaticFiles`** |
| `:145` | `UseRouting` |
| `:146` | `UseSpark` |
| `:148-153` | `UseEndpoints` |
| `:155-173` | `UseWhen(not /spark)` → `UseSpaImproved` → `UseSparkPrerendering`, then `UseAngularCliServer` in Development |

**Prerendering**: `UseSparkPrerendering`
(`libs/spark/MintPlayer.Spark/Extensions/SparkBuilderPrerenderingExtensions.cs:49-64`) sets
`spa.Options.DefaultPage = "/index.csr.html"` and calls `spa.UseSpaPrerendering(...)`. Its `ExcludeUrls` are
`/sockjs-node`, `/ng-cli-ws`, `/@vite` and `/@fs`.

**No server-side HTML rewriting**:
- `SparkPrerenderingService.OnSupplyData` (`libs/spark/MintPlayer.Spark/Prerendering/SparkPrerenderingService.cs:52-90`)
  only sets headers:
  - `Cache-Control: private, no-store`
  - `Vary: Cookie`
  - the slug 301
  - `X-Robots-Tag: noindex` on pages that are not 200
- The title, canonical link and JSON-LD come from the Angular render (`spark-seo.service.ts`).

**Compression**: Traefik does none (`apps/MintPlayer/docker-compose.yml:71-85`, TLS and www→apex only). The app's
`UseResponseCompression` is the only compression.

**Hydration**: on, via `withHydration()` (`ClientApp/src/app/app.config.ts:35`, `provide-spark-hydration.ts:42`).
TransferState goes out as `<script id="ng-state" type="application/json">`.

**Service worker**: `ngsw.json` lists `/index.csr.html` as its index (the SSR PRD's SP6). The SW **hash-checks** the
files it caches.

**Tests that read the HTML** (`tests/MintPlayer.Spark.E2E.Tests/Ssr/ServerRenderingTests.cs`):
- `:39`: the `<title>`.
- `:42`: a **quoted** `rel="canonical"`.
- `:44`: `application/ld+json`.
- `:46`: `ng-state`.
- `:112`: no hydration warnings in the browser.

**Prior judgement**: `docs/seo_analyzer_research.md:71` skips an HTML-minification check as "low value and noisy". So
far no measurement either supports or refutes that.

## 3. Why the legacy middleware sat inside the SPA callback

**Mechanically (proven, §2.2)**:
- Inside the callback, the order is prerender (outer), then WebMarkupMin, then the dev proxy or default page.
- So the legacy WebMarkupMin wrapped only what the prerenderer's `next()` returned, which is the **`index.html`
  template**. It never saw the SSR output, because that is written to the original body *after* the template is
  captured.
- The legacy site therefore minified the template, and Node then rendered from it. Production pages were only as
  minified as the template made them.

**Why it was put there (inference; no record exists)**: the position is the only one inside the SPA branch that is
both reachable and safe without thinking about compression.

1. **Scope.** It applies only to requests that reach the SPA, and only on the root host (`ShouldUseSpa`). It never
   wraps API controllers, Swagger, the sitemap or the static files served earlier in the pipeline.
2. **Encoding.** At that point `Accept-Encoding` has already been stripped by the prerenderer. The inner response is
   therefore always identity-encoded, so WebMarkupMin's `Content-Encoding` throw (§2.3) could never fire. The
   legacy pipeline had both `AddResponseCompression` and WebMarkupMin's `AddHttpCompression`. At the top level, the
   two stacks would have had to be ordered carefully.
3. **It started as a commented-out line in the first commit, next to `UseSpaPrerendering`.** It was later
   uncommented in place (d149ecd). It was never measured, so the fact that it missed the SSR output went unnoticed
   until the fork's PR #83 investigation.

**Owner observation (2026-11-05)** fits the mechanics:
- On the legacy site, a normal load returned the plain, non-minified `index.html`. A fully rendered page came back
  only on a cache-bypassing reload (Shift+F5).
- The owner had blamed the service worker in combination with SSR.
- On the current deployment from this repository, with the service worker active, pages arrive fully server-rendered
  without Shift+F5.

**Conclusion**: the placement was not a requirement. The SpaServices fork has already shown that a placement outside
the prerenderer works and minifies the actual SSR output.

The new placement must satisfy all of these:

- **Outside the prerenderer**, so it wraps the SSR write.
- **Inside the compression middleware**, so compression is outer and the `Content-Encoding` throw cannot fire.
- **Not over static files**. Minifying `/index.csr.html` served directly would change its bytes. The service worker
  compares them against the hash in `ngsw.json`, and a mismatch puts the SW into its degraded mode. This is
  inference, to be confirmed in SP3.
- **Not over `/spark` APIs** (pointless) and not over the dev-server websocket paths.

## 4. Proposed design (⚠️ superseded by §5 "Grilling": the placement rule stands, the app-level wiring and settings table do not)

**Placement**: one call in `apps/MintPlayer/MintPlayer/Program.cs`, inside the `UseWhen(not /spark)` branch, before
`spa.UseSparkPrerendering(...)`:

```csharp
appBuilder.UseSpaImproved(spa =>
{
    spa.Options.SourcePath = "ClientApp";
    spa.ApplicationBuilder.UseWebMarkupMin();          // outside the prerenderer, inside UseResponseCompression
    spa.UseSparkPrerendering(...);
    if (app.Environment.IsDevelopment()) spa.UseAngularCliServer(...);
});
```

This order satisfies every constraint in §3:
- Compression (`:128`) is outer.
- `UseStaticFiles`/`UseSpaStaticFiles` (`:132-143`) have already served `/index.csr.html` and the hashed assets.
- `/spark` is excluded by the branch.
- Prerendering is inner, so the SSR output is what gets minified.

**Services**: `AddWebMarkupMin(o => …).AddHtmlMinification(o => …)` only, with **no** `AddHttpCompression`.
`UseResponseCompression` stays the only compression (D2).

**Settings**: a conservative starting set, which SP2 confirms or relaxes:

| Setting | Value | Why |
|---|---|---|
| `RemoveHtmlComments` | **false** | NG0500: hydration needs the comment nodes that SSR emits |
| `WhitespaceMinificationMode` | `Medium`, or `Safe` if SP2 shows text-node mismatches | `Aggressive` risks hydration text nodes |
| `AttributeQuotesRemovalMode` | `KeepQuotes` | Removing quotes saves little after Brotli, and E2E `:42` asserts quoted attributes |
| `MinifyEmbeddedJsonData` | **false** | It would rewrite `ng-state` and JSON-LD for ~0 gain after Brotli |
| `MinifyInlineJsCode` / `MinifyEmbeddedJsCode` | false unless SP1 shows gain | Angular's inline scripts are already minified |
| `RemoveRedundantAttributes`, `RemoveEmptyAttributes` | SP2 decides | `RemoveRedundantAttributes` drops things like `type="text"`, which CSS selectors may target |
| `DisablePoweredByHttpHeaders` | **true** | The legacy `false` was an overwrite slip (§2.1) |

## 5. Decisions

Decided by the owner on 2026-11-05 (owner preference):
- **D2**: Brotli/gzip through `UseResponseCompression`, combined with HTML minification and SSR. No WebMarkupMin
  `AddHttpCompression`.
- **D4**: minification is on in Development too.

The rest were open until the grilling below, which supersedes D1, D3 and D5.

### Grilling (owner, 2026-11-05): owner preference unless noted

- **G1, SP1 is a measurement, not a gate** (replaces D5).
  - SP1 still records the before/after sizes (raw, Brotli, gzip) and the CPU time per page.
  - It can no longer cancel #563.
  - It only steers the settings Spark fixes internally, for example whether inline-JS minification stays on.
- **G2, a library, configured by consumers** (replaces D1). The owner's reason: "this is/will be a shipped library,
  we should let the consumers decide". The API is
  `AddSpark(..., spark => spark.AddMinification(options => ...))`.
- **G3, a separate package plus a new stage in core.**
  - **Package.** `MintPlayer.Spark.Minification` in `libs/minification/` holds the WebMarkupMin dependency. This
    follows the package-per-feature pattern: `libs/sitemap/MintPlayer.Spark.SitemapXml`, `libs/search/*`.
  - **Wiring.** These packages wire in through `ISparkBuilder.Registry`
    (`libs/spark/MintPlayer.Spark.Abstractions/Builder/SparkModuleRegistry.cs:120`, `AddMiddleware(action, stage)`).
  - **New stage.** `SparkMiddlewareStage` has no stage inside the SPA branch. Core therefore gains
    `SparkMiddlewareStage.BeforePrerendering`, which `UseSparkPrerendering` applies on the SPA builder just before
    `spa.UseSpaPrerendering(...)`. That gives the §3 placement: compression is outer, static files are already
    served, `/spark` is never touched, and prerendering is inner.
  - **SSR apps only.** `AddMinification` without `UseSparkPrerendering` must **fail loudly**, not be a silent no-op.
    That is the same rule the registry's late-registration guard (`SparkModuleRegistry.cs:111-117`) enforces.
- **G4, the options are Spark's own; WebMarkupMin stays hidden** (replaces D3). The owner picked this over passing
  WebMarkupMin's settings through. No WebMarkupMin type appears in Spark's public API.
- **G5, the option set:**

  | Option | Default | Notes |
  |---|---|---|
  | `Enabled` | `true` | Config-bindable kill switch, e.g. `Spark:Minification:Enabled=false`. The startup log says so when it is configured but off. |
  | `ExcludedPaths` | empty | Path prefixes that are never minified: the escape hatch for one page. |
  | `Whitespace` | ~~**`Aggressive`**~~ **`Safe`** (G6 superseded) | `Safe` \| `Medium` \| `Aggressive`. |
  | `RemoveComments` | `false` | The XML doc **must** warn clearly that it may break Angular hydration (NG0500). See G6. |

  Fixed by Spark and not exposed:
  - attribute quotes are kept;
  - embedded JSON (`ng-state`, JSON-LD) is not minified;
  - no WebMarkupMin compression (D2);
  - `DisablePoweredByHttpHeaders = true`;
  - on in Development (D4).

  SP1 decides inline/embedded JS minification and `RemoveRedundantAttributes`/`RemoveEmptyAttributes`.
- **G6, `Aggressive` stays the default whatever SP2 finds.** ⚠️ **Superseded (owner, 2026-11-05): the default is
  `Safe`.** The M2 run showed `Aggressive` breaking hydration on *every* page, not one (§6 SP2), so "exclude the
  failing page" would have excluded the site. Owner: "WebMarkupMin should probably be able to fully compress the css,
  but be more careful for html". The CSS minification, the whole SP1 saving, is unchanged; `Aggressive` stays
  selectable with a ⚠️ hydration warning, like `RemoveComments`. The original text:
  - If SP2 shows a page that fails hydration under `Aggressive`, the page and the error are documented, and the
    consumer excludes it through `ExcludedPaths`. The default does not change.
  - SP2 also includes a page with mixed inline content (rich text), so that the documented failure surface is real.
  - On comments: the Angular compiler already drops template comments. The comments left in SSR output are almost
    all hydration markup (`<!---->`, `<!--container-->`, `<!--ngetn-->`, `<!--ngtns-->`). That is why
    `RemoveComments` defaults to `false`, and why there is no Spark-maintained keep-list of Angular's marker names,
    which could change with any Angular major.

- **D1 Scope.**
  - Option A: app-level, in `apps/MintPlayer` only. **Recommended**: it keeps Spark core free of a third-party
    dependency, in line with "don't couple unrelated features".
  - Option B: a Spark option on the prerendering builder (`UseSparkPrerendering(p => p.MinifyHtml())`), which places
    it correctly for every SSR app.
  - Either way, `docs/guide-ssr.md` documents the ordering rule.
- **D2 Compression. ✅ Decided (owner, 2026-11-05).** `UseResponseCompression` (Brotli + gzip) only, with no WebMarkupMin
  `AddHttpCompression`. Two stacks gain nothing and risk double handling.
- **D3 Settings.** ⚠️ Superseded: the fixed settings are in §6 "SP1 result" and §8, set from SP1's numbers.
- **D4 Development. ✅ Decided (owner, 2026-11-05): on.**
  - The legacy site minified in Development too.
  - Being on in Development means a hydration break shows before production. `AllowMinificationInDevelopment
    Environment = true`.
  - The dev-server HTML (Vite client) has to pass SP3 for this.
- **D5 Go/no-go.** ⚠️ Superseded by G1: SP1 measured 2.4–3.1 % on Brotli, under these thresholds, and the owner keeps
  the feature anyway ("i kinda like it for 'obfuscation'", owner preference, 2026-11-05). The original text: if SP1 measures less than ~5 % saving on the Brotli-compressed size of the representative pages,
  or more than ~10 ms CPU per page, close #563 as "won't do". The evidence stays here. The thresholds are the owner's
  call.

## 6. Spikes

**SP1: value (a measurement only, per G1; it sets the internally fixed settings).**
- Capture the SSR HTML of the representative pages from a production build: home board, song, artist, person, a
  list page and a 404.
- For each page, record:
  - raw size;
  - Brotli and gzip size at the levels `UseResponseCompression` uses;
  - all three again after WebMarkupMin with the §4 settings and with the legacy settings;
  - minification CPU time (warm, median of N).
- This can run as a small console harness over the saved HTML. It needs neither the host nor the test suite.

**SP2: hydration safety.**
- Run `ServerRenderingTests` with minification on: the `:112` hydration-warning test plus a hydration-mismatch check
  in the browser console (NG0500/NG0501/NG0502).
- Settings matrix:
  - `RemoveHtmlComments`
  - each whitespace mode
  - `RemoveRedundantAttributes`
  - `RemoveOptionalEndTags`
  - quote removal
  - `MinifyEmbeddedJsonData`
- Record which settings break hydration, and on which page.

**SP3: pipeline and side effects** (§4 placement, Production and Development). Verify each of these:

1. A page is minified and still compressed (`Content-Encoding: br`), and the `Content-Encoding` throw does not fire.
2. `Cache-Control: private, no-store`, `Vary: Cookie`, `X-Robots-Tag`, HSTS, nosniff and Permissions-Policy are all
   unchanged.
3. The slug 301 and the SSR 404 keep their status.
4. Under the §4 placement, `/index.csr.html` fetched directly is byte-identical to the build output, and the SW stays
   in normal mode. Under a top-level placement, show that it is *not* identical, and so falsify that the static-files
   ordering is what protects it.
5. `/spark/*` JSON and the static assets are untouched.
6. The dev server's HMR websocket and Vite paths still work.
7. Behaviour when the Node render fails, so that the CSR fallback is still served.

**SP4: package on net11.**
- `WebMarkupMin.AspNetCoreLatest` (current version) restores and loads under `net11.0`.
- List its transitive dependencies, and check that the JS minifier it uses by default needs no extra adapter package.
- Check the licence (expected Apache-2.0).

### SP4 result (2026-11-05) ✅

- `WebMarkupMin.AspNetCoreLatest` 2.23.0 ships `net8.0`/`net9.0`/`net10.0` builds only. It restores and runs under
  `net11.0`: the SP1 harness (a net11 console) and the M1 package both build and minify with it.
- Transitive dependencies: `WebMarkupMin.Core`, `WebMarkupMin.AspNet.Common`, `AdvancedStringBuilder`. No JS/CSS adapter
  package is needed: the built-in `KristensenCssMinifier` and `CrockfordJsMinifier` live in `WebMarkupMin.Core`.
- Licence Apache-2.0.
- Read from the decompiled package, and the reason for some M1 choices: it throws `InvalidOperationException` when a
  `Content-Encoding` is already set (so compression must be outside it), defaults to status 200 and GET only, reads
  `IOptions<HtmlMinificationOptions>`, and logs to a `NullLogger` unless a `WebMarkupMin.Core.Loggers.ILogger` is
  registered (M1 registers `AspNetCoreLogger`).

### SP1 result (2026-11-05)

Input: the SSR HTML of eight production pages saved from mintplayer.com (home board, song, artist, person, the
people and songs lists, a 404, and the `/index.csr.html` template). A net11 console harness minified each with
WebMarkupMin 2.23.0 and compressed with `BrotliStream`/`GZipStream` at `CompressionLevel.Fastest` (the
`UseResponseCompression` default); CPU time is the median of 15 warm runs. Evidence: the harness log `run2.log`
(scratchpad, not committed); the numbers are copied here.

A server-rendered page is 350–460 KB raw, of which about 220 KB is the inline `<style>` and about 170 KB is
`ng-state` JSON (song page: 221 KB and 173 KB of 424 KB).

| Page | Raw | Brotli | Spark settings: raw / br / gzip saved | ms | Legacy settings: raw / br / gzip | ms |
|---|---|---|---|---|---|---|
| home | 419,829 | 67,394 | 4.1 / 3.0 / 3.7 % | 10.4 | 6.9 / 4.6 / 5.0 % | 10.0 |
| song | 424,091 | 69,440 | 5.3 / 3.0 / 4.3 % | 5.6 | 6.3 / 3.9 / 5.3 % | 7.7 |
| artist | 451,429 | 74,057 | 5.6 / 2.5 / 4.6 % | 10.0 | 6.7 / 3.3 / 5.5 % | 18.8 |
| person | 461,460 | 75,415 | 5.5 / 2.4 / 4.5 % | 7.7 | 6.7 / 3.3 / 5.6 % | 9.3 |
| people | 410,845 | 68,604 | 5.4 / 3.1 / 4.5 % | 7.4 | 7.1 / 4.4 / 5.4 % | 8.6 |
| songs | 407,445 | 68,434 | 5.4 / 3.0 / 4.4 % | 5.9 | 7.0 / 4.3 / 5.3 % | 10.8 |
| 404 | 347,811 | 59,388 | 5.6 / 2.7 / 4.3 % | 5.4 | 6.2 / 3.4 / 5.1 % | 5.9 |
| csr template | 6,932 | 2,778 | 2.6 / 0.7 / 1.5 % | 0.2 | 8.9 / 9.7 / 11.0 % | 0.3 |

What each setting is worth, from the other variants in the same log:

- **The inline CSS minifier is the whole saving.** Aggressive whitespace with CSS minification off saves 0.0 % raw on
  every SSR page; CSS minification alone, with whitespace off, saves the same 4–5.6 % as the Spark settings.
- **Safe, Medium and Aggressive are within 0.1 %** of each other (song: 401,473 / 401,471 / 401,451 bytes).
- **Inline JS minification and the attribute removals add ≈ 0** (≤ 0.03 %); they stay off (risk without gain).
- **Comment removal adds about 1 % on Brotli** (song 3.0 → 3.7 %), but the comments are Angular's hydration markers
  (NG0500), so it stays an opt-in with a warning (G5).
- No WebMarkupMin errors or warnings on any page in any variant.

These set the internal settings in M1 (§8). The saving is under D5's thresholds; the owner keeps the feature (D5).

### SP2 result (2026-11-05): folded into M2, and one real defect found

- **Method changed (owner, 2026-11-05).** The `playwright_node` MCP server's connection dropped on every
  `route.fulfill` call that was meant to serve the minified HTML in place of the live page (three restarts, owner:
  "This only happens because of what you're doing now"). The owner chose to run the hydration and CSS checks as M2
  E2E tests on the real host instead: `ServerRenderingTests`' hydration test (no NG05xx in the console) now runs on
  minified pages, and `MinificationTests` compares every element's computed style on `/` (minified) with `/home`
  (excluded through `ExcludedPaths`, so the same board as rendered).
- **Defect, in every whitespace mode.** Reading the minified song page, the lyrics (`div.spark-multiline-value`)
  came out on one line: the text was one text node with `\n` line breaks, kept on screen only by the CSS
  `white-space: pre-wrap`, which a minifier cannot see. Safe mode collapses the run of whitespace too.
- **Fix, in the framework (owner: "don't use pre-wrap").** `@mintplayer/ng-spark` po-detail renders a
  `MultiLineString` value as lines joined by `<br>` through a new exported `lines` pipe
  (`libs/node_packages/ng-spark/pipes/src/lines.pipe.ts`), and the `pre-wrap` rule is gone. Markup survives every
  minifier and crawler, and every Spark app gets it.
- **`Aggressive` breaks hydration on every page (M2 run, 2026-11-05).** `ServerRenderingTests.The_browser_hydrates_…`
  failed on both `/` and the song page: the browser logged `ERROR TypeError: Cannot set properties of null (setting
  'nodeValue')`, and re-fetched `GET /spark/translations` and `GET /spark/program-units`. The transfer state was not
  at fault: `ng-state` is byte-identical before and after minification (SP1 pages, home). Controls, same build, the
  same test, switched by configuration only:

  | `Spark:Minification` | Result |
  |---|---|
  | `Whitespace` = `Aggressive` | 2 failed |
  | `Enabled` = `false` | 2 passed |
  | `Whitespace` = `Safe` | 2 passed |
  | `Whitespace` = `Medium` | 2 passed |

  The cause: Angular (`preserveWhitespaces: false`) drops whitespace-only text that contains a line break but keeps
  one without it (`</span> <span>` on one line) as a `" "` text node, and hydration looks that node up;
  `Aggressive` removes it. The default became `Safe` (G6, superseded). `ServerRenderingTests` now puts the console
  output into the transfer-state assertion's message, which is how the error above was seen.

## 7. Milestones (after the spikes)

- **M0, core.** `SparkMiddlewareStage.BeforePrerendering`, applied by `UseSparkPrerendering` before
  `spa.UseSpaPrerendering(...)`. It throws when a module registered for it but the app never called
  `UseSparkPrerendering` (G3).
- **M1, `MintPlayer.Spark.Minification`** (`libs/minification/`), with:
  - `AddMinification(Action<SparkMinificationOptions>?)` and the G5 options, including the hydration warning on
    `RemoveComments`;
  - WebMarkupMin registered internally with the fixed settings and without `AddHttpCompression`;
  - registration into the new stage;
  - unit tests for the option mapping and for the fail-loud case.
- **M1b, the app.** `apps/MintPlayer` calls `spark.AddMinification()`. Update the Program.cs comment that cites the
  SeoTester checks.
- **M2.** Tests in `tests/MintPlayer.Spark.E2E.Tests/Ssr/`:
  - a rendered page is minified (no indentation newlines between block elements) and still hydrates without warnings;
  - `/index.csr.html` is byte-identical to the built file;
  - a `/spark` JSON response is untouched;
  - compression is still `br`.
  - Adjust the existing `:39-46` assertions only if D3 changes what they match, and say why in the test.
- **M3, docs.** A guide, `docs/guide-minification.md`, linked from `docs/guide-ssr.md`. It covers:
  - the options;
  - why it is SSR-only;
  - the ordering rule (§3);
  - the hydration warnings (`RemoveComments`; `Aggressive`, which SP2 showed breaks every page).
  - Add the package to whatever lists the published packages.
- **M4.** After deploy, check mintplayer.com with `curl -H "Accept-Encoding: br"` (size, headers, status codes) and
  with a browser hydration check. Record the before/after sizes here.

## 8. Implementation log

Spike results are in §6. Implementation, 2026-11-05 (one commit on `feat/issue563-html-minification`):

- **M0.** `SparkMiddlewareStage.BeforePrerendering` (`libs/spark/MintPlayer.Spark.Abstractions/Builder/SparkMiddlewareStage.cs`),
  applied by `UseSparkPrerendering` inside the SPA branch, just before `UseSpaPrerendering`, so the middleware sees the
  rendered page (the legacy call sat inside the renderer and only ever minified the template, §3).
  `SparkModuleRegistry.PendingStages` lists stages with registrations that were never applied; `SparkPendingStagesCheck`,
  an `IStartupFilter` that `AddSpark` registers, runs after `Program.cs` has built the pipeline and throws when
  `BeforePrerendering` is pending (G3: fail loudly without SSR). Test: `Without_UseSparkPrerendering_the_host_refuses_to_start`.
- **M1.** `libs/minification/MintPlayer.Spark.Minification`. The public surface is `AddMinification(...)` and
  `SparkMinificationOptions` (G4: no WebMarkupMin type). Configuration `Spark:Minification` is bound after the code's
  `configure`, so `Enabled: false` in appsettings switches it off without a deploy. Fixed internally, from SP1:
  - whitespace mode from the option (default `Safe`, G6 superseded); comments kept unless `RemoveComments` (G5);
  - inline and embedded CSS minified (the whole SP1 saving);
  - quotes kept; optional end tags, redundant/empty attributes, protocol removal, JSON and JS minification off
    (SP1: ≈ 0 gain);
  - status codes 200, 401, 403, 404 (the SSR error pages are full pages too; a 500 is the CSR fallback and stays as
    is), GET only, websockets skipped;
  - always excluded: `/index.html` and `/index.csr.html` (the service worker hash-checks the template, §6 SP3.4) and the
    dev-server paths `/sockjs-node`, `/ng-cli-ws`, `/@vite`, `/@fs` (SP3.6);
  - minified in Development (D4); no WebMarkupMin compression (D2) and no "powered by" header.
  Tests: `tests/MintPlayer.Spark.Tests/Minification/SparkMinificationTests.cs`, against a stand-in renderer that swaps
  the response body the way the SpaServices prerenderer does.
- **M1b.** `apps/MintPlayer`: `spark.AddMinification()`, project reference, Dockerfile `COPY`, and the path filters of
  `mintplayer-deploy.yml` and `mintplayer-image-check.yml` (`tools/verify-mintplayer-deploy.test.mjs` passes).
- **ng-spark.** `lines` pipe and the po-detail `MultiLineString` markup (§6 SP2), spec `lines.pipe.spec.ts`.
- **M2.** `tests/MintPlayer.Spark.E2E.Tests/Ssr/MinificationTests.cs`. The E2E host excludes `/home`
  (`MintPlayerTestHost.UnminifiedHome`), the same board as `/`, as the unminified baseline.
- **M3.** `docs/guide-minification.md`, linked from `docs/guide-ssr.md`.
- **Test run (2026-11-05, local, once at the end):** `SparkMinificationTests` 22/22; E2E `Ssr.MinificationTests` +
  `Ssr.ServerRenderingTests` 10/10 with the `Safe` default (hydration without warnings or re-fetches on `/` and the
  song page); ng-spark `lines.pipe.spec.ts` 5/5. The full sweep is left to the PR's CI.
- **Open:** M4 after deploy. A stronger CSS minifier (`WebMarkupMin.NUglify`) was raised by the owner's question and
  is not measured.
