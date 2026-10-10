# SEO / site-quality analyzer: domain research (for the PRD)

Date: 2026-10-10. Source: the owner's check list in GitHub issue #268, checked against current (2026)
sources where I was unsure. Where a fact comes from web research, the source is listed at the end. The
rest is established knowledge. Facts marked **[verify]** need checking before they are relied on.

Legend for the **Layer** column:
- **H**: raw HTML from a plain HTTP GET (cheap, about 10 ms of CPU per page)
- **R**: rendered DOM (needs a headless browser, about 100x the cost of H)
- **N**: HTTP response headers / network level (status, headers, redirects, compression, protocol)
- **T**: TLS handshake level
- **D**: DNS / registry level (resolver, RDAP)
- **X**: external API (PSI/CrUX, hstspreload.org, RDAP)
- **S**: site level, aggregated over the crawl (duplicates, orphans, link graph)

The **Sev** column gives the suggested weight inside a category: **C**ritical = 10, **H**igh = 6,
**M**edium = 3, **L**ow = 1, **I**nfo = 0 (shown, not scored).

---

## 1. Check catalogue

### 1.1 SEO (on-page)

| Check | How to measure | Layer | Sev | Suggestion pattern |
|---|---|---|---|---|
| `<title>` present | exactly one `head > title`, not empty after trimming | H (R for SPAs) | C | "Page {url} has no title. Add a descriptive `<title>` of 30–60 characters." |
| Title length | measure the **pixel width** (Arial 20px, about 580–600 px), not just characters; flag below 30 or above 60 characters as a fallback | H | M | "The title is {n} characters and Google will likely truncate it. Keep it under ~60." |
| Duplicate titles | group by normalized title across pages | S | H | "{k} pages share the title '{t}'. Make each title unique." |
| Meta description present and length | `meta[name=description]`, 70–160 characters (pixel width about 920 px on desktop) | H | M | "Add a meta description that summarises the page in about 150 characters." |
| Duplicate descriptions | group across pages | S | M | as for titles |
| SERP previewer | renders title, URL breadcrumb and description at Google's widths, desktop and mobile | H | I (feature) | — |
| Exactly one `<h1>` | count `h1`, flag 0 or more than 1; also flag an h1 that equals the title character for character (info only) | H/R | M | "Page has {n} `<h1>` elements. Use exactly one main heading." Note: since HTML5 several h1s are not a Google penalty, so keep this at Medium. |
| Heading hierarchy | no skipped levels (h2 to h4) | H | L | Overlaps with accessibility; count it once only. |
| Canonical | `link[rel=canonical]`: absolute, self or another URL, returns 200, not itself redirected or noindexed, only one present, and consistent with the `Link:` header | H+N | H | "The canonical points to {u}, which returns {status}." |
| hreflang | `link[rel=alternate][hreflang]` and the HTTP header: valid BCP-47 code, **return links** (A→B requires B→A), a self-reference, `x-default`, targets return 200 and are indexable | H+S | H when present, I when absent | "hreflang to {u} has no return link." |
| `<html lang>` | present and valid BCP-47 | H | M | (also accessibility) |
| Content-Language header | mostly ignored by Google; Bing uses it | N | I | Informational only. |
| Meta robots / X-Robots-Tag | parse noindex/nofollow/none/max-snippet; flag **noindex on pages that are in the sitemap or linked internally** | H+N | C when noindex is accidental | "{url} is noindex but is listed in your sitemap." |
| JSON-LD present and valid | parse every `script[type=application/ld+json]`: JSON syntax, `@context`, `@type`, required properties per Google's supported types (Organization, BreadcrumbList, Article, Product, LocalBusiness, Event, Recipe, FAQ, ...) | H (R if injected) | M | "JSON-LD block {i} is invalid JSON at line {l}." / "Product is missing `offers` or `review`; it won't be eligible for rich results." |
| Microdata / RDFa | detect only | H | I | — |
| Image alt (SEO side) | covered under Accessibility; count once | | | |
| `<a>` title attribute | **misleading check**: has no SEO value and is poor for accessibility (inconsistently announced, invisible on touch). Flag instead **links with no accessible name** and **generic anchor text** ("click here", "read more") | H | L | "{n} links use generic text such as 'click here'. Use descriptive anchor text." |
| Content: thin content | word count of the main content (after stripping nav/footer), with a threshold of about 200 words, info | H/R | L | |
| Keyword / content analysis | top n-grams, keyword in the title, h1 and first paragraph; readability (Flesch, language-aware) | H/R | I | Offer as a feature; don't score it. |
| URL hygiene | length, uppercase, underscores, query-string session ids, non-ASCII without encoding | H | L | |
| Meta keywords | **obsolete**: ignored by Google since 2009 | H | I or drop | Informational at most ("has no effect; safe to remove"). |
| Sitelinks search box (WebSite + SearchAction) | **retired**: Google removed the feature worldwide on 2024-11-21. The markup is harmless, and WebSite markup for the *site name* is still used | H | drop | If found: "SearchAction markup is no longer used by Google; harmless." |
| rel=prev/next | Google stopped using it in 2019 | H | drop | |
| Deprecated rich-result types | in June 2025 Google phased out Course Info, Claim Review, Estimated Salary, Learning Video, Special Announcement and Vehicle Listing (it later reinstated Book Actions); more search features were removed with effect from January 2026; FAQ/HowTo have been restricted since 2023 | H | I | "Type {t} no longer produces rich results in Google." The type list must be data, not code, so it can be updated without a deploy. |

### 1.2 Social and preview

| Check | How | Layer | Sev | Suggestion |
|---|---|---|---|---|
| Open Graph core | `og:title`, `og:description`, `og:image` (absolute https URL that returns 200 with an image/* content type, ideally 1200×630 and under 8 MB), `og:url`, `og:type`, `og:site_name` | H (+N to fetch the image) | M | "og:image is missing, so shared links will show no picture." |
| OG previewer | render Facebook/LinkedIn/Slack/WhatsApp cards from the OG tags | H | feature | |
| Twitter/X card | `twitter:card` (summary / summary_large_image); X falls back to OG for title, description and image, so **only `twitter:card` is really needed** | H | L | "Add `twitter:card=summary_large_image` to get a large preview on X." |
| X card previewer | | H | feature | |
| Share buttons | heuristic: links to share endpoints (facebook.com/sharer, x.com/intent, linkedin shareArticle, wa.me, `navigator.share`) | H/R | I | Treat as informational; it is a marketing choice, not a quality defect. |

### 1.3 Performance

| Check | How | Layer | Sev | Suggestion |
|---|---|---|---|---|
| Core Web Vitals (field) | CrUX API: LCP (good ≤ 2.5 s), INP (≤ 200 ms; replaced FID in March 2024), CLS (≤ 0.1), p75, origin and URL level, phone/desktop. Small sites often have **no CrUX data**; show "insufficient real-user data" | X | H | "Real users see an LCP of {x} s at the 75th percentile (good is ≤ 2.5 s)." |
| Lab performance | PSI API v5 (runs Lighthouse 13 on Google's infrastructure): performance score plus insight audits | X | H | Relay Lighthouse's "insights" (render-blocking requests, image delivery, LCP breakdown, font display, ...). |
| Compression | send `Accept-Encoding: br, gzip, zstd`; check `Content-Encoding` on HTML/CSS/JS/SVG/JSON over 1 KB. Report br/zstd as best, gzip as OK, none as fail | N | H | "{n} text resources are served uncompressed. Enable Brotli or gzip." |
| Cache-Control on static assets | hashed assets: `max-age ≥ 1 year, immutable`; HTML: `no-cache` or short max-age, plus a validator | N (R to list assets) | M | "{n} static assets have no long-lived Cache-Control." |
| ETag / Last-Modified | presence of a validator; actually test a **conditional GET** returns 304 | N | L | "Responses have no ETag or Last-Modified, so browsers must re-download unchanged files." |
| Minification | heuristic on CSS/JS: the ratio of whitespace and comments to size; or minify with NUglify and compare (more than 10% saving means not minified). Skip HTML minification (low value and noisy) | N | L | |
| HTTP request count / page weight | from the rendered page's network log: number of requests, transferred bytes per type, third-party share | R | M | "The page makes {n} requests ({kb} KB). Consider bundling or deferring third parties." |
| HTTP/2 / HTTP/3 | ALPN result (h2); HTTP/3 via `Alt-Svc: h3=` and an actual QUIC request | N/T | M (h2), I (h3) | "The server only speaks HTTP/1.1. Enable HTTP/2 for multiplexing." |
| Lazy-loading below the fold | images outside the first viewport without `loading=lazy`; **and the LCP image must not be lazy** (a common mistake) | R | M | "The hero image (LCP) is lazy-loaded, which delays it. Remove `loading=lazy` and add `fetchpriority=high`." |
| Layout shift | `img`/`video`/`iframe` without width and height (or aspect-ratio); CLS from CrUX/PSI | H+X | M | |
| font-display | `@font-face` without `font-display: swap/optional`; Google Fonts URLs without `&display=swap` | H/N (parse CSS) | L | |
| Resource hints | `preconnect` to critical third-party origins, `dns-prefetch`; **flag too many** preconnects (more than 4 hurts) and unused preloads | H/R | L | |
| SPA route-level code splitting | heuristic: initial JS above about 300 KB compressed and a single main bundle; Lighthouse's "reduce unused JavaScript" / unused-bytes metric from coverage | R/X | L | "Initial JavaScript is {kb} KB, of which {p}% is unused on this page. Split by route." |
| Inline styles | `style=` attributes count is a weak signal (CSP `unsafe-inline` pressure, maintainability). Present as a **Best practice** or under the CSP check, not as performance | H | I/L | |

### 1.4 Security

| Check | How | Layer | Sev | Suggestion |
|---|---|---|---|---|
| HTTPS | the site answers on 443 with a valid certificate (chain, hostname, expiry). Warn when the certificate expires in under 14 days | T | C | "The certificate expires in {d} days." |
| HTTP→HTTPS redirect | `http://host/` returns 301/308 to `https://` in **one hop** to the same host first (a requirement for HSTS preload) | N | H | |
| HSTS | `Strict-Transport-Security` on HTTPS responses: `max-age ≥ 31536000`, `includeSubDomains`; `preload` is optional | N | H | |
| HSTS preload status | `GET https://hstspreload.org/api/v2/status?domain=` gives status: preloaded / pending / rejected / unknown **[verify schema]**; plus `api/v2/preloadable` for errors | X | L (info if not) | Never auto-suggest `preload` without warning that it is hard to undo. |
| Deprecated TLS (1.0/1.1, SSLv3) | handshake probe per version (see §3.3); also weak ciphers, a missing TLS 1.3 | T | H (accepts 1.0/1.1), M (no 1.3) | "The server still accepts TLS 1.0. Disable it." |
| Mixed content | `http://` subresources on HTTPS pages (active means scripts/iframes/CSS = Critical, passive means images = Medium); upgrade-insecure-requests | H/R | H | |
| Content-Security-Policy | present; evaluate `unsafe-inline`/`unsafe-eval`/wildcards (Google's CSP Evaluator rules); `frame-ancestors` | N/H | M | |
| X-Content-Type-Options: nosniff | presence on all responses | N | M | |
| Clickjacking | `frame-ancestors` in CSP or `X-Frame-Options` | N | M | |
| Referrer-Policy | present (default `strict-origin-when-cross-origin` is fine; flag `unsafe-url`) | N | L | |
| Permissions-Policy | presence, info (replaced Feature-Policy) | N | I/L | |
| COOP/COEP/CORP | info | N | I | |
| Server signature | `Server` with a version number (e.g. `nginx/1.18.0`, `Microsoft-IIS/10.0`), `X-Powered-By`, `X-AspNet-Version`, `X-AspNetMvc-Version` | N | L | "Your server reveals '{v}'. Remove version details." Low: security by obscurity, but cheap to fix. |
| X-XSS-Protection | **obsolete**: removed from all modern browsers, and `1; mode=block` could introduce leaks. Recommend `0` or absence | N | I | "X-XSS-Protection is obsolete; rely on CSP instead." Never penalise its absence. |
| Expect-CT, Public-Key-Pins (HPKP) | **obsolete** | N | I | Recommend removal. |
| Subresource Integrity | cross-origin `script`/`link rel=stylesheet` without `integrity` (same-origin is exempt; scripts whose content is dynamic, such as GTM or analytics, **cannot** use SRI, so exclude known dynamic hosts) | H | M | "{n} third-party scripts load without SRI." |
| Outbound `target=_blank` noopener | since 2021 browsers imply `noopener` for `target=_blank`, so the **security** risk is mostly gone. `noreferrer` is a privacy choice. Flag only if the link explicitly sets `rel=opener` | H | I | Downgrade from the owner's list. |
| security.txt | `GET /.well-known/security.txt` (RFC 9116): 200, `text/plain`, required `Contact:` and `Expires:` (not in the past); optional signature | N | L | "security.txt expired on {date}." |
| /.well-known/change-password | returns a redirect (3xx) or 200 to the password page; check `/.well-known/resource-that-should-not-exist-whose-status-code-should-not-be-200` returns non-200 (spec heuristic) | N | L (only when the site has a login form) | |
| Directory listing | heuristics on likely paths (`/images/`, `/uploads/`, `/assets/`, `/css/`): title "Index of /" or IIS "- /" plus `[To Parent Directory]` | N | M | Only probe directories already seen in asset URLs, so the probe stays low-impact. |
| Exposed files | `/.git/HEAD`, `/.env`, `/.DS_Store`, `/web.config`, `/phpinfo.php`, backup files | N | C if found | **Only behind ownership verification** (it is an intrusive probe; see §4). |
| XSRF / CSRF | not measurable from outside in a safe way (would require submitting forms). Possible passive heuristic: POST forms without a hidden token-like field, cookies without `SameSite`. Report as **info** | H/N | I | |
| Cookie flags | `Set-Cookie` without `Secure`/`HttpOnly`/`SameSite` (session-like names) | N | M | |
| CAA | see Domain/DNS | | | |
| Third-party analytics consent | info only: GA/GTM detected before consent (GDPR angle for the EU audience) | R | I | |

### 1.5 Accessibility

Recommendation: **run axe-core** (MPL-2.0) in the headless page (Playwright `AddScriptTagAsync` with
axe.min.js, then `axe.run()`), and map its rules onto the owner's items, rather than hand-writing these
checks. axe covers alt text, labels, ARIA validity, contrast, landmarks, link names and `html lang`.
Lighthouse's accessibility category is axe as well.

| Check | How | Layer | Sev | Suggestion |
|---|---|---|---|---|
| img alt | `img` without an `alt` attribute (`alt=""` is **valid** for decorative images, so don't flag it) | H | H | |
| Form labels | `input/select/textarea` without `label[for]`, a wrapping label, `aria-label` or `aria-labelledby` | H/R | H | |
| ARIA validity | invalid roles or attributes, `aria-hidden` on focusable elements | R (axe) | M | |
| Colour contrast | WCAG AA 4.5:1 | R (axe) | M | |
| Touch targets | WCAG 2.2 SC 2.5.8: at least 24×24 CSS px (Lighthouse/Google recommend 48×48) at a mobile viewport | R | M | |
| Document language, landmarks, skip link, heading order | axe | R | L–M | |
| prefers-color-scheme | **info**: detect `@media (prefers-color-scheme: dark)` in CSS or a `color-scheme` meta/CSS property. Not a defect when absent | H/N (CSS) | I | "Supports dark mode: yes/no." |
| prefers-reduced-motion | info | CSS | I | |

### 1.6 PWA and mobile

| Check | How | Layer | Sev | Suggestion |
|---|---|---|---|---|
| Viewport meta | `width=device-width, initial-scale=1`; flag `user-scalable=no` / `maximum-scale=1` (accessibility) | H | C (missing) | |
| Responsiveness | render at 375 px: `document.documentElement.scrollWidth > innerWidth` means horizontal overflow; font size at least 12 px for most text | R | H | "Content is wider than a phone screen ({w} px)." |
| meta charset utf-8 | `<meta charset="utf-8">` within the first 1024 bytes **or** a `Content-Type: ...; charset=utf-8` header | H+N | M | |
| Web app manifest | `link[rel=manifest]` fetches and parses: `name`/`short_name`, `start_url`, `display` (standalone / fullscreen / minimal-ui), icons of 192 and 512 px (and a `purpose: maskable` icon), `theme_color`, `background_color`, `id` | H+N | M if a PWA is intended, otherwise I | Note: Lighthouse **dropped its PWA category in v12 (2024)**, so present PWA as an opt-in profile, not a general score penalty. |
| theme-color meta | `meta[name=theme-color]`, optionally with media variants for light and dark | H | L | |
| Service worker | registered (`navigator.serviceWorker.getRegistrations()` after load) and the page works offline (Playwright `context.SetOfflineAsync(true)`, reload) | R | L/I | |
| "PWA update" | not measurable externally beyond the SW `updateViaCache` and the `Cache-Control` on sw.js (should be no-cache or short) | N | I | "sw.js is cached for {x} s, so updates can be delayed." |
| Icons | `link rel=icon` (an SVG and/or 32 px PNG, `/favicon.ico` fallback), `apple-touch-icon` 180×180; fetch each and validate the type and dimensions | H+N | L | |
| apple-mobile-web-app-capable | **obsolete-ish**: superseded by the manifest `display` (Safari reads the manifest since iOS 16.4); `apple-touch-startup-image` is largely irrelevant | H | I | Downgrade. |
| AMP | **largely obsolete**: Google dropped the AMP requirement for Top Stories in 2021 and the AMP badge; AMP gives no ranking benefit | H | I/drop | Detect `html[amp]` / `link rel=amphtml` and **validate the pairing** only if present (a broken amphtml link is still an error). |

### 1.7 Technical and crawlability

| Check | How | Layer | Sev | Suggestion |
|---|---|---|---|---|
| Crawl internal pages | BFS from the start URL plus sitemap URLs; same registrable domain (configurable: www/apex) | — | — | (engine) |
| robots.txt | `/robots.txt`: 200 / 404 (fine) / 5xx (**Google treats 5xx as disallow-all** while it persists). Parse per RFC 9309; flag `Disallow: /` for `*` or Googlebot, blocked CSS/JS, a size over 500 KiB, and `Sitemap:` lines | N | C (blocks everything), M | |
| Sitemap | from robots.txt `Sitemap:` or `/sitemap.xml`: XML valid, under 50k URLs / 50 MB uncompressed, index files, gzip; URLs return 200, are canonical and indexable; `lastmod` is plausible | N+S | M | "{n} sitemap URLs redirect or return 404." |
| Sitemap stylesheet | `<?xml-stylesheet?>` is cosmetic | | I | |
| Broken links | internal links returning 4xx/5xx (HEAD, falling back to GET); external links sampled and rate-limited | N+S | H (internal), M (external) | |
| Redirect chains and loops | more than one hop; 302 used for permanent moves; internal links pointing at redirects | N+S | M | |
| Orphan pages | in the sitemap but not reachable via internal links (needs the sitemap to be the source of truth) | S | M | |
| Crawl depth | pages more than 3 clicks from home | S | L | |
| Status codes | soft-404 (a 200 with "not found" content or a near-duplicate of the 404 page: request `/random-guid` and compare) | N+S | M | |
| Duplicate content | SimHash of the main text; www/non-www and trailing-slash duplicates without a canonical or redirect | S | M | |
| JS-rendered content / noscript / SSR | compare **raw HTML vs rendered DOM**: the text-length ratio, presence of title/h1/links in the raw HTML. If raw is empty and rendered is full, it's a CSR-only SPA: "Search engines and social crawlers that don't run JS see an empty page; consider SSR/prerendering." `<noscript>` content is info | H+R | H | |
| Analytics in SPAs | detect GA4 (`gtag/js?id=G-`), GTM (`gtm.js?id=GTM-`); in SPAs check page_view fires on **route change** (Playwright: intercept `google-analytics.com/g/collect` requests, navigate client-side via a link click, count hits). GA4 enhanced measurement handles history changes by default, so mainly verify it | R | I/L | "Analytics doesn't record client-side route changes." |
| Opensearch description | `link[rel=search][type=application/opensearchdescription+xml]`: fetch and validate | H+N | I | |
| Favicon at /favicon.ico | 200 | N | L | |
| HTML validity | optional: Nu HTML Checker (vnu.jar, Java) is heavy; AngleSharp's parse errors as a light proxy | H | I | |
| Mobile-first | Google completed mobile-first indexing in July 2024; check that the content and links of the mobile render match the desktop render | R | M | |

### 1.8 Domain and DNS

| Check | How | Layer | Sev | Suggestion |
|---|---|---|---|---|
| Domain expiry ("long-term domain") | RDAP (§3.6): `events[eventAction=expiration]`. Warn below 30 days, **info** under 1 year. Registration length is **not** a ranking factor (Google has said so repeatedly), so phrase it as business-continuity risk, not SEO | D/X | M (under 30 days), I | "Your domain expires on {date}. Enable auto-renew." |
| CAA | DnsClient CAA query on the domain, walking up the tree (RFC 8659): present? does it allow the CA that issued the current certificate? (issuer from the cert vs `issue` tags) | D | L | |
| DNSSEC | `DS` record at the parent (AD flag via a validating resolver) | D | L/I | |
| SPF / DMARC / MTA-STS | TXT on the apex / `_dmarc` (domain hygiene; anti-spoofing) | D | L | Valuable extra, cheap. |
| IPv6 | AAAA present | D | I | |
| Nameserver redundancy | at least 2 NS in different networks | D | I | |
| www vs apex | both resolve and one redirects to the other | D+N | M | |

---

## 2. Features from commercial tools (prioritised)

**P0, must-have for a credible v1**
1. **Site crawl with a link graph**: broken links, redirect chains, duplicate titles/descriptions, noindex/canonical conflicts, sitemap vs crawl diff (orphans). (Screaming Frog, Ahrefs Site Audit, Semrush)
2. **Per-issue grouping, not per-page lists**: "Missing alt text, 23 pages", which drills down to URLs. The UX all the audit tools converge on.
3. **Score plus prioritised fix list** (impact × effort). SEOptimer, Lighthouse.
4. **Core Web Vitals via the PSI API** (lab + CrUX field data in one call: `runPagespeed?url=&strategy=mobile&category=performance&category=seo&category=accessibility&category=best-practices&key=`). Free with a key: about 25,000 queries/day and 400 per 100 s per project. Run it only on a sample (home plus the top N templates), never on every page. The CrUX API (`records:queryRecord`, plus `queryHistoryRecord` for a 40-week history) is free; check the quota in the Cloud console **[verify; one source says 150/day by default]**.
5. **Security headers grade** (securityheaders.com style A+…F) and a **TLS summary** (SSL Labs style: protocols, certificate expiry, HSTS). Easy, high perceived value.
6. **History and trend**: score per run over time, plus a "new issues since the last run" diff. Cheap if run summaries are kept (§5).

**P1**
7. **Ownership verification** (DNS TXT `spark-seo-verification=<token>`, a meta tag, or the file `/.well-known/<token>.txt`), which unlocks deep or scheduled crawls, intrusive probes and higher page limits. Google Search Console / Ahrefs model.
8. **Scheduled re-crawls and regression alerts** (email when the score drops by more than X or a Critical issue appears; certificate or domain expiring). Verified sites only.
9. **Structured-data validation** against Google's supported types (property requirements as data). The Schema.org validator has no official API, so implement the required-property tables in-house.
10. **Shareable report link and PDF export** (a public read-only token URL; the PDF via the same Playwright instance: `page.PdfAsync`).
11. **SERP / OG / X previewers** (the owner's list; client-side only and cheap).
12. **Rendered vs raw diff** (crawlability of SPAs, especially relevant for Angular users).

**P2**
13. Competitor comparison (run the same lightweight audit on up to 3 competitor home pages; unverified, so a home-page-only audit).
14. Keyword / content analysis (n-grams, on-page keyword placement, readability).
15. Accessibility deep scan (axe over all rendered pages).
16. Google Search Console integration (OAuth; real queries, impressions, indexing status). That is the *real* "backlinks and keywords" data that is free.
17. Exports (CSV per issue), API access, a white-label PDF.

**Not feasible / don't build**
- **Backlinks**: needs a web-scale crawl index (Ahrefs, Majestic, Moz). Options: GSC's Links report via the API, or a paid API (DataForSEO, Moz Links API). Mark it as out of scope or a paid add-on.
- **Keyword volumes / rankings**: paid data. Out of scope.

---

## 3. .NET implementation options

### 3.1 HTML parsing
- **AngleSharp** (MIT, actively maintained): an HTML5 spec-compliant parser (the same tree a browser builds, including error recovery), CSS selectors (`QuerySelectorAll`), and **AngleSharp.Css** to parse stylesheets for `@font-face`, `prefers-color-scheme` and `@media` rules. It is the recommendation.
- **HtmlAgilityPack**: tolerant but **not** HTML5-compliant (tree differences on malformed markup, e.g. implicit `</p>`, `<head>` element placement), XPath-oriented, and has no CSS. Slightly faster and lower-memory. Use it only if profiling demands it.
- Charset: honour BOM, then `Content-Type`, then `<meta charset>` sniffing (AngleSharp handles this when given the stream and the content type).

### 3.2 Headless rendering
- **Microsoft.Playwright** (.NET), Chromium only. One `IBrowser` per worker, a new `IBrowserContext` per site (isolation), and `page.RouteAsync` to block the fonts/media of third parties if rendering only for the DOM. Gives the network log (requests, sizes, protocol via `response.ServerAddr`/timing), console errors, a mobile viewport, offline mode, axe injection and PDF output.
- Cost: the Chromium download is about 150–180 MB; the official image `mcr.microsoft.com/playwright/dotnet:v1.x-noble` is about 1.5–2 GB with all browsers (install only chromium: `pwsh playwright.ps1 install --with-deps chromium`). RAM is about 100–300 MB per open page, CPU about 1–3 s per page render. **Budget: render a sample** (home plus up to N pages, one per URL template), not every crawled page, unless raw vs rendered shows a CSR SPA, where rendering is mandatory.
- Run the browser in a **separate worker process or container** from the web app (crash isolation, memory caps), and apply the same SSRF rules to the browser via `RouteAsync` (every request intercepted and checked) **plus** an egress proxy, because Chromium resolves DNS itself (see §4).
- Alternatives: PuppeteerSharp (CDP-only, Chromium, similar cost); a hosted render service (Browserless). Playwright is the best-supported option in .NET.

### 3.3 TLS protocol probing
- `SslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { EnabledSslProtocols = SslProtocols.Tls12 | Tls13, ... })` per version. Read `NegotiatedApplicationProtocol` (ALPN h2), `SslProtocol`, `NegotiatedCipherSuite`, and the remote certificate (chain, expiry, SAN, issuer for the CAA cross-check) in `RemoteCertificateValidationCallback` (record the errors, don't throw).
- **Caveat: probing TLS 1.0/1.1 with SslStream is usually impossible.** On Windows, SChannel disables TLS 1.0/1.1 by default in new Windows 11/Server releases (from 2024; Group Policy can re-enable it). On Linux, OpenSSL 3 at its default security level refuses TLS below 1.2 (it needs `@SECLEVEL=0` and the LEGACY crypto policy, which .NET does not expose per connection, only via OpenSSL config such as `OPENSSL_CONF`). `SslProtocols.Tls`/`Tls11` are also marked obsolete in .NET. A failure to connect therefore proves nothing about the server.
- **Recommended approach**: a **hand-rolled ClientHello probe**. Open a raw `Socket`, send a minimal TLS 1.0 (0x0301) or 1.1 (0x0302) ClientHello with common cipher suites and no `supported_versions` extension, read the first record, and see whether the reply is a ServerHello with that version or an Alert (protocol_version). About 150 lines; no crypto needed, because you stop after ServerHello. This is how testssl.sh/sslyze-style scanners detect legacy protocols portably. Same for SSLv3 (0x0300).
- Alternative: shell out to `testssl.sh` or `sslyze` in the worker container (thorough: ciphers, vulnerabilities), or call the **SSL Labs API v4** (it needs registration and enforces usage terms and rate limits; it's slow, minutes per host). A raw probe plus SslStream covers 90% of the value.

### 3.4 HTTP/2 and HTTP/3 detection
- `HttpRequestMessage { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact }` succeeds only if ALPN negotiated h2; `response.Version` reports what was used. Or read ALPN from the SslStream probe directly.
- HTTP/3: look for `Alt-Svc: h3=":443"` on the response, then `Version30` + `RequestVersionExact`. Needs **msquic** (bundled on Windows 11/Server 2022+; on Linux the `libmsquic` package must be installed in the container). If msquic is missing, report only "advertised via Alt-Svc".

### 3.5 DNS (CAA, TXT, DS, MX)
- **DnsClient.NET** (`DnsClient` package, Apache-2.0): `LookupClient.QueryAsync(name, QueryType.CAA/TXT/DS/NS/AAAA)`; supports CAA records (`CaaRecord`). Use a fixed public resolver (or the container's) and set timeouts and retries. Also used for the TXT ownership-verification lookups (`_spark-verify.example.com TXT`).

### 3.6 Domain expiry: RDAP
- RDAP (RFC 9082/9083) is JSON over HTTPS. **ICANN sunset WHOIS for gTLDs on 2025-01-28**, so RDAP is mandatory for gTLD registries and registrars.
- Bootstrap: `https://data.iana.org/rdap/dns.json` (cache it for a day; it maps TLD to base URL). Query `{base}domain/{name}`, then read `events[].eventAction == "expiration"` → `eventDate`. For thin registries, follow `links[rel=related]` to the registrar's RDAP for more data. No mature .NET RDAP library exists; a small `HttpClient` + System.Text.Json client is enough (models: events, entities, status, nameservers).
- **ccTLD gap**: many ccTLDs are not in the bootstrap. Relevant to the owner: DNS Belgium runs RDAP for .brussels/.vlaanderen, but I found **no RDAP endpoint for .be** **[verify on iana.org/domains/root/db/be.html]**, and .be WHOIS (`whois.dns.be`) does **not publish an expiry date** (to my knowledge; **[verify]**). Show "Expiry: not published by the .be registry" instead of an error. Port-43 WHOIS is a fallback for other ccTLDs (free-text parsing, brittle); treat it as best-effort.
- RDAP servers rate-limit (HTTP 429): cache per domain for 24 h or more.

### 3.7 robots.txt and sitemaps
- **TurnerSoftware.RobotsExclusionTools** (MIT): RFC 9309 compliant, handles robots.txt plus meta robots / X-Robots-Tag, and has an `IsAllowedAccess(uri, userAgent)` API. Alternatively, writing the parser is about 200 lines, with the RFC's longest-match rules (Allow wins ties) and `*`/`$` wildcards.
- **TurnerSoftware.SitemapTools** (MIT): discovers sitemaps (robots.txt plus the conventional location) and parses XML / index / txt / gzip. Or `XmlReader` streaming (important for 50 MB sitemaps: never load the whole file into an `XDocument`).
- Respect Google semantics: robots.txt 4xx means allow all; 5xx means disallow all (temporarily); a redirect is followed up to 5 hops.

### 3.8 Lighthouse
- **The PSI API** is preferred: no Chrome or Node in our containers, Google pays the CPU, and lab metrics come from consistent hardware. Downsides: only public URLs (fine for us), quota, 10–30 s latency per call, and it only accepts the page URL (no auth).
- **The Lighthouse CLI** (Node, `lighthouse <url> --output=json --chrome-flags=--headless`): full control and no quota, but adds Node to the worker image and lab results are noisy on shared CPUs (score variance of ±10). Lighthouse 13 (Oct 2025) replaced the old perf audits with "insights" (the same as DevTools), so parse insight IDs, not legacy audit IDs; PSI uses Lighthouse 13.
- Recommendation: the PSI API for performance (sampled pages), our own checks for SEO/security/crawl, and axe via Playwright for accessibility. Do not reimplement Lighthouse's performance model.

### 3.9 Other useful packages
- `NUglify` (minification check), `Nager.PublicSuffix` (registrable domain / eTLD+1 for same-site scope and RDAP), `SimHash`-style hashing (write it; trivial), `System.Threading.RateLimiting` (per-host token buckets), `Polly` / `Microsoft.Extensions.Http.Resilience` (retries).

---

## 4. Crawler safety and ethics

### 4.1 SSRF (the critical one: the tool fetches attacker-chosen URLs from our server)
- **Allowed schemes**: http/https only; ports 80/443 (optionally 8080/8443); reject userinfo (`user:pass@`).
- **Validate the IP at connect time, not at URL-parse time.** Use `SocketsHttpHandler.ConnectCallback`: resolve with `Dns.GetHostAddressesAsync(host)`, **filter every address**, then connect a `Socket` to the vetted `IPAddress` itself. Never pass the host name back to the OS. This closes **DNS rebinding** (TOCTOU between check and connect) because the address checked is the one connected.
- Block: IPv4 `0.0.0.0/8`, `10/8`, `100.64/10` (CGNAT), `127/8`, `169.254/16` (link-local, **including cloud metadata 169.254.169.254**), `172.16/12`, `192.0.0/24`, `192.0.2/24`, `192.168/16`, `198.18/15`, `198.51.100/24`, `203.0.113/24`, `224/4` (multicast), `240/4`, `255.255.255.255`; IPv6 `::1`, `::`, `fc00::/7` (ULA), `fe80::/10`, `ff00::/8`, **IPv4-mapped `::ffff:0:0/96` (unwrap with `MapToIPv4()` and re-check)**, `64:ff9b::/96` (NAT64, unwrap), `2002::/16` (6to4, embedded v4); Azure `168.63.129.16`; the Alibaba metadata service `100.100.100.200`. Prefer an **allow-list of "global unicast"** over enumerating blocks.
- Disable `AllowAutoRedirect`; **follow redirects manually** (maximum 5–10), re-validating scheme, port and IP each hop (the ConnectCallback does the IP part automatically for every new connection).
- Disable proxies (`UseProxy = false`) or the WPAD lookup will interfere (cf. the repo's own WPAD finding), and never send cookies or credentials from our side (`UseCookies = false` across sites).
- **Headless browser**: Chromium does its own DNS, so ConnectCallback doesn't apply. Mitigations, combined: (a) `page.RouteAsync("**/*")` checks each request URL's host via our resolver; (b) **force Chromium through a local egress proxy** that applies the same IP filter at connect time (`--proxy-server`, with `--host-resolver-rules` so that DNS also goes via the proxy); (c) run it in a network namespace/container whose egress firewall denies RFC1918 and metadata ranges. (c) is the robust backstop; (a) alone is bypassable via rebinding.
- Also enforce limits on response size (stream with a byte cap, e.g. 5 MB HTML, 50 MB sitemap), total time per request (connect 10 s, total 30 s), decompression bombs (cap the *decompressed* size), and redirect count.
- Run the crawler workers with **no network access to internal services** (separate container or network; no access to RavenDB from the browser container).

### 4.2 Politeness
- **robots.txt**: honour it for our UA token (and the `*` group). For **verified owners**, offer "ignore robots.txt" (as Screaming Frog does), still logged.
- **User-Agent**: `Mozilla/5.0 (compatible; SparkSiteAudit/1.0; +https://<host>/bot)`, with a public bot info page (what we do, how to block us, contact). Optionally publish our egress IP ranges.
- **Concurrency per host**: unverified, 1 connection, at least 1 s between requests (or the `Crawl-delay` if larger); verified, up to 4–5 configurable. **Global per-target-IP limiter** too (many domains on one IP, i.e. shared hosting).
- Back off on 429/503 (honour `Retry-After`), and abort the crawl if the error rate exceeds 50% or the server slows down sharply.
- Limits: unverified about 100 pages, depth 5, 10 minutes, 50 MB total transfer; verified about 2,000–10,000 pages per plan. Max one external-link check per external host per second, and external links are deduplicated globally.

### 4.3 Abuse prevention (the tool as a DDoS / scanning amplifier)
- Per-user quotas (audits/day, concurrent audits = 1–2), per-target cool-down (**the same target at most once per N hours across ALL users unless verified**, which makes a botnet of free accounts useless against one site), require sign-in (already in scope) and email confirmation, and CAPTCHA/Turnstile on submit for new accounts.
- Deduplicate concurrent audits of the same target (join the running job).
- **Intrusive probes** (exposed `.git/.env`, directory listing beyond seen paths, aggressive TLS cipher enumeration, high-concurrency crawling, scheduled crawls) **only for verified owners**. Probing someone else's server for vulnerabilities can be unlawful under computer-misuse laws (e.g. Belgian Criminal Code art. 550bis), and site operators' WAFs will flag us.
- Abuse contact plus an opt-out list (domains that asked never to be crawled).
- Logging: who requested which target and when (needed to answer abuse complaints), retained for a limited time (GDPR).

### 4.4 Why ownership verification matters (in summary)
It is the lever that separates "public, polite, shallow" (anyone can see what Google sees) from "deep,
frequent, intrusive" (only the owner may ask). It also makes the results private by default and enables
scheduled monitoring and alerts without spamming third parties. Methods (Search Console precedent): a DNS
TXT record (covers all subdomains; recommended), `<meta name="spark-site-verification" content=token>` on
the home page, or a file at `/.well-known/spark-verification-<token>.txt`. Re-verify periodically (e.g.
before each scheduled crawl) and revoke on failure.

---

## 5. Scoring model and persistence

### 5.1 Score
- Each check yields **pass / warn / fail / not-applicable / error(could not measure)**. N/A and error are **excluded from the denominator** (never penalise what we could not measure, e.g. a missing CrUX or unavailable RDAP).
- Per-page checks aggregate into a site-level check: `pass ratio = passing pages / applicable pages`, so "23 of 200 pages lack alt text" scores 88.5% on that check, not 0. Critical site-level failures (no HTTPS, robots blocks everything, CSR-only empty content) **cap** the category score (e.g. at most 40), as Lighthouse caps scores on hard failures.
- Category score = Σ(weight × check score) / Σ(applicable weights) × 100.
- Overall = weighted categories. Suggested: SEO 25, Technical/crawlability 20, Performance 20, Security 15, Accessibility 10, Social 5, Mobile/PWA 5 (the PWA sub-checks are opt-in), Domain/DNS a 0-weight info panel (except certificate or domain expiry under 30 days, which raises an alert).
- Grade: A 90–100, B 80–89, C 70–79, D 50–69, F below 50 (Lighthouse colour bands: 90+ green, 50–89 orange, under 50 red). Security headers can additionally show a securityheaders.com-style letter.
- **Version the scoring model** (`ScoringVersion` on every run) so trend charts stay comparable; recompute old runs only from persisted check results, never by re-crawling.
- Weights and check metadata (severity, suggestion template, docs link, obsolete flag) are **data** (a JSON or RavenDB document), not code, so Google's yearly changes (the 2024/2025 deprecations) are a data edit.
- Present a **prioritised fix list**: sort by `weight × affected pages × (1 - score)` and an effort estimate (an effort tag per check: config / template / content).

### 5.2 What to persist (RavenDB)
| Document | Content | Retention |
|---|---|---|
| `Sites/{id}` | owner(s), verified flag and method and token, settings (limits, schedule, robots override) | permanent |
| `AuditRuns/{id}` | site, started/finished, status, scoring version, overall and category scores, **per-check aggregates** (check id, status, affected count, sample of 5 URLs), crawl stats, CWV snapshot, TLS/DNS/RDAP summary | permanent (small, about 5–20 KB) |
| `AuditRuns/{id}/pages/{n}` or `PageResults/...` | per-page: URL, status, title, description, h1, canonical, word count, failing check ids with details, outlinks (for broken-link/orphan reports) | **@expires 30 days** (RavenDB document expiration: `@metadata.@expires`; enable the expiration feature with a sensible `DeleteFrequencyInSec`) |
| Raw HTML / screenshots / PSI JSON | **don't store** raw HTML (recomputable by re-crawl; large). PSI JSON: keep only the extracted metrics and insights. Screenshot: maybe 1 home-page thumbnail per run as an attachment, expiring with the run details, or store it outside RavenDB | expire 30 days |
| Report share tokens | token, run id, expiry | expiring |
| Caches: RDAP, hstspreload status, IANA bootstrap, robots.txt | small documents with `@expires` of 1–7 days | expiring |
| Abuse log (who audited what) | user, target, timestamp, IP | expire per policy (e.g. 90 days) |

- The **"new since last run" diff** works on the permanent per-check aggregates (check id plus affected count). A URL-level diff only works while page details still exist (30 days), which is fine for weekly or daily schedules.
- Recomputable (don't persist): grades (from scores), suggestion texts (templates plus parameters; store the parameters only), SERP/OG previews (from title, description, OG fields, which live in the page results).
- Index needs: runs by site, ordered by date (trend), sites by owner, pages by run and check id (the issue drill-down; a map index over page results that expires with them).

---

## Sources
- Lighthouse 13 release: https://developer.chrome.com/blog/lighthouse-13-0 · Moving to insights: https://developer.chrome.com/blog/moving-lighthouse-to-insights
- PSI API quotas (third-party summary): https://unlighthouse.dev/learn-lighthouse/pagespeed-insights-api/rate-limits · CrUX quota (an aggregator; conflicting): https://apis.io/rate-limits/google-chrome/google-chrome-rate-limits/
- Google, Simplifying search results (June 2025): https://developers.google.com/search/blog/2025/06/simplifying-search-results · https://searchengineland.com/google-to-remove-more-search-features-including-practice-problems-nutrition-facts-nearby-offers-and-more-464255
- Sitelinks search box removal: https://searchengineland.com/google-search-to-drop-sitelinks-search-box-447682
- Windows TLS 1.0/1.1 deprecation: https://learn.microsoft.com/windows/desktop/SecAuthN/tls-10-11-deprecation-in-windows · OpenSSL 3 SECLEVEL: https://mta.openssl.org/pipermail/openssl-users/2024-April/017086.html
- hstspreload API usage (via testssl.sh): https://git.9x0rg.com/Git/testssl.sh/commit/c54c2aa6dbdba706afd495fa3830ff8a275f52f7
- DNS Belgium RDAP (.brussels/.vlaanderen): https://www.dnsbelgium.be/en/news/rdap-brussels-vlaanderen
- Lighthouse PWA category deprecation (third-party): https://tessl.io/registry/testland/lighthouse-pwa-audit
