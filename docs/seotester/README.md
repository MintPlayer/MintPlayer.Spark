# SeoTester

SeoTester (#268) is the production app at **https://seotester.mintplayer.com**: users sign in with Microsoft, GitHub,
X or Google, register websites in an organization, and get an SEO / performance / security / accessibility report.
The spec, decisions and milestones are in [seo_analyzer_PRD.md](../seo_analyzer_PRD.md); this folder is the
operator's documentation. Treat changes to `apps/SeoTester` as production changes.

Status: **M12, code-complete; the deploy is the owner's** ([deploy.md](deploy.md)) (M8's sites, verification, catalogue, scoring, audit start, share links, abuse log and passkeys; M9's
crawl pipeline: robots.txt, the site-level probe, page batches, the aggregate that scores the run, cancel; M10's
headless-browser render of a sample, PageSpeed Insights and the PDF report; M11's pages: landing page, site, live run
report with how to fix in English and Dutch, page previews, trend, share page and badge, operator dashboard). All 110
catalogue checks are measured. M12: scheduled monitoring with alerts, the compose file, the deploy workflow and the
runbook.

## Layout

| Project | What |
|---|---|
| `apps/SeoTester/SeoTester` | the host (`Program.cs`) and the Angular SPA (`ClientApp`, nx project `@spark-apps/seo-tester`); `Entities/` (Site, AuditRun, PageResult, CheckDefinition, AuditLogEntry, TargetCooldown: here, not in the Library, because their marker interfaces are web types, PRD Q-M8a), `Actions/`, `CustomActions/` (StartAudit, StartFullCrawl, VerifySite, ResetCheckDefinition), `Interceptors/`, `Verification/` (proofs, re-check cron), `Audits/` (start, abuse log, organization cascade), `Catalogue/` (seeder, scoring snapshot), `Crawler/` (the pipeline's recipients, prober, politeness, registry lookups), `Rendering/` (the Chromium connection, the request router, the page renderer, the bundled axe-core 4.13.0, `seo-render`), `PageSpeed/` (the PageSpeed Insights client, `seo-psi`), `Reports/` (the printable report, `seo-pdf`, the PDF download), `Settings/` (every gate and limit), `Bot/` (the `/bot` page), `Dockerfile` |
| `apps/SeoTester/SeoTester.Library` | plain SDK, nothing web: the shipped check catalogue (`Catalogue/checks.json`, 110 checks) and its parser, the scoring engine (`Scoring/`), origin normalization and ownership-proof matching (`Sites/`), the crawler's identity (`SeoTesterBot`), the pipeline's queues and messages (`Messaging/SeoQueues.cs`); robots.txt (RFC 9309), sitemaps, HTML parsing (AngleSharp, no scripts), the TLS 1.0/1.1 hello probe, and the check implementations (`Checks/`: page, site and crawl level, and `RenderChecks` for the rendered page; `PageSpeed/` for PageSpeed Insights), judged from facts the host collects |
| `apps/SeoTester/chromium` | the Chromium container's `Dockerfile` and `server.mjs` ("The Chromium container" below) |
| `apps/SeoTester/SeoTester.Fetching` | `ISeoFetcher` (PRD D54): the SSRF-safe clients (`PageFetcher`), the TLS probe, DNS (`ISeoDns`), in process (`LocalSeoFetcher`) or through the fetcher (`RemoteSeoFetcher`), and the fetcher's wire protocol. No database client |
| `apps/SeoTester/SeoTester.Fetcher` | the `seo-fetcher` container: `LocalSeoFetcher` behind an internal API with a shared secret, and its `Dockerfile` ("The fetcher container" below) |
| `apps/SeoTester/docker-compose.yml`, `.env.example` | production: the app and the Chromium container, beside CodeCoverage on the VPS ([deploy.md](deploy.md)) |
| `apps/SeoTester/SeoTester.Tests` | boots the real `Program` against an embedded RavenDB (users and organizations from `Data/organizations.json`; DNS and the web faked): composition, settings, `/bot`, the route table, sites, verification, audit start, catalogue, sharing, passkeys, the crawl pipeline end to end and a host restart in the middle of a batch; the render, PageSpeed and PDF stages end to end and the browser escape test (`Rendering/`: own collections, a local Chromium, see "Running it locally"); and the pure scoring, catalogue, robots.txt, sitemap, check, PageSpeed-parser and TLS-probe tests. Nothing leaves the machine: DNS, TLS, the crawled sites, the registries and PageSpeed Insights are fakes (recorded RDAP/IANA/hstspreload/PageSpeed answers in `Data/Fixtures`) |

## What a user does (M9)

1. **Add a site** (menu SEO → Sites → New; organization Admins). The address is reduced to its origin
   (`https://example.com`); without an organization the site goes to the organization chosen in the topbar switcher (D50; lists follow it too), else the user's personal one. A site is unique per
   organization; several organizations may each add the same site.
2. **Verify ownership** (action *Verify ownership*, Admins). Publish the site's verification token one of three ways,
   then run the action:
   - DNS: a TXT record at `_seotester.<host>` with the value `seotester-site-verification=<token>`;
   - a file `https://<host>/.well-known/seotester-verification.txt` containing that line;
   - `<meta name="seotester-site-verification" content="<token>">` in the home page's `<head>`.

   If another organization already holds the site verified, its Owners get a mail asking to let the user in, and this
   organization's site stays unverified. Verified sites are re-checked daily; three failed re-checks in a row withdraw
   the verification (`SeoTester:Verification:*`).
3. **Run an audit** (action *Run audit*, Members). It joins an audit of the same site already running in one of the
   user's organizations; otherwise it counts against the user's quota (`audits` 5/day unverified, `verified-audits`
   24/day) and, for an unverified site, starts a 6 h cool-down for everyone. *Full-site crawl* (verified sites) uses the
   weekly `full-crawls` allowance and the 20 000-page ceiling. Refusals answer 429 with `Retry-After`.
   The run then goes through the pipeline (below) and ends `Completed` with a score and grade, `Blocked` (robots.txt),
   `Cancelled` or `Failed`; the participants get a notification. *Cancel audit* (Members) stops it at the crawler's
   next batch.
4. **Share** (Owners and Admins): a read-only link to a site or a run, governed by `SeoTester:Features:Sharing`
   (`VerifiedOnly` hides unverified sites' links until they verify).
5. **Passkeys** (account page): after the first external sign-in a user can add a passkey and sign in with it.
6. **Connect an AI agent** (MCP, [mcp.md](mcp.md)): the agent reads the findings of the user's sites at `/mcp` and can
   start a scan, as the user. Account → Connected applications lists the connected agents and makes personal access
   tokens. Not deployed yet (spark MCP plan M9).

## The pages (M11)

Every page is ng-spark's generic detail page; SeoTester adds attribute renderers (`ClientApp/src/app/seo/renderers.ts`)
and a few panels (`seo/po-detail-page.component.ts`). The layout (tabs, groups, which renderer draws what) is the
model JSON in `App_Data/Model`.

| Page | URL | What it shows |
|---|---|---|
| Landing | `/` → `/home` | what is checked and how; signed in: my sites and the (live) audits |
| Site | `/po/site/{id}` | latest score (gauge), settings, ownership: verified state or the three proofs with copy buttons; the audits; the score trend (scoring changes marked) |
| Audit | `/po/audit-run/{id}` | *Report*: live progress while it runs, then the score, category bars and every check (issues first; New/Fixed/Unchanged; why not measured; sample pages; how to fix with nginx/Apache/IIS/HTML snippets); the pages (7 days, then a note). *Details*: what the run was allowed |
| Page | `/po/page-result/{id}` | Google desktop/phone previews, the shared-link card from the page's Open Graph tags (its image through `/seotester/og-image`, never loaded from the site by the browser, D51), the page's findings |
| Shared report | `/shared/{Type}/{token}?id=…` | anonymous, read-only (a site or an audit) |
| Operator dashboard | `/po/operator-dashboard/main` | Administrators: usage, refusals, database size per collection, every site, the abuse log; live, refreshed every 15 s |
| Database statistics | `/po/database-statistics/main` | Administrators: the dashboards package's own page (size, documents, collections, 7-day trends) |

*Run audit* opens the run's page and follows it live. A refusal (cool-down, quota, gate) says why and when to try again.
*Download PDF* downloads a printed report at once, otherwise prints it and notifies. *Badge* (Owners and Admins) makes a
share link and shows the Markdown of a badge (`/seotester/badge.svg?site=…&token=…`, the latest score and grade) that
links to it; *Stop sharing* ends both. The check texts come from `GET /seotester/catalogue?culture=en|nl` (public: the
share page needs them). The operator dashboard is a [Spark dashboard](../guide-dashboards.md) (#504): a live virtual
page whose figures are its own attributes (`Admin/OperatorDashboard.cs`: `OperatorDashboardFigures` plus the
package's RavenDB statistics), drawn as tiles by `@mintplayer/ng-spark/dashboards` and pushed over `/spark/live`.
`Read/OperatorDashboard` is granted to the `dashboards:administrators` slot, which `security.json` binds to
Administrators. There is no separate dashboard endpoint.

**Administrators** also get the operator dashboard (SEO administration → Dashboard), the check catalogue editor (SEO → Check catalogue: edit texts, weights, severities, enable
or disable a check, add one, *Reset to shipped*) and the abuse log (SEO administration → Audit log). The catalogue is
seeded at startup: new shipped checks are added and unedited entries follow the shipped text; an edit is never
overwritten.

## Running it locally

1. RavenDB at `http://localhost:8080` (the database `SeoTester` is created on first start).
2. SparkId, the demo identity provider: `dotnet run --project apps/SparkId/SparkId --launch-profile https`
   (https://localhost:5011). Its Development migration registers the `seotester` client, so you can sign in without
   registering any OAuth app.
3. `dotnet run --project apps/SeoTester/SeoTester --launch-profile https` → **https://localhost:5013** (http 5014).
   The host starts the Angular dev server itself (`UseAngularCliServer`); do not run `ng serve` beside it. The app is
   ready when the host prints the dev server's `➜ Local:` line.

Development renders with a Chromium launched on the machine (`SeoTester:Browser:Mode = Launch`): install it once with
`pwsh apps/SeoTester/SeoTester/bin/Debug/net11.0/playwright.ps1 install chromium` after a build (the browser tests in
`SeoTester.Tests/Rendering/` need the same). PageSpeed Insights runs when `SeoTester:PageSpeed:ApiKey` is in your user
secrets.

Development writes every mail as an `.eml` file into `apps/SeoTester/SeoTester/mail-pickup/` (link confirmations,
invitations, notifications). To sign in with a real provider locally, set its keys in user secrets (below) and
register `https://localhost:5013/signin-…` as an extra callback on that provider's development app.

**Becoming an administrator** (the Settings screen): add `"Administrators"` to the `Roles` list of your `SparkUsers/…`
document (RavenDB Studio), then sign out and in again. `security.json` binds the settings package's slot
`settings:administrators` and the dashboards package's slot `dashboards:administrators` to that group; every other
user gets a 404 on the settings and the dashboards.

## Configuration and credentials the owner registers

Never commit a value: `appsettings.json` holds empty placeholders only. Locally use user secrets
(`dotnet user-secrets set "<key>" "<value>" --project apps/SeoTester/SeoTester`), in production the container's
environment (`:` becomes `__`, e.g. `Spark__Auth__Providers__GitHub__ClientSecret`). A provider whose `ClientId` is
empty is simply not offered; with **no** provider at all the app refuses to start (nobody could sign in).

### Sign-in providers (PRD §4.1, D2, D13)

| Provider | Register at | Callback (production) | Scopes / notes | Keys |
|---|---|---|---|---|
| Microsoft (Entra, personal **and** work/school) | Microsoft Entra admin center → App registrations; "Accounts in any organizational directory and personal Microsoft accounts" | `https://seotester.mintplayer.com/signin-microsoft` | `openid`, `profile`, `email` (the preset adds them); the `common` endpoint. Work accounts give no verified email | `Spark:Auth:Providers:MicrosoftAccount:ClientId`, `…:ClientSecret` |
| GitHub | GitHub → Settings → Developer settings → OAuth Apps | `https://seotester.mintplayer.com/signin-github` | `user:email` | `Spark:Auth:Providers:GitHub:ClientId`, `…:ClientSecret` |
| X | developer.x.com → Projects & Apps → User authentication settings: **OAuth 2.0**, type "Web App", enable **Request email from users** (needs a privacy policy and terms URL) | `https://seotester.mintplayer.com/signin-twitter` | OAuth 2.0 client id/secret, not the 1.0a API keys; `users.read`, `tweet.read`, `users.email` | `Spark:Auth:Providers:Twitter:ClientId`, `…:ClientSecret` |
| Google | Google Cloud console → APIs & Services → Credentials → OAuth client ID ("Web application") | `https://seotester.mintplayer.com/signin-google` | `openid profile email` | `Spark:Auth:Providers:Google:ClientId`, `…:ClientSecret` |

### Everything else

| What | Keys | Notes |
|---|---|---|
| Public origin | `Spark:Auth:PublicBaseUrl` | `https://seotester.mintplayer.com`, already in `appsettings.Production.json`; mailed and share links are built from it. ⚠️ Its host is also the **passkey relying-party id** (D46): a passkey binds to it for life, so never change it once users enrolled passkeys. Development uses `localhost` |
| Mail (SMTP relay) | `Spark:Mail:Smtp:Host` (+ `Port`, `UserName`, `Password` if the relay needs them), `Spark:Mail:From:Address` | The sender defaults to `noreply@seotester.mintplayer.com`. **Required in production:** without a transport the app refuses to start (account-link confirmations need mail). Same relay as CodeCoverage (`docs/code-coverage/hosting.md`) |
| Web Push (VAPID) | `Spark:Notifications:WebPush:PublicKey`, `…:PrivateKey`, `…:Subject` | Generate once with `MintPlayer.Spark.WebPush.VapidDetails.Generate("mailto:…")`; the private key is a secret. Empty = push off, in-app and mail still work. Changing the pair later invalidates every browser subscription |
| PageSpeed Insights / CrUX | `SeoTester:PageSpeed:ApiKey` | A Google Cloud API key with the PageSpeed Insights API enabled (restrict it to that API). Empty = both PageSpeed checks "not measured: PageSpeed Insights is not configured" (never a failure; no keyless call: its quota is 0). Sent as the `X-Goog-Api-Key` header, never in a URL, so no log line holds it |
| Headless browser | `SeoTester:Browser:Mode`, `…:Endpoint` | `Off` (the rendered checks "not measured", no PDF), `Launch` (Development: a local Chromium), `Connect` (production: `ws://seo-chromium:3000/`, already in `appsettings.Production.json`). Not a secret |
| The fetcher (PRD D54) | `SeoTester:Fetcher:Mode`, `…:Url`, `…:Key`; the fetcher's `SeoFetcher:Key` | `InProcess` (the default outside Production: this process fetches) or `Remote` (the default in Production, which refuses `InProcess`: every request to a target goes to `http://seo-fetcher:8080/`, already in `appsettings.Production.json`). The key is a shared secret of ≥ 32 characters (`SEO_FETCHER_KEY` in the server's `.env`, read by both containers) |
| Data Protection keys | `Spark:DataProtection:KeysPath` | A mounted volume (`/var/lib/seotester/dataprotection-keys` in the image), set by the compose file |
| RavenDB | `Spark:RavenDb:Urls:0`, `Spark:RavenDb:Database` | Database `SeoTester` on the VPS's RavenDB (Community licence: Changes API yes, ≤ 3 subscriptions, expiration every 36 h) |

### Administrator settings (not configuration)

Every feature gate and limit of PRD §6 and the category weights of §7 are **settings** (`Settings/SeoSettings.cs`,
`Settings/SeoQuotas.cs`): an administrator changes them on the Settings screen without a deploy. Each reads the
stored value, else the configuration key of the same name (`SeoTester:Limits:Unverified:PagesPerRun`, …), else the
PRD's default. They are never secrets.

## The crawl pipeline (M9, M10)

Five queues (`Spark:Messaging:Queues`, concurrency and handler timeout per queue) carry a run, and a sixth prints it:

1. **`seo-probe`** (`Crawler/SiteProbeRecipient.cs`): reads `/robots.txt` first. A site whose robots.txt names
   `MintPlayerSeoTester` with `Disallow: /`, disallows every crawler, or answers 5xx (RFC 9309: "crawl nothing") ends as
   **Blocked** before any page is requested, and the abuse log says so. Otherwise it measures the site once: sitemaps
   (robots.txt `Sitemap:` lines, else `/sitemap.xml`; index files followed three deep), `http://` → `https://` and the
   www/apex twin, TLS (certificate, version, ALPN, and a hand-built TLS 1.0/1.1 hello, because the server's OpenSSL 3
   cannot speak them), `security.txt`, `change-password`, `favicon.ico`, a page that cannot exist (soft 404), the home
   page's manifest, icons, stylesheets and scripts, DNS (CAA, DNSSEC, SPF, DMARC, MTA-STS, AAAA, NS), RDAP (through the
   IANA bootstrap; ".be/.eu/.nl publish no expiry" is shown, not an error) and the HSTS preload list. Registry answers
   are cached in `LookupCaches/…` (bootstrap 7 days, RDAP and hstspreload 1 day). It crawls the home page and queues
   its links and the sitemap's URLs.
2. **`seo-crawl`** (`PageBatchRecipient.cs`): batches of `SeoTester:Crawl:BatchSize` pages (default 25, and never more
   than `SeoTester:Crawl:BatchBudget`, 90 s, of fetching), only paths robots.txt allows, the tier's connections per host
   and request interval (unverified: 1 connection, 1 s; a larger `Crawl-delay` wins, up to 30 s), `Retry-After` honoured
   (a URL is asked three times at most). A batch's pages, the frontier and the next message commit together, so a host
   that stops in the middle of a batch leaves nothing half-done: the next host fetches the batch again. The crawl stops
   at the page limit, the wall time, an empty frontier, a cancel, or a server that fails most requests. For verified
   runs it then checks up to 50 links to other sites (one `HEAD` per host). Then it hands over to the next two stages,
   which run side by side.
3. **`seo-render`** (`Rendering/RenderSampleRecipient.cs`): the run's sample (3 pages unverified, 10 verified: the home
   page, then the shallowest HTML pages that answered 200, one per URL template) in Chromium, one render at a time for
   the whole application (`SeoTester:Limits:ConcurrentRenders`). Each page is rendered twice in a fresh browser context:
   as a 375 px phone (layout and text size, lazy images, the network log, Chromium's script coverage, axe-core 4.13.0
   with the WCAG 2.0–2.2 AA and best-practice rules) and at desktop width (the rendered text and links against the HTML
   as served, phone/desktop parity; on the home page one click on a same-site link to see whether an SPA route change
   records an analytics hit). **Every request the browser makes is fetched by SeoTester** (`Rendering/BrowserRouter.cs`)
   through the SSRF-safe client: redirects are handed back to Chromium one hop at a time, robots.txt applies to the
   audited host, its pages wait for the tier's interval and every request holds one of its connections, static files are
   fetched once per run, and analytics hits (GA4, Meta, Clarity, Matomo, …) are answered locally and never sent on, so an
   audit never shows up in the site's statistics. Service workers are never installed: their registration is recorded
   and the script fetched (does it handle `fetch`, how long is it cached). Without a browser the rendered checks are
   "not measured"; when the browser cannot be reached they are errors; neither counts in the score.
4. **`seo-psi`** (`PageSpeed/PsiRecipient.cs`): PageSpeed Insights (mobile, performance) for the sample (the home page;
   5 pages verified): Lighthouse's score and its insights that did not pass, and the Chrome UX Report's field data
   (Core Web Vitals at the 75th percentile, the page's own or else the origin's). Measured pages are cached a day
   (`LookupCaches/psi/…`). A 429 is Google's quota: asked again `QuotaRetries` times, then "not measured".
5. **`seo-aggregate`** (`AggregateRecipient.cs`): runs once crawl, render and PageSpeed are all done (each enqueues it;
   only the last finds them all). It adds up every page's findings, the probe's and the crawl-level ones
   (duplicate titles and descriptions, broken links, redirect chains, orphans, depth, near-duplicate text, hreflang
   return links), scores the run with the weights it started with, marks each check New / Fixed / Unchanged against the
   site's previous completed run, keeps why a check was not measured (`Note`), updates the site's latest score, deletes
   the frontier and notifies the participants.

**PDF** (`seo-pdf`, `Reports/`): *Download PDF* on a completed audit (any member of its organization, Viewers included)
prints the report in the caller's language (English or Dutch): a self-contained page (no script, no request at all)
printed by Chromium to A4. It is kept 7 days as an attachment of `{runId}/reports/{en|nl}` and downloaded from
`GET /seotester/report.pdf?run=…&culture=…` (members only; anyone else gets 404). The caller gets a notification with
the link when it is ready.

Probes for exposed files (`/.git/HEAD`, `/.env`, …) and folder listings run only when the run was granted them
(`SeoTester:Features:IntrusiveProbes`, verified sites by default). Every request to a site goes through the SSRF-safe
client (`Spark:SafeHttp`: public addresses only, ports 80/443, each redirect re-checked, 10 MiB / 50 MiB caps, 30 s), made
in Production by the fetcher container (PRD D54, below).
Progress (`Phase`, `PagesDone`, `PagesDiscovered`, `CurrentUrl`) is written at most every 2 s and reaches open pages
live. Page details (`PageResult`) expire after 7 days, the frontier after 2.

| Configuration (`SeoTester:Crawl:*`) | Default | |
|---|---|---|
| `BatchSize` | 25 | pages per `seo-crawl` message |
| `BatchBudget` | `00:01:30` | a batch takes no new page after this |
| `MaxExternalLinks` | 50 | links to other sites checked per verified run |
| `ProgressInterval` | `00:00:02` | least time between progress writes |
| `SeoTester:Lookups:RdapBootstrap`, `…:HstsPreload` | IANA's `dns.json`, hstspreload.org's v2 status | |

The verified tier's request interval is the setting `SeoTester:Limits:Verified:RequestInterval` (default 0: four
connections, no pause).

| Configuration | Default | |
|---|---|---|
| `SeoTester:Browser:Mode` | `Off` (`Launch` in Development, `Connect` in Production) | where the browser comes from |
| `SeoTester:Browser:Endpoint` | `ws://seo-chromium:3000/` in Production | the Chromium container |
| `SeoTester:Browser:NavigationTimeout` | `00:00:20` | one page load |
| `SeoTester:Browser:SettleTimeout` | `00:00:05` | after `load`, at most this long for the network to go quiet |
| `SeoTester:Browser:PageBudget` | `00:00:50` | everything one sampled page may take (10 pages fit the 10-minute handler timeout) |
| `SeoTester:PageSpeed:Strategy` | `MOBILE` | |
| `SeoTester:PageSpeed:CacheFor` | `1.00:00:00` | a measured page is reused this long |
| `SeoTester:PageSpeed:QuotaRetries`, `…:QuotaRetryDelay` | 3, `00:02:00` (× attempt) | after a 429 |

## Scheduled monitoring and alerts (M12)

A site's **Monitoring** (Off / Weekly / Monthly, on the site form) schedules audits. Switching it on needs the
`SeoTester:Features:Monitoring` gate to admit the site (verified sites by default, D11); the member who sets it becomes
*Scheduled by*: the runs are started for them, through the same start as *Run audit* (joins a running audit; their
`verified-audits` quota; refusals logged and told to them). The first run is one period after the latest one (now if
there is none); *Next scheduled audit* shows when. Every 15 minutes `MonitoringSweepCronJob` dispatches the due slots,
each once (`seo-monitor` queue, deduplicated per site and slot); slots missed while the app was down are skipped, not
run as a backlog. A scheduler who left the organization stops the runs, and its Admins are told.

When a run of a monitored site completes, **every member** of its organization is alerted (in-app, mail and Web Push,
as each chose for that organization) about:

| Alert | When | Category |
|---|---|---|
| Score drop | the score fell by at least the site's *Alert when the score drops by* (empty: `SeoTester:Alerts:ScoreDrop`, 5; 0: off) since the previous completed run, both scored with the same weights (`ScoringVersion`) | `seo-alert-score-drop` |
| New critical issue | a Critical check fails that did not fail or warn on the previous run | `seo-alert-critical` |
| Certificate | `security.certificate-expiry` fails: under 14 days | `seo-alert-certificate` |
| Domain | `domain.expiry` fails: under 30 days | `seo-alert-domain` |

The expiry alerts repeat on every run while they fail; a critical issue alerts once. Code: `Monitoring/` (host) and
`SeoTester.Library/Monitoring/` (the rules, pure).

## The Chromium container

Production renders in a container of its own, reached only by SeoTester (D3, D6): `seo-chromium` in
`apps/SeoTester/docker-compose.yml` ([deploy.md](deploy.md) §1). Its image was tested without Docker in M10 (spike S3:
the same server script run with Microsoft.Playwright's own node).

- **Image:** `apps/SeoTester/chromium/Dockerfile`: `mcr.microsoft.com/playwright:v1.63.0-noble` plus
  `playwright-core@1.63.0`, running `node server.mjs` as `pwuser` on port 3000. The version must equal the app's
  `Microsoft.Playwright` (1.63.0): `ConnectAsync` needs the same major.minor on both sides; bump both together.
- **Server:** `server.mjs` launches one headless Chromium with the flags fixed on the server side (`launchServer`), so a
  client cannot loosen them: `--host-resolver-rules=MAP * ~NOTFOUND`, `--webrtc-ip-handling-policy=disable_non_proxied_udp`,
  `--force-webrtc-ip-handling-policy`, `--disable-background-networking`, `--disable-component-update`,
  `--disable-domain-reliability`, `--disable-sync`, `--no-pings`, `--dns-prefetch-disable`, and the proxy
  `http://127.0.0.1:9` with bypass `<-loopback>` (a proxy that does not exist: whatever SeoTester does not fulfil is
  refused). A test keeps the script's flags equal to the app's `ChromiumBrowser.HardeningArgs`. Do **not** use a plain
  `playwright run-server`: it ignores client launch arguments (spike S3 measured what then escapes).
- **Compose:** service `seo-chromium` (image `ghcr.io/mintplayer/seotester-chromium:1.63.0`, built by
  `seotester-deploy.yml`) on the network `seo-browser` only, which SeoTester also joins and which is `internal: true`
  (no egress: every request is fulfilled by SeoTester); `init: true`; `shm_size: 512m`, `mem_limit: 1g` (S3 measured
  ~160–210 MB of Chromium processes on small pages on Windows; the Debian numbers are measured at the deploy, PRD
  Q-M10b, deploy.md §5); no capabilities, no published port. SeoTester's `SeoTester__Browser__Endpoint=ws://seo-chromium:3000/`
  is the Production default.
- **The app's own image** needs no browser, only Microsoft.Playwright's driver (its `node` and `playwright-core`, copied
  by `dotnet publish` into `.playwright/`, ~100 MB on Windows): its size in the published image is measured at the
  first deploy (deploy.md §5, Q-M10b).

## The fetcher container

PRD D54: in Production every request to a target (the probe, the crawl, external links, RDAP/IANA/hstspreload,
PageSpeed Insights, the verification file, meta tag and TXT record, the domain checks' DNS, the TLS probes, the
og-image proxy and every request Chromium sends) is made by `seo-fetcher`, a container with internet egress and no
database client, no network to RavenDB or the relay. The app keeps the politeness (`HostPacer`), robots.txt, the
analytics sink and the caches, and calls the fetcher through `ISeoFetcher`.

- **API** (`SeoTester.Fetcher/FetcherHost.cs`, protocol in `SeoTester.Fetching/SeoFetcherProtocol.cs`): `POST /fetch`,
  `/tls`, `/dns`, `/dns/zone`, each with the shared secret in `X-Seo-Fetcher-Key` (constant-time compare; otherwise
  401 and nothing is read); `GET /health` open. Requests ≤ 2 MiB, five client profiles with their own body caps
  (browser 16 MiB, crawler 10 MiB, PageSpeed 8 MiB, lookups 2 MiB, verification 1 MiB), GET/HEAD except the browser's,
  one call ≤ 120 s. A fetch answers as a frame (metadata JSON + the body as bytes); the app bounds and checks every
  field, since the fetcher is as untrusted as the sites behind it.
- **SafeHttp stays on** inside the fetcher (`MintPlayer.Spark.SafeHttp`), so a caller with the key still cannot point it
  at a private address or follow a redirect to one.
- **The PageSpeed key** is sent per request in its header and never logged; it stays configured on the app only.
- **Outage:** a fetcher that cannot be reached fails the crawl stage (Messaging retries it), never a page or a proof.
- **Locally** nothing changes: Development runs everything in one process (`InProcess`). To try the remote path, run
  `dotnet run --project apps/SeoTester/SeoTester.Fetcher` with `SeoFetcher__Key` set and start the app with
  `SeoTester__Fetcher__Mode=Remote`, `…__Url` and `…__Key`.
- **Tests:** `SeoTester.Tests/Fetcher/` boots the real fetcher in memory (or on Kestrel for the latency figures):
  the key, caps, deadlines, redirect and address rules over real sockets, the no-RavenDB guard on its restore graph, and
  an audit and verification end to end through it.

## The crawler's identity

User agent `MintPlayerSeoTester/1.0 (+https://seotester.mintplayer.com/bot)`; robots.txt token `MintPlayerSeoTester`
(the only opt-out, D19). `/bot` is rendered by the server (`Bot/BotPageEndpoint.cs`) in English or Dutch and states the
current limits from the settings.

## Privacy policy and terms (D47)

`/privacy` and `/terms` are server pages like `/bot` (`Legal/`, English or Dutch, no script, version and effective date
on top), linked from the SPA's footer under every page, the sign-in page included, and left to the server by the service
worker. ⚠️ The texts are a draft written from the code for the owner to review, not legal advice. The operator's name,
address, contact address, enterprise number, court and hosting provider are configuration (`SeoTester:Legal:*`, in
production `LEGAL_*` in the server's `.env`, [deploy.md](deploy.md) §4.3). An empty name, contact address or hosting
provider shows "[to be completed]"; the address and the enterprise number are optional and left out when empty, and an
empty court reads "the competent Belgian courts".
