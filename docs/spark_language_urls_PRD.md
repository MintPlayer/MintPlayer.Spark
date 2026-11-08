# Language in the URL (`?lang=`), hreflang, sitemap alternates, linkable language picker — PRD and plan

Status: **investigated, decisions proposed, not yet grilled** (2026-11-08). Issue: #604. Related: #602 (generic slugs,
its D9 waits on this). One pull request (Spark core + sitemap + ng-spark + the apps that change).

## 1. Goal

Each presentation language of a page has **its own URL**, and every page tells crawlers which language versions exist:

- **`?lang=xx`** selects the language. The framework honours it on the server (before and during SSR) and on the client.
  It wins over the `spark-lang` cookie and `Accept-Language`.
- Every SSR page renders **`<link rel="alternate" hreflang>`** for each supported language plus `x-default`, also when
  the app has only one language (owner, 2026-11-08). `<html lang>`, `og:locale` / `og:locale:alternate` and JSON-LD
  `inLanguage` agree with it.
- The **XML sitemap** lists the language alternates of each URL.
- The **language picker** is made of real links that crawlers can follow, instead of a `<select>` that writes a cookie.

## 2. What exists today (evidence)

### How the language is chosen

- **Supported languages and default:** `App_Data/culture.json` `{languages, defaultLanguage}`, loaded by
  `CultureLoader.cs:35-94` (fallback `["en"]`/`"en"`). Display names come from translation keys `culture.languages.{code}` (:92).
  - en/fr/nl: CodeCoverage, Fleet, HR, MintPlayer, QnA, SparkId.
  - en/nl: SeoTester.
  - English only (no culture.json): DemoApp, Festival, MailRelay, ReferenceData.
- **API requests:** no `UseRequestLocalization`. `IRequestCultureResolver` (`RequestCultureResolver.cs:16-43`) uses
  `Accept-Language`, then the base culture, then the default. It never reads the cookie.
- **SSR:** `SparkPrerenderingService.ResolveLanguage` (:93-99) uses the cookie when it names a configured language, else
  the resolver.
  - The result travels as `data.spark.language` (:85).
  - Every in-process API call made during the render gets `Accept-Language = page.Language` (`SparkPrerenderDispatcher.cs:140`).
  - In Node it becomes `SPARK_SERVER_LANGUAGE` through an AsyncLocalStorage (`create-spark-server-renderer.ts:29-31,75`;
    `provide-spark-server-rendering.ts:23-24`; `models/src/translated-string.ts:19-27`).
- **Client:** `SparkLanguageService` (`services/src/spark-language.service.ts`).
  - Precedence: server language, then cookie, then the legacy localStorage value, then the default (:45, 52-69).
  - `setLanguage` sets the signal and writes the cookie (:88-93, 100-104). It does not navigate or reload.
  - `/spark/translations` returns every language at once (:95-98); labels are resolved on the client.
  - `<html lang>` is set by an effect (:38), on the server and in the browser.
- **API calls carry the chosen language only with `withSparkLanguage()`** (`spark-language.interceptor.ts:27-31`). Only
  MintPlayer, CodeCoverage and SeoTester add it. In the other apps, strings the server resolves (notifications,
  `GetTranslatedMessage`, dashboards, search/MCP labels) follow the browser's header, not the picker.
- **Nothing reads `?lang`** in the framework. The only place that does is SeoTester's own server pages (`ServerPage.cs:16`).
- **Nothing keeps a query parameter across navigations:** there is no global `queryParamsHandling`; `'merge'` is only used
  for in-page navigations (`spark-query-list.component.ts:144,253,260`).
- **Caching:** there is no output cache or ETag. Prerendered pages are `private, no-store`, `Vary: Cookie`
  (`SparkPrerenderingService.cs:55-56`).
- **Hydration:** the transfer cache keys on URL and body, not headers (`provide-spark-hydration.ts:41-45`). The client
  must start in the same language as the server render, or the page flips during hydration.
- **The breadcrumb does not depend on the culture** (`BreadcrumbResolver` has no culture code). That matters for #602's
  slug question (§3 D10).

### SEO tags

- All client SEO lives in `services/src/spark-seo.service.ts`.
  - `forObject` (:80-110) sets title, robots, description, OG tags, the canonical link (`origin()` + path) and JSON-LD.
  - `forPage` (:119-130) builds the canonical from the path **with the query removed** (:126). With `?lang` it would point
    every language at one URL, so canonical and hreflang must be designed together.
  - `reset` (:139-145) clears `SEO_META`, the canonical and the JSON-LD.
  - `setCanonical` (:177) is the pattern for new link tags.
- **No hreflang, `og:locale`, `inLanguage` or `x-default` anywhere** in the framework or the apps.
- Model SEO block: `EntityTypeSeo` (`EntityTypeDefinition.cs:268-294`), with no language field. The server writes no head
  tags of its own.
- **SeoTester defines what "correct" means:**
  - `seo.html-lang` requires valid BCP 47 (`PageChecks.cs:78-80`).
  - `seo.hreflang` (:247-259) is not applicable when there are no alternates. It fails on invalid codes and fails without
    a **self-reference that matches the crawled URL exactly**, so a `?lang=nl` page must list itself with `?lang=nl`.
  - Return links: if A lists B, B must list A (`CrawlChecks.cs:58-65`).
  - Guidance (`Catalogue/checks.json:169-188`): every version lists all versions including itself, plus `x-default`; the
    targets return 200 and are indexable.

### Sitemap

- `SparkSitemapReader.IndexAsync` (:24-40) writes one `<sitemap>` per `PageSize` (= `QueryReadPath.MaxTake`) rows of each
  indexable type. `PageAsync` (:43-56) writes one `Url { Loc = DetailUrl(...) }` per object, as an anonymous user, with no
  language.
- `SparkSitemapXml.Write` (:51-63) uses `XmlSerializer` with empty default namespaces. Caching is a `Cache-Control`
  header only (default 1 h).
- The NuGet `MintPlayer.AspNetCore.SitemapXml.Abstractions` 10.0.0 already has `Url.Links` (`Link{Rel, Href, HrefLang}`) and
  the `xhtml` namespace, so alternates need no new model.

### Language picker

- `spark-language-selector` (`shell/src/spark-language-selector.component.ts:18-29`) is a `<bs-select>`, rendered as a
  native `<select>` with `<option>`s. It hides itself with one language (:20). On change it calls `setLanguage`: cookie
  only, no URL change.
- It is the shell's default trailing topbar item (`spark-shell.component.html:25`). Six apps override that slot and put it
  back (Fleet, QnA, MintPlayer, SparkId, SeoTester, HR: `shell/shell.component.html:5-6`). **CodeCoverage has its own copy**
  of the `bs-select` (`shell.component.html:10-19`).
- It is in the SSR HTML, but as `<option>`s, which crawlers don't follow, and the server would ignore the choice anyway.
- `@mintplayer/ng-bootstrap/dropdown`'s `bs-dropdown-menu` in nav mode renders `<details><summary>` with the panel always
  in the DOM, so anchors inside it are in the SSR HTML. The `[bsDropdown]` overlay probably renders only when it is open,
  so it doesn't suit crawlable links. `bs-dropdown-item` supports `routerLink`.
- **Legacy mintplayer.com** used `?lang=` (none for English, `nl`, `fr`; `app.component.ts:169-177`), with three flag links
  in the sidebar (`main-sidebar.component.html:9-17`, `<a [queryParams]="{lang:'nl'}"><app-flag>`) and hreflang tags for
  en/fr/nl (`helpers/hreflang-tag.helper.ts:20-46`). No flag assets exist in this repo.

## 3. Proposed decisions

| # | Decision | Evidence / reason |
|---|---|---|
| D1 | **Precedence: `?lang` (if configured) → cookie → `Accept-Language` → default**, the same on the server (`ResolveLanguage`) and the client (`SparkLanguageService`). An unknown `?lang` value is ignored. | The server render already passes its language to the in-process API calls and to Node (§2), so reading the query in `ResolveLanguage` is enough for SSR. The client must use the same order, or hydration flips. |
| D2 | **URL policy: every language has an explicit `?lang=xx` URL, and the bare URL is `x-default`**, which keeps negotiating (cookie, then `Accept-Language`). Each `?lang=xx` page is its own canonical. *Open: Q1.* | Google's documented use of `x-default` is the "no language chosen / auto-negotiating" page. A bare URL declared as `en` would serve Dutch to a visitor with a `nl` cookie, so the declared and served language could differ. Legacy's alternative (no parameter = English) is simpler but has that mismatch. |
| D3 | **Following a `?lang` link also writes the cookie**, so the visitor stays in that language on bare URLs. | Today the picker writes the cookie; a link must not do less. |
| D4 | **Internal links keep `?lang`** when the current URL has it, generically at the router level (S1), so a crawler that enters at `?lang=nl` keeps crawling Dutch. | Nothing keeps query parameters today (§2). Without this, Dutch is reachable only one link deep and through the sitemap. |
| D5 | **`SparkSeoService` renders the alternates on every page** (`forPage` and `forObject`): one `<link rel="alternate" hreflang>` per configured language (`?lang=xx` URLs), plus `x-default` (bare URL), self-reference included; always, also with one language. `reset()` removes them. The canonical keeps `lang` and drops every other query parameter. | Owner, 2026-11-08 ("always, even for just one language"). SeoTester requires an exact self-reference and return links (§2). |
| D6 | **The other language statements agree**: `<html lang>` (already set), `og:locale` + `og:locale:alternate`, JSON-LD `inLanguage`. Locale form (`nl` vs `nl_BE`) per S2. | Owner: "there are other places where the language is defined (meta tags)". |
| D7 | **Sitemap: one `<url>` per object per language** (`?lang=xx`), each with `xhtml:link` alternates for every language plus `x-default`. The sitemap page size is divided by the number of languages to stay under the protocol's 50,000 URLs per file. | The NuGet model already has `Url.Links` (§2). The 50,000 limit is the sitemap protocol's. Serializer namespace check in S3. |
| D8 | **The picker becomes a list of real links** (`<a href="?lang=xx">`, current path kept), in a `bs-dropdown-menu` whose items are in the SSR HTML. The label is the **language name in its own language** (English, Français, Nederlands). Flags are optional, per app. CodeCoverage's copy is replaced by the shared component. *Open: Q2.* | Crawlers follow `<a href>`, not `<option>` (§2). Flags stand for countries, not languages: Belgian users read Dutch and French, and a `nl` flag or a `fr` flag misrepresents them. Legacy used flags. |
| D9 | **Switching language is a full page load** (a plain `href`, no `routerLink`), so the server renders the new language. | Strings the server resolves (notifications, breadcrumbs an app resolves itself, dashboards) don't refresh after `setLanguage` today. A full load fixes that without a refetch mechanism. Switching language is rare. |
| D10 | **`withSparkLanguage()` becomes the default**: the client always sends the chosen language as `Accept-Language`. | Without it, six apps resolve server-side strings in the browser's language, not the chosen one (§2). Preview, so no compat flag. |
| D11 | **#602 D9 resolves to one slug per object**: the breadcrumb doesn't depend on the culture (§2), so the slug is the same in every language and only `?lang` differs. | `BreadcrumbResolver` has no culture code. If an app's breadcrumb ever became translated, the 301 would still be correct because it compares against the same breadcrumb. |

## 4. Spikes

Each spike is time-boxed (≤ 1 h), ends with a decision written here, and ships nothing on its own.

- **S1 — keeping `?lang` on internal links (D4).** Compare: a custom `UrlSerializer` (or `UrlTree` hook) that appends `lang`
  to every serialized URL; `withRouterConfig` + per-link `queryParamsHandling: 'merge'`; or the #602 link pipe adding it.
  Check `routerLinkActive`, URL equality, `RedirectCommand`, SSR, links that must drop `lang` (share links, external URLs).
- **S2 — URL policy and locale form (D2, D6).** Confirm Google's current guidance on `x-default` and on query-parameter
  language URLs; decide `og:locale` values (`en_US`? `nl_BE`?) and whether `culture.json` needs a region per language. Run
  SeoTester's `seo.hreflang` check against a prototype head.
- **S3 — sitemap (D7).** Does `XmlSerializer` with the empty-namespace override write `xmlns:xhtml` and `xhtml:link`
  correctly? Page size versus 50,000 URLs and the 50 MB limit with three languages; robots.txt's `Sitemap:` line unchanged.
- **S4 — the picker (D8).** `bs-dropdown-menu` nav mode: SSR output, keyboard and screen-reader behaviour, closing on
  selection, the topbar slot API, CodeCoverage's copy. Flags: reuse legacy's `app-flag` or skip them.
- **S5 — single-language apps and `x-default`.** What exactly is rendered with one language (`hreflang="en"` + `x-default`,
  both self-referencing)? Does `?lang=en` on such an app redirect, 404 or render?

## 5. Milestones (one PR)

1. **M1 — resolution:** `?lang` in `ResolveLanguage` (server), in `SparkLanguageService` (client) and the cookie write
   (D1, D3); `withSparkLanguage` by default (D10); tests on the precedence, including an unknown value.
2. **M2 — links keep `lang`** per S1.
3. **M3 — head tags:** hreflang alternates, canonical with `lang`, `og:locale(:alternate)`, `inLanguage` (D5, D6); unit tests
   on `SparkSeoService` for one and for three languages.
4. **M4 — sitemap alternates** per S3; tests on the XML output (namespaces, alternates, page size).
5. **M5 — picker** per S4 in ng-spark; the six apps' slots and CodeCoverage's copy switch to it.
6. **M6 — docs + release note** (ng-spark minor: the selector's markup and the interceptor default change; NuGet minor).
7. **M7 — verify once at the end:** Spark + ng-spark tests; on a running MintPlayer host: `?lang=nl` renders Dutch over a
   `fr` cookie, the SSR HTML has the alternates and the right `<html lang>`, a click keeps `lang`, the sitemap lists the
   alternates; a SeoTester scan of the local site shows no `seo.hreflang` findings.

## 6. Open questions for the owner

- **Q1** Default-language URL: explicit `?lang=en` for every language with the bare URL as a negotiating `x-default` (D2),
  or legacy's "no parameter = default language"?
- **Q2** Picker: language names (recommended), flags, or both?
- **Q3** Should `?lang` stay on every internal link (D4), or only matter on entry (cookie afterwards, sitemap for crawlers)?
- **Q4** Order with #602: both touch `SparkSeoService` canonical and the link helpers. Which lands first? Recommendation:
  #602 first, as planned; this PR then adds `lang` to its link helper.
