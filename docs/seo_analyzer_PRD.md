# SeoTester (#268): PRD and plan

Status: **spec consolidated after 8 grilling rounds** (2026-10-10); M1–M12 built (M12's deploy pending the owner). Issue: https://github.com/MintPlayer/MintPlayer.Spark/issues/268

§1–§11 are the current spec. §12 is the decision log (D1–D46 plus measurements): each decision with its evidence, in the order it was taken. Where the body and the log disagree, the log is the record and the body is wrong. Fix the body.

Research behind this document:
- `docs/seo_analyzer_research.md`: domain research, the full check catalogue (~90 checks), .NET options, crawler safety, scoring. Facts marked **[verify]** are unconfirmed and become spike work.
- `docs/seo_analyzer_research_live_updates.md`: current streaming queries and the unified live-update design (X4).
- `docs/seo_analyzer_research_settings.md`: the settings package (X9).
- Three codebase inventories (Spark app/data features, background work, external logins), produced by a research team on 2026-10-10 and summarised in §2.

---

## 0. Status

| Item | State |
|---|---|
| Research (4 agents + 2 follow-ups) | ✅ 2026-10-10 |
| Grilling rounds 1–8 | ✅ 2026-10-10, D1–D32 (§12) |
| Changes API on the production Community licence | ✅ measured 2026-10-10 (§12) |
| Body consolidated with §12 | ✅ 2026-10-10 |
| Grilling round 9: live-update details | ✅ 2026-10-10, D33–D37 |
| Round 10: Web Push, SafeHttp ports and redirects, expiry ownership | ✅ 2026-10-10, D38–D41 (D39–D41 built, §12) |
| Spikes S1–S13 | ⏳ (S1, S2 (Windows; Debian Q-M9b), S3 (escape test and timings; the container Q-M10b), S5 (real answers with the owner's key), S6, S7, S8 (L5 open), S9, S11, S12 (Chrome; Q-M6a open), S13 ✅ 2026-10-10, §12; S4, S10 researched) |
| Milestones M0–M12 | ⏳ |
| M1 ✅ | 2026-10-10, commit `72c84933`: X2 SSRF-safe client in `libs/spark/MintPlayer.Spark/Http/`, adopted by the IdP (§12 "Spike S1") |
| M2 ✅ | 2026-10-10, commit `6df64fb3`: X1 document expiration in `libs/spark/MintPlayer.Spark/Expiration/` (`IExpiring`, `"expiresAfter"`, stamper, read filter, one 36 h configurator), adopted by Messaging, Mail, Moderation and the IdP (§12 "Spike S6"). ⚠️ `model.schema.json` gains `expiresAfter`: CI mints the next `schemas/v{n}` tag on merge (no tag created by hand) |
| M3 ✅ | 2026-10-10: X9 settings package `libs/settings/MintPlayer.Spark.Settings` (alias `settings`, `11.0.0-preview.104`) and X7 per-queue `HandlerTimeout` in Messaging (§12 "Spike S11"). Owner round 10 (D38–D41) committed separately before it |
| M4 ✅ | 2026-10-10: X11 organizations package `libs/organizations/MintPlayer.Spark.Organizations` (alias `organizations`, `11.0.0-preview.104`), the `ISparkSignedInHandler<TUser>` hook in Authorization (`preview.104`), ng-spark `/organizations` (`withOrganizationRoutes()`, `22.33.0`), and a startup refusal of unsupported verb combinations in `security.json` (§12 "Spike S13"). Guides `docs/guide-organizations.md` and `docs/guide-settings.md` (M3's). ~~Open: Q-M4a, Q-M4b (§11)~~ answered 2026-10-10: D42, D43 (§12 Round 11) |
| M6 ✅ | 2026-10-10 (§12 "Spike S12 and M6"): X10 notifications `libs/notifications/MintPlayer.Spark.Notifications` (alias `notifications`) with the hand-rolled Web Push library `libs/webpush/MintPlayer.Spark.WebPush` (both `11.0.0-preview.104`), preferences per organization (D44); X3 quotas (`[SparkQuota]`, `ISparkQuotas`, settings package; core `ISparkCustomActionGate`); X5 ng-spark score renderers (`@mintplayer/ng-spark/score-renderers`), CodeCoverage switched over with its DOM pinned; X6 share links in core (`ISparkShareable`, `AddShareLinks`, `ShareLink`/`RevokeShareLink`, `/spark/share`); ng-spark notifications/share UI; M5 follow-ups; S12 measured on Chrome (5/5 delivered). Guides: notifications, quotas, share links, score renderers. Open: Q-M6a–Q-M6d (§11) |
| M7 ✅ | 2026-10-10 (§12 "M7"): the SeoTester skeleton `apps/SeoTester` (host + SPA, `.Library`, `.Tests`; https 5013 / http 5014; database `SeoTester`), composed per §4.1 (external sign-in only with `ConfirmByEmail`, the four provider presets from configuration, MailManager, Messaging with the six crawl queues declared, SafeHttp, settings with every §6 gate and limit and the §7 weights, three quotas, notifications, organizations, the sharing switch); server-rendered `/bot`; Dockerfile; nx, solution and CI lists; `docs/seotester/README.md` (credentials to register). SparkId registers a Development `seotester` client. M6 leftovers: share-page headings, a clicked push is read. Open: Q-M7a, ~~Q-M7b~~ (§11; Q-M7b answered by D46) |
| M8 ✅ | 2026-10-10 (§12 "M8"): `Site` (organization rows, live), ownership verification by DNS TXT / well-known file / meta tag with the claim flow (D18, D42) and an hourly re-check; the check catalogue (110 checks, 8 categories) seeded into editable `CheckDefinition`s with "Reset to shipped"; the pure scoring engine (snapshot + `ScoringVersion`, headline and category scores); `AuditRun` / `PageResult` shapes; `StartAudit` / `StartFullCrawl` (join, 6 h cool-down, three quotas, gates, first message enqueued); share links for `Site` and `AuditRun` (core: per-object share gates); `AuditLogEntry`; D26 cascade; passkeys (D46). Open: Q-M8a–Q-M8e (§11) |
| M9 ✅ | 2026-10-10 (§12 "M9"): the crawl pipeline: `seo-probe` (robots.txt first and the opt-out, Q-M8e; sitemaps, redirects, TLS incl. the raw TLS 1.0/1.1 hello, well-known files, DNS, RDAP and hstspreload through `LookupCache`), `seo-crawl` (`CrawlFrontier`, page batches, per-host politeness, tier limits, full-crawl ceiling), `seo-aggregate` (scores with the run's snapshot, New/Fixed/Unchanged, notifications), `CancelAudit`; 92 of 110 checks (18 wait for Chromium/PSI, M10); core `SafeHttpTrace` and `ISafeHttpSockets`; spikes S2, S7, S9 measured. Open: Q-M9a, Q-M9b (§11) |
| M10 ✅ | 2026-10-10 (§12 "M10"): `seo-render` (a sample per tier, one URL template each, in Chromium over Playwright: launched locally or `ConnectAsync` to the container's `server.mjs`; every browser request fetched by the app through the SSRF-safe client, one redirect hop at a time (core `SafeHttpOptions.FollowRedirects`), robots.txt and the tier's connections, analytics hits answered locally; axe-core 4.13.0 bundled; phone and desktop renders), `seo-psi` (PageSpeed Insights with the key in a header, recorded real answers, 429 deferred then "not measured"), `seo-pdf` (the report printed per language, kept 7 days, members-only download), the aggregate waiting for crawl, render and PSI; **110 of 110 checks**; `RunCheckResult.Note` says why a check was not measured; the Chromium container's image and server script for M12; spikes S3 and S5 measured. Open: Q-M10a–Q-M10f (§11) |
| M11 ✅ | 2026-10-10 (§12 "M11"): the pages, on ng-spark's generic detail page with SeoTester's renderers and panels: landing page, site (gauge, verification with copy buttons, trend), run report (live progress over `/spark/live`, gauge and category bars, every check with New/Fixed/Unchanged, why not measured, how to fix in en/nl with platform snippets; PDF download), page previews and findings, share page, badge (`ShareBadge`, `/seotester/badge.svg`), operator dashboard (`OperatorDashboard`, `/seotester/admin/dashboard`), public catalogue texts; the notification link fixed to `/po/audit-run/{id}`; refusals say when to try again; ng-spark fixes found in the browser (gauge badge, share page, action errors, language header, notification links). Q-M6d reviewed. Open: Q-M11b, Q-M11c (§11). Q-M11a, Q-M11d answered (D50, D51, §12 Round 14) and built with the phone and light-mode review (§12 "M11 follow-up") |
| M12 ✅ code-complete, deploy pending the owner | 2026-10-10 (§12 "M12"): scheduled monitoring (`Site.Monitoring` Off/Weekly/Monthly, the 15-minute sweep `MonitoringSweepCronJob`, queue `seo-monitor`, one run per slot started for the member who set the schedule through `AuditStarter`) and alerts (score drop per site or `SeoTester:Alerts:ScoreDrop`, new critical issue, certificate < 14 d, domain < 30 d; every member, through `ISparkNotifier`); `apps/SeoTester/docker-compose.yml` (app + `seo-chromium` on `seo-browser`, `internal: true`; the app on Traefik's `web` and CodeCoverage's `code-coverage_coverage-internal`), `.env.example`, `GET /health`, workflows `seotester-deploy.yml` / `seotester-image-check.yml`, `tools/verify-seotester-deploy.test.mjs`; runbook `docs/seotester/deploy.md` (VPS setup, credentials, post-deploy checklist for Q-M5a, Q-M6a, Q-M9b, Q-M10b). **The owner deploys** (deploy.md §3, §4). Q-M12a–c answered (D47–D49, §12 Round 13): `/privacy` and `/terms`, the shared network `mintplayer-backend` (CodeCoverage's compose and deploy changed), alerts to all members |
| Shared RavenDB hardening (D55, D56) | ✅ code, compose, tests, runbook (2026-10-10, §12 Round 15); ✅ proven in Docker with the pinned image (2026-10-11, §12 "Q-D56a"). **The live migration is the owner's** (`docs/code-coverage/raven-tls-migration.md` §4) |
| Fetcher container (D54) | ✅ code-complete 2026-10-10 (§12 "D54"): `seo-fetcher` with egress and no database makes every request to a target (`ISeoFetcher`, `SeoTester.Fetching`, `SeoTester.Fetcher`, core `MintPlayer.Spark.SafeHttp`); `seo-fetch` (internal) + `seo-egress`; shared secret `SEO_FETCHER_KEY`; third image in both workflows. Deployed with the rest by the owner. Q-D54a closed: kept as built (§11) |
| M5 ✅ | 2026-10-10, commits `6162b583` (server, apps, tests, guides) and `e75cf81d` (ng-spark): X4 live updates over `GET|CONNECT /spark/live` in core (`libs/spark/MintPlayer.Spark/Live/`), `"live": true` on types and queries replacing `isStreamingQuery`, `Spark:Live` options, `ISparkLiveNotifier`, app streams (`LiveQueryArgs`), ng-spark `SparkLiveService` / `[live]` grids / po-detail refresh / po-edit merge on push; the five streaming defects fixed; `MintPlayer.Spark.Testing` `preview.103`. Guide `docs/guide-live-updates.md` (§12 "Spike S8"). Open: Q-M5a (§11) |

---

## 1. Goal

SeoTester is a production app at **seotester.mintplayer.com** (D1, D27, D29). Users sign in with Microsoft, GitHub, X or Google (D2) and register websites in an **organization** (D18, D21). The app crawls each site in the background, runs a catalogue of SEO, performance, security, accessibility, social and PWA checks, and shows:
- a 0–100 headline score with an A–F grade, plus a score per category (D15);
- a **prioritised fix list**: each suggestion has a plain-language summary and an expandable technical "how to fix" with per-platform snippets, in English and Dutch (D16, D9);
- previewers: Google SERP snippet, Open Graph card, X card;
- history: a score trend and "new / fixed since last run";
- live progress while a run is going, over a WebSocket (D4).

Organizations that have **verified** ownership of a site get deeper crawls, intrusive probes and scheduled monitoring, with alerts by email, in-app notification and Web Push (D11, D20). Sites can be shared through a public read-only link and an SVG badge (D31). The admin gets an operator dashboard (D28) and edits the check catalogue at runtime (D32). Every feature gate and limit is an admin setting (D11).

The app also proves Spark out. Where Spark lacks something, we build it in the framework (§5, X1–X11) in the same PR (one-PR rule). The libraries are in preview, so **no backward compatibility** is kept (D5).

### Hosting constraints

SeoTester is a Docker container on Debian on the VPS shared with CodeCoverage. It follows the same model: `apps/CodeCoverage/CodeCoverage/Dockerfile`, `docker-compose.yml`, `docs/code-coverage/hosting.md`, and a deploy workflow like `.github/workflows/code-coverage-deploy.yml` (ghcr → SSH → compose).

- **Compose topology (D3, D6):**
  - SeoTester joins the existing RavenDB network and a new **`seo-browser`** network. The RavenDB network is ~~CodeCoverage's `code-coverage_coverage-internal`~~ ~~the external network `mintplayer-backend` (D48)~~ **`seo-raven`** (the app and `coverage-raven` only), and mail goes over **`seo-mail`** (the app and `coverage-smtp` only): both external, `internal: true`, created by either deploy (D55). The database is reached over HTTPS with SeoTester's own client certificate, limited to the database `SeoTester` (D56).
  - A **Chromium container** joins **only** `seo-browser`, so it has no route to RavenDB (unsecured, internal) or postfix.
  - The goal is `internal: true` on `seo-browser` (no egress at all), which works if every browser request is fulfilled by SeoTester (§6); S3 proves it.
  - Memory limits and `shm_size` are set on Chromium, and a memory limit on the app (CodeCoverage's compose file sets none). The values are measured at the first deploy (Q-M10b).
- **TLS probing:** the `aspnet:11.0` images are Debian with OpenSSL 3, which refuses TLS 1.0/1.1, and `SslProtocols.Tls/Tls11` are obsolete. A failed `SslStream` handshake proves nothing, so we use a raw ClientHello probe (S2).
- **HTTP/3:** needs `libmsquic` (packages.microsoft.com, Debian repo) in the image; otherwise we report h3 from `Alt-Svc` only (S2).
- **RavenDB on the Community licence** (measured, §12):
  - The Changes API works.
  - At most 3 subscriptions per database.
  - Revisions: at most 2 kept, maximum age 45 days.
  - Expiration runs **at most every 36 h**, so expired documents can linger up to 36 h and every read must hide them (X1).

---

## 2. What Spark already gives us (evidence)

| Need | Spark feature | Evidence |
|---|---|---|
| App shape | host + `.Library` + `.Tests`, `AddSpark(...)`, App_Data model/security JSON | `apps/QnA/QnA/Program.cs:29-81,101-116` |
| Sign-in with Microsoft, GitHub, X, Google | `spark.AddExternalProviders(config)`; presets in `SparkExternalProviderExtensions.cs` / `GitHubAuthenticationExtensions.cs`; config `Spark:Auth:Providers:{GitHub,Google,MicrosoftAccount,Twitter}`; X is OAuth 2.0 + PKCE. No new packages needed | research: external logins |
| PWA-safe popup login | `withExternalLogin()`, `loginWithProvider` (#464/#490) | PR #499 |
| Account linking | `ExternalLoginLinking.ConfirmByEmail` (D7); unlink guard | research: external logins |
| Durable background work | `AddMessaging()`, `IMessageOutbox.EnqueueAsync(session,msg)` (atomic with the save), `IRecipient<T>`, `IMessageCheckpoint`, `NonRetryableException`, per-queue `MaxConcurrency`/`MaxPerInterval` | `libs/messaging/.../SparkBuilderExtensions.cs:136`, `IMessageOutbox.cs:187`, `SparkQueueOptions.cs:38,58`, `MessageProcessor.cs:174,260,279-293` |
| Restart survival | the retry sweeper reclaims abandoned claims every 30 s | `Services/MessageRetrySweeper.cs:10-60` |
| Scheduled sweeps | `ISparkCronJob` + sweep → `BroadcastOnceAsync({siteId}:{slot})` | `SparkCronScheduler.cs:228`, `ManifestScanScheduler.cs:38-72` |
| Mail | `AddMailManager()`, `ISparkMailer.SendAsync` (MJML) | `ISparkMailer.cs:13-60`, `docs/guide-outgoing-mail.md` |
| Save-time logic | `IBeforeSave<T>`, `IAfterSave<T>`, `IAfterSaveCommitted<T>` | `docs/guide-interceptors.md:94-175` |
| Row security | `GetRowFilterAsync` (the X11 helper builds on it) | `apps/Fleet/Fleet/Actions/CarActions.cs:41-55` |
| Custom actions | `SparkCustomAction` + actions.json + `Verb/Type` rights | `docs/guide-custom-actions.md` |
| Library-owned model/security | `[assembly: SparkLayer]`, slot bindings, UUIDv5 model ids (SPARK045) | `docs/spark_composition_PRD.md:113,333,361`, `SparkSecurityLayers.cs:298` |
| Live data | streaming queries over WebSocket. **Replaced** by X4; existing defects listed in §5 | `Endpoints/Queries/StreamExecuteQuery.cs` |
| Score UI precedents | CodeCoverage coverage-bar, `BsTrendChart`, sparkline, delta renderers; anonymous SVG badge | `apps/CodeCoverage` (`app.config.ts:34-102`, `BadgeController.cs`) |
| Dark mode, PWA | preboot + toggle; `provideSparkServiceWorker()` | `docs/guide-theming.md`, `docs/guide-pwa.md` |

---

## 3. Product scope

### 3.1 Check catalogue

The starting catalogue is research §1: about 90 checks in 8 categories, each with an id, layer, severity and suggestion pattern.

**The catalogue is admin-editable at runtime (D32).**
- Each check is a `CheckDefinition` document: id, category, severity, weight, enabled, en/nl title, en/nl `summary` template, en/nl `howToFix` per platform (nginx, Apache, IIS/ASP.NET Core, HTML), docs link.
- **Seeding:** an embedded JSON ships with the app. The seeder adds new checks and **never overwrites admin edits**; a "Reset to shipped" action restores one entry.
- The check **implementation** is a C# class keyed by check id. An entry without an implementation shows as unavailable, and an implementation without an entry is not run.
- Each run **snapshots the weights and severities it used**. `ScoringVersion` is a hash of that snapshot, so history stays interpretable after an edit.

How the owner's issue list is treated:

| Owner item | Treatment | Why |
|---|---|---|
| Sitelinks searchbox | **drop** (detect, info: "no longer used") | Google retired it on 2024-11-21 |
| AMP | **info**; validate the amphtml pairing if present | no ranking benefit since 2021 |
| `<a title>` | **replaced** by "links without accessible name / generic anchor text" | no SEO value |
| Outbound noopener | **info** | implied for `target=_blank` since 2021 |
| apple-mobile-web-app-* | **info** | superseded by the manifest (iOS 16.4+) |
| Long-term domain | **reframed**: RDAP expiry alert (< 30 d), info otherwise | not a ranking factor; business-continuity risk |
| Counter XSRF | **info heuristic only** | not measurable safely from outside |
| PWA | **opt-in profile** per site | Lighthouse dropped its PWA category in v12 |
| Backlinks | **out of scope** (§9) | needs paid web-scale data |
| Everything else | in the catalogue with a severity | research §1 |

Additions beyond the issue: broken links, redirect chains, duplicate titles and descriptions, orphan pages, soft 404s, a raw-vs-rendered diff (CSR-only SPAs), mixed content, CSP evaluation, cookie flags, certificate expiry, SPF/DMARC, Core Web Vitals via PSI/CrUX (D9), structured-data validation, and axe-core accessibility.

### 3.2 Features

| # | Feature | Decisions |
|---|---|---|
| F1 | **Organizations**: an implicit personal org per user; create orgs; roles Owner/Admin/Member/Viewer; invite; join request by email with an acceptance link | D18, D21–D23, X11 |
| F2 | **Sites** in an org, with a profile (PWA on/off, language, schedule) | D12 |
| F3 | **Ownership verification per org**: DNS TXT `_seotester.<host>`, meta tag, or `/.well-known/` file; re-checked periodically. Verifying a site another org already holds mails that org's owner an acceptance link | D18 |
| F4 | **Audit run**: background crawl, live progress, cancel; **join a running audit** if a member of the owning org | D4, D17, D18 |
| F5 | **Report**: headline + category scores, issues grouped per check (affected count, sample URLs), prioritised fix list (weight × pages × gap + effort tag), summary + how-to-fix toggle (a user preference remembers the default) | D15, D16 |
| F6 | **Page drill-down**: per-page findings for 7 days, then "page details expired"; one result per page and site, the latest (a later scan's fetch replaces it, D8) | D8 |
| F7 | **Previewers**: SERP (desktop/mobile pixel widths), OG card, X card | — |
| F8 | **History**: score trend (marks `ScoringVersion` changes), "new / fixed since last run" | D9, D32 |
| F9 | **Monitoring** (verified, setting-gated): off/weekly/monthly per site | D11, D12 |
| F10 | **Alerts** (score drop ≥ N, new critical issue, certificate < 14 d, domain < 30 d) by **email, in-app notification and Web Push** | D20, X10 |
| F11 | **Share link + SVG badge**: created/revoked by org Owner/Admin, viewable by anyone with the link; allowed for unverified sites; global setting can disable it | D11, D31, X6 |
| F12 | **Headless checks** on a sample via the Chromium container: rendered DOM, axe-core, 375 px responsiveness, request count/weight, LCP/lazy-load, SPA analytics route change | D3, D6 |
| F13 | **PageSpeed Insights / CrUX** on a sample (the owner provides the key; no key gives "not measured") | D9, D13 |
| F14 | **PDF export** of the report, rendered by the Chromium container (`page.PdfAsync`, print CSS) | D9 |
| F15 | **Data export** (JSON) and **cascade delete** (org → sites → runs → pages → invites; account → personal org + memberships) | D26 |
| F16 | **Operator dashboard** (admin): usage stats, abuse log, all websites/orgs, database size per collection and total | D28 |
| F17 | **Admin settings**: every gate and limit (§6), category weights, sharing switch | D11, X9 |
| F18 | **Catalogue editor** (admin) | D32 |
| F19 | **`/bot` page**: who we are, the limits, robots.txt opt-out | D17, D19 |

---

## 4. Architecture

### 4.1 App

- **Projects:** `apps/SeoTester/SeoTester` (host + ClientApp), `SeoTester.Library` (entities, check implementations, catalogue seed JSON), `SeoTester.Tests`, plus (D54) `SeoTester.Fetching` (`ISeoFetcher`: the SSRF-safe clients, TLS probes, DNS and the fetcher protocol; no database) and `SeoTester.Fetcher` (the `seo-fetcher` container: that code behind an internal API with a shared secret). Every request to a target goes through `ISeoFetcher`: in process outside Production, the fetcher container in Production. Ports 5013/5014 (the next free pair).
- **Hand-maintained lists to update:** root `package.json` workspaces, `MintPlayer.Spark.slnx`, `.github/workflows/pull-request.yml` (137, 148, 173), `tools/verify-ngsw-config.test.mjs:64`, `tools/test-local.mjs:33` (if E2E), Dockerfile, and a deploy workflow modelled on `code-coverage-deploy.yml:20-40`.
- **Composition:**

  ```
  AddSpark(config, spark => {
      UseContext<SeoContext>(); AddActions(); AddCustomActions(); AddMigrations();
      AddAuthentication<SparkUser>(o => { o.LocalCredentials = SparkLocalCredentials.Disabled;
                                          o.ExternalLoginLinking = ExternalLoginLinking.ConfirmByEmail; });
      AddExternalProviders(config);   // Microsoft, GitHub, X, Google
      AddMailManager();               // X and Entra work accounts give no verified email
      AddMessaging(); AddRecipients(); AddCronJobs(); AddHistory();
      AddExpiration();                // X1
      AddSafeHttp();                  // X2
      AddLive();                      // X4
      AddSettings(typeof(SeoSettings));   // X9
      AddNotifications();             // X10 (in-app + Web Push)
      AddOrganizations();             // X11
  })
  ```

- **Login:** external only (D2); `Spark:Auth:PublicBaseUrl` set in production. The owner registers the providers (D13):

  | Provider | Config key | Callback | Notes |
  |---|---|---|---|
  | Microsoft (Entra, personal + work/school) | `Spark:Auth:Providers:MicrosoftAccount` | `https://seotester.mintplayer.com/signin-microsoft` | `common` endpoint; email trusted for personal accounts only |
  | GitHub | `Spark:Auth:Providers:GitHub` | `/signin-github` | scope `user:email` |
  | X | `Spark:Auth:Providers:Twitter` | `/signin-twitter` | **OAuth 2.0** client id/secret (not 1.0a keys); enable "Request email from users" |
  | Google | `Spark:Auth:Providers:Google` | `/signin-google` | `openid profile email` |
  | PSI / CrUX | `SeoTester:PageSpeed:ApiKey` | — | Google Cloud API key |
  | Web Push | `Spark:Notifications:WebPush:{PublicKey,PrivateKey,Subject}` | — | VAPID keys |

### 4.2 Data model (storage stays modest)

Org-owned documents carry `OrganizationId`. Row security for every SeoTester type is "the current user is a member of the owning org, with role ≥ R", using the X11 helper (D18, D21). Composed custom-query rows bypass row security (research), so report queries are Actions-method queries **with** the membership filter, each covered by a test.

| Document | Lifetime | Size target | Notes |
|---|---|---|---|
| `Organization`, `OrganizationMembership`, `OrganizationInvitation` (X11) | permanent; invitations `@expires` 14 d (`expiresAfter`) | tiny | the personal org is created on first sign-in; a membership's id is `OrganizationMemberships/{userId}/{orgId}` (read by prefix, S13) |
| `Site` | permanent until deleted | < 2 KB | org, origin, profile, verification (method, token, verifiedAt, lastCheckedAt), schedule, alert thresholds, `LatestRunId/Score/Grade`, share-token hash |
| `AuditRun` | **permanent** (D8) | 5–20 KB | site + org, participants (who started or joined), status/phase/progress, `CancelRequested`, limits used, **weights snapshot + `ScoringVersion`**, category scores, overall + grade, `CheckResults` [ValueObject] list (checkId, status, affected count, ≤ 5 sample URLs, delta New/Fixed/Unchanged), timing, error |
| `PageResult` | **`@expires` 7 d** (D8); one per page and site, the latest fetch's | ~1–3 KB | run, URL, status, depth, timings, title/description/h1 snapshot (previewers), findings `(checkId, status, params)`. Shown as a sub-query of `AuditRun`, not AsDetail |
| `CrawlFrontier` | `@expires` 2 d, deleted on completion | grows with the crawl | pending URLs + visited-set hashes, the checkpoint for page batches |
| `LookupCache` (RDAP, hstspreload, robots, PSI) | `@expires` 1–7 d | small | natural ids (`rdap/example.com`) |
| `TargetCooldown` | `@expires` = cool-down | tiny | per-target throttle across **all** users for unverified targets (D17) |
| `CheckDefinition` | permanent | small | admin-editable catalogue (D32) |
| `RunReport` (M10) | `@expires` 7 d | tiny + a ~90 KB PDF attachment | `{runId}/reports/{en\|nl}`: a printed report (F14), printed again on demand (Q-M10d) |
| `AuditLogEntry` | permanent (`@expires` 1 y, D37) | tiny | who audited what, when, from which org (D28) |
| `Setting` (X9), `Notification` + `PushSubscription` (X10) | per package | tiny | — |

We never store raw HTML, full PSI JSON or screenshots. Texts, grades and previews are recomputed from stored parameters, so wording edits need no data migration.

### 4.3 Crawl pipeline (Messaging)

**`StartAudit`** is a custom action on `Site`. It:
1. checks the role (Member+), the quotas (X3: runs/day, D14's weekly full crawl), the cool-down, and the robots.txt opt-out;
2. if a run of the same origin is in progress **and** the user is a member of that run's org, **joins** it by adding a participant (D17, D18);
3. otherwise creates the `AuditRun` and calls `IMessageOutbox.EnqueueAsync(session, new SiteProbeMessage(runId))` in **one** `SaveChanges`.

Queues (`[MessageQueue]`; per-queue `HandlerTimeout`, X7):

| Queue | Message | Work | MaxConcurrency |
|---|---|---|---|
| `seo-probe` | `SiteProbeMessage` | robots.txt, sitemap(s), TLS probe, HTTP→HTTPS, HSTS (+ preload API), DNS (CAA, DNSSEC, SPF/DMARC, AAAA, NS), RDAP, security.txt, change-password, favicon, soft-404; seeds the frontier | 2 |
| `seo-crawl` | `PageBatchMessage(runId, batchNo)` | fetches up to B pages (S7) through X2, runs raw-HTML + header checks, writes `PageResult`s, extends the frontier, writes throttled progress, honours `CancelRequested`, then chains the next batch or finishes | 2 (per-host politeness inside the handler) |
| `seo-render` | `RenderSampleMessage(runId)` | Chromium on the sample (§6): rendered DOM, axe-core, 375 px, network log, raw-vs-rendered diff | setting, default 1 |
| `seo-psi` | `PsiMessage(runId)` | PSI/CrUX on the sample | 1, `MaxPerInterval` within quota |
| `seo-aggregate` | `AggregateMessage(runId)` | site-level checks (duplicates, orphans, broken links, redirect chains, hreflang return links), scoring with the snapshot, delta vs the previous run, `Site.Latest*`, alerts | 1 |
| `seo-pdf` | `PdfMessage(runId, culture)` | Chromium prints the report page | 1 |
| `seo-monitor` (M12) | `MonitoredAuditMessage(siteId, slotUtc)` | starts a monitored site's scheduled audit through `AuditStarter` | 1 |

~~The stages run independently. Each one sets its flag on the run when it finishes, and the last one enqueues the aggregate with `BroadcastOnceAsync(runId)`.~~ The crawl's last batch starts `seo-render` and `seo-psi` side by side (their sample is taken from the crawled pages, Q-M10c). Each stage adds its flag to `StagesDone` and enqueues the aggregate in the same commit; the aggregate runs only when `crawl`, `render` and `psi` are all there (§12 "M10"). Page work is idempotent: a `PageResult` id is `{runId}/pages/{urlHash}`.

**Monitoring:** a cron sweep every 15 min selects the sites with `NextMonitoringRunAtUtc <= now` that the monitoring gate admits (verified by default) and calls `BroadcastOnceAsync(MonitoredAuditMessage, $"{siteId}:{slot}")`, then moves the site to its next slot. This is the `ManifestScanScheduler` pattern. The recipient starts the run for the member who set the schedule (`Site.MonitoringBy`); the aggregate alerts the organization's members (F10). Built in M12 (§12 "M12").

### 4.4 Live updates (X4)

The crawler writes `AuditRun.Progress` (phase, pages done/discovered, current URL) at most every 2 s. The Changes API notices the write. Every open run page, and the "running audits" grid, subscribe over the single `/spark/live` socket and receive **patches** (D24). Notifications (X10) arrive over the same socket.

### 4.5 UI (ng-spark + ng-bootstrap)

- **Dashboard:** `spark-query-card` tiles (my sites, latest grades, running audits live, notifications).
- **Org pages:** members, roles, invitations, join requests (X11 UI).
- **Site page:**
  - score gauge and category bars (X5), trend chart;
  - latest fix list;
  - Run now / Join running audit;
  - Verify ownership wizard;
  - share and badge links (Owner/Admin);
  - monitoring schedule.
- **Run page:**
  - live progress while running (X4);
  - then categories → issues per check (summary, expandable how-to-fix per platform, affected pages, sample URLs, docs link) → page sub-query → page detail with the SERP/OG/X previewers;
  - PDF download.
- **Public share page:** an anonymous read-only latest report (X6).
- **Admin:** settings (X9), catalogue editor (D32), operator dashboard (D28).
- **`/bot`.**
- Dark mode and PWA as usual; Web Push opt-in prompt (X10). Languages en + nl.

---

## 5. Framework additions (Spark libs, same PR)

| # | Addition | Why | Evidence of the gap |
|---|---|---|---|
| X1 | **Document expiration** (✅ M2, §12 "Spike S6"): `IExpiring` (`DateTime? ExpiresAtUtc`, the source of truth) plus an optional `"expiresAfter"` in the model JSON for new documents; a store-level `OnBeforeStore` mirrors it into `@expires` (pipeline and raw sessions); a core row policy hides expired documents on every read path (they linger up to 36 h); `UseSpark()` enables expiration once for every library that declares `Registry.RequireDocumentExpiration` (Messaging, Mail, Moderation, IdP) or any `IExpiring` model type, with a **36 h floor refused below on every licence**; `AddExpiration(o => …)` only for options; `ExpiresAtUtc` is **framework-protected** on persistent-object writes (D41: a client can neither extend nor clear it). Fixes the gap where Mail and Moderation wrote `@expires` without enabling expiration | pages, frontier, caches, cool-downs, invitations | no PO hook; `IBeforeSave` can't set metadata on create (`SparkInterceptors.cs:291-295`); store created without events (`SparkMiddleware.cs:158-188`) |
| X2 | **SSRF-safe HTTP client** (`AddSafeHttp()`): `ConnectCallback` resolves → allow-list of global unicast (unwraps IPv4-mapped/NAT64/6to4) → connects to the checked IP (closes DNS rebinding); manual redirects re-checked per hop; no proxy, no cookies; byte, decompression and time caps; a configured list **replaces** its default, so configuration can remove 80/443 (D39); optional `RedirectsWithinOriginOnly` (D40). **Adopted by the IdP** (`JwksUri`, back-channel: ≤ 3 redirects within the registered origin) | user-chosen URLs fetched from inside the compose network | nothing blocks private addresses; `OidcClientKeys.cs:24-34`, `OidcSessionStore.cs:132` |
| X3 | **Per-user quotas for custom actions** (✅ M6, §12 "Spike S12 and M6": `[SparkQuota]`, `ISparkQuotas`, sliding windows in the settings package) (e.g. `[SparkQuota("audits", …)]`), evaluated after authentication, with limits read from X9 settings | runs/day, D14 weekly full crawl | `AddRateLimiter` is per-IP and pre-auth (`SparkBuilderRateLimiterExtensions.cs:115,128-130`) |
| X4 | **Unified live updates**: one multiplexed socket at `GET\|CONNECT /spark/live`; subscribe to **persistent objects and queries** (full `ExecuteQueryRequest`: parent, paging, sort, search); change source configurable via **`Spark:Live:Source` = `Actions` \| `Changes` \| `Both`, default `Both`** (D33): `Actions` = every write through a RavenDB session of this process, from the document store's own events (not `IAfterSaveCommitted`, which needs Messaging: §12 "Spike S8"), plus an optional `ISparkLiveNotifier.Changed(id)`; `Changes` = the RavenDB Changes API (measured working on Community; **not** a data subscription, one multiplexed connection per database per process); `Both` merges and de-duplicates by id + change vector inside the throttle window. App `IAsyncEnumerable` streams stay for non-RavenDB data. **Low-code (D34):** `"live": true` in the model JSON is all an app writes for the common case, and under `Both` even writes outside the Spark pipeline (the crawler) are picked up without notifier calls; every push re-runs the HTTP read path (row security, redaction, attribute rights); `gone` = deleted or forbidden alike; **patches**; **opt-in** per type/query; throttling, bounded queues, socket lifetime ≤ login ticket. ng-spark: `SparkLiveService`, `livePersistentObject`/`liveQuery`, `spark-query-grid [live]`, po-detail refresh, po-edit banner + existing three-way merge. **Replaces** streaming queries (`isStreamingQuery`, `StreamingQueryArgs`, `/spark/queries/{id}/stream`) and fixes their defects (below) | run progress, notifications | research: live updates; D4, D5, D24 |
| X5 | **Score/threshold renderers in ng-spark** (✅ M6, `@mintplayer/ng-spark/score-renderers`): gauge, threshold bar, trend, delta, generalised from CodeCoverage (which then uses them) | score UI | ng-spark has no chart renderers |
| X6 | **Capability-token read of one PO** (share link; ✅ M6, core `AddShareLinks`, allow-lists declared in code): hashed token on the object, anonymous read of exactly that object + declared children, revocable, rate-limited | F11 | only precedent is the badge token |
| X7 | **Per-queue `HandlerTimeout`** (✅ M3, §12 "Spike S11"): `SparkQueueOptions.HandlerTimeout`, configuration over code like every per-queue option, read through `SparkMessagingOptions.HandlerTimeoutFor(queue)` in both subscription modes | long probes | global only (`SparkMessagingOptions.cs:126`) |
| X9 | **Settings package** (✅ M3, §12 "Spike S11") `libs/settings/MintPlayer.Spark.Settings` (alias `settings`): `SparkSetting<T>(Key, Default, Description)` declared in code; `ISparkSettings` returns the stored value, else `IConfiguration`, else the default; parsing via the `ConfigurationBinder` converter; startup seeder of one `Settings/{Key}` document per key (never touches `Value`); rights `QueryReadEdit/Setting` bound by the app (`settings:administrators`); 30 s TTL cache, cleared on save; no secrets | D11 | no settings feature in `libs/` |
| X10 | **Notifications package** (✅ M6, preferences per organization D44; push subscriptions as own documents, Q-M6b): in-app `Notification` entity pushed over X4 with an unread count; **Web Push**, hand-rolled in its own library in this solution (D38: RFC 8291 `aes128gcm` + RFC 8292 VAPID on built-in .NET crypto, verified against the RFC 8291 Appendix A vectors; VAPID keys from config, per-device `PushSubscription` on `SparkUser`, opt-in UI, push + click handlers via `provideSparkServiceWorker()`); one `ISparkNotifier` beside the mailer | D20, D30 | none in Spark |
| X11 | **Organizations package** (✅ M4, §12 "Spike S13") `libs/organizations/MintPlayer.Spark.Organizations` (alias `organizations`, depends on Authorization only, D25): `Organization`, `OrganizationMembership` (ordered roles Owner > Admin > Member > Viewer), `OrganizationInvitation` (`IExpiring`, 14 d); implicit personal org on first sign-in (new Authorization hook `ISparkSignedInHandler<TUser>`); invitations (Admins) and join requests (`ISparkOrganizations.RequestToJoinAsync`) mailed with a single-use, hashed-token link to ng-spark's accept page (`withOrganizationRoutes()`, `POST /spark/organizations/accept`); ownership transfer; `IOrganizationDataHandler` for cascade delete and export (account deletion and personal-data export through Authorization's existing hooks); the row-filter helper is **declarative**: `IOrganizationOwned` + `[RequiresOrganizationRole(action, role)]`, enforced by one row policy (`OrganizationId in (…)` from the caller's memberships, read by id prefix once per request); `security.json` slot `organizations:users` gates the types | D18, D21–D23, D26 | no org concept in Spark |

**Defects in today's streaming queries, fixed by X4** (research: live updates):
1. Removed rows are never signalled (`StreamingDiffEngine.cs:399`).
2. Added rows never appear client-side (`spark-query-list.component.ts:419-430`).
3. Access denied closes with 1011, so the client retries 10× for nothing.
4. A revoked right closes with 1000, so the grid silently keeps stale rows.
5. The comment at `StreamingQueryExecutor.cs:81-83` claims RavenDB is watched; it isn't.

Not built: X8 (dead-letter viewer, resilience policies, dynamic per-entity schedules), see §9.

---

## 6. Crawler safety and ethics

The full reasoning is in research §4.

- **SSRF, three layers:**
  0. **Network (D54):** every request to a target is made by the `seo-fetcher` container, which has internet egress and
     no database client, no route to RavenDB or the relay, and talks to the app only on `seo-fetch` with a shared secret.
     The app keeps pacing, robots.txt and the caches and calls it through `ISeoFetcher` (in process outside Production).
  1. Every server-side fetch goes through X2 (inside the fetcher too).
  2. Chromium sits on a network shared only with SeoTester (D6). SeoTester drives it remotely (Playwright `ConnectAsync`) and intercepts **every** request with `RouteAsync`, fetches it through X2 and fulfils it. Service workers are blocked, and Chromium starts with `--host-resolver-rules="MAP * ~NOTFOUND"`, so it never resolves or connects anywhere itself.
  
  S3 proves both layers and lists what escapes routing (WebSockets, WebRTC, prefetch). Those are blocked by flags or the `internal: true` network.
- **Politeness:**
  - robots.txt (RFC 9309) for the token `MintPlayerSeoTester`, which is also the **only opt-out** (D19);
  - UA `MintPlayerSeoTester/1.0 (+https://seotester.mintplayer.com/bot)`;
  - unverified: per host 1 connection and ≥ 1 s between requests;
  - `Retry-After` honoured on 429/503;
  - hard caps on pages, bytes and time.
- **Abuse (D17):**
  - sign-in required;
  - per-user quotas (X3);
  - a **per-target cool-down across all users** for unverified targets;
  - **joining** a running audit instead of starting a duplicate (members of the owning org only);
  - the `/bot` page;
  - an abuse log (D28).
  
  No CAPTCHA.
- **Verified-only by default, each a setting (D11):**
  - deep-crawl limits;
  - scheduled monitoring + alerts;
  - intrusive probes (`.git/HEAD`, `.env`, backups, directory listing beyond seen asset dirs). Probing sites you don't own can fall under computer-misuse law (e.g. Belgian Criminal Code art. 550bis).
  
  Share link + badge are allowed for unverified sites.

**Limits.** All of these are X9 settings; the values are defaults.

| Limit | Unverified | Verified |
|---|---|---|
| Pages per run | 100 | 2 000 |
| Wall time | 10 min | 60 min |
| Connections per host | 1 | 4 |
| Rendered sample | 3 pages | 10 pages |
| PSI sample | 1 (home) | 5 |
| Runs per user per day | 5 | 24 (incl. schedule) |
| **Full-site crawl** | — | **1 per user per week, across all their verified sites** (D14), safety ceiling 20 000 pages / 6 h |
| Target cool-down (all users) | 6 h (D37) | none |
| Concurrent renders (Chromium) | 1 (global) | |

---

## 7. Scoring

- **Check results:** pass / warn / fail / not-applicable / error. N/A and error are **excluded** from the denominator.
- **Page-level checks** aggregate to a pass ratio across pages.
- **Critical failures cap** their category (Lighthouse-style).
- **Category weights (defaults, admin-editable as settings):** SEO 25, Technical 20, Performance 20, Security 15, Accessibility 10, Social 5, Mobile/PWA 5. Domain/DNS is an info panel plus alerts. A category with no measurements is dropped from the weighted mean and shown as "not measured".
- **Headline score:** the weighted mean. **Grades:** A ≥ 90, B ≥ 80, C ≥ 70, D ≥ 50, else F (D15).
- Each run stores its **weights snapshot** and `ScoringVersion` = hash of the snapshot (D32). The trend chart marks snapshot changes.

---

## 8. Spikes

Each spike ends with a measured result recorded in §12 (numbers, file:line, or a red→green test).

| # | Spike | Question | Done when |
|---|---|---|---|
| S1 | SSRF-safe handler | Does a `ConnectCallback` guard block loopback, private, link-local, `169.254.169.254`, IPv4-mapped IPv6, NAT64, DNS rebinding, and survive redirects? Overhead? | red→green tests per case; overhead measured |
| S2 | TLS/protocol probing on Debian | raw ClientHello for TLS 1.0–1.3 from the `aspnet:11.0` Debian image vs badssl.com (`tls-v1-0.badssl.com:1010`, …); h2 via ALPN; h3 with `libmsquic` vs `Alt-Svc` only | matches badssl; msquic decision |
| S3 ✅ (container: Q-M10b) | Chromium container | image/RAM per page on Debian; `shm_size`; axe-core time; `ConnectAsync` + `RouteAsync` + host-resolver rules: **does any request escape?** Can `seo-browser` be `internal: true`? PDF render time | escape test red→green; numbers |
| S4 | RDAP coverage | expiry for .com/.net/.org/.eu/.nl/.be/.io; confirm .be has no RDAP or WHOIS expiry | TLD table |
| S5 ✅ | PSI + CrUX | real quota, latency, Lighthouse 13 insight ids, CrUX availability for small sites | real responses stored as fixtures |
| S6 | Expiration on Community | per-document `@expires` (7 d, 2 d, 14 d) with the 36 h frequency; visibility window; the read filter hiding expired docs; storage of a 100- and a 2 000-page run | sizes + window recorded; filter test red→green |
| S7 | Crawl chaining | batch size B well under `HandlerTimeout` at 1 req/s; resume after killing the host mid-batch | kill test passes; B with evidence |
| S8 ✅ | Live updates | L1–L4, L6–L8 measured (§12 "Spike S8"); L5 (real proxy) open as Q-M5a. Questions were: patch vs full size, cost per push with 1/10/50 viewers, slow clients, nginx/Traefik upgrade + idle timeout, index freshness after BatchCompleted, login expiry on long sockets, po-edit merge under pushes | numbers per L-item |
| S9 | HTML parsing | AngleSharp (+ .Css) time/memory per page; pixel-width title measurement without a browser | numbers; decision |
| S10 | hstspreload.org API | v2 status/preloadable schema | real fixture |
| S11 | Settings ✅ | stored-overrides-config and delete-falls-back; TTL propagation; rights via slot binding | red→green tests (§12 "Spike S11") |
| S12 ✅ | Web Push | ~~VAPID library choice for .NET 11~~ decided: hand-rolled RFC 8291/8292 library (D38); remaining: delivery to Chrome/Firefox/Edge/Safari (iOS needs an installed PWA), done in M6 | RFC 8291 Appendix A vectors pass; push delivered on each |
| S13 ✅ | Organizations row filter | membership filter cost in queries (a `Membership` lookup per request vs a claim cached per session), and its interaction with existing `GetRowFilterAsync` composition | query plan + timing; design chosen (§12 "Spike S13") |

---

## 9. Out of scope (not being done)

- **Backlinks and keyword volumes:** require paid web-scale data. Search Console integration was not chosen.
- **Running the Lighthouse CLI ourselves:** PSI runs Lighthouse 13 on Google's hardware.
- **Paid plans and billing.**
- **CAPTCHA/Turnstile** (D17).
- **Opt-out form or block list:** robots.txt only (D19).
- **Comparing two runs side by side / competitor comparison** (D9).
- **X8:** dead-letter viewer/replay, resilience policies, dynamic per-entity schedules (D28).
- **Org claims through OpenID Connect / SparkId** (D25).

---

## 10. Milestones (one PR)

Tests run once at the end (batched), per the repo rules.

| # | Milestone | Depends on |
|---|---|---|
| M0 | Spikes S1–S13, results in §12 | — |
| M1 | X2 safe HTTP client + IdP adoption | S1 |
| M2 | X1 expiration (+ Mail/Moderation gap, expired-doc read filter) | S6 |
| M3 | X9 settings package; X7 per-queue timeout | S11 |
| M4 | X11 organizations package | S13 |
| M5 | X4 unified live updates, replacing streaming queries (Fleet, DemoApp, schema tag, tests, guides) | S8 |
| M6 | X10 notifications + Web Push; X3 quotas; X5 renderers (CodeCoverage switched over); X6 share links | M4, M5, S12 |
| M7 | SeoTester skeleton: projects, ports, workspace lists, logins, mail, security.json, org row filters, Dockerfile | M3, M4 |
| M8 | Sites, verification, catalogue (seed + editor), scoring engine (unit-tested on fixtures) | M7 |
| M9 | Crawl pipeline: probe, crawl batches, aggregate, cancel, join, cool-down, quotas, audit log | M1, M2, M8, S2, S4, S7, S9 |
| M10 | Chromium container: render sample, axe-core, PDF; PSI/CrUX | M9, S3, S5 |
| M11 | UI: dashboard, org, site, run, page, previewers, trend, live progress, share page, `/bot`, admin dashboard, export/delete | M5, M6, M9 |
| M12 | Monitoring cron, alerts (mail + in-app + push), deploy (compose networks, Chromium limits, workflow), docs `docs/seotester/` incl. the credential registration list | M10, M11 |

---

## 11. Open decisions

Round 9 (live-update details and loose ends) was answered on 2026-10-10: D33–D37 in §12. New questions found during spikes go here first.

- ~~**Q-M4a (from M4): what happens when a user verifies a site that sits in someone's *personal* organization?**~~ **Closed by D42** (§12 Round 11): option (b), personal organizations take members. The question and its options moved to D42.
- ~~**Q-M4b (from M4): may anyone holding an invitation link accept it, or only the account that owns the invited address?**~~ **Closed by D43** (§12 Round 11): only the account that owns the invited address. The question moved to D43.
- ~~**Q-S6a (from S6): should the framework own `IExpiring.ExpiresAtUtc` on persistent-object writes?**~~ **Closed by D41** (§12 Round 10): yes, SoftDelete's rule. ~~Today it is an ordinary model attribute, so a user with `Edit/T` can extend or clear a document's expiry unless the app marks it read-only or `[IgnoreProperty]`. The alternative is SoftDelete's rule: an edit or create through the pipeline keeps the stored value (code via `IDatabaseAccess`/raw sessions still sets it). SeoTester's `PageResult` is not user-editable either way.~~
- **Q-M5a (from M5, spike S8 L5): where is the real-proxy check (nginx/Traefik upgrade and idle timeout for `/spark/live`) run?** It needs a reverse proxy in front of a running Spark host. Not run in M5: Docker Desktop was not running on the build machine and no nginx/Traefik binary is installed, and probing production was not part of this milestone. What is known: production fronts the apps with Traefik (`apps/CodeCoverage/docker-compose.yml:221-233`), which forwards WebSocket upgrades by default; the server sends a `ping` frame after 30 s of silence (measured, `LiveSocketTests.A_quiet_socket_gets_a_ping_at_the_heartbeat`), below nginx's default 60 s `proxy_read_timeout`; the nginx recipe is in `docs/guide-docker-deployment.md`. Options: (a) start Docker locally and run nginx and Traefik in front of a test host; (b) measure on the production VPS with a throwaway container, as L1 was; (c) measure at the M12 deploy of SeoTester, which is the first app with a live type. **Recommendation: (c)**, because it is the real proxy and the real app, and nothing before M11 opens a live socket in production (Fleet and DemoApp are demos). **M12:** the exact commands are in `docs/seotester/deploy.md` §5; the owner runs them at the first deploy and records the result in §12.
- **Q-M6a (from S12): where is delivery to Firefox, Edge and Safari measured?** Chrome 154 was measured (5/5 delivered and decrypted, §12 "Spike S12 and M6"). Edge refused to subscribe under automation (`Registration failed - permission denied`: its WNS channel is not reachable from an automation profile), and no Firefox or Apple device is reachable from the build machine. All three accept `aes128gcm` (RFC 8291) per their documentation, and the library is byte-exact against the RFC vectors. Options: (a) the owner subscribes a desktop Firefox, a desktop Edge and an iPhone with SeoTester installed to the Home Screen and the result is recorded; (b) a scripted run in a non-automation browser profile here; (c) do it at the M12 deploy, on the real origin over HTTPS. **Recommendation: (c)**, with (a)'s devices: iOS needs an installed PWA on a real HTTPS origin anyway, which only exists at M12. **M12:** the exact commands are in `docs/seotester/deploy.md` §5; the owner runs them at the first deploy and records the result in §12.
- **Q-M6b (from M6): push subscriptions are documents of their own, not a list on `SparkUser`.** D30 says "stored per device on the SparkUser". Built as `PushSubscriptions/{userId}/{hash}` (read by id prefix), because a list on the user document would put every subscribe on Identity's concurrency stamp (a sign-in racing a subscribe fails one of them) and make Authorization know about push. Options: (a) keep separate documents; (b) move them onto `SparkUser`. **Recommendation: (a).**
- **Q-M6c (from M6): what a share link shows is declared in code** (`AddShareLinks(o => o.Share<Site>(attributes, queries))`, allow-lists) rather than in the model JSON. Options: (a) keep it in code; (b) a `"share"` block on the persistent object in the model (low-code, but it moves the model hash and the schema, and a model edit could widen a public link). **Recommendation: (a).**
- **Q-M7a (from M7): a feature gate has three values, not two.** D11 says the owner wants to "enable or disable" monitoring, intrusive probes, deep-crawl limits and sharing, with defaults "verified-only" for the first three and "allowed for unverified sites" for sharing. An on/off switch cannot express both, so each gate is built as `SeoFeatureScope` = `Off` / `VerifiedOnly` / `AllSites` (`SeoTester:Features:*`, defaults as D11). Options: (a) the three-valued gate; (b) a boolean per gate with "verified-only" fixed in code (then "intrusive probes on unverified sites" can never be switched on, which D11's legal note argues for); (c) two booleans per gate (enabled, unverified allowed). **Recommendation: (a)**: one setting per gate, every D11 default expressible, and `AllSites` for probes remains an explicit administrator choice.
- ~~**Q-M7b (from M7): passkeys on SeoTester?**~~ **Closed by D46** (§12 "M8", owner answer 2026-10-10): option (b), passkeys on. ~~D2 says external sign-in only. CodeCoverage, also external-only, enables passkeys as a second, forge-independent way in. Built without them (the account page excludes them; the Authorization package ships its passkey rights to signed-in users regardless, as in every app; with passkeys off no passkey can be enrolled). Options: (a) keep them off (D2 as written); (b) enable them (`auth.Passkeys = SparkPasskeys.Enabled` and `PasskeyServerDomain = seotester.mintplayer.com`, pinned for life). **Recommendation: (a)** until the owner asks: a passkey's relying-party id cannot be moved later, so turning them on is a decision to make once, deliberately.~~
- **Q-M8a (from M8): SeoTester's entities live in the host, not in `SeoTester.Library`.** §4.1 puts "entities" in the Library, but `IOrganizationOwned` (Organizations), `ISparkShareable` and `IExpiring` (core Abstractions) are in web assemblies, and an entity library refuses ASP.NET (SPARKLIB001, the build fails). Options: (a) entities in the host, the Library keeps what is pure (catalogue JSON, scoring, origin and proof parsing; the check implementations in M9); (b) move the three marker interfaces and `RequiresOrganizationRoleAttribute` into plain-SDK assemblies (a framework move touching every package that declares them). **Recommendation: (a)**: nothing outside SeoTester needs its entities, and (b) moves public types of three packages for a placement preference.
- **Q-M8b (from M8): the critical cap's exact rule.** §7 says "critical failures cap their category (Lighthouse-style)" without numbers. Built: a Critical check whose ratio is below 0.5 (it fails on at least half its pages, or a site-level check fails) holds its category at 49 (an F). Options: (a) as built; (b) cap on any failing page; (c) a cap per severity. **Recommendation: (a)**: one page without a title must not make a 2 000-page site's SEO an F, but a site where most pages lack one cannot score above F. Rules are versioned (`ScoringSnapshot.Algorithm`), so a change marks the trend.
- **Q-M8c (from M8): "a site already held in another organization" = verified there.** Several organizations may add the same site unverified (each tests it). The claim flow (D18) starts only when the proof is found and another organization holds the same **host** verified: that organization's Owners get a join request, and the second organization's site stays unverified. A holder that lost the domain loses its verification at the re-check (3 failed checks in a row, a setting), after which the other can verify. Options: (a) as built; (b) "held" = registered anywhere, verified or not. **Recommendation: (a)**: (b) would let anyone who registers a site first block its real owner.
- **Q-M8d (from M8): how-to-fix texts.** §3.1 asks for "en/nl `howToFix` per platform". Built: a translated explanation (`HowToFixEn`/`HowToFixNl`) plus one code snippet per platform (nginx, Apache, IIS/ASP.NET Core, HTML) that is not translated, since code is the same in both languages. Options: (a) as built; (b) eight translated fields per check. **Recommendation: (a).**
- **Q-M8e (from M8): robots.txt opt-out at StartAudit.** §4.3 lists the robots.txt opt-out among StartAudit's checks; the robots.txt fetch and RFC 9309 matcher belong to the M9 probe (`SiteProbeMessage`). Built: StartAudit does not fetch robots.txt; the probe will stop the run before any page is fetched. Options: (a) as built (the run is created and then ends "opted out"); (b) a synchronous robots.txt fetch inside the action as well. **Recommendation: (a)**: one fetch per run, in the background, through the same SSRF-safe client, and the abuse log still records the attempt.
- **Q-M9a (from M9): what a site that disallows every crawler gets.** RFC 9309 makes the `*` group bind any crawler
  that has no group of its own, and makes an unreachable robots.txt (5xx) a complete disallow. Built: the run ends
  `Blocked` after the robots.txt request, with no score and no further request to the host (a staging site with
  `User-agent: *` / `Disallow: /` cannot be audited at all). Options: (a) as built; (b) still score what needs no page
  of the host (DNS, RDAP, hstspreload; TLS needs a handshake but no path) and mark the crawl "blocked"; (c) for
  **verified** owners, an "ignore robots.txt for this site" profile switch (research §4.2, as Screaming Frog offers),
  logged. **Recommendation: (a)** now, and (c) only if owners ask: D19 makes robots.txt the one opt-out, and (b) still
  touches the host (TLS) of a site that said no.
- **Q-M9b (from M9, S2): where the TLS probe is measured from the Debian image.** The raw TLS 1.0/1.1 hello is built
  byte by byte, so it does not depend on the platform's TLS library, and it matched badssl.com from Windows 11 (§12
  "M9"). The platform handshake (certificate, version, ALPN) on the `aspnet:11.0` image's OpenSSL 3 was not run: Docker
  Desktop was not running on the build machine. Options: (a) measure at the M12 deploy, from the real container on the
  VPS; (b) start Docker locally and run the spike in the image now. **Recommendation: (a)**, as Q-M5a: it is the real
  image on the real network, and nothing before M12 runs the crawler in production. **M12:** the exact commands are in `docs/seotester/deploy.md` §5; the owner runs them at the first deploy and records the result in §12.
- **Q-M10a (from M10): how politely the browser loads a page.** A rendered page asks for its CSS, scripts, fonts and
  images, often 30–100 requests. Built: on the audited host, every browser request holds one of the tier's connections
  (unverified: 1), its **documents** also wait for the tier's interval (1 s), sub-resources do not; other hosts 4 at a
  time; static files fetched once per run. Options: (a) as built; (b) the interval for every request (an unverified
  render of 3 pages × ~50 resources × 2 viewports would take minutes and run into the page budget); (c) no limit on
  sub-resources (a browser's normal behaviour, 6 connections per host). **Recommendation: (a)**: one connection is
  what an unverified host agreed to, and a page's resources are what one visitor causes, once (cached across the sample).
- **Q-M10b (from M10, S3): where the Chromium container is measured.** Image size, RAM per page and `shm_size` on
  Debian, and whether `seo-browser` can be `internal: true` in the real compose network, need Docker; Docker Desktop was
  not running on the build machine and was not started. Everything else of S3 was measured here (escape test over a
  launched browser, over `ConnectAsync` to a run-server and to the container's own `server.mjs`; timings). Options:
  (a) measure at the M12 deploy, as Q-M5a and Q-M9b; (b) start Docker locally and measure the image now.
  **Recommendation: (a)**: the real VPS, network and limits, and nothing renders in production before M12. **M12:** the exact commands are in `docs/seotester/deploy.md` §5; the owner runs them at the first deploy and records the result in §12.
- **Q-M10c (from M10): render and PageSpeed run after the crawl, not beside it.** §4.3 says the stages run
  independently. Built: crawl → (render ∥ PageSpeed) → aggregate. Options: (a) as built; (b) start render and PageSpeed
  after the probe, on a sample taken from the home page's links. **Recommendation: (a)**: the sample is one page per URL
  template of the crawled pages (not just the home page's first links), and on an unverified host a render beside the
  crawl would be a second connection.
- **Q-M10d (from M10): the PDF is printed on demand and kept 7 days.** Built: *Download PDF* prints the report in the
  caller's language once, stores it as an attachment of `RunReport` (expires after 7 days, like page details), and
  notifies with the link; a later request prints it again. Options: (a) as built; (b) print both languages when the run
  completes and keep them with the run (forever, ~90 KB × 2 per run). **Recommendation: (a)**: §4.2 keeps storage
  modest and the run (kept for good) is all a report is printed from.
- **Q-M10e (from M10): the rendering browser's user agent.** Built: a real Chrome phone/desktop string with
  `MintPlayerSeoTester/1.0 (+https://seotester.mintplayer.com/bot)` appended, sent on the wire (as Googlebot's renderer
  does), so a site that serves phones differently is seen as a phone and still knows who asks; robots.txt is matched for
  the token as before. Options: (a) as built; (b) the bare crawler user agent for every request (D29 to the letter; the
  mobile-parity check then misses sites that serve phones by user agent). **Recommendation: (a).**
- **Q-M10f (from M10): what PageSpeed Insights measures.** Built: strategy MOBILE (Google indexes mobile-first),
  category PERFORMANCE only; accessibility comes from our own axe-core run on the rendered page, SEO and best practices
  from our checks. Options: (a) as built; (b) also desktop (a second call per page, twice the quota); (c) all four
  Lighthouse categories (the answer grows ~40 %, the scores duplicate our checks). **Recommendation: (a).**
- ~~**Q-M11a (from M11): an organization switcher.**~~ **Closed by D50** (§12 Round 14): option (b), a generic switcher,
  built (§12 "M11 follow-up"). ~~M11's brief lists one; ng-spark's organizations UI has none. Built:
  no switcher. Every list (sites, audits) shows the rows of all the caller's organizations through the row policy, with
  an *Organization* column to sort and filter by; a new site picks its organization on the form (empty = personal,
  D21); organizations are managed on the generic Organizations pages (members, roles, invitations, leave, delete).
  Options: (a) as built; (b) a switcher in the topbar that scopes every list to one organization (a generic ng-spark
  feature: a "current organization" the row policy reads, carried per request). **Recommendation: (a)**: a user in two
  organizations sees both sites' state at once, which a switcher would hide, and (b) adds per-request state to every
  query for a filter the grids already have.~~
- **Q-M11b (from M11): the catalogue texts are public.** `GET /seotester/catalogue` answers anonymously: the share page
  (anonymous by design, X6) shows a run's results and needs the check titles and how-to-fix. The catalogue holds no user
  or organization data (D32; `CheckDefinitionActions.RowSecurityRationale`). Options: (a) public, as built; (b) only
  signed-in users, and the share page shows check ids. **Recommendation: (a).**
- **Q-M11c (from M11): the badge is the share link's.** F11 asks for a share link and a badge; built as one secret: the
  badge URL carries the site's share token, *Badge* makes a new link (replacing the old one, like *Share link*) and
  shows the Markdown `[![SEO score](…/seotester/badge.svg?site=…&token=…)](share link)`, *Stop sharing* ends both,
  the sharing switch hides both. Options: (a) as built; (b) a separate badge token per site (a second secret to
  store, show and revoke). **Recommendation: (a)**: a badge is a public pointer to the shared report anyway.
- ~~**Q-M11d (from M11): the social card has no image or own title.**~~ **Closed by D51** (§12 Round 14): option (b),
  with the image served through the server, never hot-loaded by the viewer's browser (§12 "M11 follow-up"). ~~F7's
  previewers draw the Google results from the
  page's title and description; the Open Graph / X card uses them too, because the crawl keeps neither `og:title`,
  `og:description`, `og:image` nor `twitter:card` on `PageResult` (the checks judge them, the values are not stored).
  Options: (a) as built; (b) store those four values on `PageResult` (expires with it, 7 days) and draw the real card
  with its image (fetched by the viewer's browser from the audited site). **Recommendation: (b)** if the owner wants the
  card to be exact; (a) keeps the page free of third-party requests. Proceeding with (a) until answered.~~
- ~~**Q-M11e (from the M11 follow-up): `CrawlResumeTests` failed once in a full SeoTester.Tests run.**~~ **Closed by
  D52** (§12 "Q-M11e"): option (c). Cause: every test host is stopped twice at once (the factory and the app's own
  `app.Run()`), and Messaging ran a teardown per call, so one call's drain logged into the host the other had already
  disposed. Fixed in Messaging; the EventLog provider is also filtered out of every test host. ~~Its host restart
  (`SeoTesterHostFixture.RestartHostAsync` → `WebApplicationFactory.DisposeAsync`) stops Messaging, whose `DrainAsync`
  logs its expected "did not drain" warning (the test leaves a page hanging on purpose); the Windows EventLog logger
  provider threw `ObjectDisposedException` on that write, failing the test and its collection cleanup. Passed alone and
  in the next full run (190/190); nothing changed in the follow-up is on that path. Not a load flake by assumption: the
  disposed `EventLog` is a real ordering defect somewhere in host disposal. Options: (a) locate it now (time the
  disposal order, see who disposes the provider first); (b) remove the EventLog provider from the test hosts' logging
  (`ConfigureLogging(l => l.ClearProviders())` or keep console only), which test hosts do not need; (c) both.
  **Recommendation: (c)**: (b) takes the Windows-only provider out of every test host, (a) still says why it was disposed.~~
- ~~**Q-M6d (from M6, X5): the renderers' look.** The letter-grade colours (A, B green; C, D amber; F red), `spark-delta`'s defaults being CodeCoverage's (1 decimal, small), and the gauge (a 48 px ring with a grade badge; no CodeCoverage precedent, not yet seen in a browser). **Recommendation:** keep them and review the gauge in SeoTester's UI (M11).~~ **Reviewed in M11** (§12 "M11"): the colours and the gauge are kept; the grade badge was unstyled and is fixed (ng-bootstrap's `bs-badge`).
- ~~**Q-M12a (from M12): SeoTester has no privacy policy or terms of service.**~~ **Closed by D47** (§12 Round 13): option (a), and the owner asked for the texts to be written: `/privacy` and `/terms` are built. ~~X's OAuth 2.0 app settings require a  privacy policy URL and a terms URL before "Request email from users" can be switched on, and Google's consent screen  asks for a privacy policy link to leave testing mode; the app also processes personal data (accounts, addresses,  audit logs for a year, D37). Nothing in the PRD names these pages. Options: (a) the owner writes the texts (en + nl)  and the app serves them server-rendered at `/privacy` and `/terms`, like `/bot`; (b) link to a MintPlayer-wide policy  elsewhere; (c) leave X without "request email" (its users then confirm an address by mail). **Recommendation: (a)**:  the texts are the owner's (legal), the pages are an hour's work once they exist. Proceeding without them: the runbook  says where the URLs go.~~
- ~~**Q-M12b (from M12): SeoTester shares CodeCoverage's RavenDB server and mail relay.**~~ **Closed by D48** (§12 Round 13): option (b), a separate shared network `mintplayer-backend`. ~~§1 says "joins the existing  RavenDB network". Built: the app joins `code-coverage_coverage-internal` as an external network (database `SeoTester` on  `coverage-raven`, mail through `coverage-smtp`, which then needs `seotester.mintplayer.com` in its allowed sender  domains and a DKIM key, deploy.md §3.2). Consequences: SeoTester starts only when CodeCoverage's stack is up (the deploy  checks), and CodeCoverage's `docker compose down` meets a network still in use (Compose reports it and reuses it on  `up`; to confirm at the first CodeCoverage deploy after SeoTester's, deploy.md §5). Options: (a) as built; (b) a  network both stacks declare `external` (created once by hand), which changes CodeCoverage's compose file; (c) a  RavenDB server and relay of SeoTester's own (a second licence activation, DKIM, PTR and memory on the same VPS).  **Recommendation: (a)**, and (b) if the CodeCoverage deploy check shows a failure.~~
- ~~**Q-M12c (from M12): who receives a monitored site's alerts.**~~ **Closed by D49** (§12 Round 13): option (a), as built. ~~F10 says alerts reach "the user". Built: every member of  the site's organization, Viewers included, each through their per-organization preferences (D44: in-app, mail, push  per organization); a skipped scheduled run is told to the member who set the schedule (or the Admins when that  member left). Options: (a) as built; (b) Admins and Owners only; (c) an opt-in per member and site. **Recommendation:  (a)**: a member who reads the site wants to know it broke, and D44's preferences already let anyone turn a channel off  for that organization.~~
- ~~**Q-D56a (from D56): the Docker spike of the secured RavenDB, before the live migration.**~~ **Closed** (§12
  "Q-D56a", 2026-10-11): option (a), the owner started Docker and the spike ran with the pinned image; everything held,
  the runbook's DRAFT banner is gone. ~~The owner asked for a local
  spike in Docker with the pinned image before the migration steps are written as fact. Docker Desktop was not running
  on the build machine (`docker version`: no daemon), and it was not started. What ran instead (§12 Round 15, "Spike"):
  the same settings on RavenDB **7.1.3** (the `RavenDB.Embedded` server, Windows), unlicensed and with the Developer
  licence: every security property held. Not covered: the **7.1.10 image**, Linux file ownership (the three container
  uids of the runbook §4.3), the exact curl one-shot (the local curl is Schannel-only), **the Community licence** (the
  Developer licence refused activation while the server certificate was valid for more than 4 months; whether Community
  limits certificates is unknown), and the rollback's unsecured restart on a volume that ran secured. Options: (a) the
  owner starts Docker Desktop and the spike of runbook §8 runs here (Community licence from the VPS copy, never
  printed); (b) run §8 on the VPS against a throwaway container and volume; (c) migrate on the 7.1.3 evidence.
  **Recommendation: (a)**, then remove the runbook's DRAFT banner. Until then `docs/code-coverage/raven-tls-migration.md` is a draft and the PR must not be
  merged before the migration (the CodeCoverage deploy refuses without the certificate files anyway).~~
- **Q-D54a: closed 2026-10-11, owner answer "Keep as built"** (option (a): Web Push stays in the app behind the
  push-service allow-list and the SSRF-safe client; no egress proxy). Original question kept below for the record.
- ~~**Q-D54a (from D54): the app container still has a general route out.**~~ With the fetcher, the app makes no request to
  a target, but it still needs the internet for the sign-in providers' token and user-info endpoints and for Web Push
  (the browsers' push services, URLs a browser hands us), and its only route is Traefik's `web`, which compose cannot
  narrow to those hosts. So the guarantee "the app never talks to an audited site" is the code's (no client for targets
  in Production, which refuses `InProcess`), not the network's. Options: (a) as built; (b) put the app on an internal
  network only and give it an egress proxy (e.g. Squid with an allow-list of the four providers and the push services:
  `*.googleapis.com`, `*.push.apple.com`, `*.notify.windows.com`, `updates.push.services.mozilla.com`), which needs
  Traefik to reach the app on that internal network too; (c) send Web Push through the fetcher as well (push endpoints
  are URLs a user's browser supplies, already behind the SSRF-safe client) and keep (a) for OAuth. **Recommendation:
  (c)** first (it moves the last user-supplied URL out of the app at the cost of one more profile), then (b) if the owner
  wants the network to enforce it.


---

## 12. Decision log


### Round 1 (owner, 2026-10-10)

- **D1 (G1): production app.** `apps/SeoTester`, deployed on the same Debian VPS as CodeCoverage with its own compose service and deploy workflow. Treat changes as production changes from the first deploy. *Evidence: owner preference.*
- **D2 (G2): signed-in only.** ~~External logins only~~ External logins (Microsoft, GitHub, X, Google), plus passkeys added after the first external sign-in (**superseded in part by D46**, §12 "M8"); no local passwords, no anonymous audits. *Evidence: owner preference; abuse control, §6.*
- **D3 (G6): one Chromium container in `docker-compose.yml`, beside the app.** It runs on the app's network, with a limited number of renders at a time (queue `MaxConcurrency`). This supersedes ~~separate worker on a locked-down network~~ in §3.2 F11, §4.3 and §6. *Evidence: owner preference.*
  - **Consequence to resolve (round 2):** the app's network today also holds unsecured RavenDB and the postfix relay. Proposal: the app joins **two** networks, `internal` (RavenDB, postfix) and `browser` (Chromium only). Chromium joins only `browser`, which is "the same network as the app" without seeing RavenDB.
  - **Second layer, also to prove in S3:** the app drives Chromium remotely (Playwright `ConnectAsync`) and intercepts **every** request with `RouteAsync`. It fetches each one through the X2 safe client and fulfils it, with service workers blocked and Chromium started with `--host-resolver-rules="MAP * ~NOTFOUND"`. Chromium then never resolves or connects anywhere itself, so the SSRF guard covers browser traffic too. S3 must check what escapes routing: WebSockets, WebRTC, prefetch.
- **D4: the front-end is kept up to date over a WebSocket.** The user sees run progress and completion live. This makes X4 mandatory, not optional. *Evidence: owner preference.*
- **D5 (G10): all of X1–X7 land in Spark libs in this PR.**
  - **X4 is a unification:** streaming queries already exist over WebSocket (`StreamExecuteQuery.cs`), but there is nothing for a single PersistentObject. Design one live-update mechanism that serves both queries and persistent objects, rather than a second channel beside streaming queries.
  - **No backward compatibility:** the libraries are still in preview, so the existing streaming-query API may change shape.
  - X8 stays a suggestion.
  
  *Evidence: owner preference.*

### Round 2 (owner, 2026-10-10)

- **D6: three compose networks.** This resolves D3's open consequence; it is the owner's suggestion and the best option.
  - `ravendb` holds RavenDB and the existing apps; postfix stays where it is today.
  - The SEO app joins `ravendb` and a new `seo-browser` network.
  - **Chromium joins only `seo-browser`**, so it shares a network with the SEO app alone and has no route to RavenDB or postfix.

  The second layer from D3 is **kept**: every Chromium request goes through `RouteAsync` and the X2 safe client, and Chromium's own DNS is disabled. With both layers, a hole in one still doesn't reach the internal services. S3 proves both. Chromium still needs internet egress only if interception is incomplete; S3 decides whether `seo-browser` can be `internal: true` (no egress at all), which is the goal.
- **D7 (G3): `ExternalLoginLinking = ConfirmByEmail`.** *Evidence: owner preference.*
- **D8 (G4): retention.**
  - **Run summaries are kept forever.**
  - **Per-page results expire after 7 days** (`@expires`), superseding ~~30 d~~ in §4.2.
  - **One result per page and site, the latest** (owner decision 2026-10-12, MCP plan M5 follow-ups): a scan's fetch of a
    page replaces the result an earlier scan stored (id `{siteId}/pages/{hash}`), and the 7 days count from that fetch.
    A run's page list holds the pages whose latest result is still its own; what each run measured (scores, per-check
    totals and samples, the delta) stays on the run. *Evidence:* `CrawlPipelineTests.A_new_scan_replaces_each_pages_result_and_counts_only_the_pages_it_fetched`
    (red on per-run ids: 0 pages under the site), `RunPagesTests`. No migration (owner, 2026-10-12: no important data
    in production); per-run results written before it expire within 7 days.
  - Seven days is above the 36 h Community floor. Once pages expire, the report still shows the per-check aggregates and the ≤ 5 sample URLs stored on the summary; the page drill-down says "page details expired".
  
  *Evidence: owner preference.*
- **D9 (G7–G9): extras in scope.**
  - **PageSpeed Insights / CrUX** (the owner provides the API key; an absent key gives "not measured").
  - **PDF export**, rendered server-side by the Chromium container (`page.PdfAsync` of the report page, print CSS).
  - **Suggestions in English and Dutch.**
  - **Not chosen: compare runs / competitor.** Moved to §9. The trend chart and "new / fixed since last run" stay, as part of F7.
  
  *Evidence: owner preference.*

### Round 3 (owner, 2026-10-10)

- **D10 (G5): limits.**
  - **Unverified:** as proposed in §6.
  - **Verified:** the proposed limits, plus "**one entire website per week per signed-in user**". The exact meaning is clarified in round 4 (D14).
  - Every limit is a setting (D11), not a constant.
- **D11: every feature gate and limit is an admin-changeable setting.** The owner wants to enable or disable each of these:
  - scheduled monitoring;
  - intrusive probes;
  - deep-crawl limits;
  - share links and badges.
  
  Only the admin may change them. Defaults:
  - verified-only: scheduled monitoring + alerts, intrusive probes, deep-crawl limits;
  - allowed for unverified sites: share link + badge.
  
  This needs X9 (§5). *Evidence: owner preference.*
- **D12: monitoring schedule is off / weekly / monthly, per site.** A cron sweep every 15 min dispatches due runs. *Evidence: owner preference.*
- **D13: the owner creates the credentials** (Google Cloud API key for PSI/CrUX; OAuth apps for Entra personal + work, GitHub, X OAuth 2.0 with "request email", Google). The plan lists exactly what to register: callback URLs, scopes and config keys (M9 docs). *Evidence: owner preference.*
- **X9 (new framework addition, owner direction): Spark Settings, lean.**
  - **Not** a new authorization design: "who can configure settings" is already covered by the Authorization package (security.json groups and rights on the `Setting` type).
  - **Shape:** a `Setting` entity (key, value, plus description/type for the UI) with its persistent object for the frontend, and a new service + interface that **coalesces the `Setting` value with the `IConfiguration` value of the same key**. A stored setting wins; otherwise the configuration value (appsettings / env / user-secrets) applies; otherwise the code default.
  - **Open for the spike (S11):** implement the coalescing as a plain service (`ISparkSettings.Get<T>(key)`), or as a RavenDB-backed `IConfigurationProvider` added to `IConfiguration`, which would give `IOptionsMonitor` reload for free; and how a change reaches every instance (RavenDB Changes API vs short TTL).
  
  *Evidence: owner, 2026-10-10: "we already have the .net IConfiguration — we could add a new Setting entity with its corresponding PersistentObject … coalesce the Setting-key value with the configuration-key value … the 'who can configure settings' is already supported by the Authorization library".*

### Round 4 (owner, 2026-10-10)

- **D14 (clarifies D10): each user gets one full-site crawl per week, in total.** It applies across all of that user's verified sites, with no page cap except a hard safety ceiling (proposal: 20 000 pages / 6 h, as a setting). All other runs use the normal verified limits from §6. The weekly allowance is a per-user quota (X3), and its window is a setting. *Evidence: owner preference.*
- **D15: a headline score plus category scores.** One 0–100 overall score with an A–F grade, and a score per category. Category weights are data, and overridable as settings (X9). *Evidence: owner preference.*
- **D16: suggestions serve both audiences.**
  - Each suggestion has a plain-language summary for site owners, and an expandable "how to fix" with technical detail and per-platform snippets (nginx, Apache, IIS / ASP.NET Core, and an HTML snippet).
  - The catalogue JSON (§3.1) therefore carries `summary` and `howToFix` (per platform) templates, both in en and nl.
  - A user-level toggle remembers whether "how to fix" opens by default.
  
  *Evidence: owner preference.*
- **D17: abuse protection beyond sign-in and quotas** (§6):
  - a **per-target cool-down** across all users for unverified targets;
  - a public **`/bot` info page with domain opt-out**;
  - **joining a running audit**: a second request for an origin that is already being audited joins that run, and the user is told it is shared.
  - **Not chosen: CAPTCHA/Turnstile.**
  
  *Evidence: owner preference.*

### Round 5 (owner, 2026-10-10)

- **D18: organizations.** This supersedes ~~per-user verification~~ and ~~a per-user `Site` owner~~ in §4.2.
  - A user can create an **Organization** holding several verified websites. **Ownership verification is per organization.**
  - If another user tries to verify a site that already belongs to an organization, the **organization owner gets an email with an acceptance link**. Accepting makes that user a member.
  - **Joining a running audit is allowed for members of the organization** that owns the site.
  - Row security for `Site`, `AuditRun` and `PageResult` is by organization membership. Unverified sites live in the user's personal space; round 6 settles whether that is an implicit personal org (D21).
  
  *Evidence: owner preference.*
- **D19: opt-out is robots.txt only** (`User-agent: MintPlayerSeoTester` / `Disallow: /`), documented on `/bot`. It is already honoured by the crawler, so no form and no block list. *Evidence: owner preference.*
- **D20: alerts reach the user three ways.**
  - **Email** through MailManager, en + nl MJML templates.
  - **In-app notifications** pushed live over the X4 socket.
  - **Web Push** for the PWA.
  
  In-app notifications and Web Push are new Spark framework features (X10). *Evidence: owner preference.*

### Round 6 (owner, 2026-10-10)

- **D21: every user gets an implicit personal organization.** Unverified sites and their runs live there, so one row-security rule (membership) covers everything. *Evidence: owner preference.*
- **D22: Organizations are a Spark framework feature (X11),** not app code.
  - It is a reusable package with: an organization entity; membership with roles; the invite/join-request mail with an acceptance link; a row-filter helper ("current user is a member of the owning org, with role ≥ R").
  - The SEO app is the first consumer; CodeCoverage could adopt it later.
  - Who manages organizations at the framework level still comes from the Authorization package. Org roles are data-level, not security.json groups.
  
  *Evidence: owner preference.*
- **D23: organization roles are Owner, Admin, Member and Viewer.**
  - **Owner:** everything, including deleting the org and transferring ownership.
  - **Admin:** manages sites, members and join requests.
  - **Member:** runs audits, joins running audits, edits site settings.
  - **Viewer:** reads reports only.
  
  *Evidence: owner preference.*
- **D24: live-update defaults (X4).**
  - **Opt-in** per type and query in the model JSON, not live by default.
  - **Patches** instead of full pages/objects.
  - **App data streams (`IAsyncEnumerable`) are kept** next to the Changes-API-driven updates, for non-RavenDB data such as the Fleet and DemoApp demos.
  - Both **PersistentObject updates and query updates** go over the one socket.
  
  *Evidence: owner preference.*

### Round 7 (owner, 2026-10-10)

- **D25: the Organizations package (X11) depends on MintPlayer.Spark.Authorization only,** where `SparkUser` lives. There is no OpenID Connect involvement, no org claims and no SparkId change. *Evidence: owner: "The SparkUser lives in the Authorization package, so I think that should be the dependency".*
- **D26: deletion is cascade plus export.**
  - Deleting an org deletes its sites, runs, pages and pending invites.
  - Leaving an org removes access only. The last Owner cannot leave without transferring ownership.
  - Deleting an account deletes the user's personal org and memberships.
  - Users can export their data as JSON.
  
  *Evidence: owner preference.*
- **D27: hostname `seotester.mintplayer.com`,** superseding ~~seo.mintplayer.com~~. The bot page is `https://seotester.mintplayer.com/bot`. The app/folder name and the bot User-Agent token are settled in round 8 (D29). *Evidence: owner preference.*
- **D28: an operator dashboard for the admin** with:
  - usage stats: audits per day, quota and cool-down hits, Chromium and PSI usage;
  - an **abuse log**: who audited what and when, searchable;
  - **websites**: all registered sites/orgs;
  - **database size**: per collection and total, from RavenDB stats.
  
  It is admin-only through security.json. **Not chosen: a queue/dead-letter view** (X8 stays out, moved to §9). *Evidence: owner preference.*

### Round 8 (owner, 2026-10-10)

- **D29: the app is named SeoTester.**
  - Projects: `apps/SeoTester` with `SeoTester`, `SeoTester.Library` and `SeoTester.Tests`. Earlier "SeoAnalyzer" text in this document has been renamed.
  - User-Agent: `MintPlayerSeoTester/1.0 (+https://seotester.mintplayer.com/bot)`; robots.txt token `MintPlayerSeoTester`.
  - Verification TXT record: `_seotester.<host>`.
  
  *Evidence: owner preference.*
- **D30: Web Push and in-app notifications (X10) are generic Spark features.**
  - VAPID keys come from configuration.
  - Push subscriptions are stored per device on the `SparkUser`, with an opt-in prompt UI.
  - Push and notification-click handlers are added through `provideSparkServiceWorker()`.
  - An in-app notification list with an unread count updates over the live socket.
  
  *Evidence: owner preference.*
- **D31: org Owners and Admins create and revoke share links and badges; anyone with the link views** the read-only latest report. A setting can disable sharing globally. *Evidence: owner preference.*
- **D32: the check catalogue is fully editable at runtime.** This supersedes ~~"catalogue = embedded JSON, only weights overridable"~~ in §3.1.
  - Catalogue entries are RavenDB documents edited as persistent objects by the admin: texts and how-to-fix in en/nl, weight, severity, category, enabled.
  - They are **seeded** from the embedded JSON shipped with the app: new checks are added and **admin edits are never overwritten**. A "reset to shipped" action restores one entry.
  - The check *implementations* stay C# classes keyed by check id; a catalogue entry without an implementation is shown as unavailable.
  - To keep history interpretable, each `AuditRun` **snapshots the weights and severities it used** (they are small), and `ScoringVersion` becomes a hash of that snapshot. The trend chart marks runs whose snapshot differs from the previous run's.
  
  *Evidence: owner preference.*

### Round 9 (owner, 2026-10-10)

- **D33: the live-update change source is configurable, `Spark:Live:Source` = `Actions` | `Changes` | `Both`, default `Both`.**
  - **The owner's concern:** the Changes API might be another database subscription, and Community allows only 3.
  - **Clarified with evidence:** Changes API connections are **not** data subscriptions. The licence-verification probe below watched a document, a collection and all documents with **no** subscription created. The RavenDB client multiplexes every watch for a database over **one** Changes connection per process. Per-document watches are cheap; nothing in the design watches every document.
  - **The owner's principle stands:** a library must not lock downstream developers into our choice, hence the configuration key.
  - **Both:** the source merges in-process actions events (instant) with Changes notifications (writes from other instances or tools), de-duplicated by document id + change vector inside the throttle window.
  
  *Evidence: owner: "these are libraries, and they should not restrict what other developers can do … make this configurable through the .NET configuration"; "Both by default".*
- **D34: low-code for downstream developers.** The common case needs only `"live": true` in the model JSON:
  - no notifier calls;
  - no socket code;
  - ng-spark's generic pages and grids subscribe by themselves.
  
  `ISparkLiveNotifier` and `livePersistentObject()` / `liveQuery()` exist for custom pages and non-Spark writers under `Source = Actions`. *Evidence: owner: "make it low-code for app-developers/downstream consumers".*
- **D35: `spark-po-detail` auto-refreshes when the type is live.**
  - An untouched edit form adopts the new version.
  - An edited form shows a banner and the existing three-way merge (`po-edit/src/conflict-merge.ts`, `SparkPoConflictDialogComponent`).
  - 409 stays the server-side guarantee.
  
  *Evidence: owner: "I tend to say yes", subject to D33.*
- **D36: one model-JSON flag, `"live": true`, on both queries and types.** It replaces `isStreamingQuery` and needs a new `schemas/v*` tag. Default limits, all configurable: ≤ 1 push per subscription per 500 ms (latest wins), ≤ 50 subscriptions per socket, ≤ 5 sockets per user. *Evidence: owner preference.*
- **D37: the abuse log (`AuditLogEntry`) expires after 1 year; the unverified-target cool-down is 6 h across all users.** Both are settings. *Evidence: owner preference.*

### Licence verification (2026-10-10): ✅ the Changes API works on the production Community licence

The X4 design depends on the RavenDB Changes API, so this was measured on the **production server** (coverage.mintplayer.com VPS, `ravendb/ravendb:7.1.10-ubuntu.22.04-x64`, `/license/status` → `Type = Community`, not expired).

**Method:**
1. A throwaway database `ChangesProbe` was created.
2. A temporary `node:24-alpine` container on `code-coverage_coverage-internal` opened `ws://coverage-raven:8080/databases/ChangesProbe/changes` with `watch-doc probes/1`, `watch-collection Probes` and `watch-docs`, then wrote documents over HTTP.

**Result:**
- All three subscriptions were confirmed.
- Every write produced a `DocumentChange` notification, **4–22 ms** after the PUT, with `Type = Put`, `Id`, `CollectionName` and `ChangeVector`. A write to another collection arrived through `watch-docs`.
- The connection delivered **one** notification per change even with three overlapping watches.

**Revert, verified:**
- The database was hard-deleted (`GET /databases?name=ChangesProbe` → 404).
- The script, log and `node:24-alpine` image were removed from the host.
- All three compose services were still healthy afterwards.

**Licence limits that do apply to this PRD** (ravendb.net/buy, Community footnotes, read 2026-10-10):
- **Subscriptions:** 3 per database and 15 per cluster; no revision subscriptions. This is why X4 uses the Changes API, not subscriptions.
- **Revisions:** at most 2 kept, maximum age 45 days, and no default policy. Run history must therefore be documents, not revisions; the D8 summaries already are.
- **Expiration/refresh frequency:** cannot be set shorter than every 36 hours. So `@expires` documents linger up to 36 h past their time, and the row policy must hide expired-but-not-yet-deleted documents (S6).
- **Custom sorters and analyzers:** 1 of each per database.
- The Changes API is not listed among the Community limits, and the measurement above confirms it.

### Findings from the second investigation (2026-10-10)

- **X4 live updates.** Full report: `docs/seo_analyzer_research_live_updates.md`.
  - **Proposal:**
    - One multiplexed socket at `GET|CONNECT /spark/live`, with `subscribe` / `update` / `unsubscribe` for query results (carrying the full `ExecuteQueryRequest`, including parent, paging, sort and search) and for single persistent objects.
    - Updates are driven by the **RavenDB Changes API** (`ForDocument`, `ForIndex`, `ForDocumentsInCollection`). The app's `IAsyncEnumerable` remains for data that isn't in RavenDB.
    - Every push re-runs the normal HTTP read path, so row security, redaction and attribute rights apply per push. An object that becomes unreadable is reported as `gone`, the same as a delete (#453 parity).
    - No frame is sent when the visible result didn't change.
    - Load control: throttle to 250–500 ms with the latest state winning, a bounded per-socket queue, and the socket lifetime capped at the login ticket.
    - ng-spark gets a shared `SparkLiveService` with `livePersistentObject` / `liveQuery` signals. `spark-po-edit` reuses the existing three-way merge and conflict dialog when the user is editing.
    - `isStreamingQuery` and `StreamingQueryArgs` are replaced (no backward compatibility).
  - **Existing defects found in today's streaming queries, to fix in this PR:**
    1. Removed rows are never signalled (`StreamingDiffEngine.cs:399`).
    2. Added rows never appear client-side (`spark-query-list.component.ts:419-430`).
    3. Access denied closes with 1011, so the client retries 10× for nothing.
    4. A revoked right closes with 1000, so the grid silently keeps stale rows.
    5. The comment at `StreamingQueryExecutor.cs:81-83` claims RavenDB is watched; it isn't.
  - **Measurements:** L1–L8 in the report. L1, whether the Changes API works on the **Community** licence, gates the design and becomes part of S8.
- **X9 settings.** Full report: `docs/seo_analyzer_research_settings.md`. Recommendation:
  - **Package:** a new opt-in package `libs/settings/MintPlayer.Spark.Settings` (alias `settings`), built like the IdentityProvider library. It is not core, because core ships no model and a `Setting` type would land in every app.
  - **Service, not a provider:** a plain `ISparkSettings` service rather than an `IConfigurationProvider`. A RavenDB provider would let anyone with Edit on `Setting` override connection strings and OAuth secrets, and it has a startup-order gap.
  - **Declared in code:** settings are `SparkSetting<T>(Key, Default, Description)`, so keys are never typed by the admin. Values are stored as strings and parsed with the `ConfigurationBinder` converter, so stored and configured values parse identically.
  - **Seeding:** a startup seeder upserts one `Settings/{Key}` document per declared key without touching `Value`.
  - **Rights:** only `QueryReadEdit/Setting`, so there is no New or Delete. The app binds `settings:administrators`.
  - **Cache:** a 30 s TTL, cleared on the saving instance. Changes-API invalidation can be added later behind the same API.
  - **No secrets in settings.**

### Spikes S4, S5, S10, S12 (2026-10-10, research only)

Full report: `docs/seo_analyzer_spikes_S4_S5_S10_S12.md`. Real responses are in `docs/seo_analyzer_spike_fixtures/`; M8–M10 move the ones they use into `SeoTester.Tests`.

- **S4 RDAP:**
  - Expiry available through the IANA bootstrap: com, net, org, dev, app, brussels, vlaanderen.
  - **.nl:** RDAP exists but publishes no expiry.
  - **.be and .eu:** no RDAP; WHOIS publishes no expiry either.
  - **.io:** works at `rdap.identitydigital.services`, which is **not** in the bootstrap.

  **Decision:** RDAP through the bootstrap only. Show "expiry not published by the registry" when it is missing, and let the org enter a renewal date manually; the domain-expiry alert then uses that date. No WHOIS scraping: it adds nothing, and the DNS Belgium terms forbid automated extraction. No hard-coded `.io` endpoint.
- **S5 PSI:**
  - Unauthenticated calls return **429 with `quota_limit_value: "0"`**, so an API key is mandatory and no real fixture exists yet.
  - Lighthouse is 13.5.0. Its 17 `*-insight` audits have weight 0.
  - Score paths are `lighthouseResult.categories.{performance,accessibility,best-practices,seo}.score`. CrUX field data is `loadingExperience` / `originLoadingExperience`, both optional.
  - CrUX API quota: 150 queries/min per project.

  **Decision:** no key gives "not configured". PSI runs as a background job with a 120 s timeout, 429 means retry later, and we store only scores, metrics and insights. **Blocked until the owner's key exists:** a real fixture for the parser tests in M10.
- **S10 hstspreload.org:**
  - `status` returns `{name, status: unknown|pending|preloaded|rejected, bulk, preloadedDomain}`. `preloadedDomain` can be a parent domain or a TLD.
  - `preloadable` / `removable` return `{errors[], warnings[]}` of `{code, summary, message}`.

  **Decision:** key on `code`; cache for 1 day.
- **S12 Web Push:**
  - The `WebPush` NuGet package is **rejected**: it uses the legacy `aesgcm` encoding, not RFC 8291 `aes128gcm`, pulls in BouncyCastle, and is MPL-2.0.
  - `Lib.Net.Http.WebPush` 3.3.1 is acceptable: MIT and aes128gcm, but it targets net6.0 at most and was last released in March 2025.
  - A hand-rolled implementation is about 300 lines on built-in `ECDiffieHellman`/`HKDF`/`AesGcm`/`ECDsa`, verifiable against the RFC 8291 Appendix A test vectors.
  - Apple: iOS 16.4+ and an installed web app only; subscribe from a user gesture; no silent pushes.
  - Responses: 404/410 deletes the subscription; 429/5xx means back off.

  **Recommendation: hand-rolled**, pending the owner's confirmation (asked with the milestone questions).

### Spike S1 (2026-10-10)

**Answer: yes.** A `SocketsHttpHandler.ConnectCallback` guard blocks every listed case, closes DNS rebinding and survives redirects. Its overhead is a few µs per pooled request. Built as X2 and adopted by the IdP in M1, commit `72c84933`.

- **Where:**
  - `libs/spark/MintPlayer.Spark/Http/`: `SafeHttpOptions`, `SafeHttpPolicy`, `SafeHttpConnector`, `SafeHttpMessageHandler`, `SafeHttpBlockedException`, `SafeHttpServiceCollectionExtensions`.
  - `Extensions/SparkBuilderSafeHttpExtensions.cs`.
  - The library sits in core because the IdP, SeoTester and any app that fetches a user's URL all need it, and it adds no dependency.
- **API:**
  - `spark.AddSafeHttp(o => …)` registers the named client `SparkBuilderSafeHttpExtensions.ClientName` (`"Spark.SafeHttp"`). Configuration comes from `Spark:SafeHttp` first, then code.
  - `services.AddSafeHttpClient(name, section, configure)` registers a client with limits of its own.
  - Defaults: ports 80/443, 10 MiB on the wire, 50 MiB decompressed, 30 s total, 10 s connect, 5 redirects, no extra networks.
  - `AllowedNetworks`/`BlockedNetworks` take CIDRs. Blocked wins, and is also checked against embedded IPv4.
  - ~~The configuration binder **appends** to the list defaults, so configuration adds ports and networks but never removes 80/443.~~ Superseded by D39 (Round 10): a list the section names replaces its default.
- **Mechanism:**
  - The connect callback resolves once, keeps only allowed addresses, and dials one of **those**. A mixed answer (public + private) dials only the public one.
  - IP literals are vetted without a lookup. That includes decimal forms such as `http://2130706434/`, which `Uri` normalises.
  - Only `2000::/3` passes for IPv6, minus `2001::/23` (except Teredo, which is unwrapped), `2001:db8::/32` and `3fff::/20`.
  - NAT64 `64:ff9b::/96`, 6to4 `2002::/16`, Teredo `2001::/32` (server, and client inverted) and IPv4-mapped addresses are judged by their IPv4 address.
  - `UseProxy=false`, `UseCookies=false`, `AllowAutoRedirect=false`, `AutomaticDecompression=None`.
  - The outer handler follows 301/302/303/307/308 itself, re-checking scheme and port per hop; the address is checked again at connect. 303, and POST under 301/302, become GET.
  - Across origins it drops `Authorization` and `Cookie`.
  - It decompresses gzip/deflate/br itself, with a wire-byte cap (also checked early against `Content-Length`) and a decompressed-byte cap, both enforced while streaming.
  - A refusal raised inside the sockets handler is re-thrown unwrapped.
  - `SafeHttpBlockedException : HttpRequestException` carries a `Reason` and a fixed message that never names an address (asserted in tests).
- **Tests, red→green:**
  - The red run had the checks stubbed: `IsAddressAllowed` returned true, `CheckUri` did nothing, and the caps and the redirect limit were off. Result: **76 of 99 failing**. The green run: **99/99 passing**.
  - `tests/MintPlayer.Spark.Tests/Http/SafeHttpPolicyTests.cs`:
    - 43 refused addresses: every IPv4/IPv6 range above, plus mapped/NAT64/6to4/Teredo forms of 127.0.0.1, 10/8, 192.168/16 and 169.254.169.254;
    - 10 allowed;
    - allow and deny CIDRs;
    - scheme and port.
  - `SafeHttpConnectorTests.cs`, with a fake resolver and a fake dialler:
    - **DNS rebinding:** the resolver answers public, then private. The connector resolves once and dials the public address; the next connection is refused.
    - a mixed answer dials only the public address;
    - IP literals are not looked up;
    - no address in the message;
    - resolution failure;
    - the port is checked before resolving;
    - explicit loopback allowance.
  - `SafeHttpClientTests.cs`, end to end against Kestrel on 127.0.0.1, which is allowed only through the explicit `AllowedNetworks = ["127.0.0.1/32"]` plus the port:
    - loopback is refused by default, by literal and by name;
    - **redirects to `169.254.169.254`, `10.0.0.1`, `[::1]`, `[::ffff:169.254.169.254]`, decimal 127.0.0.2, `ftp:`, `file:` and port 6379 are refused**;
    - the redirect chain: 5 followed, the 6th refused, `MaxRedirects=0` follows none;
    - byte cap, chunked and with `Content-Length`;
    - a gzip bomb (64 MiB of zeros, small on the wire) is refused at the 1 MiB decompressed cap;
    - gzip is decompressed;
    - `Authorization` is dropped across origins;
    - 303 → GET and 307 keeps the POST body;
    - handler flags;
    - timeout.
  - IdP: `tests/MintPlayer.Spark.Tests/IdentityProvider/OidcClientRequestsSafeHttpTests.cs`, **7/7 red** before the adoption and green after. It covers internal JWKS and back-channel URIs refused, the per-client limits, and loopback opened only by configuration. `OidcSessionLogoutTests` and `OidcClientAuthenticatorTests` still pass: 127/127 in the combined run.
- **Overhead** (scratch console, Release, local Kestrel on 127.0.0.1, 2 KiB body, median of 5 alternating rounds, two runs):
  - keep-alive: plain 163–180 µs, safe 170–203 µs, so **+7 to +23 µs per request** (4–13 %), from the outer handler and the stream wrapping;
  - new connection per request: plain 626–710 µs, safe 563–610 µs, so **no measurable cost**. Vetting is sub-µs, and the safe dialler's IPv4 socket with `NoDelay` beats the default dual-mode connect.
  - DNS was excluded (IP literal). The safe client does the same single lookup the default handler would.
- **IdP adoption (X2):**
  - `OidcClientKeys` (jwks_uri) and `OidcSessionStore.SendBackChannelAsync` (back-channel logout) use the safe clients `SparkIdentityProviderExtensions.JwksHttpClient` and `BackChannelLogoutHttpClient`, with their names unchanged so test stubs still apply.
  - Limits: JWKS 10 s / 1 MiB / 5 redirects. Back-channel 5 s / 64 KiB / ~~**no redirects**, because a redirect would re-send the logout token to an unregistered URI~~ superseded by D40 (Round 10): ≤ 3 redirects, all within the registered URI's origin.
  - There is no implicit Development exception. No app, demo or E2E test registers a `jwks_uri` or back-channel URI on loopback (searched `apps/`, `tests/`). A local relying party is opened with `Spark:IdentityProvider:ClientRequests:AllowedNetworks`/`AllowedPorts` (README table).
- **Other outbound fetches in `libs/` and `apps/`:** none fetches a user-supplied URL, so none was adopted.
  - The IdP's `SectorIdentifierUri` is never fetched (host only, `OidcSubjects.cs:42`), `request_uri` by reference is unsupported (`Discovery.cs:78-80`), and front-channel logout URIs are loaded by the browser.
  - These all use config-set or fixed hosts: Authorization's introspection (`SparkIntrospectionHandler.cs:95`) and the external-login backchannels, Replication (`ReplicationHttpClientProvider.cs:69`), GitHub webhooks (`GitHubInstallationService.cs:151`) and the Smee/dev-tunnel clients, CodeCoverage's GitHub calls (fixed `api.github.com`/`raw.githubusercontent.com`/`github.com` hosts, the repository name only in the path), and HR's Fleet proxy (`FleetCarsProxyEndpoint.cs:39`).
  - `MintPlayer.Spark.Client` is a client library whose base URL is its caller's.

### Spike S6 (2026-10-10)

**Answer: yes, with the read filter as the guarantee.** Per-document `@expires` works on the Community frequency; an expired document lingers in storage up to 36 h but is invisible to every Spark read from the instant it expires. Built as X1 in M2, commit `6df64fb3`. Guide: `docs/guide-expiration.md`.

- **Where:**
  - `libs/spark/MintPlayer.Spark.Abstractions/IExpiring.cs` (`DateTime? ExpiresAtUtc`), `EntityTypeDefinition.ExpiresAfter` (model JSON `"expiresAfter"`), `SparkModuleRegistry.RequireDocumentExpiration(name)`.
  - `libs/spark/MintPlayer.Spark/Expiration/`: `ExpirationStamper` (store-level `OnBeforeStore`), `ExpirationRowPolicy`, `ExpirationStartup` (checks + the one configurator), `SparkExpiration` (`MinimumDeleteFrequency`, `ValidateDeleteFrequency`), `SparkExpirationOptions` (`Spark:Expiration`); `Extensions/SparkBuilderExpirationExtensions.cs` (`AddExpiration`).
  - Core, not a package: Messaging, Mail, Moderation and the IdP all need the configurator, and Messaging references only the abstractions, which is why the requirement is declared on the registry and executed by `UseSpark()`.
- **Design decisions** (each with its evidence):
  - **Always on, no opt-in needed.** The row policy and the stamper are registered by `AddSpark` and act only on `IExpiring` types. An opt-in that could be forgotten would leave expired rows readable, the failure mode X1 exists to prevent. `AddExpiration()` remains for options. *Evidence: `ExpirationTests` run without any `AddExpiration()` call.*
  - **The property is the source of truth, `@expires` mirrors it.** A row filter is a LINQ predicate rebound by member name (`RowFilterExpressions.cs`), so it cannot read metadata; a field it can read must exist on the document. The model default applies to a new document only (no `@change-vector` in its metadata), so code can clear it later to keep a document. *Evidence: `Clearing_the_expiry_of_a_stored_document_makes_it_permanent`, `An_edit_through_the_pipeline_keeps_the_expiry`.*
  - **Store-level `OnBeforeStore`**, installed by `UseSpark()` on the DI store (the test host's store included). It stamps creates through the pipeline and raw-session writes alike. Patches, bulk inserts and ETL raise no event and are documented as not covered.
  - **`!(x.ExpiresAtUtc <= now)`, not `x.ExpiresAtUtc == null || x.ExpiresAtUtc > now`.** With the naive filter swapped in, `Documents_with_no_expiry_or_no_field_at_all_stay_visible` fails: the document without the field disappears from the list (expected 2 ids, found 1). An absent field matches no `== null` (`AbsentVersusNullFieldTests`). The negated range matches null, absent and future alike on Corax.
  - **System context included** (`BypassInSystemContext = false`): background work through `IDatabaseAccess` must not act on a document that is gone in every sense but storage.
  - **The 36 h floor is refused below on every licence**, not only when `/license/status` says Community. With the check stubbed, `AddExpiration(o => o.DeleteFrequency = 1 h)` was **accepted by the Developer licence** the tests run on, which is exactly how CI would miss it; production Community would reject it and lose the server (Messaging's measured note of 2026-09-07, now on `SparkExpiration.MinimumDeleteFrequency`).
  - **One configurator.** Messaging and the IdP each sent `ConfigureExpirationOperation` with their own pinned 36 h ("they must agree or whichever starts last wins"). Both now declare `RequireDocumentExpiration`, and so do MailManager (`SparkMailDeliveries` retention) and Moderation (daily counters, IP observations), which wrote `@expires` but relied on Messaging happening to enable expiration. The database is not touched when nothing needs it (`Nothing_that_needs_expiration_leaves_the_database_record_alone`).
- **Visibility window:** 0 for Spark reads (the filter compares with the request's `TimeProvider` time). Storage lingers until the next sweep, at most `DeleteFrequency` (36 h) after `@expires`. Raw session queries are not filtered (documented on `IExpiring` and in the guide).
- **Tests, red→green** (`tests/MintPlayer.Spark.Tests/Expiration/`):
  - Red: stamper not installed, filter returning null, clamp and startup checks off. **14 of 20 `ExpirationTests` failing.** Green: **20/20**, plus `ExpiresAfterModelTests` (sync keeps a hand-written `expiresAfter`) and the storage spike: 21/21.
  - Stamping: pipeline create (7 d from the model), raw session store, explicit 2 d and 14 d overriding the model's 7 d, a `Local` time stored as UTC, a type without `expiresAfter` stamped only when code sets it, clearing, an edit keeping the expiry.
  - Read filter: an expired-but-stored document is **404 on `/spark/po/load`**, missing from the list query, from a custom sub-query on its parent and from that sub-query's distinct values. Each test asserts the document is **still stored**, so the filter, not a sweep, hid it.
  - Database: 129 600 s after startup; 48 h from options is used; 1 h refuses startup and nothing is written; a library's `RequireDocumentExpiration` alone configures it; nothing configures nothing.
  - Startup refusals: `expiresAfter` on a non-`IExpiring` type, an explicitly implemented `ExpiresAtUtc`.
  - Still green: SoftDeleteTests, RevisionsModelTests, SparkSchemaGeneratorTests, SparkModuleRegistryTests, MailEndpointQueryTests, ModerationToolsTests, OidcSessionLogoutTests (88/88 in the combined run).
- **Storage** (`ExpirationStorageSpikeTests`, Developer licence, RavenDB 7.2 test server). A `PageResult`-shaped document: run id, URL, status, depth, timings, bytes, title/description/h1/canonical snapshot, `@expires` and **20 findings** (`checkId`, `status`, `params` on half of them):
  - one document: **1 999 B** actual (`/docs/size`);
  - 2 000 documents: **4 169 728 B** of documents, **2 085 B/doc** including storage overhead (`GetDetailedCollectionStatisticsOperation`);
  - **100-page run ≈ 204 KiB; 2 000-page run ≈ 4.0 MiB.** Indexes over the collection come on top.
  - With a 7-day expiry and a sweep at most every 36 h, a page result is stored at most **8.5 days**. One 2 000-page run per site per day therefore holds at most ~8.5 × 4.0 ≈ **34 MiB** per site at steady state; a weekly 100-page run, at most two runs ≈ 0.4 MiB. This fits the "storage stays modest" budget of §4.2 (1–3 KB per page confirmed).
- **Schemas:** `model.schema.json` gains `expiresAfter` (generated from `EntityTypeDefinition`). The set is untracked and versioned by tag; CI mints the next `schemas/v{n}` on merge to master (`docs/guide-json-schemas.md`). No tag is created by hand.
- ~~**Open:** `ExpiresAtUtc` is an ordinary model attribute after synchronization; an app that must keep users from editing it marks it read-only or `[IgnoreProperty]` (guide). Whether the framework should own it on pipeline writes is Q-S6a (§11).~~ Closed by D41 (Round 10): the framework owns it on pipeline writes.

### Round 10 (owner, 2026-10-10)

- **D38: Web Push is hand-rolled, in its own library in this solution.** RFC 8291 `aes128gcm` content encryption and RFC 8292 VAPID, on built-in .NET crypto (`ECDiffieHellman`, `HKDF`, `AesGcm`, `ECDsa`), verified byte-for-byte against the RFC 8291 Appendix A test vectors. No `WebPush` / `Lib.Net.Http.WebPush` package. Built in M6 (X10); research in `docs/seo_analyzer_spikes_S4_S5_S10_S12.md` §S12. *Evidence: owner preference.*
- **D39: SafeHttp configuration may remove the default ports 80/443.** A list the configuration section names (`AllowedPorts`, `AllowedNetworks`, `BlockedNetworks`) **replaces** the list beneath it instead of appending; a list it does not name, or no section, keeps the secure default. Code that runs after the section (`configure`) still adds with `AllowedPorts.Add(…)`. Built in `SafeHttpServiceCollectionExtensions.Bind` (reload-on-change kept through a `ConfigurationChangeTokenSource`). Consequence: the IdP's `ClientRequests:AllowedPorts: [5001]` now allows 5001 **only** (README updated).
  - *Evidence: owner preference.* Red→green: `tests/MintPlayer.Spark.Tests/Http/SafeHttpConfigurationTests.cs` (5 tests: configured ports replace the defaults; a removed port 80 is refused with `Reason = Port`; no section keeps 80/443; a configured list replaces a code default registered beneath it while unnamed lists keep theirs; code after the section adds to the configured list) and `OidcClientRequestsSafeHttpTests.Loopback_…` (`AllowedPorts` = `[5001]`). With the old appending bind restored: 4 of 5 red (all but "no section"), plus the IdP test.
- **D40: the IdP's back-channel logout follows redirects only within the registered URI's origin.** New `SafeHttpOptions.RedirectsWithinOriginOnly`: a redirect whose scheme, host or port differs from the URI first requested is refused with `SafeHttpBlockReason.CrossOriginRedirect` before the next hop is sent; every same-origin hop is still checked (scheme, port, address at connect) and `MaxRedirects` still applies. The back-channel client sets it with `MaxRedirects = 3` (was 0); the JWKS client is unchanged (5 redirects, any origin).
  - *Evidence: owner preference; reason: following a cross-origin redirect would resend the logout token to an unregistered URI.* Red→green: `SafeHttpClientTests.Within_origin_only_*` (cross-origin by host, port and scheme refused — red: followed, or a connect error; same-origin followed; the limit respected) and `tests/MintPlayer.Spark.Tests/IdentityProvider/OidcBackChannelRedirectTests.cs` through the IdP's registered client against Kestrel (a 307 on the same origin delivers `logout_token`; a 307 to `localhost` on the same port is refused and the landing endpoint receives nothing; 3 redirects followed, the 4th refused). Red with the check stubbed and `MaxRedirects = 0`: 3/3 IdP tests and 3/3 cross-origin cases failed.
- **D41: `IExpiring.ExpiresAtUtc` is framework-protected, like SoftDelete's fields.** Core `Expiration/ExpirationInterceptor.cs` (an `IBeforeSave` registered by `AddSpark`, so before any app interceptor): an edit, revert or restore keeps the stored value; a create starts with none, so the stamper applies `expiresAfter`. Only server code changes it: a raw-session write, or an app before-save interceptor (which runs after the framework's). Closes Q-S6a (§11). Guide: `docs/guide-expiration.md`.
  - *Evidence: owner preference.* Red→green in `ExpirationTests`: `A_client_edit_cannot_change_the_expiry` (extend to 2099 and clear to null; the rest of the edit is applied), `A_client_create_cannot_choose_the_expiry` (the posted 2099 is ignored, `expiresAfter` 7 d applies) — **3 of 3 red** without the interceptor (stored 2099 / null / 2099); `Server_code_after_the_framework_still_sets_the_expiry` green in both.
- **Part A run:** red 15 of 65 (the cases above); green **181/181** over `SafeHttp*`, `OidcBackChannelRedirectTests`, `OidcClientRequestsSafeHttpTests`, `OidcSessionLogoutTests`, `Expiration*` and `SoftDeleteTests`.

### Spike S11 (2026-10-10)

**Answer: yes.** A stored value overrides configuration and clearing or deleting it falls back; the cache propagates within its TTL and at once on the saving instance; who may edit is exactly the group the application binds to the library's slot. Built as X9 (and X7 beside it) in M3.

- **Where (X9):** `libs/settings/MintPlayer.Spark.Settings/` (alias `settings`, `11.0.0-preview.104`, the version core carries on this branch, as new packages start at core's current preview), modelled on the IdP's library layers:
  - `SparkSetting.cs`: `SparkSetting<T>(Key, Default, Description)` (a record; also `ISparkSetting<T>` / untyped `ISparkSetting`); parsing and formatting with the type's `TypeConverter` in invariant culture, which is what `ConfigurationBinder` uses. Keys match `^[A-Za-z0-9][A-Za-z0-9:._-]*$` (they become the id `Settings/{Key}`).
  - `SparkSettingsCatalog` (declared keys, case-insensitive like configuration; a malformed or twice-declared key refuses registration), `ISparkSettings` + `Services/SparkSettings.cs`, `Services/SettingsSeeder.cs`, `Interceptors/SettingInterceptors.cs`, `Actions/SettingActions.cs` (`Custom.Settings`), `SparkSettingsExtensions.AddSettings(params Type[])`, `SparkSettingsOptions` (`Spark:Settings:CacheDuration`, 30 s).
  - `App_Data/Model/Setting.json` (ids stamped by `npm run stamp:library-model-ids`, verified by SPARK045 at build): `Key`, `Default`, `Type`, `Description` read-only (the schema write gate refuses them), `Value` the only writable attribute; `security.json` grants `QueryReadEdit/Setting` to `settings:administrators`; `programUnits.json` one unit (alias `settings`, order 95); `translations.json` en/fr/nl. README packed. Wired into `MintPlayer.Spark.slnx` and `tests/MintPlayer.Spark.Tests` (Nx infers the project through `@nx/dotnet`, as for every sibling library; none has a `project.json`). Not in `AllFeatures` (opt-in, like IdP and Moderation).
- **Design decisions** (each with its evidence):
  - **Snapshot by id prefix, not a query.** `LoadStartingWithAsync("Settings/")` reads documents directly, so a value saved a moment ago is never missed behind a stale auto-index; a few dozen documents per load. *Evidence: the saving-instance test reads the new value right after the HTTP save.*
  - **Read never throws for data.** A stored or configured value that does not parse is skipped with a warning and the next source applies; an undeclared `SparkSetting` passed to `GetAsync` is a programming error and throws.
  - **Seeder in `IHostedLifecycleService.StartedAsync`**, after the web host built the pipeline (`UseSpark()`); writes with `OptimisticConcurrencyMode.Writes` and an empty change vector for new documents, retrying on `ConcurrencyException`, so a concurrent administrator save is never overwritten. Leaves documents of keys no longer declared alone (reads ignore them; a save of one is refused).
  - **Validation as a before-save interceptor** (#482): an edit keeps `Key`/`Type`/`Default`/`Description` from the stored document; the key must be declared; whitespace is stored as null ("not overridden"); any other value must parse as the declared type. After-save and after-delete interceptors call `Invalidate()`.
  - **Refusals are 404, not 403.** The brief said "non-member 403"; the framework answers a refused PO or query request exactly like a missing row (M-3, `DenyAllEndpointMirrorTests`), so the tests assert 404 and pair it with the member's 200 on the same routes.
- **Tests, red→green** (`tests/MintPlayer.Spark.Tests/Settings/`, Developer licence):
  - `SparkSettingsTests` (service over the test store, fake `TimeProvider`): stored `"25"` overrides configured `10`; clearing the value and deleting the document each fall back to `10`; neither gives the default; `TimeSpan` (`00:05:00`, `1.02:00:00`) and `bool` (`true`, `True`) parse identically from both sources and agree with `ConfigurationBinder.GetValue`; a stored `"lots"` for an `int` falls back to configuration with one warning and no exception; an undeclared setting throws; **TTL**: another instance keeps `10` at +29 s and reads `25` at +30 s while the invalidating instance reads `25` at once; the seeder writes 3 documents, then rewrites only a stale description (1), then nothing (0), keeping both stored values; a malformed key and a clashing declaration are refused.
  - `SettingsEndpointTests` (real route table, `FromJson` security binding `settings:administrators` to a test group, other libraries off): the bound group lists the 3 seeded rows and edits `Value` (200, stored, and `ISparkSettings` on that host returns 25 at once); a signed-in non-member is refused edit and list (404); create and delete are refused even for the bound group (404) and the document stays; `"lots"` for an `Int32` is a 400 naming the type; a document whose key is no longer declared is a 400; whitespace stores null and the default applies again; **an application that binds no `settings:administrators` does not start** (the exception names the slot).
  - Red run, mechanisms stubbed (reads return the default, the seeder rewrites `Value` and always writes, the save interceptor validates and invalidates nothing, the grant widened to `QueryReadEditNewDelete`, key validation off, `HandlerTimeoutFor` returning the global value): **16 of the 20 new tests red**. Green in both, as expected: the default case, the undeclared-setting guard, the non-member refusal and the unbound-slot startup refusal (the last two are the framework's existing slot mechanics, exercised here through this library's grant).
- **X7, per-queue `HandlerTimeout`:** `SparkQueueOptions.HandlerTimeout` (`TimeSpan?`, null = global), `SparkMessagingOptions.HandlerTimeoutFor(queue)`, passed into `ClaimedExecution.RunAsync` by both `MessageQueueRouter` (single subscription) and `MessageSubscriptionWorker` (subscription per queue). Configuration beats code through the existing `ApplyQueueConfiguration`. Tests: `SparkMessagingOptionsBindingTests.A_queue_HandlerTimeout_overrides_the_global_one_and_configuration_overrides_code` (code 20 min, configuration 45 min wins; a shorter queue value; a queue without one and an undeclared queue keep the global 10 min) and `MessageSubscriptionWorkerE2ETests.A_queue_HandlerTimeout_cancels_a_hung_handler_although_the_global_bound_is_long` (global 10 min, queue 500 ms: the hung handler is cancelled and the message parked as `Failed`). Both red with the lookup stubbed (the E2E one ran into its 20 s poll bound). README table updated.
- **Green run:** 442/442 over `Settings`, the messaging binding, subscription-worker E2E and queue-router tests, and the `Layering`, `Extensions`, `Architecture`, `Schemas` and `Model` suites plus `OidcAdminRegistrationTests` (a new library in the test process composes into every host, so the layering goldens and library inventories were checked).
- **§10 unchanged.** Not built here and still owned by later milestones: the SeoTester declarations and binding (M7), and settings-driven X3 quotas (M6).

### Spike S13 (2026-10-10)

**Answer: (a), a per-request lookup by id prefix feeding `OrganizationId in (…)`.** It costs 0.3–2.8 ms for 1–100 memberships, a member's grid page 7–14 ms end to end over 10 000 rows, it is consistent the moment a membership changes, and it ANDs with every other policy. (b) is stale by design and (c) cannot be expressed. Built as X11 in M4.

- **Measured** (`tests/MintPlayer.Spark.Tests/Organizations/OrganizationFilterSpikeTests.cs`, Developer licence, RavenDB 7.2 test server, 10 000 `OgNote`s over 200 organizations with a fifth carrying a future `ExpiresAtUtc`, users in 0, 1, 10 and 100 organizations; medians of 15 runs after 3 warm-ups, second of two passes, the spike run on its own):

  | Memberships | (a) read by id prefix | query on `UserId` (auto-index) | raw page of 25 + total | HTTP grid page (`/spark/queries/execute`, both policies) | (b) claim size |
  |---|---|---|---|---|---|
  | 0 | 0.32 ms | 0.75 ms | — | 7.1 ms (was **169–188 ms**, below) | 0 B |
  | 1 | 0.33 ms | 0.66 ms | 1.07 ms | 10.1 ms | 23 B |
  | 10 | 0.51 ms | 1.45 ms | 1.43 ms | 9.5 ms | 239 B |
  | 100 | 2.83 ms | 3.22 ms | 0.91 ms | 14.0 ms | 2 399 B |

- **RQL** (the raw equivalent of the composed policies, membership AND expiration): `from 'OgNotes' where OrganizationId in ($p0) and (true and not ((ExpiresAtUtc <= $p1 and ExpiresAtUtc != $p2)))`. The id list is one parameter: nothing is spliced into the query.
- **Design decisions** (each with its evidence):
  - **(a) by id prefix, not a query.** A membership's id is `OrganizationMemberships/{userId}/{organizationId}`, so a user's memberships are `LoadStartingWithAsync` straight from storage: no index, so an accepted invitation or a removed member counts on the next request. A query on `UserId` costs about the same (table) but rides an auto-index, and a removed member would keep reading the organization's rows while it lags. *Evidence: `A_membership_counts_on_the_next_request_after_it_is_saved_or_removed` (load 404 → 200 after accepting → 404 after leaving, with no wait).* The fixed id also makes (user, organization) unique and "my role in X" one load by id. Cached once per request (`OrganizationMembershipReader`, scoped) and shared by every type; dropped when the package changes a membership in the same request, so a created organization is readable in its own response.
  - **(b) rejected:** a claim or session cache is stale until the cookie is reissued, so a removed member keeps access (the opposite of D26 "leaving removes access"); and 100 organizations already need 2.4 KB of claim, against a 4 KB cookie before chunking.
  - **(c) rejected:** RQL loads documents for the projection only. `from OgNotes as n load n.OrganizationId as o where o.Name = $name` → `ParseException: 1:46 Expected end of query but got: where`. Filtering through an index would need `LoadDocument` of a member list on the organization, re-indexing every row of an organization on each membership change.
  - **An empty membership set is an impossible id, not a constant `false`, for `Query`.** A constant false is evaluated in memory, so the list read and dropped the whole collection: **169–188 ms** for 10 000 rows against 7–14 ms for a member (first spike run). The list now gets `OrganizationId in ([a random per-process id])`, answered from the index: **7.1 ms**. Every write and single-row check keeps the constant false, so no row can be created in or moved to the sentinel.
  - **Declarative helper.** `IOrganizationOwned` (`string? OrganizationId`) plus `[RequiresOrganizationRole(action, role)]` on the type; defaults Query/Read Viewer, New/Edit and custom actions Member, Delete Admin; Query and Read follow each other, Restore/Revert follow Edit, Purge follows Delete. One row policy (`OrganizationRowPolicy`, `IsVisibilityDecision`) applies it to every implementer and to `Organization` itself (`o.Id.In(…)`; New is not row-limited, the creator becomes Owner in the same commit). `In()`, never `Contains` (CodeCoverage `ApiTokenActions`); a row without `OrganizationId` is visible to nobody.
  - **Composition** with the existing filters is plain AND (#460): the expiration policy's predicate sits beside the membership one in the RQL above, and an expired row of the caller's own organization is a 404 (`The_membership_filter_ANDs_with_the_expiration_policy`).
- **Other decisions taken while building** (evidence in the tests named):
  - **Personal organization on first sign-in** through a new Authorization hook, `ISparkSignedInHandler<TUser>`, run by `SparkSignInManager.SignInWithClaimsAsync` on every fresh sign-in (never a refresh); a failing handler is logged and the sign-in goes ahead. The organization's id is fixed (`Organizations/personal/{userId}`) and stored with an empty change vector, so racing sign-ins create it once (`The_personal_organization_is_created_once…`: 4 concurrent calls, one document; `Signing_in_creates_the_personal_organization_once` through the real `SignInManager`). ~~A personal organization takes no members, cannot be left, transferred or deleted, and goes with the account. See Q-M4a.~~ Superseded by D42 (Round 11): it takes members; its user stays its only Owner, cannot leave, and it goes with the account.
  - **Invitation links** carry 256 random bits; only the SHA-256 is stored; accepting deletes the invitation under optimistic concurrency (single use); an expired one is refused on accept even before the sweep; every refusal answers the same words (`A_used_forged_expired_or_misaddressed_link_is_refused_in_the_same_words`). Accepting is a POST from ng-spark's page on an explicit click, because mail scanners open links. ~~The token is the credential (Q-M4b).~~ Superseded by D43 (Round 11): the token and an account that owns the invited address. Mails are queued `Sensitive` with an expiry before the link's, en + nl, and need `Spark:Auth:PublicBaseUrl` outside Development (never the request's Host header).
  - **Join requests** go to every Owner; an Owner or Admin accepts, the requester cannot accept their own, a Member holding the link cannot (`A_join_request_mails_every_Owner…`).
  - **Account deletion** checks everything before deleting anything: the only Owner of an organization others are in stops the deletion with the account intact (D26's last-Owner rule); an organization the user alone was in goes with its data.
  - **Fail-closed without `AddOrganizations()`.** A library's model and rights come with the reference, its row policy only with the call. The package's Actions classes therefore close its three types when `ISparkOrganizations` is not registered (no row for anyone); with the package added they add nothing. An application's own `IOrganizationOwned` type is already covered by core: startup refuses a type every signed-in user may list without a row rule (`VerifyRowPolicyDeclarations`, met while writing the test). *Red→green:* `OrganizationRowFilterTests.Without_AddOrganizations_the_packages_types_are_closed_to_everyone` (red with the filter off: the organization loaded, 200).
  - **Every host now composes an `IExpiring` model type** (`OrganizationInvitation`, from the referenced layer), so `ExpirationTests.Nothing_that_needs_expiration_leaves_the_database_record_alone` failed in the wide run (the record was configured, 129 600 s). That is the documented rule (any `IExpiring` model type configures expiration), so the test now runs `ExpirationStartup` over a model of non-expiring types only, and `A_referenced_librarys_expiring_model_type_alone_configures_expiration` pins the new fact.
  - **Mail templates of a library must be registered** (`AddSparkMailTemplates`), as the IdP learned in its E2E journey; the package does it, and `MailManagerTests.The_render_helper_renders_every_shipped_organizations_template_in_English_and_Dutch` renders all four.
- **Defect found and fixed in core:** a `security.json` right whose action is a run of core verbs that is not a combined action (`QueryReadEditDelete/OrganizationMembership`, `QueryReadDelete/OrganizationInvitation`, written in this package's first draft) was read as a custom action's name and granted nothing: the types answered 404 `Query '…' not found` while the library build (SPARK047) and startup accepted the file. `SecurityConfigurationValidator.ValidateRights` now refuses it at startup and names the supported combinations. *Red→green:* `SecurityConfigurationValidatorTests.A_core_verb_combination_that_is_not_a_combined_action_is_rejected` (3 cases) red with the check off, green with it; custom actions, single verbs and real combinations stay accepted.
- **Tests, red→green** (`tests/MintPlayer.Spark.Tests/Organizations/`, Developer licence): `OrganizationServiceTests` (13), `OrganizationRowFilterTests` (13, real route table: fail-closed without the package, member/non-member/anonymous, role per action, WITH CHECK on create and move, expiration AND, consistency, generic-UI create/members/invitations/role edits/delete, the Leave and Make Owner custom actions, the accept endpoint), `OrganizationAccountTests` (4: sign-in hook, account deletion both ways, export). Red run with the mechanisms stubbed (policy returning null, token stored plain, hash/expiry/approver/role/last-Owner checks off, cascade and replacement off, creator membership not stored, interceptor rules off, sign-in/deletion/export hooks off): **23 of 29 red**. Green in both, as expected: creation and the personal organization (the service paths were not stubbed), leaving through the action, no join request for a member or a personal organization, a personal organization's refusals, and the expiration AND (the expiration policy alone hides the row). ng-spark: `spark-accept-organization-invitation.component.spec.ts` 4/4 (no POST until the click; posts id + token; one refusal; no token, no POST).
- **Green run:** **2174/2174** over `Organizations` (31, the spike included), the organizations mail templates, and the `Layering`, `Extensions`, `Architecture`, `Schemas`, `Model`, `Authorization`, `Settings`, `Expiration`, `Endpoints` and `IdentityProvider` suites (a new library composes into every host, and the sign-in manager and the security validator changed).

### Round 11 (owner, 2026-10-10): Q-M4a, Q-M4b

Both answers go against the recommendation that M4 shipped with.

- **D42: personal organizations take members (Q-M4a, option (b)).** The question was what happens when a user verifies a site that sits in someone's *personal* organization: M4 refused members there, so `RequestToJoinAsync` answered `false` and mailed nobody, while F3 (D18) assumed the holding organization's owner lets the user in. Now invitations and join requests work for a personal organization like any other, so the second user's join request mails its user the normal acceptance link. What stays personal: it is created on first sign-in (D21), it is the user's default (`Organization.PersonalIdFor(userId)`), it goes with the account (D26), and its Owner role never changes hands: no transfer, no second Owner (role edit refused), its user is neither demoted, removed nor able to leave, and it is not deleted on its own. Its other members are edited, removed and leave as anywhere. **Defect fixed with it:** account deletion deleted *every* personal organization the user belonged to; it now deletes only the user's own (`OrganizationAccountHooks.cs`), since another user's personal organization can now hold them as a member. *Evidence: owner answer 2026-10-10. Tests (red with the M4 rules restored, green after): `OrganizationServiceTests.A_personal_organization_takes_members_by_invitation_and_by_join_request`, `…A_personal_organization_can_be_neither_left_by_its_user_nor_deleted_nor_transferred` (a member leaves; its user cannot, nor transfer), `OrganizationRowFilterTests.In_a_personal_organization_members_are_edited_and_removed_but_the_Owner_role_never_moves` (generic UI: role edit 200, Owner grant 400, user step-down 400, member removal 200), `OrganizationAccountTests.Deleting_a_members_account_leaves_the_personal_organization_they_joined`.*
- ~~**D43: an invitation is accepted only by the account that owns the invited address (Q-M4b, "the stricter rule").**~~ **SUPERSEDED by "D43, corrected" below (owner correction 2026-10-10): the confirmation requirement moved from the invitee to the inviter, and `SparkEmailOwnership` and the IdP change were reverted.** Original text: M4 made the token the only credential. Now `AcceptAsync` also requires the signed-in account to own the invitation's address, by the identity provider's rule (`OidcInvitations.AcceptAsync`: the account's `NormalizedEmail` equals the address normalized the Identity way). That rule moved to Authorization as `SparkEmailOwnership` and both packages call it: `IsAddressOf` (the IdP's check, behaviour unchanged) and `Owns` (the same plus `EmailConfirmed`, which Organizations uses so that an address no provider vouched for, such as an X or Entra sign-in without a verified email or a never-confirmed registration, proves nothing; those users confirm an address first). The refusal is distinct from the generic one: `reason: "otherAddress"` with its own words, and the ng-spark accept page shows it (`organizations.spa.otherAddress`, en/fr/nl). It needs a valid token, so it discloses nothing the holder does not already know (#453), and it does not spend the link. **Join requests keep no address rule**: the requester signed in to ask and an Owner or Admin approves (`AcceptAsync`'s join-request branch checks only the approver's role). The IdP's own acceptance still does not require a confirmed address; aligning it is a separate decision. *Evidence: owner answer 2026-10-10. Tests (red with the check removed, green after): `OrganizationServiceTests.An_invitation_opened_by_another_account_is_refused_with_its_reason_and_stays_live`, `…An_invitation_needs_the_invited_address_confirmed_on_the_account`, `OrganizationRowFilterTests.The_accept_endpoint_redeems_a_link_for_the_signed_in_user_only` (`reason` on the wire); ng-spark `spark-accept-organization-invitation.component.spec.ts` "says why when the invitation was sent to another address" (red: the page showed `invalid`). Confirmed rather than red: `OrganizationServiceTests.A_join_request_is_approved_whatever_the_approvers_or_requesters_address` (unconfirmed Admin and requester).*
- **Runs** (Developer licence, in a clean worktree of 422c167b plus these changes, because the shared working tree held another agent's half-finished M5 edits): red **7 failed / 42 passed** of 49 (`Organizations` 37 + `OidcInvitationTests` 12); green **49/49**; ng-spark accept spec red 1/5, green 5/5.
- **D43, corrected: the SENDER's address must be confirmed, not the invitee's; the IdP stays out of it.** Person A invites person B: `InviteAsync` refuses with a `SparkValidationException` ("Confirm your email address before you invite anyone: an invitation is sent on your behalf.", nothing stored or mailed) when A's account has no confirmed email (`EmailConfirmed` and an `Email`). The *Invite* action (`InviteOrganizationMember`) checks the same before its prompt and notifies the translated reason `organizations.confirmEmailToInvite` (en/fr/nl). B may not be registered yet, so on accept only the address must match (signed-in account's `NormalizedEmail` equals the invited address normalized the Identity way); B's need not be confirmed, and any other account still gets `reason: "otherAddress"` (wording changed to "the account that uses that address"). Join-request mails also go out on a user's behalf (they carry the requester's address to the Owners), so `RequestToJoinAsync` applies the same rule to the requester; the approver's address plays no part. The rule lives in an internal `OrganizationEmail` helper inside the Organizations package: `SparkEmailOwnership` and its Authorization README section are removed, and `OidcInvitations.cs` is back to its pre-ae66691a code (`git diff ae66691a~1 -- libs/identity_provider libs/authorization` is empty). Organizations does not reference the IdP. *Evidence: owner correction 2026-10-10. Tests (Developer licence, clean worktree of ae66691a plus these changes, the shared tree holding M5 edits): red with the new rules removed and the confirmed-invitee rule restored, **4 failed / 48 passed** of 52 (`Organizations` 40 + `OidcInvitationTests` 12): `OrganizationServiceTests.An_inviter_without_a_confirmed_email_is_refused_and_nothing_is_stored_or_sent`, `…An_invitee_whose_matching_address_is_unconfirmed_accepts` (replaces `…An_invitation_needs_the_invited_address_confirmed_on_the_account`), `…A_join_request_needs_the_requesters_address_confirmed_but_not_the_approvers` (replaces `…A_join_request_is_approved_whatever_the_approvers_or_requesters_address`), `…The_invite_action_tells_an_inviter_without_a_confirmed_email_why` (red: the prompt opened); green **52/52**. Confirmed rather than red: `…An_inviter_with_a_confirmed_email_invites`, `…An_invitation_opened_by_another_account_is_refused_with_its_reason_and_stays_live` (`otherAddress`).*

### Spike S8 and M5 (2026-10-10): live updates (X4)

Built in core (`libs/spark/MintPlayer.Spark/Live/`, `Endpoints/Live/LiveSocket.cs`) and ng-spark; guide
`docs/guide-live-updates.md`. Measured on this machine with the Developer licence (Raven.Server is a separate
process: "test-process CPU" is the Spark host and the test clients only). Tests in
`tests/MintPlayer.Spark.Tests/Live/`.

- **L1** (earlier, above): the Changes API works on the production Community licence, 4–22 ms.
- **L2 — full page vs patch** (`LiveSpikeTests.L2_…`, 50 rows × 8 columns of a run-list shape, through the real
  projection): full `result` **54 731 B**; patch for 1 changed row **1 096 B** (2 %); 5 rows **5 262 B**; one row
  moving first→last **3 392 B**. A persistent object of 8 attributes: full `po` **3 051 B**; a one-attribute
  `poPatch` plus the page patch of the same write **1 531 B**. **Decision: patches (D24), computed on the wire
  form** (`LiveDiff`, `JsonNode.DeepEquals`), with a guard: a patch never larger than the full result is sent
  as the full result (`A_page_that_turned_over_…`).
- **L3 — cost per push** (`LiveSpikeTests.L3_…`, one document written 30× at ~6.6 Hz (4.5–4.8 s), every viewer
  subscribed to it; class run alone):

  | Throttle | Viewers | Evaluations (per viewer) | ms / evaluation | Frames | RavenDB requests / evaluation | CPU / evaluation |
  |---|---|---|---|---|---|---|
  | 500 ms | 1 | 7 (7.0) | 5.17 | 7 | 5.3 (first host: JIT, index stats) | 214 ms (JIT) |
  | 500 ms | 10 | 60 (6.0) | 2.22 | 60 | 1.5 | 8.1 ms |
  | 500 ms | 50 | 300 (6.0) | 3.33 | 300 | 1.1 | 2.5 ms |
  | 250 ms | 1 | 12 (12.0) | 3.31 | 12 | 3.5 | 9.1 ms |
  | 250 ms | 10 | 120 (12.0) | 1.66 | 120 | 1.3 | 3.1 ms |
  | 250 ms | 50 | 600 (12.0) | 2.40 | 600 | 1.1 | 0.44 ms |

  30 writes cost each viewer 6 evaluations at 500 ms and 12 at 250 ms: the throttle, not the write rate, sets
  the cost, linearly in viewers, about one RavenDB request (the load) and 2–3 ms each. **Decision: keep 500 ms
  (D36); no shared raw load per change** — at 50 viewers a push costs ~2.5 ms CPU, well inside budget, and a
  shared load would have to be re-secured per viewer anyway. Rate limiting: the upgrade is one HTTP request and
  passes the app's limiter like any other; frames inside the socket do not, and are bounded instead by the
  throttle, 50 subscriptions per socket and 5 sockets per user (`A_subscription_over_the_limit…`,
  `A_socket_over_the_per_user_limit_is_closed_with_4429`).
- **L4 — slow clients** (`LiveSpikeTests.L4_…`, a socket whose peer never reads): with a 100 ms heartbeat and a 1 s
  send timeout the socket was aborted after **1 116 ms**, with **1** send started and none completed. Nothing
  queues meanwhile by construction: a subscription is a dirty bit, the single worker sends inline, so a stuck
  frame stops evaluation of that socket and memory stays at the last state per subscription. **N = 10 s**
  (`SendTimeout` default). Not measured: TCP back-pressure through Kestrel (the test host is in-memory), part of
  L5.
- **L5 — real proxy: not run, open as Q-M5a (§11).** Known: the 30 s heartbeat is sent
  (`A_quiet_socket_gets_a_ping_at_the_heartbeat`, ≥ 2 pings in 1.2 s at 300 ms), below nginx's default 60 s;
  production uses Traefik; nginx recipe in `docs/guide-docker-deployment.md`.
- **L6 — index freshness** (`LiveSpikeTests.L6_…`, a page sorted by an auto-indexed field, 20 writes each moving
  a row to the top, `Source = Actions` so no Changes echo re-runs a stale page): with the wait for the indexes
  over the changed collection (`SparkIndexWait`, shared with `DatabaseAccess`'s post-write wait) **20/20** pushes
  fresh; with the wait removed **19/20 fresh, 1 stale** — ~~and a stale evaluation that diffs as unchanged sends
  nothing, leaving the viewer stale until the next change or `Revalidate`~~ (superseded 2026-10-11, "L6 in the
  sweep" below: a wait that runs out now schedules a bounded re-read). **Decision: watch collections, not
  `ForIndex`**; each query evaluation after a collection change waits (bounded, `IndexWait` 5 s) for that
  collection's indexes; one write costs one evaluation instead of two (write + index batch). Not covered: a
  change reached only through `LoadDocument`, a reference's breadcrumb or a multi-map index — `Revalidate` (1 min)
  or `ISparkLiveNotifier.CollectionChanged` (guide).
- **L7 — login expiry** (`LiveSessionTests.L7_…`, a real Identity cookie with a 3 s ticket, no sliding): the
  socket closed with **4401 after 2 693 ms** (connected just after sign-in); reconnecting with the expired cookie
  is anonymous and the note is `gone`; after a fresh sign-in the reconnect is authenticated and gets the `po`.
  The deadline is read before the upgrade (`AuthenticateAsync` cannot run after it: the cookie handler registers
  `Response.OnStarting`). **Decision:** lifetime = min(ticket expiry, `MaxSocketLifetime` 30 min = Identity's
  default security-stamp interval); the client reconnects at once on 4401.
- **L8 — po-edit under pushes:** server half `LiveSessionTests.L8_…`: a save carrying the etag from before a push
  is still **409** and the stored value stays the other user's. Client half (ng-spark `po-edit/src/spark-po-edit.live.spec.ts`,
  10 tests): a clean form adopts the push; a dirty form keeps the edits and shows the banner; Review merges
  (`mergeThreeWay`) or opens `SparkPoConflictDialogComponent`; pushes during an in-flight save or an open dialog
  are set aside and re-checked afterwards; `gone` shows the 409 `deleted` message.

**Decisions taken while building** (evidence in the tests named; red = the mechanism removed, test failed):

- **`Actions` source = the document store's events, not `IAfterSaveCommitted`.** That interceptor is durable
  and a startup error without Messaging (`SparkCommittedInterceptorsStartupCheck`), and sees only the pipeline.
  `OnAfterSaveChanges` reports a stored document with its new change vector; a delete is remembered from
  `OnBeforeDelete` and reported at the session's next store or its disposal. A handler never throws into the
  writer, and is removed when the host goes: test hosts share a store, and a disposed host's handler failed every
  later save (`ObjectDisposedException` in `RouteTableSnapshotTests`, fixed). Red: install removed →
  `A_write_in_this_process_…_Source_Actions…` failed.
- **Changes API per document and per collection, ref-counted on one connection; confirmed before the first
  evaluation.** Red: watches removed → the `Both` and `Changes` rows of
  `A_write_from_another_process_reaches_the_viewer_only_through_the_Changes_API` failed; the `Actions` row proves
  the opposite (another process's write is invisible without the Changes API).
- **`Both` de-duplicates by id + change vector** (5 s window): one write is one evaluation. Red: dedupe removed →
  `LiveHubTests.Under_Both_the_echo…` and `LiveSocketTests.Under_Both_one_write_costs_one_evaluation…` failed.
- **One read path.** `/spark/po/load` and `/spark/queries/execute` bodies moved into `PersistentObjectReadPath` and
  `QueryReadPath`; each evaluation runs them in a fresh DI scope under the socket's principal.
  `The_pushed_object_and_page_are_exactly_what_load_and_execute_return` asserts deep equality.
- **No frame when nothing visible changed** (change-timing oracle). Red: unchanged diff sent as a result →
  `A_write_to_a_row_the_viewer_cannot_see_sends_nothing` and two `LiveDiffTests` failed.
- **#453 parity:** deleted, expired and forbidden objects are one `gone`; unknown, forbidden and not-live queries or
  types are one `error unavailable` (`An_expired_note_is_gone_like_a_deleted_one`,
  `Unknown_forbidden_and_not_live_answer_alike`).
- **Revalidation every minute** without a write, so a lost membership or right applies. Red: revalidation removed
  → `A_member_removed_from_the_organization_gets_gone_without_any_write_to_the_note` failed (green 6 s, red 12 s
  timeout).
- **Throttle 500 ms, latest wins.** Red: throttle removed → `A_burst_of_writes_costs_at_most_one_push_per_throttle_window…`
  failed (more frames than leading + one per window + trailing).
- **Streaming defects** (§5): (1) removed rows: red without `remove` → `LiveDiffTests.A_row_that_left_the_set…`,
  `LiveAppStreamTests.A_row_missing_from_the_next_whole_set…`, `LiveSocketTests.A_new_row_is_added_and_a_deleted_row_removed…`
  failed; (2) added rows dropped client-side: ng-spark red (patch applied to held rows only); (3) 1011 + ten retries:
  now a per-subscription `error`, ng-spark red (error turned into a socket retry); (4) revoked right closing
  silently: red without the per-evaluation `Query` check → `LiveAppStreamTests.A_revoked_right_ends_the_subscription…`
  failed, and ng-spark red (rows kept on error); (5) the false "watches RavenDB" comment is gone with the file.
- **App streams keep D24's `IAsyncEnumerable`** with `LiveQueryArgs` (parent, search, paging, sort, uncapped
  session); whole-set yields; search, sort and paging in memory with a real total; started only for a viewer with
  the `Query` right (`The_method_is_not_started_for_a_viewer_without_the_Query_right`); startup refuses the
  signature on a non-live query. The app-stream decision is by actions *type*, never instance, at startup: a
  library's actions class may need services only its own registration adds (found:
  `OidcAuditEventActions` failed to construct in every host without the IdP).
- **`"live"` is not structural in the model hash** (`Turning_a_query_live_does_not_move_the_hash`): it opens no read
  `/execute` does not. Removing `"isStreamingQuery": false` from 36 model files did move every app's hash;
  regenerated with `--spark-synchronize-model` (no database).
- **An `update` is always answered with a full `result`**, so a grid knows its page, sort or search took effect
  (asked by the client implementation).
- **Runs:** `Live/` 48/48 green (6 classes incl. spikes and sessions); the touched areas (endpoints, organizations,
  expiration, schemas, model, query loader, composed queries, custom actions, reflection cache, index wait)
  595/595; route snapshots regenerated (10 files). ng-spark vitest 29 files, 644 tests. Not run: the full sweep,
  E2E (`WebSocketOriginTests` re-pointed at `/spark/live` and extended to read the Fleet app stream's first page).

### Round 12 (owner, relayed with the M6 brief, 2026-10-10)

- **D44: notification preferences are per organization.** A user chooses the channels (in-app, mail, Web Push)
  for each organization separately. *Evidence: owner answer, relayed in the M6 brief ("preferences per
  organization (owner answers)").* Built as `NotificationPreference` (below).
- **D45: Web Push lives in its own library in this solution** (D38 restated by the brief: "Hand-rolled, perhaps in
  its own library in this solution"): `libs/webpush/MintPlayer.Spark.WebPush`, no package or project dependencies.

### Spike S12 and M6 (2026-10-10): notifications, Web Push, quotas, renderers, share links

Commits `bc74c7d6`, `1cd8c597`, `9a85b1f3` (X5), `9d7db45a` (Web Push), `4cab0b1a` (X10), `fa226b15` (X3),
`d28946ff` (M5 follow-ups), `9345d422` (X6), `9981b4d0`, `0e64aebf` (ng-spark notifications and share UI), `44f8924e`, `e427bbe7` (snapshots, goldens, app model hashes). Guides `docs/guide-notifications.md`,
`docs/guide-quotas.md`, `docs/guide-share-links.md`, `docs/guide-score-renderers.md`.

**S12, Web Push delivery** (`scratchpad` spike: a static page on `http://localhost`, a real subscription in the
Playwright browser, pushes sent by the library through the SSRF-safe client, `MintPlayer.Spark.WebPush` +
`AddSafeHttpClient`):
- **Chrome 154 (Windows), FCM endpoint: 5/5 delivered and decrypted** by the page's service worker (each payload
  read back as JSON with the sent title). `201 Created` each; 382 ms for the first request (TLS to
  `fcm.googleapis.com`), then 45–66 ms.
- A forged endpoint path: **410** → `SubscriptionGone`. `https://10.0.0.1/push`: refused by the SSRF guard
  (`SafeHttpBlockedException`) before sending.
- Encryption cost: **2.2 ms per message** (2 000 messages after 200 warm-ups, Release, this machine): a fresh P-256
  key pair and ECDH per message, as RFC 8291 requires. Negligible beside the 45 ms round trip.
- **Edge under automation:** `pushManager.subscribe` → `AbortError: Registration failed - permission denied` (Edge
  pushes through WNS, which an automation profile does not reach). **Firefox and Safari were not reachable** from
  this machine's tooling. Open as Q-M6a (§11).
- The RFC 8291 §5 example's `Content-Length: 145` is off by one: its body (which the library reproduces byte for
  byte) decodes to 144 bytes (86 + 41 + 1 + 16).

**X10, notifications** (`libs/notifications/MintPlayer.Spark.Notifications`, alias `notifications`,
`11.0.0-preview.104`; `MintPlayer.Spark.WebPush` `11.0.0-preview.104`):
- **Web Push library** (D38, D45): `WebPushEncryption` (one aes128gcm record, fresh salt and ephemeral key per
  message), `VapidDetails`/`VapidAuthorization` (ES256 JWT, `aud` = push-service origin, `exp` 12 h, one token per
  origin reused until an hour before expiry: Apple asks for at most one refresh an hour), `WebPushClient` (`TTL`,
  `Urgency`, `Topic`; every status mapped to `Delivered` / `SubscriptionGone` (404, 410) / `RetryLater` (429, 5xx,
  `Retry-After`) / `Rejected`). `ECDsa.SignData`'s default IEEE P1363 output is JWS ES256 (DER is the pitfall).
  *Evidence: `WebPushEncryptionTests` reproduce the RFC 8291 §5 body and every Appendix A intermediate value, the
  latter from the user agent's side with an independently written receiver, so a symmetric mistake cannot pass.
  Red with key_info's two keys swapped, DER signatures, no token reuse, 404 not "gone" and the topic check off:
  9 of 28 failed; green 28/28.*
- **One call, three channels, no second channel** (D20, D30, D44): `ISparkNotifier.NotifyAsync`. In-app is a
  `Notification` document; the type and its queries `MyNotifications` / `UnreadNotifications` are `"live": true`,
  so an open UI gets it over `/spark/live` without any notifier call (the store's own events, X4), and the bell's
  unread count is `UnreadNotifications`' `totalItems`. *Evidence: `The_unread_count_reaches_an_open_socket_without_any_notifier_code`
  (0 → 1 after `NotifyAsync`, → 0 after marking read, on one socket); red with `"live": false`.*
- **Preferences per organization** (D44): `NotificationPreference` `NotificationPreferences/{userId}/{orgId|default}`;
  the user's row for the organization, else their default row, else `Spark:Notifications:Defaults` (all on): two
  loads by id. The package does **not** reference Organizations: an app without organizations would otherwise have
  to bind `organizations:users` (startup refuses an unbound slot) for a package it does not use; the organization
  is an opaque id the app may turn into a reference in its own model layer. *Evidence:
  `Preferences_per_organization_choose_the_channels_and_fall_back_to_the_default_row_then_the_options`.*
- **Push subscriptions are their own documents, not a list on `SparkUser`** (D30 says "on the SparkUser"; see
  Q-M6b): `PushSubscriptions/{userId}/{sha256(endpoint)[..32]}`, read by id prefix like memberships (S13). A list
  on the user document would put every browser's subscribe on Identity's concurrency stamp (a sign-in racing a
  subscribe fails one of them) and couple Authorization to push.
- **Durable push:** each device is one `WebPushDeliveryMessage` in the notifier's own `SaveChanges` (Messaging
  outbox), queue `spark-webpush` (8 attempts, backoff 30 s → 6 h, `MaxConcurrency` 4). 404/410 deletes the device;
  429/5xx throws (retried); anything else is a `NonRetryableException`. Startup refuses bad VAPID keys, a TTL over
  28 days and Web Push without `AddMessaging()`. *Evidence: `A_push_reaches_every_device_encrypted_and_a_gone_device_is_deleted`
  (the fake push service behind the package's SSRF-safe client receives two posts; the test decrypts the phone's
  with its own key: title, body, absolute `data.url`, `onActionClick`, `notificationId`, `Urgency: high`,
  `Topic: cert-expiry`; the 410 device is deleted).*
- **A push-service allow-list** (Google, Mozilla, Apple, Microsoft and subdomains; a configured list replaces it,
  as D39): the endpoint comes from the browser, so without it any signed-in user could make the server POST to any
  public URL. Private addresses are already refused by X2. Plus 20 devices per user (the oldest goes). *Evidence:
  `Subscribing_refuses_other_push_services_and_bad_keys_and_keeps_a_bounded_device_list`.*
- **Mail only to a confirmed address** (the D43 reasoning: an X or Entra sign-in proves no address). *Evidence:
  `A_mail_goes_only_to_a_confirmed_address_with_an_absolute_link`.*
- Rows are their user's alone (`UserId == caller` on every action, an anonymous caller none): another user's
  notification is the 404, an anonymous load the framework's 401. Account deletion and export hooks.
- **Red→green:** `NotificationTests` red with the row rule, preference lookup, gone-device delete, mark-read owner
  check, allow-list, device cap, account deletion and the live flag stubbed: **8 of 8 failed**; green **8/8**.

**X3, quotas** (settings package): core gains `ISparkCustomActionGate`, run by `/spark/actions/execute` after
authentication, the type right, the catalogue, the row gates and the disabled-action gate, right before the
action; a `SparkThrottledException` there is the existing 429 with `Retry-After`. The settings package adds
`SparkQuota(Name, SparkSetting<int> Limit, SparkSetting<TimeSpan> Window)`, `AddQuotas`, `[SparkQuota]` and
`ISparkQuotas`.
- **Sliding window**, not fixed: "5 a day" and D14's "1 full crawl a week" mean any 24 h / 7 d, and a fixed weekly
  window would allow two full crawls minutes apart across its boundary. Uses are a short list on
  `SparkQuotaUsages/{quota}/{key}` (`IExpiring`: gone with its last use), written under optimistic concurrency.
  *Evidence: `Concurrent_uses_never_overspend_the_limit` (20 parallel uses, limit 5: exactly 5 admitted),
  `The_window_slides_…`, `The_sixth_run_in_a_day_is_a_429_with_Retry_After_and_does_not_run` (Retry-After 19 h when
  the first of 5 hourly uses is 5 h old).*
- **An admitted run counts even if the action fails** (Moderation's daily counters do the same, "never
  refunded"), so a failing action cannot be retried for free; where code decides what counts ("joining a running
  audit does not") the app calls `ISparkQuotas` instead of the attribute.
- Limit 0 refuses, a negative limit is unlimited; key = user id, else `ip:{address}`; startup refuses a
  `[SparkQuota]` naming an undeclared quota.
- **Red→green:** `SparkQuotaTests` red with the gate not called, concurrency off, no pruning, no unlimited and the
  startup check off: **6 of 6 failed**; green **6/6**.
- **Not deduped with Moderation's counters:** they are written on the request's own session, in the same
  transaction as the post or vote they count (`ModerationInterceptor.ThrottleNewAccountAsync`,
  `SparkModeration` lazy batch), which a separate quota write cannot reproduce.

**X5, score renderers** (`@mintplayer/ng-spark/score-renderers`, `libs/node_packages/ng-spark/score-renderers/`):
`spark-gauge` (SVG ring, score inside, grade beside, ARIA meter), `spark-threshold-bar`, `spark-trend` (sparkline, or
a chart with `display: 'chart'`; also the standalone `<spark-trend-sparkline>` / `<spark-trend-chart>`) and
`spark-delta`; bands `{ min?, color, label? }` with the presets `'traffic-light'` (default: green ≥ 80, amber ≥ 60)
and `'letter-grades'` (D15: A ≥ 90, B ≥ 80, C ≥ 70, D ≥ 50, F); no value is never zero (a muted "—");
`provideSparkScoreRenderers(appRenderers?)`.
- **CodeCoverage switched over, behaviour pinned first:** `score-renderers.characterisation.spec.ts` (46 cases:
  null/missing, 0, 59.95 % (shows "60.0%" but stays red: the band is decided on the unrounded value), 60, 79.99, 80,
  100, the flat /api shape, ±0.04 deltas, numeric strings, empty and single-point series) compares the visible DOM
  (classes, inline styles, aria). Written and committed against the old components (`bc74c7d6`, 46/46), then passes
  unchanged against the new path (`9a85b1f3`). Red: dropping the bar's `label: 'Coverage'` 9/46, swapping to
  `'letter-grades'` 1/46 (the 59.95 edge). Model renderer names unchanged; no model JSON changed.
  Mapping: `coverage-bar` and the per-file bar → `spark-threshold-bar` (0–100, traffic-light, 1 decimal, `%`);
  `coverage-sparkline` → `spark-trend-sparkline` (yMin 0, yMax 100, 90×24); `coverage-delta` → `SparkDeltaComponent`
  (its defaults are CodeCoverage's); the coverage-over-time card → `spark-trend-chart` (goal 80 %).
- **Bundle:** the sparkline and the chart are separate modules so the chart stays in CodeCoverage's lazy
  po-detail chunk; `main` 440.05 kB vs 436.43 kB before (+0.9 kB transferred), initial total unchanged.
- ng-spark `score-renderers.spec.ts` 30/30; CodeCoverage's 8 affected spec files 136/136.
- **Dedupe:** CodeCoverage's `formatDelta` (and its spec) moved into ng-spark as `formatSparkDelta`; three chart-stub
  overrides in CodeCoverage specs are one helper (`stubSparkTrendCharts()`). Owner review of the look: Q-M6d.

**ng-spark client** (`9981b4d0`, `0e64aebf`): `@mintplayer/ng-spark/notifications` (`SparkNotificationsService`:
`unreadCount` from the live `UnreadNotifications` total, `recent`, `markRead`, `open`, push state and
`enablePush()` asking permission inside the click before any await; `<spark-notification-bell>`,
`<spark-push-toggle>`), `pwa` `SparkNotificationClicks` (an open app navigates in-app to a same-origin `data.url`;
hence the payload's `focusLastFocusedOrOpen`, `e427bbe7`), `@mintplayer/ng-spark/share` `withShareRoutes()` and
`share/page` `SparkSharedObjectComponent` (attributes through `<spark-grid-cell>`, so renderers apply; child tables
paged through `/spark/share/query`). Specs 64/64 (service 16 on a real `SparkLiveService` over a fake socket, bell
and toggle 11, clicks 9, shared page 9, plus the existing pwa spec); red with five mechanisms broken at once
(count from `items.length`, permission after an await, no same-origin check, 404 not treated as not-found, bell not
opening): 6 of 64 failed. Not done (small, noted): a push click does not mark the notification read (`pwa` does not
depend on `notifications`); the share response carries no type or query labels, so the page heads each table
with the query name.

**X6, share links** (core): `ISparkShareable` (`ShareTokenHash`, framework-protected like `ExpiresAtUtc`, D41),
`spark.AddShareLinks(o => o.Share<T>(attributes, queries))`, the built-in actions `ShareLink` / `RevokeShareLink`
(core `actions.json`), `ISparkShareLinks`, `ISparkShareGate`, anonymous `POST /spark/share/load|query`.
- **Declared in code as allow-lists, not in the model JSON:** what an anonymous reader sees is a security decision;
  an allow-list of attribute and column names keeps a later model change (a new attribute) out of every link, and
  adding it to `EntityTypeDefinition` would have moved the model hash and the schema for a code-level concern.
  A child query must also be one of the type's own sub-queries and always runs with the shared object as parent.
- **Who may share is ordinary authorization:** the rights `ShareLink/T` / `RevokeShareLink/T` and the row rules for
  that action name; D31's "Owners and Admins" is `[RequiresOrganizationRole("ShareLink", OrganizationRole.Admin)]`.
  *Evidence: `A_Member_may_not_share_and_a_stranger_cannot_even_see_the_action_target` (404 for both).*
- **The read** runs the normal read paths (so renderers, redaction hooks and references behave as on a page) in a
  fresh scope under a system principal with a **share grant**: `PermissionService` answers only `Read/T` and
  `Query/{child entity}`, row policies that bypass the system context (organizations) are skipped for that one
  object and its children, the expiration filter is not. *Evidence: `An_Admin_shares_and_anyone_…` (only `Title`,
  only the `Text` column, only that note's comments, none of `Secret`, `Author`, the other note or the hash;
  paging), `An_expired_object_is_gone_from_its_link_too`.*
- 256-bit token, SHA-256 stored, compared in fixed time; a new link replaces the old one; one 404 for every refusal
  (#453); the global switch (`Spark:Share:Enabled`) and `ISparkShareGate` (D11/D31: the app's settings switch);
  60 reads per minute per address. *Evidence: `Every_refusal_is_the_same_404` (5 refusals, one body),
  `Revoking_or_replacing_a_link_ends_it_at_once`, `Switching_sharing_off_…`, `Share_reads_are_rate_limited_per_address`,
  `A_client_can_neither_set_nor_clear_the_token_hash_through_an_edit`.*
- **Red→green:** `ShareLinkTests` red with the token check, both allow-lists, the sub-query check, the gates,
  revocation, the interceptor and the limiter stubbed: **6 of 8 failed**; green **8/8** (the two green in both are
  the organizations role check and the expiration filter, existing policies).

**M5 follow-ups:**
- `spark-query-grid` emits `(error)` for a live error too, as an `HttpErrorResponse` with the status `/execute`
  gives (unavailable 404, invalid 400, limit 429, failed 500). Red before: no emit.
- The nested `spark` translations group **was** resolved by the server: `CoreActionTextsTests.The_nested_spark_group_is_served_under_dotted_keys`
  is green without a change. The client was wrong: `spark-query-grid` and `spark-query-list` wrote
  `lang.t(key) || 'fallback'`, and `t()` returns the key when the text is missing, so before the translations
  arrived a user saw `spark.query.unavailable`. `SparkLanguageService.t(key, fallback)` fixes it. Red before: the
  language spec's fallback case.

**Dedupes (behaviour kept identical, with tests):**
- **The public-base-URL rule** was copied in `SparkAuthLinkBuilder`, `OidcPortalLinks` and `OrganizationMail`; it is
  `SparkPublicBaseUrl.Resolve` in Abstractions now, used by those three, notifications and share links. Each
  caller keeps its own failure (throw with its own words / warn and skip). `SparkPublicBaseUrlTests` pins the rule
  (options over configuration, trailing slash, blank = unset, Development-only request origin with path base,
  never outside Development); the callers' existing tests are green in the runs below (Authorization, IdentityProvider, Organizations).
- **X5:** CodeCoverage's `formatDelta` moved into ng-spark (`formatSparkDelta`) and three chart-stub overrides in its
  specs became one helper (see X5).
- Also fixed: the `Organization.IsPersonal` description still said "Nobody else can join it", stale since D42.

**Production check, CodeCoverage:** core's two new actions changed every app's model hash
(`config actions.json … layer library 'spark' changed`), so the first `CodeCoverage.Tests` run was **46 red**
(every host refused to start). Hashes regenerated with `--spark-synchronize-model` for all six apps (no model file
changed), CodeCoverage's route snapshot gains the anonymous `/spark/share/*` routes (nothing is shareable there:
both answer 404): **1213/1213 green**. Its UI behaviour is pinned by the X5 characterisation spec.

**Two M5 leftovers the conventions caught:** `LiveSocket` located services through `RequestServices` (now
`[Inject]`ed, the same request-scope instances) and `LiveAppStreamMethod` passed `HookInvoke` through an alias the
`HookInvocationTests` scan cannot see (now literal).

**Runs** (Developer licence; no full sweep, no E2E): the affected areas of `MintPlayer.Spark.Tests` (Layering,
Architecture, Schemas, Model, Extensions, Endpoints, Organizations, Settings, Notifications, Sharing, WebPush,
Expiration, IdentityProvider, Authorization, Mail, Live, Services, Messaging) **3489/3513**, the 24 being route
snapshots (10), the action and translation goldens (6), the catalogue tests (4: core now ships five actions), the
XSRF surface (the share reads, exempt) and the three convention findings above; after those, their classes plus
Live and Mail **156/156**, and the notification, share and translation classes again after the last changes
**38/38**. `CodeCoverage.Tests` 1213/1213. ng-spark: grid, language, query-list 138/138; notifications/pwa/share
64/64; score renderers 46/46.

### M7 (2026-10-10): the SeoTester skeleton

Commits: "Share page headings from model labels; a clicked push is read" (`966af902`, the M6 leftovers), then the
skeleton, the SparkId development client and this record. Operator documentation: `docs/seotester/README.md`.

**Layout.** `apps/SeoTester/SeoTester` (host, `ClientApp` = nx `@spark-apps/seo-tester`, `Settings/`, `Bot/`,
`Dockerfile`), `SeoTester.Library` (plain-SDK entity library: `SeoTesterBot`, `SeoFeatureScope`, the queue messages),
`SeoTester.Tests`. https 5013 / http 5014, database `SeoTester`, cookie `.SparkAuth.SeoTester`, user secrets
`seotester-app-5b1e0c7d`. Wired into the root `package.json` workspaces, `MintPlayer.Spark.slnx`,
`.github/workflows/pull-request.yml` (build, verify model, verify security) and `tools/verify-ngsw-config.test.mjs`
(`SeoTester: ['!/bot']`). Not in `tools/test-local.mjs`: no E2E yet. New SPA icons (a magnifying glass over a rising
bar chart, indigo), not QnA's.

**Composition (§4.1), as built, with what differs from the sketch:**
- `AddAuthentication<SparkUser>` with `LocalCredentials = Disabled`, `ExternalLoginLinking = ConfirmByEmail`;
  `AddExternalProviders(configuration)` with empty `MicrosoftAccount`/`GitHub`/`Twitter`/`Google` placeholders in
  `appsettings.json` (a provider without a `ClientId` is not registered: `SparkExternalProvidersConfigurationExtensions.cs:99`).
  *Evidence: `CompositionTests.The_sign_in_page_offers_exactly_Microsoft_GitHub_X_and_Google_and_no_password_form`
  (`/spark/auth/capabilities`: `localCredentials: Disabled`, schemes GitHub, Google, Microsoft, Twitter).*
- **"An app that runs without them" = Development signs in through SparkId.** With `LocalCredentials = Disabled` and
  no provider, Spark refuses to start (`LocalCredentialEndpointFilter.GuardAgainstUnreachableSignIn`), which is right
  for production. Development registers SparkId (the demo identity provider) in `appsettings.Development.json`, as
  QnA and HR do, so a fresh clone signs in with no OAuth app. SparkId gains the migration
  `M_202610101200_SeoTesterRelyingParty` (Development only; a migration of its own so databases that ran
  `M_202610081200_RelyingParties` get it too). Production offers the four providers only.
- `AddMailManager()` always: `ConfirmByEmail` needs a link-confirmation sender, which only MailManager registers
  (the startup guard, `docs/guide-outgoing-mail.md` §1). Development uses the pickup folder; production must set
  `Spark:Mail:Smtp:Host` or the app refuses to start (README).
- `AddMessaging()`; the six queues of §4.3 are **declared in configuration** (`Spark:Messaging:Queues`, with
  `MaxConcurrency` and a `HandlerTimeout` each, render 1 at a time) and their messages in `SeoTester.Library`
  (`[MessageQueue]`). A declared queue counts before any recipient exists
  (`MessageSubscriptionManager.cs:59-62`). *Evidence: `CompositionTests.The_crawl_queues_are_declared_…`.*
- **Not called, and why:** `AddActions`, `AddCustomActions`, `AddRecipients`, `AddCronJobs` are generated over the
  app's assembly only once it declares such a class (the build failed with CS1061 for `AddActions` and
  `AddCronJobs`); they arrive with M8, M9 and M12. `AddLive` and `AddExpiration` are core and always on; both take
  options only (`SparkBuilderLiveExtensions.cs:17`, `docs/guide-expiration.md:31`). `AddHistory()` is called.
- `AddSafeHttp()`; `AddSettings(typeof(SeoSettings))` and `AddQuotas(typeof(SeoQuotas))`; `AddNotifications()`;
  `AddOrganizations()`; `AddShareLinks(_ => { })` with `Share<Site>` in M8, plus `SharingSwitch : ISparkShareGate`
  (the D11/D31 switch, `SeoTester:Features:Sharing` ≠ `Off`).
- `security.json`: groups Anonymous visitors, Signed-in users, Administrators; bindings `organizations:users` and
  `notifications:users` → Signed-in users, `settings:administrators` → Administrators; no rights of its own yet (the
  packages ship theirs). `securityPosture.txt`: nothing reachable anonymously. An administrator is a user whose
  `SparkUser.Roles` holds `Administrators` (Spark reads groups from role claims).
- `appsettings.Production.json` sets `Spark:Auth:PublicBaseUrl` to `https://seotester.mintplayer.com` (Development
  uses the request origin).

**Settings (X9, D11, §6, §7).** `SeoSettings`: four gates (`SeoTester:Features:{Monitoring, IntrusiveProbes,
DeepCrawl, Sharing}`, three-valued, see Q-M7a), every §6 limit per unverified/verified (pages, wall time,
connections per host, request interval, rendered sample, PSI sample), the full-crawl ceiling (20 000 pages / 6 h,
D14), the target cool-down (6 h, D37), concurrent renders (1), the audit-log retention (1 year, D37) and the seven
category weights (§7). `SeoQuotas`: `audits` 5/day, `verified-audits` 24/day, `full-crawls` 1/7 days (D14). They
live in the host, not the Library: the settings package is a web package, and an entity library refuses ASP.NET
(SPARKLIB001).
- *Evidence: `SettingsAccessTests` (an administrator lists them; a signed-in non-administrator gets 404 on the list
  and on an edit, and the value is unchanged; anonymous 404/401) and
  `CompositionTests.Every_gate_and_limit_of_the_PRD_is_a_declared_setting_…`.*
- **Spark refuses the dangerous widening by itself:** binding `settings:administrators` to Signed-in users does not
  start (`VerifyRowPolicyDeclarations`: "grants 'QueryReadEdit/Setting' to the 'authenticated' role … but
  'SettingActions' declares no row rule"). So a non-administrator's 404 cannot be turned red by widening the binding;
  the startup refusal is the red.

**`/bot` is rendered by the server, not the SPA** (`Bot/BotPageEndpoint.cs`, a generator endpoint, anonymous, English
or Dutch by `Accept-Language` or `?lang=`). Its readers are site operators following a URL from an access log, often
with curl or a text browser, so it must read without JavaScript; and it states the **current** limits from the
settings (pages per run, connections and interval per host, wall time, cool-down, the intrusive-probe gate), never
numbers copied into a page. It is excluded from the service worker (`ngsw-config.json` `!/bot`, enforced by the tools
test) and the SPA links to it with a plain `href`. This also covers M11's "/bot" item. *Evidence: `BotPageTests`
(user agent, the robots.txt opt-out block, "At most 100 pages and 10 minutes per check", a stored 250 shows at once,
Dutch for `nl-BE`).*

**No SeoTester documents yet.** §4.2's shapes are listed, but each type brings model JSON, row rules
(`IOrganizationOwned`, `[RequiresOrganizationRole]`), rights and tests; M8 builds `Site` with its verification and
share declaration, M9 the run documents. The skeleton shows organizations, settings and notifications, which are the
packages' types.

**Tests** (`SeoTester.Tests`, one host per collection, a principal from `X-Seo-User` / `X-Seo-Roles` role claims,
`HandleCookies = false` so each request carries exactly the antiforgery pair minted for its identity):
`CompositionTests` 5, `SettingsAccessTests` 3, `BotPageTests` 3, `RouteTableSnapshotTests` 1 (anonymous routes: `/bot`,
the external-login callbacks, `/spark/share/*`). **Red→green:** with the settings slot bound to an unused group, the
`/bot` endpoint unmapped, `AddQuotas` removed and the `seo-render` queue undeclared: **7 of 12 failed** (the
administrator list, the route table, the settings catalogue, the queues, the three bot-page tests); the five still
green are the non-administrator and anonymous refusals (red only through the startup refusal above), the sign-in
options, `ConfirmByEmail` and the composed services. Green **12/12**.

**M6 leftovers:**
- **Share-page headings.** `POST /spark/share/load` answers the type's `label` and `queryLabels` (each shown child
  query's label, translated like any label); the page heads the object with the type label and each table with its
  query label, falling back to the names. *Evidence: `ShareLinkTests.The_share_carries_the_type_label_and_a_label_per_child_query`
  (red: no `label`), two share-page specs (red, then 11/11).*
- **A clicked push is read.** `SparkNotificationsService` subscribes to `SwPush.notificationClicks` and marks
  `data.notificationId` read (`pwa` still knows nothing of notifications). *Evidence: two service specs (red, then
  green; notifications, pwa and share specs 68/68).* Limit: when no tab of the app is open, ngsw opens one and
  broadcasts the click right away; the click marks the notification read only if the new page's service is listening
  by then. Not measured in a browser (Q-M6a's devices, M12).

**Dedupe (behaviour kept identical, with tests):** SparkId's relying-party seed (`EnsureRelyingPartyAsync`, private to
`M_202610081200_RelyingParties`) is `DevelopmentRelyingParties.EnsureAsync`, used by both migrations.
`SparkId.Tests.DevelopmentRelyingPartiesTests` pins the shape (confidential, PKCE, exact `/signin-SparkId` and
`/signout-callback-SparkId` redirects, implicit consent, Live, openid/profile required and email optional, the secret
stored hashed and verifiable) and that an administrator's edit survives a re-run.

**Seen running** (`dotnet run --launch-profile https`, Development, local RavenDB): the host starts, the SPA serves at https://localhost:5013, the sign-in page offers only "Spark Identity" (no real provider configured), `/bot` renders. A full SparkId sign-in was not exercised (it needs a SparkId account).

**Found along the way:** the committed route snapshots of SparkId, DemoApp, Fleet, HR and QnA predated M5 and M6 (they still listed
`/spark/queries/{id}/stream` and lacked `/spark/live` and `/spark/share/*`), so those five test projects were red on this
branch (M6 regenerated only CodeCoverage's); regenerated, the same four-line diff in each.

### M8 (2026-10-10): sites, verification, the catalogue, scoring, audit start

Commits: `494cb92f` (core: per-object share gates), `2a7539a7` (SeoTester.Library: catalogue, scoring, origins and
proofs), `69b4ba4b` (SeoTester host and tests), then this record. Operator documentation: `docs/seotester/README.md`.

- **D46: passkeys on SeoTester (owner answer 2026-10-10, closes Q-M7b, supersedes D2's "external logins only").** A
  user adds a passkey on the account page after the first external sign-in, through Spark's existing support
  (`auth.Passkeys = SparkPasskeys.Enabled`). The relying-party id is pinned, never taken from the request: the host of
  `Spark:Auth:PublicBaseUrl` (production `seotester.mintplayer.com`), `localhost` in Development. `LocalCredentials`
  stays `Disabled` (no passwords). The SPA's `withAccount()` no longer excludes the passkeys page. *Evidence: owner
  answer 2026-10-10. Test `SharingAndPasskeyTests.Passkeys_are_on_and_a_signed_in_user_reaches_their_passkeys_page`
  (`/spark/auth/capabilities` says `passkeys: true`, `request-options` answers with `rpId: localhost`, the passkeys
  page `me` loads for a signed-in user, not anonymously); red with `SparkPasskeys.Disabled`. The route snapshot gains
  `POST /spark/auth/passkeys/request-options` and `…/sign-in`.*

**Where things live.** Entities in the host (`apps/SeoTester/SeoTester/Entities`), because `IOrganizationOwned`,
`ISparkShareable` and `IExpiring` are web types and the entity library refuses ASP.NET (SPARKLIB001): Q-M8a. The
Library holds what is pure: `Catalogue/checks.json` and its parser, the scoring engine, `SiteOrigin` and
`OwnershipProof`. Indexes: none; every query is a dynamic one (application-side by construction).

**Domain model (§4.2).**

| Document | Rows | Notes |
|---|---|---|
| `Site` | `IOrganizationOwned`: Viewer reads; Member edits the profile and starts audits; Admin adds, deletes, verifies, shares (D23, D31) | `live`; origin normalized and unique per organization, fixed after creation; personal organization when the form leaves it empty (D21); server-owned verification and latest-run fields (`SiteSaveInterceptor`) |
| `AuditRun` | `IOrganizationOwned`, read + share only (never a form) | `live`; status, progress, participants, limits fixed at start, `ScoringSnapshot` + `ScoringVersion`, `CategoryScores` and `CheckResults` value objects; sub-query `site-runs` of `Site` |
| `PageResult` | `IOrganizationOwned`, read only | `expiresAfter` 7 d (D8), ~~id `{runId}/pages/{hash}`~~ id `{siteId}/pages/{hash}` (one per page and site, the latest; D8), findings value objects; sub-query `run-pages` of `AuditRun` (the pages whose latest result is the run's) |
| `CheckDefinition` | everyone signed in reads (`ISparkOwnsRowSecurity`, stated rationale); Administrators edit, create, reset | id `CheckDefinitions/{checkId}`; `SeededHash` (not a model attribute) |
| `AuditLogEntry` | Administrators | `ExpiresAtUtc` = now + `SeoTester:AuditLog:Retention` (1 y, D37) |
| `TargetCooldown` | not a persistent object | `TargetCooldowns/{host}`, `IExpiring` = the cool-down |

**Verification (F3, D18, D29, D42).** Three methods, as F3 lists, and no others: a TXT record at `_seotester.{host}`
with `seotester-site-verification={token}`, the same line in `/.well-known/seotester-verification.txt`, or a meta tag
`seotester-site-verification` in the home page's `<head>`. The token is 128 random bits per site. DNS goes through
`IDnsTxtResolver` (DnsClient 1.8.0, 5 s timeout, no cache); the two HTTP proofs through a named SSRF-safe client with
`RedirectsWithinOriginOnly` (an open redirect elsewhere proves nothing), 15 s, ≤ 3 redirects, at most 256 KB read.
Everything read is untrusted: exact comparison, bounded input, comments and the body ignored for the meta tag,
regexes with a timeout. **Claim flow (D18/D42):** with the proof found, a site of the same host verified by another
organization makes `VerifySite` call `ISparkOrganizations.RequestToJoinAsync` for that organization (its Owners get
the acceptance mail, personal organizations included) instead of verifying; a caller already in that organization is
told so. "Held" means verified there (Q-M8c). **Re-check:** `ReverifySitesCronJob` hourly re-checks verified sites last
checked more than `SeoTester:Verification:RecheckInterval` (1 day) ago, up to 200 a pass; `FailuresBeforeWithdrawal`
(3) failed checks in a row withdraw the verification (logged).

**Catalogue (§3.1, D16, D32).** 110 checks: Seo 19, Social 3, Performance 15, Security 26, Accessibility 10, Mobile 11,
Technical 17, Domain 9; Critical 6, High 16, Medium 38, Low 27, Info 23. Built from research §1 (features such as the
previewers left out; "count once" rows counted once; rel=prev/next dropped; meta keywords and the sitelinks search box
kept as Info "no effect"); severities the research leaves open were chosen as: certificate expiry High, zoom disabled
Medium. Each entry: id `{category}.{kebab}`, category, severity, weight (Critical 10, High 6, Medium 3, Low 1, Info 0),
layer, enabled, en/nl title, summary (placeholders `{url}`, `{n}`, …, the same in both languages) and how-to-fix, code
snippets per platform (Q-M8d), docs link. `CatalogueSeeder` (the settings seeder's pattern, optimistic writes) adds
new checks, refreshes an entry whose content still hashes to its `SeededHash`, and never touches an edit or an
administrator's own entry; `ResetCheckDefinition` restores one entry. An edit keeps the check id (implementations
are keyed by it). The editor is the generic persistent-object UI (menu "Check catalogue").

**Scoring (§7).** `ScoringSnapshot.Create(categoryWeights, checks)`: enabled checks only, ordered by id; `Version` = the
first 16 hex digits of a SHA-256 over a canonical form (algorithm number, category weights, each check's id, category,
severity and weight), independent of input order and culture. `ScoreCalculator.Score(snapshot, outcomes)` is pure: a
check's ratio is (pass + ½ warn) / (pass + warn + fail), N/A and errors excluded; a category is the weight-weighted
mean of its measured, weighted checks; a Critical check below 0.5 caps its category at 49 (Q-M8b); the headline is the
category-weight-weighted mean of measured categories (Domain weighs 0: shown, not counted); rounding half away from
zero at the end; grades per D15. `StartAudit` stores the snapshot on the run (the M9 aggregate scores with it).

**StartAudit / StartFullCrawl (§4.3, §6, D11, D14, D17).** In order: a full crawl needs a verified site and the
DeepCrawl gate (the action is also withheld from unverified sites); an in-progress run of the same origin in an
organization where the caller is Member or more is **joined** (participant added, no quota); an unverified target in
its cool-down (`TargetCooldowns/{host}`, 6 h, all users) is refused with 429 and `Retry-After`; the quota
(`audits`, `verified-audits` or `full-crawls`) is consumed or 429; then the run (limits from the gates and settings,
snapshot), the cool-down (conditional write: of two simultaneous starts one is refused), the log entry and
`SiteProbeMessage` are saved in one commit. No recipient exists yet (M9); the declared queue holds the message.
Robots.txt is the probe's (Q-M8e). Every start, join and refusal is an `AuditLogEntry`.

**Share links (X6, D11, D31).** `Share<Site>` (origin, verified, latest score, grade, date; child query `site-runs`
with date, status, score, grade, version) and `Share<AuditRun>` (status, dates, scores, category scores, check
results). A query is named by its **alias or id** in the declaration (`IQueryLoader.ResolveQuery`), not its name; the
guide now says so. `SharingSwitch` answers per object under `VerifiedOnly` (an unverified site's link stops, and comes
back once verified), through a new core seam:
- **Core change (dedupe-neutral, behaviour kept):** `ISparkShareGate.IsEnabledForAsync(typeName, entity)`, a default
  interface method answering yes, asked with the stored object after the type gate when a link is made
  (`ISparkShareLinks.IsEnabledAsync(type, id)`, used by `ShareLink`) and on every read. Without such a gate nothing
  changes: the 12 existing sharing tests pass unchanged. *Evidence: `ShareLinkTests.A_gate_can_refuse_one_object_on_making_and_on_reading_its_link`,
  red (1/13 failed) with the two calls removed, green 13/13.*

**D26.** `SeoOrganizationData` (`IOrganizationDataHandler`): deleting an organization deletes its page results, runs
and sites by query (parameters for values, `RqlIdentifier.Collection` for the constant collection names); the export
lists sites and runs. The abuse log and the per-host cool-downs stay.

**Tests** (`SeoTester.Tests`, the shared host now seeded from `Data/organizations.json` with `SeedFromJsonAsync`: Ann
Owner of Acme with Max Member and Vic Viewer, Bob Owner of Globex, personal organizations; DNS and the site answered
by `FakeDns` / `FakeWeb`, the latter replacing only the verification client's primary handler so the SSRF-safe
redirect rules still run): `ScoreCalculatorTests` 17, `ShippedCatalogueTests` 3, `SiteOriginAndProofTests` 16,
`SiteTests` 5, `VerificationTests` 6, `StartAuditTests` 8, `CatalogueTests` 5, `SharingAndPasskeyTests` 3, plus M7's 12.
**Red→green** (Developer licence; 25 mechanisms removed at once: the interceptor's kept fields, the personal-org
default, the check-id keep and id-from-check-id, the per-site sharing answer, the claim branch, the withdrawal, the DNS
proof, `RedirectsWithinOriginOnly`, passkeys, the seeder's hosted service and its edit guard, the reset, join,
cool-down, quota, the deep gate, the full-crawl guard (both), the critical cap, warn = ½, the head cut, the user-info
refusal): **24 of 75 failed**; green **75/75**. Confirmed rather than red: the row rules (framework: a Member cannot
add, a Viewer cannot edit or start, Bob reaches nothing, a Member cannot share or verify), the headline ignoring
Domain (a weight of 0 contributes nothing, so no mutation can turn it red), read-only server fields on create (the
pipeline already does not write read-only attributes; the interceptor resets them as a second line), the full-crawl
limits, the shipped catalogue's shape.

**Also:** two settings `SeoTester:Verification:{RecheckInterval, FailuresBeforeWithdrawal}`; menu groups "SEO" (sites,
audits, check catalogue) and "SEO administration" (audit log); `securityPosture.txt` gains the new grants (none
anonymous); translations en/nl for every label, action and message. Not run here: the browser (M11 builds the pages).

### M9 (2026-10-10): the crawl pipeline (probe, page batches, aggregate, cancel) and spikes S2, S7, S9

Commits: `b4f92702` (core: `SafeHttpTrace`, `ISafeHttpSockets`), `dae3746f` (SeoTester.Library: robots.txt, sitemaps,
HTML, the TLS hello probe, 92 checks), `97e94699` (the pipeline, cancel, tests), `151fb94f` (Retry-After bound), then
this record. Operator documentation: `docs/seotester/README.md` ("The crawl pipeline").

**Pipeline (§4.3), as built.** Recipients for `seo-probe`, `seo-crawl` and `seo-aggregate` (`apps/SeoTester/SeoTester/Crawler/`);
render, PSI and PDF stay M10's.
- **Probe** (`SiteProbeRecipient`, `SiteProber`): robots.txt is read **first** (Q-M8e as recommended): an opt-out by
  name, a `*` disallow-all, or a 5xx/unreachable robots.txt (RFC 9309 §2.3.1.4: "complete disallow") ends the run with
  the new status **`Blocked`** and an abuse-log entry (`RobotsBlocked`) before any other request to the host (see
  Q-M9a). Otherwise, paced like the crawl: home page, sitemaps (robots.txt `Sitemap:` lines on the same host, else
  `/sitemap.xml`; gzip; an index followed ≤ 3 children), `http://` → `https://`, the www/apex twin (only when it
  resolves), TLS, `security.txt`, `change-password` (only with a login form), `favicon.ico`, a random path (soft 404),
  the home page's manifest, icons, OpenSearch description, ≤ 3 stylesheets and ≤ 3 scripts (compression, caching,
  minification, `@font-face`, `prefers-color-scheme`, `prefers-reduced-motion`); then DNS (`ISeoDns`), RDAP and
  hstspreload (`ExternalLookups`, cached in `LookupCaches/{kind}/{key}`: bootstrap 7 d, RDAP and hstspreload 1 d, a
  failure 15 min). `/.git/HEAD`, `/.env`, `/.DS_Store`, `/web.config`, `/phpinfo.php`, `/.htpasswd` and the listing of
  ≤ 3 folders the home page's assets live in are requested **only** when the run holds `IntrusiveProbes` (D11). The
  site-level findings, the home page's `PageResult`, the seeded `CrawlFrontier` and `PageBatchMessage(run, 0)` commit
  together; a redelivery finds the frontier (stored "must not exist") and stops.
- **Crawl** (`PageBatchRecipient`): a batch takes up to `SeoTester:Crawl:BatchSize` (25) pages allowed by robots.txt,
  stops taking more after `BatchBudget` (90 s), fetches through `HostPacer` (the tier's connections per host and request
  interval, a robots.txt `Crawl-delay` when larger, capped at 30 s; `Retry-After` defers, a URL is asked ≤ 3 times),
  judges each page (`PageChecks`), extends the frontier (same host, breadth first, files and assets skipped, at most the
  page limit + 50 % queued), writes progress at most every 2 s (patches: live, X4), and commits pages, frontier (with its
  change vector: a duplicate delivery of the same batch cannot commit twice), progress and the next message (delayed by
  a `Retry-After`) in one `SaveChanges`. It stops at the page limit, the wall time (the run's `WallTimeMinutes`, i.e.
  the full-crawl ceiling for full crawls), an empty frontier, a cancel, or > 50 % errors after ≥ 10 pages; a verified
  run then checks ≤ 50 external links (one `HEAD` per host, `GET` on 405/501) and adds `crawl` to `StagesDone` and
  enqueues the aggregate (M10's render and PSI stages will each enqueue it when they finish last).
- **Aggregate** (`AggregateRecipient`): pages are read by id prefix (~~`{runId}/pages/`~~ the site's `{siteId}/pages/`,
  kept to the URLs the run stored, `CrawlFrontier.Stored`: D8's one result per page; `StreamAsync`: no index, never
  stale), so a page an earlier scan left is not counted and one a concurrent scan of the site fetched again still is; the
  render and PageSpeed findings come from the frontier too (`SampledFindings`), which a concurrent fetch cannot replace; `RunTally` gives one vote per (check, URL), the worst status winning when a page check and the crawl's look at
  the same URL disagree; `CrawlChecks` adds the site-level S checks over all pages; `ScoreCalculator.Score` with the
  run's own snapshot (D32); `Delta` New/Fixed/Unchanged against the site's previous completed run; one commit finishes
  the run (patches), updates `Site.Latest*` and deletes the frontier; then `ISparkNotifier` tells the participants
  (a failed notification is logged, never retried: the run is over).
- **The run is written only by patches** (`RunPipeline`), so the pipeline never overwrites a concurrent join or cancel
  and vice versa. **Fixed in M8's join on the way:** `JoinAsync` stored the whole loaded run, which would have put a
  stale `Status`/progress back over the crawler's writes; it now patches `Participants`. A stage failing on its queue's
  last attempt marks the run `Failed` (otherwise a dead-lettered run would stay `Running`, and joinable, for ever).
- **Cancel** (`CancelAudit` on `AuditRun`, Members by `[RequiresOrganizationRole]`, right `CancelAudit/AuditRun` to
  signed-in users; `securityPosture.txt`: nothing anonymous): patches `CancelRequested`; the probe or the next batch
  ends the run `Cancelled` and deletes the frontier; a finished run answers "already finished".
- **What a redirect is.** A URL that redirects is judged as a redirect (chain length, temporary vs permanent), not by
  the content it led to; its target is queued and judged under its own URL. *Evidence: before this, `/old` (301 → `/a`)
  was counted as a second page titled like `/a` and `seo.duplicate-titles` sampled three URLs: red in
  `CrawlPipelineTests.An_audit_probes_crawls_in_batches_scores_and_finishes_the_run`, then green.*

**Checks (§3.1):** 92 of 110 implemented; the 18 others need Chromium or PSI and wait for M10
(`CheckImplementations.AwaitingRenderOrPsi`; the test `Every_catalogue_check_is_implemented_now_or_waits_…` pins
both lists against the catalogue). Per category, implemented / M10: **Seo 19/0, Social 3/0, Performance 10/5, Security
25/1, Accessibility 4/6, Mobile 8/3, Technical 14/3, Domain 9/0.** M10: `performance.{core-web-vitals, lab-score,
page-weight, lazy-loading, code-splitting}`, `security.analytics-consent`, `accessibility.{aria, color-contrast,
touch-targets, heading-order, landmarks, axe-other}`, `mobile.{responsive, service-worker, service-worker-caching}`,
`technical.{raw-vs-rendered, spa-analytics, mobile-parity}`. Checks the catalogue marks H+R (single h1, structured data,
thin content, resource hints, mixed content, form labels, share buttons) are measured on the raw HTML now; M10 adds the
rendered view. Three levels: `PageChecks` (49, per page), `SiteChecks` (36, once per run), `CrawlChecks` (11, over all
pages); `performance.compression`, `seo.canonical`, `seo.hreflang` and `technical.sitemap` are measured at two levels
and add up. Thresholds chosen where the research leaves a range (all data in code, versioned by the scoring snapshot
only through weights): title < 30 or > 60 characters or > 600 px, description 70–160, thin content < 200 words,
> 4 preconnects, > 20 `style=` attributes, HSTS ≥ 1 year, a static asset's `max-age` ≥ 30 days, minified when
whitespace and comments are ≤ 10 %, certificate < 14 d fail / < 30 d warn, domain < 30 d fail / < 1 y warn, crawl depth
> 3, near-duplicate when SimHash (word 3-shingles) differs in ≤ 3 of 64 bits (pairwise up to 5 000 pages, exact
matches above). Untrusted input everywhere: body caps (3 MiB pages, 10 MiB sitemaps, 512 KiB lookups), AngleSharp with
scripting off, `JsonDocument` with a depth limit, `XmlReader` with DTDs prohibited, regexes with timeouts, robots.txt
matched by a hand-written wildcard walk (a hostile 4 000-character pattern against a 20 000-character path: < 2 s,
tested).

**Framework additions (behaviour of existing callers unchanged):** `SafeHttpTrace` (opt-in per request: redirect hops
and the wire `Content-Encoding`) and `ISafeHttpSockets` (a raw connection under a named client's port and address
rules, for the TLS probes), both in `libs/spark/MintPlayer.Spark/Http/`. *Evidence: the 116 existing SafeHttp tests pass
unchanged; 3 new tests (hops and encoding, hops up to a refused redirect, sockets refused/allowed).* `MintPlayer.Spark`
was already `11.0.0-preview.104` on this branch: no version change.

**Dedupe:** `IDnsTxtResolver` (M8, verification) became `ISeoDns` (`apps/SeoTester/SeoTester/Dns/`): the same TXT call
plus `QueryAsync(name, type)` and `ZoneApexAsync(host)` for the domain checks. Verification behaves identically
(`VerificationTests` 6/6 unchanged).

**Spike S2 (TLS on the target, msquic).** Measured 2026-10-10 from the build machine (Windows 11 26200, .NET 11),
`LegacyTlsProbe` and an `SslStream` handshake per badssl.com reference host:

| Host | TLS 1.0 hello | TLS 1.1 hello | Platform handshake |
|---|---|---|---|
| `tls-v1-0.badssl.com:1010` | **Accepted** | Refused | Tls (1.0), no errors |
| `tls-v1-1.badssl.com:1011` | Refused | **Accepted** | Tls11, no errors |
| `tls-v1-2.badssl.com:1012` | Refused | Refused | Tls12, no errors |
| `badssl.com:443`, `expired.`, `wrong.host.`, `self-signed.` | Accepted | Accepted | Tls12; `ChainErrors` (expired 2015-04-13, self-signed), `NameMismatch` (wrong.host) |

  The hand-built hellos match badssl's own labels on all seven hosts (the 443 hosts do serve 1.0/1.1, as badssl's
  dashboard lists), 240–380 ms per hello. The raw probe sends bytes and reads one record, so it gives the same answer on
  any platform; that is why it exists (§1 hosting: OpenSSL 3 at its default level, like current Windows by policy,
  refuses to *speak* 1.0/1.1). Notably this Windows machine's SChannel still *spoke* TLS 1.0 both as client
  (`tls-v1-0.badssl.com` above) and as server (`TlsProbeTests` first saw its own loopback server accept the 1.0 hello
  until the test pinned `Tls12 | Tls13`): an `SslStream` result depends on the machine, the raw probe does not.
  Tests: `TlsProbeTests` (canned ServerHello 1.0 → Accepted, alert / ServerHello 1.2 / closed → Refused, silence and
  non-TLS → Inconclusive; a loopback `SslStream` server through `ISafeHttpSockets`: version, ALPN `h2`, expiry,
  untrusted certificate, legacy refused; a private address refused before any byte). **HTTP/3 decision:** no
  `libmsquic` in the image; `performance.http3` (Info, weight 0) reports "advertised (Alt-Svc)" or not. **Not run:** the
  platform handshake inside the Debian `aspnet:11.0` image (Docker Desktop not running): Q-M9b.

**Spike S7 (crawl chaining).** The batch is bounded twice: `BatchSize` 25 pages **and** `BatchBudget` 90 s
of fetching (no new page after it). At the unverified 1 request/s a full batch is ~25 s (the pacer's gaps measured exact:
≥ 280 ms at a 300 ms setting across probe and batches, one connection); the worst case, a server that lets every
request run into the 30 s client timeout, is 90 s + one 30 s fetch per connection = 2 min, a fifth of `seo-crawl`'s
10-minute `HandlerTimeout`. A 100-page unverified run is 4 batches; the 20 000-page full-crawl ceiling 800 batches,
each commit writing one frontier (pending URLs + visited hashes, ~16 hex characters per URL). **Kill test**
(`CrawlResumeTests`): batches of 10, the host stopped while `/k5` hangs in batch 0 (`/k0`–`/k4` fetched, nothing of the
batch committed; Messaging drains handlers on a graceful stop, so the host's 2 s shutdown timeout cancels the hanging
one, as a killed container would end it); a new host on the same database reclaims the message after `ClaimTtl` (4 s
here, 5 min in production) and finishes the run: 21 pages, each stored once, `/k0` fetched twice. *Red first: with a
graceful stop alone the batch simply finished (`/k0` fetched once), which proved nothing; green 1/1, 11 s.*

**Spike S9 (HTML parsing).** **Decision: AngleSharp 1.7.2** (MIT, HTML5 tree construction, no scripting;
AngleSharp.Css not needed: `@font-face`/`prefers-*` are found by bounded scans). Measured 2026-10-10 (Release, build
machine, 20 iterations after warm-up, synthetic pages with navigation, sections, images, links, scripts):

| HTML | Parse | Allocated | All page checks + links + fingerprint | Allocated |
|---|---|---|---|---|
| 19 KB | 2.1 ms | 0.4 MB | 6.4 ms | 1.5 MB |
| 174 KB | 24.5 ms | 3.0 MB | 76.6 ms | 12.7 MB |
| 1.3 MB | 50.7 ms | 21.9 MB | 304 ms | 75 MB |

  Even the 1.3 MB page costs a third of a second against ≥ 1 s between requests to an unverified host, so CPU is not
  the crawl's limit; pages are parsed one at a time per connection and dropped after their result is stored (no raw
  HTML kept, §4.2). Parsing stops at 2 Mi characters (`HtmlPage.MaxCharacters`), the body read at 3 MiB.
  **Title width without a browser:** `TitleWidth` (Arial = Helvetica AFM advance widths at 20 px) against Chromium's
  `measureText` with `20px Arial` (playwright, same machine): 468.0 vs 467.3, 566.4 vs 566.3 (30 × W), 133.2 vs 133.3
  (30 × i), 588.0 vs 585.8 px: within 0.4 %, so the ~600 px cut is judged without rendering.

**Politeness, measured.** `HostPacer` counted the interval from the *scheduled* start; a timer that fires late made two
real starts 138 ms apart at a 150 ms interval (`CrawlerUnitTests.The_pacer_keeps_one_connection_…`, red). It now counts
from the real start (green). End to end at 300 ms: every request start to an unverified host ≥ 280 ms apart, across
the probe and every batch, never more than one in flight (`CrawlPipelineTests.An_unverified_site_gets_one_connection_…`).
A URL answering 429 with `Retry-After: 1` is asked three times, each ≥ 950 ms apart, then recorded as a 429 page
(before the bound it held the crawl until the wall time).

**Tests** (`SeoTester.Tests`, 127; nothing leaves the machine: the host fakes DNS (`FakeDns`), TLS (`FakeTls`) and
every named client's primary handler (`FakeWeb`), so the SSRF-safe handlers still run; recorded RDAP/IANA/hstspreload
answers in `Data/Fixtures`): `RobotsTxtTests` 11, `SitemapReaderTests` 5, `PageChecksTests` 8, `SiteAndCrawlChecksTests`
7, `TlsProbeTests` 5, `CrawlerUnitTests` 7, `CrawlPipelineTests` 8 (own host, collection "SeoTester pipeline host"),
`CrawlResumeTests` 1 (own host), plus M7/M8's 75 (one assertion relaxed: a started run is `Queued` **or** `Running`, the
probe now starts at once). Spark.Tests: `SafeHttpClientTests` +3. **Red→green** (Developer licence; 20 mechanisms removed at once (robots.txt Disallow in the batch, the opt-out, cancel,
the interval, the intrusive-probe gate, "a redirect is not judged by its target", the redirect trace (core), the delta,
the external-link check, the legacy hello's version compare, allow-wins-a-tie, the notification, the Retry-After bound,
the real-start pacing, the aggregate's link set, the near-duplicate scan, the active/passive mixed-content split, the
CAA issuer match, the sitemap DTD prohibition): **15 of 127 SeoTester tests failed** and **2 of 30**
`SafeHttpClientTests` (the trace); green **127/127** and 30/30 (`SafeHttp*` 119/119). Several mechanisms share the end-to-end
test `An_audit_probes_crawls_in_batches_…` (robots Disallow, intrusive gate, redirect judging, notification, link set),
so only their union is shown red. Confirmed rather than red: the sitemap DTD (with DTDs ignored instead of prohibited the
undeclared entity still fails the read, and `XmlResolver = null` blocks external entities anyway); the frontier's change
vector against a concurrent duplicate batch (no test makes two deliveries race); M8's join now patching (its tests pass
unchanged; the race it closes is not reproduced). Before the run, three defects were found red and fixed: the redirect
judged by its target's content, the pacer counting from the scheduled start, and a URL answering 429 holding the crawl.)

**Open (§11):** Q-M9a, Q-M9b.

### M10 (2026-10-10): render sample, PageSpeed Insights, PDF; spikes S3 and S5

Commits: `da08f4c8` (core: SafeHttp `FollowRedirects`), then the Library (render/PSI checks, parser, fixtures), the host
(render, PSI, PDF stages, pipeline join, tests) and this record. Operator documentation: `docs/seotester/README.md`
("The crawl pipeline", "The Chromium container (for M12)").

**Pipeline (§4.3), as built.** The crawl's last batch enqueues `RenderSampleMessage` and `PsiMessage` (instead of the
aggregate) in its commit; each of the two writes its findings, adds its stage to `StagesDone` and enqueues
`AggregateMessage` in **one** commit (`RunPipeline.FinishStageAsync`); the aggregate returns unless `crawl`, `render`
and `psi` are all there, so only the last stage's message aggregates. This replaces "the last one enqueues the
aggregate with `BroadcastOnceAsync`" (§4.3): deciding "last" after a commit is not crash-safe (a host stopping between
commit and broadcast strands the run), the in-commit enqueue is (outbox). Render and PSI start **after** the crawl, not
beside it: their sample is chosen from the crawled pages, and an unverified host keeps one connection (Q-M10c).
*Evidence: removing the aggregate's wait made `RenderPipelineTests.An_audit_renders_…` fail ("technical.raw-vs-rendered
not present": the run was scored before the render) and `CrawlPipelineTests.An_audit_probes_…` fail; restored, green.*

**Sample (§6).** `RenderSample.Pick`: HTML pages that answered 200 and did not redirect, the home page first, then the
shallowest, **one per URL template** (`/blog/a` and `/blog/b` are `/blog/*`, digits generalised) before a second of any;
`RenderedSample` (3 / 10) for the render, `PsiSample` (1 / 5) for PageSpeed. One render at a time per process
(`RenderGate`, the `ConcurrentRenders` setting read per render; a cancelled waiter no longer blocks the next one).

**Render (F12).** `PageRenderer`: each sampled page twice, each in a fresh context with service workers blocked:
375 px phone (`isMobile`, touch, DPR 2: layout and text size, images and the LCP element, the network log, V8 precise
coverage through a CDP session, axe-core 4.13.0 with tags wcag2a/aa, wcag21a/aa, wcag22aa, best-practice) and 1350 px
desktop (rendered text and links, the SPA route probe on the home page). The served HTML is the routed main-document
response, parsed by AngleSharp (`RawView`). The checks are pure (`SeoTester.Library/Checks/RenderChecks.cs`), with these
thresholds: page weight warn > 80 requests or > 3 MB, fail > 150 or > 6 MB (decompressed bytes: the router's client
decompresses, so above Lighthouse's transfer-size limits); lazy-loading fails a lazy LCP image, warns ≥ 3 eager images
below the first screen; code-splitting warns ≥ 150 KB with ≥ 50 % never run, fails ≥ 1 MB with ≥ 60 %; responsive fails
on horizontal overflow at 375 px or > 50 % of text below 12 px (Lighthouse: 60 % legible); raw-vs-rendered fails when the
served HTML has < 1/5 of ≥ 100 rendered words or lacks a title/h1 the render has, warns < 60 %; mobile parity fails < 50 %
of the desktop words, warns < 70 % or > 10 % of its same-site links (≥ 3) missing; axe: a critical/serious violation
fails its check, moderate/minor warns. axe rules map to checks by id (`color-contrast`, `target-size`, `heading-order`,
`landmark-*`/`region`/`bypass`/`skip-link`, `aria-*`), the rest to `axe-other` except the rules a raw check already
measures (image-alt, label, html lang, document title, meta viewport, link name: not counted twice). Service worker: the
registration is recorded by the init script (never performed, §6) and the script fetched through the router: pass when it
answers 2xx and handles `fetch` (it can serve offline), warn otherwise, fail only for a site with the PWA profile when
none is registered; caching warns when `max-age` > 0. Analytics: GA4, GTM, Meta, Clarity, Hotjar, Plausible, LinkedIn,
Bing UET, TikTok, Matomo recognised by request; consent warns on a hit before any interaction or a tracker without a
known consent manager (Info, weight 0); SPA analytics fails when a click on a same-site link changed the URL without a
document request and no hit followed.
- **Measured on the way:** an emulated phone zooms out to fit overflowing content, so `scrollWidth > innerWidth` missed a
  900 px block at 375 px (red in `RenderPipelineTests`, `mobile.responsive` Pass); compared with `screen.width`, green.

**Routing (§6, D6), `RenderNetwork` + `BrowserRouter`.** Every HTTP(S) request of every context is fetched by the app
through the new named SSRF-safe client `SeoTester.Browser` with `FollowRedirects = false` (core, below): a 3xx is handed
to Chromium, which asks for the target, which is routed and checked again. The audited host's requests hold one of its
tier connections (`HostPacer.WaitAsync(paced:)`), its documents also wait for the tier's interval (Q-M10a); other hosts
4 at a time; robots.txt Disallow applies to the audited host; GET sub-resources are fetched once per run (≤ 2 MB, not
`no-store`); request bodies ≤ 1 MiB; analytics hits are answered 204 locally and never sent on (*evidence: with the sink
removed, the fake web received `google-analytics.com/g/collect` and the test failed*). The browser's user agent is a
phone/desktop Chrome string **plus** `MintPlayerSeoTester/1.0 (+…/bot)`, sent on the wire (Q-M10e). WebSockets are
closed before they connect (`RouteWebSocketAsync`, no server connection).

**Core change, behaviour of existing callers unchanged:** `SafeHttpOptions.FollowRedirects` (default `true`; `false`
returns a redirect as it is, `MaxRedirects` not applied). *Evidence: 2 new tests red (302 followed) → green;
`SafeHttp*` 121/121 (the 119 before, unchanged, + 2).* `MintPlayer.Spark` already `11.0.0-preview.104` on this branch: no
version change. **Dedupe:** `HostPacer` (M9) gained an unpaced slot for the render instead of a second pacer;
`PageFetcher.FetchAsync` takes extra request headers (the PSI key) instead of a second fetcher; the PSI cache is a
`LookupCache` like RDAP's.

**Spike S3 (Chromium; measured 2026-10-10 on the build machine, Windows 11, Chromium 153.0.8010.12 headless shell,
Playwright 1.63.0; no Docker).** `BrowserEscapeTests`: a page with every vector the PRD names (WebSocket, WebRTC STUN
and TURN, WebTransport, prefetch, dns-prefetch, preconnect, `sendBeacon`, a keepalive fetch, `<a ping>`, EventSource,
img/iframe/stylesheet/fetch, a service worker, an iframe's own `RTCPeerConnection`) against TCP and UDP listeners on
loopback:

| Setup | Reached the listeners |
|---|---|
| control: routing alone (every routed request aborted), no flags, no init script | `GET /prefetch`, `GET /ws`, `POST /beacon`, keepalive `GET /fetch-localhost`, `GET /ws-localhost`, `POST /ping`, 6 STUN datagrams |
| E1: flags + init script + WebSocket routing, no proxy | nothing |
| E2: dead proxy only, no flags/init/WebSocket routing | 6 STUN datagrams (WebRTC) |
| Connect to a plain `playwright run-server`, flags sent as `x-playwright-launch-options` | prefetch, beacon, keepalive fetch, ping: **the server drops client `args` unless started with `--unsafe`** |
| all layers, launched; over `ConnectAsync` to `run-server` (proxy as a launch option, which it keeps); over `ConnectAsync` to the container's `server.mjs` | **nothing** (green) |

  So: Playwright's routing does not see prefetch, beacons, keepalive fetches, pings, WebSockets or UDP. Two
  independent guards close them: the dead proxy (`http://127.0.0.1:9`, bypass `<-loopback>`, a Playwright *launch
  option* so even a plain run-server applies it) for TCP, and the init script (WebRTC/WebTransport removed in every
  frame) plus `--webrtc-ip-handling-policy=disable_non_proxied_udp` for UDP; `--host-resolver-rules="MAP * ~NOTFOUND"`
  alone also stopped the TCP vectors (E1). Every routed request to loopback was **refused by the SSRF-safe client**
  (layer 1: the network log shows `/img`, `/style.css`, `/frame`, `/fetch`, `/eventsource`, `/ping` = blocked). The
  container therefore runs `apps/SeoTester/chromium/server.mjs` (`launchServer`, flags fixed server-side; a test keeps
  them equal to `ChromiumBrowser.HardeningArgs`), not `run-server`. **`internal: true` for `seo-browser`:** nothing the
  browser needs leaves it (every request is fulfilled by the app), so yes, to be confirmed in the real network at M12.
  Timings (fixture pages, warm): browser ready 1.1–1.4 s launched, 0.4–2.5 s over ConnectAsync; per viewport load
  12–100 ms + settle ≥ 500 ms (the network-idle window), axe-core 120–160 ms, coverage 1–13 ms, collect 2–17 ms, the SPA
  probe 2.1 s; Chromium's processes 160–210 MB working set (headless shell, contexts closed between pages); **PDF**
  1.3 s for an 89.5 KB A4 report. Static resources fetched once per run: 9 renders of 3 pages made 20 network fetches.
  **Not measured (no Docker on the build machine): the image's size and RAM per page on Debian, `shm_size`, and the
  `internal: true` network itself: Q-M10b.**

**Spike S5 (PageSpeed Insights; measured 2026-10-10 with the owner's key, 5 calls).** The key was read from SeoTester's
user secrets by a throwaway tool and sent as `X-Goog-Api-Key` (never printed, never in a URL; the fixtures were checked
for it and a test asserts no `key=`/`AIza` in them).

| Call | Status | Latency | Answer |
|---|---|---|---|
| example.com, mobile, 4 categories | 200 | 8.3 s | 419 KB; Lighthouse 13.5.0; performance 1.00, a11y 0.96, BP 1.00, SEO 0.80; CrUX page **and** origin data, all FAST (LCP p75 1038 ms, INP 87 ms, CLS 0) |
| mintplayer.com, mobile, performance | 200 | 29.4 s | 305 KB; performance 0.46; **no CrUX data** (`loadingExperience` = `{initial_url}` only, no origin block) |
| mintplayer.com, desktop, performance | 200 | 15.9 s | 397 KB; performance 0.71 |
| example.com with `fields=…audits/*(…)` | 400 | 0.24 s | a field mask cannot name map entries ("Cannot find field 'value'") |
| example.com with `fields=…,audits` | 200 | 8.1 s | 295 KB (-30 %): the mask SeoTester uses (`PsiReport.FieldMask`) |

  Fixtures (`SeoTester.Tests/Data/Fixtures/psi_*.json`): the three 200 answers **trimmed** to the fields the parser reads
  (categories, metrics, insights, CrUX; real values), plus S4/S5's keyless 429 JSON (`quota_limit_value: "0"`) and the
  HTML 429. Decisions: mobile, performance only (accessibility is axe's, Q-M10f); the 5 weighted metrics; insights
  scoring < 0.9 are the "main opportunities", ordered by their LCP + FCP savings; lab score ≥ 90 pass / ≥ 50 warn / else
  fail; Core Web Vitals by CrUX's own category (FAST/AVERAGE/SLOW, worst metric wins; CLS's percentile is × 100), the
  page's data else the origin's, else not measured; a measured page cached a day; client timeout 100 s (five pages fit
  the queue's 10 minutes); a 429 deferred `QuotaRetries` (3) × `QuotaRetryDelay` (2 min × attempt), then "not measured:
  the PageSpeed Insights quota is exhausted". No key: both checks not measured, "PageSpeed Insights is not configured".
  *Evidence: the parser tests run on the recorded answers; the quota test (fake web answering the recorded 429) asks
  twice with `QuotaRetries` 1 and records "not measured"; with the retry removed it asked once (red).*

**Not measured, with why (§7).** `RunCheckResult.Note` (model attribute, label "Not measured because"): the reason of a
not-applicable finding or the message of an error finding, e.g. "PageSpeed Insights is not configured", "the headless
browser is not configured", "the Chrome UX Report has no field data for this page or its origin". *Evidence:
`CrawlPipelineTests.An_audit_probes_…` (no browser, no key) asserts both notes; red with the note removed.* Without a
browser (`SeoTester:Browser:Mode` Off, the default outside Development and Production) the 16 rendered checks are not
measured; an unreachable browser makes them errors; neither counts in the score, and the run completes.

**PDF (F14).** `ExportReport` on a completed `AuditRun` (`[RequiresOrganizationRole(…, Viewer)]`, right
`ExportReport/AuditRun` to signed-in users; posture: nothing anonymous) enqueues `PdfMessage(run, culture, requester)`
(culture from the request, en or nl), or returns the link when a report of that language exists. `seo-pdf` renders
`ReportPage` (self-contained HTML: inline print CSS, no script, no image, every value encoded; scores, categories,
issues by category with the catalogue's current how-to-fix text and sample URLs, "not measured" with notes) in a context
with JavaScript off and every request aborted, prints A4, and stores it as the attachment `report.pdf` of
`RunReport` `{runId}/reports/{en|nl}`, **expiring after 7 days** (X1; a report can always be printed again from the
kept run, Q-M10d); the requester is notified with `GET /seotester/report.pdf?run=…&culture=…`, which serves it to
members of the run's organization only and answers 404 to anyone else, exactly as for a missing report (#453).
*Evidence: `RenderPipelineTests` downloads it as a Viewer (`%PDF-`, 89.5 KB) and gets 404 as Bob (another organization)
and anonymously; with the membership check removed Bob got 200 (red).*

**Checks: 110 of 110** (Seo 19, Social 3, Performance 15, Security 26, Accessibility 10, Mobile 11, Technical 17,
Domain 9). `CheckImplementations.AwaitingRenderOrPsi` is gone; `Every_catalogue_check_is_implemented` pins
`Implemented` = the catalogue.

**Tests** (`SeoTester.Tests` 127 → **160, all green** (2 min 42 s); Spark.Tests `SafeHttpClientTests` +2, `SafeHttp*` 121/121). New:
`RenderAndPsiChecksTests` 25 (parser on the recorded answers, thresholds, axe mapping, analytics recognition, coverage,
sample, the Dutch report), `BrowserEscapeTests` 5 (S3, collection "SeoTester browser"), `RenderPipelineTests` 3 (own
host "SeoTester render host": browser Launch, a fake PSI key). The browser tests need `playwright.ps1 install chromium`.
**Red → green** (Developer licence): all 15 render/PSI/PDF/escape mechanisms removed at once failed 9 of 41 affected
tests; the ones that end-to-end test masks were then removed one at a time (the sink, the key in a header, the
responsive width, the quota retry, the PDF membership check, the redirect exclusion in the sample): each red alone;
restored, green. The full SeoTester.Tests run: 160/160 (the route snapshot gained `GET /seotester/report.pdf`, an
intended change).

**Open (§11):** Q-M10a–Q-M10f.

### M11 (2026-10-10): the pages

Commits: `8e443891` (core: `ISparkShareLinks.IsValidAsync`), `cc85abdb` (ng-spark: refusal text and the action
result), `5d4f6234` (SeoTester server: catalogue texts, badge, operator dashboard, the start opens the run),
`7854d197`, `9f341153`, `c0c476b4`, `2628d8c5` (ng-spark fixes found in the browser), `260924a7` (the SPA, the model
layout, en/nl), then this record. Operator documentation: `docs/seotester/README.md`.

**How the pages are built: the generic detail page, the model and renderers, not hand-written pages.** Every page is
ng-spark's `spark-po-detail` (one `poDetail` override, `seo/po-detail-page.component.ts`, adds the panels a renderer
cannot be). The report is the run's own attributes laid out by the model (tabs *Report* / *Details*, groups) and drawn
by full-width attribute renderers (`seo/renderers.ts`), so the live type, the actions, sharing and the share page all
work on it unchanged:

| Renderer | Attribute | Draws |
|---|---|---|
| `seo-score` | `AuditRun.Score`, `Site.LatestScore` | ng-spark's `spark-gauge` (letter grades) at the model's size, 32 px in a grid row |
| `seo-run-progress` | `AuditRun.Phase` | the run's state: live stage (1–5), pages of the run's limit, current page; why it was blocked, failed or cancelled |
| `seo-category-scores` | `AuditRun.CategoryScores` | a `spark-threshold-bar` per category (catalogue order), "not measured", "capped by a critical issue", Domain "not scored" |
| `seo-check-results` | `AuditRun.CheckResults` | every check: Issues / Not measured / Passed / All, grouped by category, issues ordered by status, severity, weight × pages (F5); New / Fixed / Unchanged; why not measured (`Note`); sample pages; how to fix (the catalogue's explanation in the viewer's language, a snippet per platform with copy, the docs link); "show how to fix" remembered per browser |
| `seo-verification` | `Site.VerificationToken` | verified (how, last check, failing re-checks) or the three proofs exactly as `OwnershipProof` matches them, each with a copy button |
| `seo-previews` | `PageResult.Title` | Google desktop and phone results (cut at 60/70 and 158/120 characters), a shared-link card |
| `seo-page-findings` | `PageResult.Findings` | the page's findings, failures first, the summary filled with the page's own values |

Panels (`extraContentTemplate`): the site's score trend (`site-runs`, completed runs, `spark-trend-chart` 0–100, a
line per `ScoringVersion` change, F8); the run's "page details expired" note (7 days, D8); the operator dashboard.

**Pages and routes.** `/` → `/home` (the landing page: what is checked and how; signed in, *my sites* and the live
audits as query cards). The redirect moved to the top level: as a child of the shell's `''` route it never ran and `/`
was blank (CodeCoverage's routes say the same). `/po/site/{id}`, `/po/audit-run/{id}` (Report, Details, the pages
sub-query), `/po/page-result/{id}`, `/po/operator-dashboard/main` (menu *SEO administration → Dashboard*),
`/shared/{Type}/{token}?id=…` (the public share page), the generic organization, notification and settings pages.
**The notification link `/po/auditrun/{id}` from M9 opened nothing**: the SPA resolves `/po/{type}` by the type's
alias or id, and the alias is `audit-run`; it is now `RunPipeline.RunUrl` = `/po/audit-run/{id}`, pinned by
`CrawlPipelineTests`. *Run audit* and *Full-site crawl* navigate to the run (a `Navigate` client operation, joined runs
included), where it is followed live.

**Server additions.**
- `GET /seotester/catalogue?culture=en|nl` (anonymous; Q-M11b): the catalogue's texts in one language and the category
  and severity labels, for the report and the share page.
- `GET /seotester/badge.svg?site=…&token=…` and the action *Badge* (`ShareBadge`, site Owners and Admins, D31): F11's
  badge rides on the site's share link (Q-M11c); core gains `ISparkShareLinks.IsValidAsync` (the share page's own token
  check made public), every refusal is one 404.
- The operator dashboard (F16, D28): a virtual persistent object `OperatorDashboard` (hand-written model, no
  documents) gated by `Read/OperatorDashboard` (Administrators), so its menu entry is hidden from everyone else, and
  `GET /seotester/admin/dashboard` (same right, else 404): users, organizations, sites (verified), audits (total, 24 h,
  7 days by status, running), refused starts of the week by kind, the database's size on disk and per collection, and
  every organization's sites (500 newest) with the organization's name. The abuse log is the `audit-log` query card
  under it. A parentless query cannot be the virtual object's sub-query: Spark refuses to run it with a parent
  ("cannot be scoped to that parent"), seen in the browser.
- Refusals are translated (`seo.coolDownRefused`, `seo.auditQuotaRefused`, …) and keep 429 + `Retry-After`; the abuse
  log keeps an English detail. The SPA appends "Try again in 5 hours." (ng-spark, below).
- `AuditRunActions.OnDisableActionsAsync`: a finished run offers no *Cancel audit*, an unfinished one no *Download PDF*.
- *Download PDF* downloads at once when the report is already printed (the action's result reaches the page through
  ng-spark's `customActionExecuted`), else the notification with the link follows.

**ng-spark (22.33.0 already on this branch; no bump), each red→green:**
- `describeSparkError` (services): a refused action's envelope reason (`result.error`, validation messages) plus "Try
  again in {wait}" from `retryAfterSeconds`/`Retry-After` (`spark.tryAgainIn`, `spark.wait.*`, en/fr/nl in core's
  translations). The detail page and the grid showed Angular's "Http failure response … 429" before.
- The detail page shows an action failure as a dismissible alert above the page (`actionError`); it used to replace
  the whole page with the alert.
- `customActionExecuted` carries the action's result.
- **Q-M6d, the renderers in a browser:** the gauge's grade was `<span class="badge text-bg-…">`, but ng-bootstrap ships
  the badge CSS with its component, so the letter was unstyled (black on the dark page). It is `<bs-badge>` now. The
  letter-grade colours read well (A/B green, C/D amber, F red) and the ring at 72 px on the report, 56 px on the site,
  32 px in a grid. `spark-delta` is not used by SeoTester's pages (the trend shows the history), so its defaults stay.
  Seen in dark mode only (the owner's default); light mode not looked at.
- The share page handed an AsDetail array's `value` (empty) to its renderer: `objects` holds the rows; it uses
  `rendererValue` like the detail page. Its `row`/`col`/`table` classes had no CSS (grid and table CSS ship with
  `bs-grid`/`bs-table`), so it was unstyled edge to edge; component styles lay it out.
- `withSparkLanguage()`: CodeCoverage's `sparkLanguageInterceptor` moved into ng-spark unchanged (with its spec);
  CodeCoverage and SeoTester call it. Server-resolved texts (the dashboard title, refusals) followed the browser's
  language, not the selector.
- A notification (bell or push click) whose same-origin link no route matches (`/seotester/report.pdf`) is loaded as a
  page; the router threw NG04002.
- SeoTester's own: `provideSparkAuth({ loginUrl: '/sign-in' })` (the auth bar and sign-out went to `/login`, which an
  external-only app does not have).

**Seen in a browser** (`dotnet run --launch-profile https`, SparkId, a dev account `ann.m11@example.com`, Chromium
over the playwright MCP, dark theme, 1400 px; screenshots in the session scratchpad, not the repo): the landing page
signed in; a site (gauge, verification instructions, copy buttons, trend panel's one-run note, the audits grid);
`https://example.com` and `https://coverage.mintplayer.com` audited end to end (unverified tier; the second followed
live from "Reading robots.txt" through "Rendering in a browser" to the completed report without a reload); the report
in English and Dutch (catalogue texts, labels, categories, how-to-fix with snippets); the page view with previews and
findings; *Run audit* during the cool-down ("… Try again in 5 hours.", page kept); *Download PDF* (printed, then
downloaded); the notification bell, a run notification opening `/po/audit-run/…`, the PDF notification; a run's share
link opened in a second tab; the badge (`200 image/svg+xml`, "SEO score: 73 C"); the operator dashboard as an
administrator (my dev user's `Roles` += `Administrators` in the local database, as the README describes); the
organizations and notification-preference pages. **Not seen:** the landing page signed out, Web Push (no VAPID keys
locally: the bell's push key answers 404, push is off), a verified site and a full crawl, joining a running audit as a
second member (server-tested), phone widths, light mode, the badge on a forge.

**Tests.** Server (`SeoTester.Tests`): `ReportSurfaceTests` (catalogue in two languages, the badge's life cycle, the
dashboard's access and figures, the actions per run state), `StartAuditTests` +1 (navigate, Dutch refusal with the
wait), the notification URL pinned; core `ShareLinkTests` +1. **Red:** core stub 1/11; SeoTester with 7 mechanisms
removed at once (Dutch catalogue, badge token check, dashboard right, disable Cancel, navigate, translated refusal,
notification URL): 8 of 23 failed. Green: the full SeoTester.Tests 167/167, ng-spark 1546/1546, CodeCoverage's SPA 261/261 (its interceptor moved), core's translation goldens regenerated (+8 keys each). SPA (vitest, 25 specs): 8 mutations
→ 8 failed; green 25/25. ng-spark: every change red first (2 po-detail, 1 grid, 1 gauge, 1 share, 1 notifications, 1
pwa, then the keep-the-page change 2), green: services, grid, po-detail, share, score renderers, notifications, pwa.

**Open (§11):** Q-M11a–Q-M11d.

### M12 (2026-10-10): monitoring, alerts, the deploy files and the runbook

Code-complete; **nothing is deployed** (the owner's step: `docs/seotester/deploy.md` §3, §4). Commits `9c2b7782`
(monitoring and alerts), `9a44e885` (compose, workflows, runbook).

**Scheduled monitoring (F9, D11, D12).**
- A site's `Monitoring` (Off / Weekly / Monthly) gets two server-owned fields: `NextMonitoringRunAtUtc` (shown on the
  site) and `MonitoringBy`, the member who set the schedule. `SiteScheduleInterceptor` (beside `SiteSaveInterceptor`,
  in a file of its own) sets them: switching on, or changing the period, needs the `SeoTester:Features:Monitoring` gate
  to admit the **stored** verification (a Member cannot post `IsVerified`), makes the writer `MonitoringBy` and puts the
  first slot one period after the latest run (now if there is none or it has passed; `MonitoringPlan.FirstSlot`); an
  unchanged schedule keeps both whatever is posted; Off clears them. *Evidence:
  `Switching_monitoring_on_needs_a_verified_site_and_schedules_the_writer` (refused on an unverified site, 400 with the
  translated message; posted `MonitoringBy`/`NextMonitoringRunAtUtc` ignored; another member's change takes it over;
  Off clears; a threshold of -1 refused); red with the gate check removed.*
- **Scheduled runs are started for `MonitoringBy`, through `AuditStarter`.** §6 counts scheduled runs in the user's
  `verified-audits` quota ("24 (incl. schedule)"), so a run needs a user; the one who switched the schedule on is the one
  who asked for it. The same gates, quota, cool-down and join as *Run audit*; the abuse log's `AuditStarted` says
  "scheduled". A refusal (quota, gate) is logged by the starter and told to that member (`seo-monitoring-skipped`); not
  retried, the next slot comes on schedule. A scheduler who is no longer a Member or more of the organization stops the
  runs, and its Admins are told; another member setting the schedule takes it over. *Evidence:
  `The_sweep_starts_each_due_slot_of_a_verified_site_once_for_the_member_who_scheduled_it` (`StartedBy` = Max, the
  log detail "scheduled").*
- **The sweep** (`MonitoringSweepCronJob`, `*/15 * * * *`, up to 200 slots a pass, Spark cron's cluster claim):
  `MonitoringScheduler` queries the due sites the gate admits (non-stale, untracked), broadcasts one
  `MonitoredAuditMessage(siteId, slot)` per site with `BroadcastOnceAsync` keyed `{siteId}:{slot}` on the new queue
  `seo-monitor` (concurrency 1, 1 min), and patches only `NextMonitoringRunAtUtc` (a whole number of periods on, past
  now: slots missed while the app was down are skipped, `MonitoringPlan.NextAfter`). The recipient reads the site and the
  gate again (switched off, or verification withdrawn since: nothing). *Evidence: the sweep test above (two sweeps, one
  slot, one run; an unverified monitored site and a site not due get none; the next slot is the slot + 7 days), red
  with the patch removed; `The_next_slot_skips_the_slots_missed_while_the_app_was_down`, red with the catch-up loop removed.*

**Alerts (F10, D20, D44).**
- After the aggregate commits a completed run (and the participants' completion notice), `SiteAlerter` alerts **only a
  monitored site** that the monitoring gate admits (D11 pairs "scheduled monitoring + alerts"), manual runs included.
  `AlertRules` (pure, Library) decides:
  - **score drop:** the score fell by at least the site's `AlertScoreDrop` (new profile field, 0–100; empty = the
    setting `SeoTester:Alerts:ScoreDrop`, default 5; 0 = off) since the previous completed run, **only when both runs have
    the same `ScoringVersion`**: an administrator's weight or severity edit is not the site getting worse (the trend marks
    such changes, F8);
  - **new critical issue:** a Critical check fails with delta New (it neither failed nor warned on the previous run):
    once, not on every run;
  - **certificate / domain:** `security.certificate-expiry` fails (< 14 days, the check's own Fail threshold) or
    `domain.expiry` fails (< 30 days): on every run while it fails.
- Each alert is one `ISparkNotifier.NotifyAsync` to **every member** of the organization (Q-M12c), with the organization
  id (their D44 preferences pick in-app / mail / push), the run's page as the link, category `seo-alert-score-drop` /
  `-critical` / `-certificate` / `-domain` (also the push topic), `Urgent` for all but the score drop. Texts are English,
  as the pipeline's other notices. Never throws: the run is over either way.
- *Evidence: `MonitoringRulesTests` (7: first slot, catch-up, a month after the 31st, slot key, drop threshold /
  same scoring / 0 = off, new-critical-only, expiry repeats; red with the Delta filter loosened and the catch-up loop
  removed); `A_run_of_a_monitored_site_alerts_every_member_about_a_new_critical_issue_and_a_score_drop` (the site loses
  its titles: Ann, Max and Vic get `seo-alert-critical` and `seo-alert-score-drop`, Bob nothing; the same change on an
  unmonitored verified site sends no alert), red with the aggregate's alert call removed.*
- The previous run is loaded once (`PreviousRunAsync`); the deltas are computed from it as before.

**Dedupe.** The three-valued gate test moved from `AuditStarter.Allows` (removed) to `SeoFeatureScope.Admits` in the
Library, used by the start, the sweep, the recipient, the alerter and the interceptor. Same switch, same results
(`A_gate_admits_sites_by_its_scope`; `StartAuditTests` unchanged and green). No other package was touched.

**Deploy files.**
- `apps/SeoTester/docker-compose.yml` (project `seotester` in `/var/www/seotester`): `seotester-app`
  (`ghcr.io/mintplayer/seotester:master`, pull-only and floating like CodeCoverage's) on `web`,
  `code-coverage_coverage-internal` (external) and `seo-browser`; `seo-chromium`
  (`ghcr.io/mintplayer/seotester-chromium:1.63.0`, the Playwright version) on `seo-browser` only, `internal: true`.
  Chromium: `shm_size: 512m`, `mem_limit: 1g`, `pids_limit: 512`, `init: true`, `cap_drop: ALL`, `no-new-privileges`,
  no port; the app `mem_limit: 1g`, `stop_grace_period: 120s`, and `depends_on` Chromium *started* (a broken browser
  errors the rendered checks, it does not take the site down). Healthchecks: the app `GET /health` (new
  `Health/HealthEndpoint.cs`, CodeCoverage's contract: always 200, depends on nothing), Chromium a TCP connect to 3000.
  Traefik labels as CodeCoverage's (hard-coded Host, `traefik.docker.network=web`); `/spark/live` needs nothing
  (Traefik upgrades WebSockets by default; the 30 s heartbeat is under its idle timeout). Database `SeoTester` on
  `coverage-raven` (`EnsureDatabaseCreated`), no certificate (unsecured on the internal network; `Spark:RavenDb` has no
  certificate option); mail through `coverage-smtp` (Q-M12b). `docker compose config` accepts the file (Docker 29.8.1,
  client only; no container started).
- **Memory limits are a first guess** (1 GB each): S3 measured 160–210 MB of Chromium on small pages on Windows; the
  Debian figures are Q-M10b's, at the deploy.
- `.github/workflows/seotester-deploy.yml`: push to master on SeoTester or its libraries (the path list = the Dockerfile's
  COPY lines = the csproj graph), or *Run workflow*. Builds and pushes the app image and the Chromium image (tag read
  from the Dockerfile's `FROM`), attestations, packages made public (best effort), then SSH with CodeCoverage's secrets
  (`VPS_HOST`, `VPS_USERNAME`, `VPS_SSH_KEY`, `VPS_PORT`, `VPS_SSH_KEY_PASSPHRASE`): no new secret. It refuses without
  `.env` or without CodeCoverage's network, then pull / down / up and waits for `/health` (36 × 10 s ≥ the 300 s start
  period). `seotester-image-check.yml` builds both images on pull requests, no push; separate gha cache scopes.
  **No workflow ran:** nothing was pushed.
- `tools/verify-seotester-deploy.test.mjs` (`npm run test:tools`, which `pull-request.yml` runs; `yaml` comes with nx):
  10 tests over the compose file, both workflows, the Dockerfiles, the csproj graph, `appsettings.Production.json`,
  `server.mjs` and `.env.example`. *Evidence: red with six rules broken in the real files (internal false, tag 1.62.0,
  no `traefik.docker.network`, a library missing from the deploy filter, a readiness bound of 200 s, `MAIL_HOST`
  undocumented): 6 of 10 failed; green 10/10.*
- `docs/seotester/deploy.md`: topology, the workflow, first-time setup (DNS and TLS checks, mail domain and DKIM on the
  shared relay, the directory and `.env`, the database and the 36 h expiration check, packages, first deploy, the
  administrator role by a **parameterized** patch), the credential list with exact redirect URIs and where each value
  goes, VAPID generation with openssl in Spark's format, and the post-deploy checklist for Q-M5a, Q-M6a, Q-M9b, Q-M10b
  plus CodeCoverage's next deploy and a first scheduled run.

**Tests.** Red first, then green (above). The affected suite at the end, with the other agent's uncommitted M11 follow-up in the tree: the full `SeoTester.Tests` **186/186** (2 m 31 s; `StartAuditTests`, `CompositionTests` with the seventh queue, `RouteTableSnapshotTests` with `GET /health` included), `npm run test:tools` **74/74**. No full sweep.

**Open (§11):** Q-M12a (privacy policy and terms pages), Q-M12b (shared RavenDB server and relay), Q-M12c (alert
audience). The deferred measurements Q-M5a, Q-M6a, Q-M9b and Q-M10b stay open until the owner runs deploy.md §5.

### Round 13 (owner, 2026-10-10): Q-M12a, Q-M12b, Q-M12c

- **D47 (Q-M12a): SeoTester has a privacy policy and terms of use, and the texts were written here.** *Evidence: owner
  answer 2026-10-10* ("write the privacy policy and terms of service texts", factual, from the code and the PRD; GDPR,
  the owner is in Belgium). Built (commit `dde13371`):
  - `GET /privacy` and `GET /terms`, server-rendered like `/bot` (`Legal/LegalPages.cs`; the shared head and language
    choice moved to `Pages/ServerPage.cs`, `/bot` unchanged), English or Dutch (`Accept-Language`, `?lang=`), no script,
    **version 1, effective 2026-10-10** (`LegalPages.Version` / `EffectiveDate`, to raise on a change in substance);
    anonymous; in the route snapshot; excluded from the service worker (`ngsw-config.json`, `verify-ngsw-config`).
  - Linked from a footer in the SPA shell, under every page; the sign-in page is a shell child, so it shows it too.
  - The privacy policy states what the code keeps and for how long: account data from the providers (name or user name,
    e-mail, provider ids; passkeys as public keys), organizations and invitations (14 d), sites and verification, audit
    summaries until deleted, page details and PDFs 7 d, notifications 90 d, push subscriptions until switched off or gone,
    the abuse log and cool-downs (their periods read from the settings), mail records 30 d; legal bases (art. 6(1)(b),
    (f), (a)); recipients (the sign-in provider, PageSpeed Insights with page URLs only, the browser's push service with
    encrypted content, RDAP/IANA, hstspreload.org and DNS with domain names only, the hosting provider); cookies
    (sign-in, antiforgery, external sign-in, language/colour mode/time zone; no analytics or tracking, no third-party
    scripts); the GDPR rights through the account page's export and deletion, and the Belgian DPA. The terms: acceptable
    use (only sites you may test), verification, the crawler and `/bot`, quotas and availability, results and sharing,
    as is, liability (not for intent or gross negligence, nor where the law forbids), ending, Belgian law.
  - **The controller's identity is configuration, never the repository:** `SeoTester:Legal:{OperatorName, Address,
    ContactEmail, EnterpriseNumber, Court, HostingProvider}` (empty in `appsettings.json`; `LEGAL_*` in the server's
    `.env`, `.env.example` and deploy.md §4.3). ~~An empty value shows **"[to be completed]"** ("[nog in te vullen]");
    production logs a startup warning naming the keys (`LegalConfigurationCheck`), never a failure.~~ **Amended
    2026-10-11 (owner: "this is not a registered entity anywhere"; the service runs as a private person):** only
    `OperatorName`, `ContactEmail` and `HostingProvider` are required, and only they show **"[to be completed]"** and
    count in the startup warning. `Address` and `EnterpriseNumber` are optional and left out of the sentence when empty
    (GDPR art. 13(1)(a) asks for an identity and contact details; the address and KBO number are Book XII e-commerce
    duties for paid services). An empty `Court` reads "the competent Belgian courts" / "de bevoegde Belgische
    rechtbanken" (`LegalPages.DefaultCourtEn/Nl`), one text per language, since a configured value appears in both.
    Version stays 1: nothing was published yet. *Tests: `LegalPagesTests` (below).*
  - **Amended 2026-10-11 (owner: X is skipped, its project is pay-per-use with no credits):** the privacy policy names
    only the sign-in providers actually offered (`LegalPages.ConfiguredSignInProviders`: the same rule as
    `AddExternalProviders`, a non-empty `Spark:Auth:Providers:{Key}:ClientId`), in both places it lists them; none
    reads "an external sign-in provider". *Test: `The_policy_names_only_the_sign_in_providers_that_are_offered`.*
  - ⚠️ **The texts are a draft written from the code for the owner to review, not legal advice.**
  - *Tests: `LegalPagesTests` (both pages anonymous in English with the retention figures and the marker; Dutch by
    header and by `?lang`; configured values replace the marker, HTML-encoded, mailto link). Red: the three HTTP tests
    failed (the SPA's `index.html` answered); the service-worker guard failed with the old `ngsw-config.json`. Green: with
    `BotPageTests`, the route snapshot and `CompositionTests` 13/13; `test:tools` 76/76.*
- ~~**D48 (Q-M12b): a separate shared network, `mintplayer-backend`.**~~ **Superseded by D55** (§12 Round 15: pairwise
  internal networks `seo-raven` and `seo-mail`; `mintplayer-backend` is gone from both compose files and both deploys).
  *Evidence: owner answer 2026-10-10* ("a separate
  external network that coverage-raven, coverage-smtp and seotester-app join, so neither stack's up/down touches it").
  Supersedes the M12 entry's ~~`code-coverage_coverage-internal` as an external network~~. Built (commit `53dcbb3e`):
  - `apps/CodeCoverage/docker-compose.yml` (production): `coverage-raven` and `coverage-smtp` also join
    `mintplayer-backend` (external, that name); `coverage-internal` stays for CodeCoverage's own services, and
    `coverage-app` does not join the new network. `apps/SeoTester/docker-compose.yml`: `seotester-app` joins it instead.
    The host names (`coverage-raven`, `coverage-smtp`) are the same.
  - Both deploy workflows run, before `down`/`up`, `docker network inspect mintplayer-backend || docker network create
    mintplayer-backend` (a concurrent create by the other deploy is tolerated by a second inspect), so whichever stack
    deploys first finds the network. SeoTester's deploy then refuses, before touching its own stack, while `coverage-raven`
    is not on the network ("deploy CodeCoverage first").
  - A plain bridge, not `internal: true`: `coverage-smtp` delivers mail to the internet, and the network publishes no
    port, as `coverage-internal` does not.
  - Migration of the live CodeCoverage stack: its ordinary deploy on merge (creates the network, recreates raven and smtp
    on both networks); then SeoTester (deploy.md §3.0). `docs/code-coverage/hosting.md` §1 and §7 updated.
  - *Tests: `tools/verify-seotester-deploy.test.mjs` +2 (both compose files declare the network external with the same
    name; raven and smtp join it, coverage-app does not; both deploys create it before `down`/`up`; SeoTester's
    pre-check). Red with the four deploy files as committed before: 3 of 12 failed; green 12/12. `docker compose config`
    accepts both compose files.*
- **D49 (Q-M12c): a monitored site's alerts go to every member of its organization, as built.** *Evidence: owner answer
  2026-10-10.* `A_run_of_a_monitored_site_alerts_every_member_about_a_new_critical_issue_and_a_score_drop` pins it.

### Round 14 (owner, 2026-10-10): Q-M11a, Q-M11d

- **D50 (Q-M11a): an organization switcher, generic in ng-spark's organizations entry point.** *Evidence: owner answer
  2026-10-10* ("add an organization switcher … lists, and the default organization for new sites, follow it; build it
  generically … low-code for app developers"). Decided here, built in commits `28a64b75` (core, Organizations),
  `85276329` (ng-spark), `09ca241d` (SeoTester):
  - **Where the choice lives: a cookie**, `spark-organization` (per browser, like the theme cookie of #462; HttpOnly,
    SameSite=Lax, a year), set by `POST /spark/organizations/current` and read by `GET /spark/organizations/current`.
    Not a server document: switching then writes nothing and costs no load per request (the row policy already reads
    the caller's memberships once per request, S13), and nothing on `SparkUser` (Q-M6b's reason).
  - **It is never trusted:** every request checks it against those memberships. A cookie naming an organization the
    caller is not (or no longer) a member of counts as no choice, so it can only narrow what the row policy allows;
    posting one answers the missing-row 404 (#453).
  - **"All organizations" stays, and is the default.** Q-M11a's own argument for (a) still holds for a user who wants
    every site at once; the switcher adds the narrowing without taking that away.
  - **How lists filter: the existing organization row policy**, no new mechanism. `OrganizationRowPolicy` intersects
    the allowed organizations with the choice for the `Query` action of `IOrganizationOwned` types (grids, query cards,
    reference pickers, distinct values, live lists). Not narrowed: a detail page, an edit, custom actions, the
    organization list (the switcher picks from it) and **sub-queries**: core's `RowPolicyContext.IsSubQuery` (set by the
    query read path, distinct values and live app streams when the request names a parent), so a site opened from a
    notification of another organization still shows its own audits.
  - **New rows:** `OrganizationOwnedDefaults` gives a create whose form leaves `OrganizationId` empty the chosen
    organization when the caller holds the type's `New` role there, else the personal one (D21). SeoTester's own
    personal default is gone; its site interceptor runs at the Finalize stage so its duplicate check sees the default.
  - **Losing membership:** the next request shows all of the caller's organizations; `GET /current` deletes the stale
    cookie, so the switcher reads "All organizations".
  - **Low code:** `AddOrganizations()` maps it; the SPA adds `<spark-organization-switcher />` to its topbar (renders
    nothing for a user in one organization). A choice reloads the page so grids and live sockets read the new scope.
    Guide: `docs/guide-organizations.md` "The organization switcher".
  - *Tests: `OrganizationSwitcherTests` (4); red with the mechanisms removed 4/4, and the sub-query assertion with
    `IsSubQuery` forced false; ng-spark switcher spec (4, 3 red with its guards removed).*
- **D51 (Q-M11d): store the Open Graph values; the card's image only through the server.** *Evidence: owner answer
  2026-10-10* ("store og:title, og:description, og:image and og:url on PageResult … the og:image is shown only as a
  URL/preview through a safe path"). Built (commit `09ca241d`):
  - `PageResult.OgTitle`, `OgDescription`, `OgImage`, `OgUrl` (as written, trimmed, capped at 300/500/2000/2000; model
    attributes; they expire with the page). The card uses them and falls back to the title, description and address,
    as networks do, saying which it shows.
  - **The CSP check:** SeoTester sends no Content-Security-Policy (nor does any Spark app: the Authorization package
    notes there is none), so nothing *blocked* hot-loading. The PRD's own reason decided it: Q-M11d (a) "keeps the page
    free of third-party requests", and anyone can add a site, so a hot-loaded image would tell that site every viewer's
    address. **Chosen: the proxy**, `GET /seotester/og-image?page={pageResultId}`: the caller must be able to read the
    page (the `Read/PageResult` right and the organization row policy; an expired page is gone), else one 404; the
    server fetches the tag's URL over the SSRF-safe crawler client (no private addresses, every redirect re-checked,
    the crawler's user agent); only absolute http(s) without user info; at most 5 MB (refused, never cut); only PNG,
    JPEG, GIF or WebP by their bytes, never SVG and never by the site's `Content-Type`; answered with `nosniff`,
    `Content-Security-Policy: default-src 'none'; sandbox` and `Cache-Control: private`; results (images and
    refusals) kept 10 minutes in a 32 MB memory cache. The card shows the image, its pixel size and its address as
    text. Measured: `https://mintplayer.com/` stored its four values and the card drew its 512 × 512 PNG through the
    proxy.
  - *Tests: `OpenGraphCardTests` (9) and the SPA's card specs (3); red as recorded in "M11 follow-up".*

### M11 follow-up (2026-10-10): the switcher, the Open Graph card, depth 0, phone and light mode

Commits `28a64b75`, `85276329`, `09ca241d`, then this record. D50 and D51 above.

**Depth 0 showed as "-".** A generic ng-spark bug: the detail page printed `value || '-'`, so every falsy value (the
number 0, `false` outside the boolean branch) read "-". The new `orDash` pipe shows "-" for null, undefined and a blank
string only, in all four value branches. Spec: 0 shows "0", null and "" show "-" (failed with the old template).

**Phone (375 px) and light mode, seen with the playwright MCP** (`dotnet run --launch-profile https`, SparkId, my dev
user with a second organization made for the switcher; landing, site, run report, page result, share page, operator
dashboard, an organization, notifications and the bell; 1400 px and 375 px, dark and light; screenshots in the session
scratchpad). Found and fixed:
- The topbar pushed every page sideways and cut off *Logout*: the auth bar shows icons below 576 px (names kept as
  `aria-label`/`title`; ng-spark gains the built-in icons `person`, `box-arrow-right`) and is never squeezed; the
  shell's trailing group may shrink with a tighter gap on phones; the language selector keeps 4.25rem (it collapsed to
  its caret); the switcher takes 4.25–6rem.
- The landing page's query-card grid (`minmax(22rem, 1fr)`) and the dashboard's (`20rem`) were wider than the screen:
  `minmax(min(…, 100%), 1fr)`; the dashboard's collections table scrolls inside its card.
- The run report's category bars (fixed 160 px plus a third note column) overflowed: two columns, the bar shrinks
  (`min(160px, 100%)`), notes wrap under it.
- The share page's label column took 40 % of the width: labels stack over values on a phone; a wide query table scrolls.
- Light mode: the gauge, letter-grade badges, category bars, the result previews (Google blue on white) and the card
  read correctly; nothing to fix.

Not changed (seen): on a phone the detail page's action bar collapses every action, *Back* included, into *More*
(ng-bootstrap's priority nav); the bell's menu opens flush with the left edge.

**Tests.** Red then green as each commit records. Green at the end: MintPlayer.Spark.Tests (Organizations, row-policy,
live, distinct, sub-query, sharing, soft-delete classes) 316/316; SeoTester.Tests 190/190; ng-spark 1553/1553;
SeoTester SPA 28/28. In the first full SeoTester run `CrawlResumeTests` failed once inside its own host restart
(`WebApplicationFactory.DisposeAsync` → Messaging's `DrainAsync` logged its expected "did not drain" warning to the
Windows EventLog provider, which threw `ObjectDisposedException`); it passed alone and in the second full run, and no
changed file is on that path. Recorded as found, cause not yet located (Q-M11e).

~~**Open (§11):** Q-M11e.~~ Located and fixed: §12 "Q-M11e" below (D52).

### Q-M11e (2026-10-10): two stops of one host, and a drain per stop

- **D52 (Q-M11e): `MessageSubscriptionManager.StopAsync` runs one teardown however often it is called, and no
  pump outlives it; test hosts never call the EventLog provider.** *Evidence below.* Option (c) of Q-M11e.

**Cause, with evidence.** A `WebApplicationFactory` host is stopped **twice, concurrently**: the factory's
`DisposeAsync` calls `Host.StopAsync`, which raises ApplicationStopping; the app's own `app.Run()` (Program.cs line
181; the factory starts the host through the real entry point) wakes on it in `WaitForShutdownAsync` and calls
`Host.StopAsync` too. `RunAsync`'s `finally` then disposes the host, logger providers included, as soon as **its**
stop returns (decompiled `HostingAbstractionsHostExtensions.RunAsync`, .NET 11 rc.1; `Host.StopAsync` awaits every
service and has no guard against a second call). Shown with temporary ordering logs in one `CrawlResumeTests` run: the
same `MessageSubscriptionManager` instance entered `StopAsync` twice, 0.18 ms apart (20:29:54.84258 and .84276), one
stack from `WaitForShutdownAsync`, the other from the factory's `Host.StopAsync`; each ran its own
`router.DrainAsync(ClaimTtl)` on its own 4 s timer, and both logged "did not drain" (20:29:58.885302 and .885324).
Whichever chain finished first let `RunAsync` dispose the host; when it was the app's, the factory's drain wrote its
warning into the disposed EventLog (Warning is the EventLog provider's default level, so it is the one provider such
a warning reaches) and `ObjectDisposedException` came out of the factory's stop: two timers firing microseconds apart,
so it failed only sometimes. Every log of the unpatched run shows the doubled "MessageSubscriptionManager stopping".
A second, smaller leak on the same path: `MessageQueueRouter.DisposeAsync` cancelled the pumps and did not wait for
them, so a cancelled handler's errors ("Error in handler PageBatchRecipient", "Pump for queue … failed") were logged
after the stop had returned.

**Fix** (`libs/messaging`):
- `MessageSubscriptionManager.StopAsync` is single-flight: the first call runs the teardown, every other call awaits
  that same task, and none gives up on its own token. The teardown is bounded: the drain by `ClaimTtl`, the pumps
  below, the lease release by `MessagingLease.ReleaseTimeout`. `StopMessagingAsync` is single-flight the same way, so
  a lease-lost teardown in `ExecuteAsync` and a host stop join rather than drain twice.
- `MessageQueueRouter.DisposeAsync` waits for the pumps it cancels, bounded by `PumpStopTimeout` (5 s), and warns
  if a handler ignores its token past that.

**Defense in depth.** `tests/Shared/NoEventLog.cs`, compiled into every `*.Tests` project by
`Directory.Build.targets` beside `NoSystemProxy.cs`, sets `Logging__EventLog__LogLevel__Default=None` (unless set)
in a module initializer. Every default host reads environment variables into its configuration, so no test host,
nor an app an E2E test starts, calls the EventLog provider. No test-host helper is shared by all apps, so this is the
central place.

**Tests.** Red then green:
- `MessageSubscriptionManagerLifecycleTests.A_second_concurrent_stop_does_no_work_after_the_first_one_returns`: a
  held handler that takes 0.5 s to unwind when cancelled, `ClaimTtl` 1 s, two `StopAsync` calls 300 ms apart, and a log
  sink that throws `ObjectDisposedException` once the first stop returns (as `RunAsync` disposes the host). Red before
  the fix with the production error: the second stop threw "Cannot access a disposed object. Object name: 'EventLog'"
  out of its drain. A first draft without the unwind delay was red on the other half: the cancelled pump's three
  errors were logged after the stop returned. Green after.
- `NoEventLogInTestHostsTests`: a default `WebApplication` whose EventLog provider is disposed logs a warning without
  throwing. Red without `NoEventLog.cs` ("Object name: 'EventLogInternal'"), green with it.

Green at the end, built from HEAD plus only these changes in a separate worktree (the shared working tree held
another, unfinished change that did not compile): `CrawlResumeTests` 10/10, each restart now logging "stopping" once
per host and "did not drain" once; MintPlayer.Spark.Tests' Messaging namespace and the new EventLog test 178/178;
SeoTester.Tests 190/190 (2 m 54 s).

~~**Found, not fixed here:** after a lease-lost teardown the router keeps its lanes, whose channels `DrainAsync`
completed. If the host takes the lease back, `RouteAsync` finds the old lane and writes to a completed channel
(`ChannelClosedException`). This comes from reading the code, not from a run.~~ **Fixed (2026-10-10), reproduced
first.** `MessageSubscriptionManagerLifecycleTests.A_host_that_regains_the_lease_after_losing_it_routes_and_handles_messages_again`:
one message creates the queue's lane; the lease's compare-exchange value is deleted, so the next renewal fails and
`ExecuteAsync` tears down (what an eviction looks like to the holder); the loop finds the lease free and restarts
messaging; a second message on the same queue must be handled. Red before the fix: the second message was never
handled (60 s bound), and the feeder logged "subscriber error, retrying in 00:00:30" with
`SubscriberErrorException <- ChannelClosedException` on every retry. **Fix:** `MessageQueueRouter.StopAsync`
(the body `DisposeAsync` had, which now calls it) cancels the pumps, waits for them bounded by `PumpStopTimeout`, and
drops every lane; the messaging teardown calls it last, after cancelling the lease lifetime, so a re-acquired lease
builds fresh lanes on first use. `Start` cancels a lifetime left over (defensive; it does not occur). The order of
drain, then release, then cancel is unchanged. Green: the new test (10 s), and the Messaging namespace 178/178.

### Round 15 (owner, 2026-10-10): minimal network exposure, a secured RavenDB

D52 is Q-M11e's and D54 the fetcher container's (both written by the other agents on this branch the same day); these
two take D55 and D56 so no number is shared.

- **D55: pairwise networks in both stacks, internal unless a member needs a way out.** *Evidence: owner answer
  2026-10-10* ("each backend service is reachable only by the app container that uses it; RavenDB and the mail relay
  never share a network"). Supersedes D48. Built:

  | Network | Members | Route out |
  |---|---|---|
  | `web` (Traefik's, external) | `coverage-app`, `seotester-app` | yes |
  | `coverage-db` | `coverage-app`, `coverage-raven`, `coverage-raven-license` | no |
  | `coverage-mail` | `coverage-app`, `coverage-smtp` | no |
  | `coverage-smtp-egress` | `coverage-smtp` | yes (delivery to recipients' MX) |
  | `seo-raven` (external) | `coverage-raven`, `seotester-app` | no |
  | `seo-mail` (external) | `coverage-smtp`, `seotester-app` | no |
  | `seo-browser` | `seotester-app`, `seo-chromium` | no |

  - **RavenDB has no egress** (cited, not measured: `Raven.Server` 7.1.3 `Commercial/LicenseManager`, decompiled):
    `ActivateAsync` reaches `api.ravendb.net` only for an expired or pre-v6 licence (`GetUpdatedLicenseForActivation`)
    and for `Setup.Mode=LetsEncrypt`; the 24 h timer's `LeaseLicense(throwOnError: false)` only raises a
    "Failed to lease license" warning when it cannot connect. Cost: `POST /admin/license/renew` from inside the
    container (used 2026-09-28) stops working; a renewal is applied by replacing `raven-license.json` and re-running the
    one-shot.
  - The cross-stack networks are `external` in both compose files; both deploys create them `--internal` when missing,
    before compose, and refuse one that exists but is not internal. SeoTester's deploy also waits for `coverage-smtp`
    on `seo-mail`. The fetcher container (D54) is left room: nothing here assumes the app crawls, and the tests pin
    `seo-raven` and `seo-mail` to exactly their two members, so a fetcher with database access fails them.
- **D56: RavenDB over HTTPS with client certificates; one certificate per app, limited to its database.** *Evidence:
  owner answer 2026-10-10*, and the spike below. Built:
  - `coverage-raven`: `https://0.0.0.0:8080`, `tcp://0.0.0.0:38888`, `PublicServerUrl` `https://coverage-raven:8080`
    (the certificate's name), server certificate from a private CA, `RAVEN_Security_WellKnownCertificates_Admin` =
    the operator certificate (the only cluster admin), no unsecured-access flag. The licence one-shot authenticates with
    the operator certificate; the apps never get it.
  - `codecoverage` → `ValidUser`, **Admin on `Coverage`** only; `seotester` → **Admin on `SeoTester`** only. Database
    Admin, not ReadWrite, because Spark deploys indexes and configures expiration at startup (both measured to work
    with it). `EnsureDatabaseCreated=false`: creating a database is cluster admin; the operator creates them.
  - **Spark core (generic, low-code):** `Spark:RavenDb:CertificateFile` (PEM or PKCS#12), `CertificateKeyFile`,
    `CertificatePassword`, `ServerCertificateAuthorityFile`, applied by `SparkRavenDbSecurity.Apply` in `AddSpark`
    (public for apps that build a store of their own). Nothing configured = nothing changes (local dev, the tests).
    It refuses an `http://` URL with a certificate, a key without a certificate, a certificate without its key. The CA
    is trusted through RavenDB's one process-wide hook (`RequestExecutor.RemoteCertificateValidationCallback`): it
    accepts a clean certificate (once any callback exists, Raven asks the callbacks instead of its default, read from
    the decompiled `OnServerCertificateCustomValidationCallback`) or one that chains to the CA; a wrong host name is
    never forgiven. PEM keys go through PKCS#12 (SChannel refuses an ephemeral key). `MintPlayer.Spark` is already at
    `11.0.0-preview.104` on this branch: no version change. Not changed: the Replication package's own
    `SparkModules` store (`ModuleDirectory`), which has its own options and no secured deployment.
  - Runbook `docs/code-coverage/raven-tls-migration.md` (~~DRAFT until Q-D56a~~ proven 2026-10-11): certificates, VPS paths, owners and modes, the migration with exact commands
    (hot backup, openssl, files, cold copy, the new compose file staged by hand before the merge, registration, start,
    verification, rollback), ~~expected downtime ~5 min plus the cold copy, renewal.~~ **Amended 2026-10-11 (owner:
    "5 minutes is really long"; "This must go automatically"):** no cold copy, everything but the switch prepared
    beforehand, the switch one scripted block of about 30 s (rehearsed from master's unsecured stack: 18 s to the first
    TLS 200, rollback 14 s; an unsecured 7.1.10 refuses certificate registration with HTTP 500, so the two
    registrations stay inside the window at 2 s). Renewal is automatic: `raven-cert-renew` + a nightly timer,
    decisions R1-R10 in runbook §6 (rehearsed 43/43).
  - *Tests: `SparkRavenDbSecurityTests` (9; certificates made in the test; a real TLS handshake against a Kestrel
    server that requires a client certificate). Red with the `Apply` call removed: 6 of 9 failed (no certificate on the
    store, no refusals, no handshake); green 9/9, with `AddSparkDocumentStoreFactoryTests` 13/13.
    `tools/verify-backend-networks.test.mjs` (7): red against the committed compose and workflow files, 6 of 7 failed
    (Chromium-only-on-seo-browser already held); green 7/7, `npm run test:tools` 81/81. `docker compose config` accepts
    both files (the client works without the daemon) and refuses CodeCoverage's without the two `RAVEN_*` values.
    CodeCoverage.Tests not run: its compose fixture copy pins only the ghcr image, which did not change.*

**Spike (2026-10-10).** Docker Desktop was not running, so not in Docker (Q-D56a). Instead: the runbook's openssl
commands (OpenSSL 3.2.3; CA RSA 4096, server and clients RSA 2048, SAN `coverage-raven` + `localhost`), and
`Raven.Server` **7.1.3** from the `RavenDB.Embedded` package started with the compose file's `RAVEN_*` settings on
127.0.0.1, probed by a console that used Spark's `SparkRavenDbSecurity.Apply` and raw HTTPS. All passed, unlicensed
(`/license/status` Type None):
- plain `http://` to the TLS port got no answer (`HttpRequestException`); HTTPS without a client certificate: 403;
- the operator (well-known admin, never registered) created `Coverage` and `SeoTester` (201) and registered both app
  certificates (201; registering the same one again: 201 too);
- `codecoverage` through Spark's store: `GetDatabaseNames` (what `WaitForRavenDbConnection` calls) returned only
  `Coverage`; write and read; a static index deployed; expiration configured (129600 s); a subscription batch received
  over TLS on the TCP port;
- `codecoverage` refused (`AuthorizationException`) reading and writing `SeoTester`, creating a database, deleting
  `SeoTester`, registering a certificate; 403 listing certificates and activating a licence; `seotester` wrote its own
  database and was refused reading `Coverage`;
- the operator exported `Coverage` (200, the runbook's backup endpoint);
- licence activation with the operator certificate: with a 825-day server certificate the **Developer** licence was
  refused (500, "The server certificate total duration is greater than 4 months. This is not allowed when using
  Developer license"); with a 90-day server certificate it activated (Type None → 5, Expired false).
The server, its data, logs and every certificate were deleted afterwards; no key or licence was printed.


### D54 (owner, 2026-10-10): the fetcher container

- **D54: every request to a target leaves from `seo-fetcher`, a container with internet egress and no database.**
  *Evidence: owner answer 2026-10-10* ("move SeoTester's internet-facing fetching out of the web app into its own
  container … internet egress and NO database access … even if the SSRF guard had a bug, the code that talks to
  untrusted websites cannot reach RavenDB, the mail relay, or anything else internal"). Built in commits `9d00fcd5`
  (core: `MintPlayer.Spark.SafeHttp`) and `9632fcf1` (fetcher, app, compose, workflows, tests). Decisions taken here,
  each with its evidence:
  - **One interface, two implementations.** `ISeoFetcher` (`apps/SeoTester/SeoTester.Fetching`): `FetchAsync`,
    `ProbeTlsAsync`, `QueryDnsAsync`, `ZoneApexAsync`. `LocalSeoFetcher` is the code the app ran before (the
    SSRF-safe clients through `PageFetcher`, `TlsProber`, DnsClient), moved, not rewritten; `RemoteSeoFetcher` calls the
    fetcher. The fetcher container runs `LocalSeoFetcher` behind its API, so the same code fetches in both modes and the
    test host's fakes (`FakeDns`, `FakeTls`, `FakeWeb` on the named clients' primary handlers) keep working unchanged.
    *Evidence: the 190 existing SeoTester tests pass in process unchanged (two construct `RenderNetwork` with an
    `ISeoFetcher` now); the same crawl passes through the remote client (below).*
  - **What moved out of the app:** the probe's and the crawl's fetches, the external-link check, RDAP / IANA bootstrap /
    hstspreload, PageSpeed Insights, the verification file and meta-tag fetches and DNS TXT, the domain checks' DNS, the
    TLS hello probes and handshake, the og-image proxy's fetch, and every request Chromium sends (`RenderNetwork`
    fulfils `RouteAsync` with `ISeoFetcher`). **What stays in the app:** `HostPacer` (connections and interval per
    host), robots.txt, the analytics sink, the per-run cache of static resources, the caches in RavenDB
    (`LookupCache`), all parsing and scoring. A thin fetcher, so the per-host limits are still one process's.
  - **Mode:** `SeoTester:Fetcher:Mode` = `InProcess` | `Remote`; unset = `Remote` in Production, `InProcess` elsewhere
    (Development and tests stay one process). **Production refuses `InProcess` at startup**, and in `Remote` the app
    registers no client that can reach a target (not even `spark.AddSafeHttp()`'s, which SeoTester never used).
  - **The trust boundary (the API).** `POST /fetch`, `/tls`, `/dns`, `/dns/zone`; `GET /health` (no key, 200, for the
    healthcheck). Every other call needs the shared secret in `X-Seo-Fetcher-Key`: both sides hash to SHA-256 and
    compare with `CryptographicOperations.FixedTimeEquals`; a missing or wrong key is 401 before anything is read
    (an unknown path too, so it does not reveal the routes); both refuse a key under 32 characters at startup. The
    fetcher reads at most 2 MiB of request, and checks every field (`SeoFetchLimits.Problem`): absolute http(s) only,
    no user info, ≤ 8 192 characters; one of five **client profiles** (crawler, lookups, browser, PageSpeed,
    verification: the SSRF-safe clients with their old limits); GET/HEAD only, other methods and a request body only
    for the browser's profile (≤ 1 MiB); ≤ 100 headers, RFC 9110 token names, no CR/LF/NUL; DNS names ≤ 253 of
    `[A-Za-z0-9_.-]`. **Each profile caps the body it returns** (browser 16 MiB, crawler 10 MiB, PageSpeed 8 MiB,
    lookups 2 MiB, verification 1 MiB; the same cut in process). One call ends after 120 s on the fetcher (PageSpeed's
    100 s client timeout plus room), the app's deadline is that plus 15 s.
  - **The answer is untrusted.** A fetch comes back framed (4 bytes of length, ≤ 256 KiB of metadata JSON, then the
    body as bytes, not base64); the app refuses a frame larger than it asked for, and bounds or drops every field
    (status 0 or 100–599, ≤ 200 headers, ≤ 50 cookies, ≤ 20 hops, http(s) URLs only, the HTTP version 1–3, Retry-After
    ≤ 1 day, strings cut). A compromised fetcher can hand the app no more than a website could.
  - **A fetcher outage is ours, not the site's:** `RemoteSeoFetcher` throws `SeoFetcherUnavailableException`, so a probe
    or crawl batch fails and Messaging retries it (never "the page did not answer"); the browser's request fails; the
    og-image proxy answers its usual 404 and caches nothing; verification throws instead of counting a missing proof (an
    outage of ours must not withdraw anyone's verification after three re-checks).
  - **The PageSpeed key travels per request** in its header (as before), never configured on the fetcher: one place to
    set it (`SeoTester:PageSpeed:ApiKey`), the app still decides "not configured", and the fetcher logs no header.
  - **SafeHttp stays on inside the fetcher** (defense in depth): to run it without RavenDB it moved into the package
    `MintPlayer.Spark.SafeHttp` (`libs/spark/`, `11.0.0-preview.104`, namespace `MintPlayer.Spark.Http` kept,
    `MintPlayer.Spark` references it, `spark.AddSafeHttp()` unchanged). *Evidence: `SafeHttp*` 121/121, the same 121 as
    before the move; IdP, WebPush and notification classes 635/635.*
  - **The no-RavenDB guard:** `FetcherIsolationTests` reads the fetcher's `project.assets.json` (every package and
    project, transitive, used or not) and refuses `RavenDB*`, `Raven.*`, `Sparrow*`, `MintPlayer.Spark` and any Spark
    package but SafeHttp, Attributes, Messaging.Abstractions and the two generators; a second test walks the compiled
    references. `tools/verify-seotester-deploy.test.mjs` checks the csproj graph the same way.
  - **Compose:** `seo-fetcher` (`ghcr.io/mintplayer/seotester-fetcher:master`, pull-only and floating like the app,
    built by the same deploy), `read_only` with a tmpfs `/tmp`, `cap_drop: ALL`, `no-new-privileges`, 512 MB, 256 pids,
    `/health` healthcheck, no port, no volume, on **`seo-fetch`** (internal: app + fetcher) and **`seo-egress`** (this
    project's own bridge: the fetcher alone) and nothing else. `SEO_FETCHER_KEY` in `.env.example`, read by both
    containers (`SeoFetcher__Key`, `SeoTester__Fetcher__Key`, compose fails without it); the deploy checks its presence
    and length without printing it. Workflows build and push the third image; the deploy waits for it; the image check
    builds it on pull requests.
  - **The app's own egress (checked):** it still needs a route out, for the sign-in providers' token and user-info
    endpoints and for Web Push to the browsers' push services; mail goes to the relay on `seo-mail`. It has one: Traefik's
    `web` (a plain bridge). Compose cannot narrow that to those hosts, so the guarantee is the code's: in Production the
    app holds no client for a target. Narrowing the app's egress (an egress proxy with an allow-list) is Q-D54a (§11).
  - **Latency per routed request, measured** (`FetcherLatencyTests`, build machine, Windows 11; the fetcher on Kestrel
    over real loopback sockets, both sides against the same fake web, 200 requests after 20 warm-ups, browser profile):

    | Response | In process p50 / p95 | Through the fetcher p50 / p95 | Added p50 |
    |---|---|---|---|
    | 20 KB HTML | 0.02 / 0.15 ms | 0.41 / 0.79 ms | 0.39 ms |
    | 150 KB script | 0.14 / 0.32 ms | 0.75 / 2.75 ms | 0.62 ms |
    | 1 MB image | 0.64 / 1.97 ms | 3.75 / 23.80 ms | 3.11 ms |

    Against S3's per-viewport load (12–100 ms on fixture pages, a real site's network far more) and the ≥ 500 ms settle
    window, under a millisecond for a typical resource is noise; a 1 MB image costs ~3 ms (one copy through the frame).
    A Docker bridge adds a few tens of microseconds per exchange on top; to be measured at the deploy with Q-M10b.
  - *Tests (red → green), `SeoTester.Tests/Fetcher/`, 12 new:* `FetcherApiTests` (no key, an empty key, one character
    off, one too many, the wrong key on `/tls` and `/dns`: 401, nothing fetched; an unknown path 401 without the key and
    404 with it; `/health` open; the app's client throws on a wrong key · a body cut at the size asked for and at the
    profile's cap · a 2 MiB+1 request, a 1 MiB+1 browser body, non-JSON, `file:`, `gopher:`, an unknown client, POST on
    the crawler, a body outside the browser, user info, a CR/LF header, a bad DNS name, port 70 000: 400 · a 20 s target
    ends at the fetcher's bound (1 s in the test) and a hanging fetcher at the app's deadline · redirects re-checked in
    the fetcher: port, scheme, 7 hops, cross-origin for verification; the browser gets the 302 itself · real sockets,
    `127.0.0.2` allowed as the "public" site: `127.0.0.1`, `localhost`, `[::ffff:127.0.0.1]`, `169.254.169.254`,
    `10.0.0.1`, a redirect from the public site to loopback and the TLS probe all "refused: Address", **the private
    listener accepted 0 connections** · TLS facts and DNS records cross the API intact), `FetcherIsolationTests` (2),
    `RemoteFetcherPipelineTests` (an audit in Remote mode against an in-test fetcher: completed, 4 pages, duplicates,
    the redirect chain, a certificate failing from the fetcher's TLS, RDAP; the app's own web, DNS and TLS fakes received
    **nothing** · verification by DNS and by file through the fetcher, a cross-origin redirect refused),
    `FetcherLatencyTests` (above). **Red:** the key check, the frame's size check, the clamp, both deadlines, the SSRF
    rules (all networks and ports allowed, 50 redirects, cross-origin allowed), a RavenDB reference on the fetcher and
    the Remote mode removed at once: 8 of 12 failed. The compiled-reference guard stayed green with an unused
    `MintPlayer.Spark` reference (the compiler trims it), which is why the restore-graph guard exists; it failed
    ("found RavenDB.Client"). Green 12/12; **the full SeoTester.Tests 202/202** (3 min 10 s). `test:tools` 86/86 (5 new
    fetcher rules in `verify-seotester-deploy`, `verify-backend-networks` gains `seo-fetch`/`seo-egress`); red with the
    fetcher on `seo-raven`, writable, and the key missing from `.env.example`: 6 failed.
  - *Not run:* the images were not built (no Docker run here; the workflows build them on the next push), and nothing
    was deployed.

### Untrusted content: proof tests (owner request, 2026-10-11)

- **A site cannot make the fetcher or the browser carry its cookies, keep its state, save its downloads, or leave its
  content behind.** *Evidence: owner request 2026-10-11* ("tests that prove users can't make SeoTester's fetcher or
  browser carry malicious cookies or downloads"). Commit `9085b023`. Each test was red with its mechanism disabled
  (mutations in a throwaway worktree, reverted), then green:
  - **Cookies, no jar anywhere.** `SafeHttpClientTests.A_cookie_a_site_sets_is_never_sent_back_by_the_client` (Spark.Tests;
    `a.example.com`/`b.example.com` via the fake resolver, `Domain=example.com`): the next request, the redirect hop, a
    redirect to and a request to the other host, a later request all arrive with no `Cookie`.
    `FetcherCookieTests` (2: `LocalSeoFetcher` and the fetcher service over its API; real sockets on `127.0.0.2` and
    `127.0.0.3`, the wire's request heads recorded): `Set-Cookie` still reported in `FetchResult.SetCookies`, none of 9
    requests (next, redirect hop, other site, browser profile, later run) carries `Cookie`. *Red:* `UseCookies = true` on
    the SSRF-safe primary handler: SafeHttp got `cookie=s=evil` on the next request; both fetchers sent
    `Cookie: s=evil; r=evil` on `/next`, `/hop`, `/after`, `/away`, `/later` and `Cookie: b=evil` to the other site.
  - **No state between renders.** `BrowserStateTests.Nothing_a_site_stores_in_one_render_reaches_the_next` (the real
    `PageRenderer`, one launched Chromium, the fake web): render 1 sets a server cookie, a script cookie, localStorage,
    sessionStorage, IndexedDB and the Cache API, and its own report and next request prove they took (the control);
    render 2 of the same site and of another site (which loads an image from the first) reports all five empty, and no
    request of render 2 carries `Cookie`. *Red:* one context reused across renders: render 2 reported
    `cookie=srv=evil; js=evil`, `ls-evil`, `idb-evil`, `cache-evil`.
  - **No downloads.** `BrowserStateTests.A_page_cannot_make_the_browser_save_a_file`: a frame answered
    `Content-Disposition: attachment`, scripted `<a download>` clicks (a served file and a blob), and a navigation to
    `application/octet-stream`; Playwright's artifacts folders are scanned throughout (it deletes a context's downloads
    when the context closes), the temp folder's top level and the user's Downloads folder at the end, for any new file
    whose name or bytes hold the marker: none; the render finishes without an error and the next one works. **Chromium
    does announce the downloads** (a Playwright `download` event per attempt), and every one ends failed ("Pass
    { acceptDownloads: true } …"): no bytes are written, so the test asserts no completed download and no file rather
    than no event. *Red:* `AcceptDownloads = true`: six files in `playwright-artifacts-*` (one a `.crdownload`), the
    downloads "completed".
  - ~~**Nothing crawled is stored.**~~ **No crawled text or markup is stored; element selectors are kept with the latest
    page result** (owner decision 2026-10-12, after MCP spike S7: selectors are accepted).
    `CrawledContentTests.No_text_or_markup_of_a_crawled_and_rendered_page_is_stored_only_selectors`: a full audit (crawl,
    render, PageSpeed) of a site whose marker sits only in the body (text, a comment, an attribute, a script,
    script-written text); a smuggler export of the whole database (documents, revisions, attachments, counters,
    compare-exchange, expired and artificial included, gzip) holds the marker nowhere, while it holds the run and the
    page's title (controls). A second marker is the id of a heading that `seo.single-h1` flags: it is stored as the
    selector `#…` (the control for the allowance), and with every stored selector string taken out of the export it is
    found nowhere. *Red:* `PageResult.Fingerprint` set to the body text: the marker found in the export (M9); an HTML
    excerpt beside the selectors (spike S7): the body marker found in it.
    `FetcherFilesystemTests.The_fetcher_service_works_with_no_writable_folder_and_leaves_no_file` runs the real
    `SeoTester.Fetcher` process with its working directory, content root, `TMP`/`TEMP`/`TMPDIR`, `ASPNETCORE_TEMP`,
    home and app-data folders all pointed at one folder it may not write to (a deny-write ACL for the user on Windows,
    mode 0555 elsewhere; the test first proves a write there fails), and fetches a 1 MB page and posts a 512 KB browser
    body: both succeed, the folder stays empty, and no file in its binaries' folder holds the marker. Chosen because it is
    deterministic: a write fails loudly instead of having to be caught by a scan or a watcher before it is deleted
    (stricter than production, whose `read_only` container keeps a tmpfs `/tmp`). *Red:* a temp file written per fetch:
    "The fetcher answered 500."
  - **og-image proxy.** `OpenGraphCardTests.The_image_proxy_serves_only_raster_images_typed_by_their_bytes`: HTML, SVG,
    SVG served as `image/png`, a PNG-signature polyglot, a PNG with `<script>` in a `tEXt` chunk and an over-5 MB image
    all 404 (each was fetched); PNG served as `text/html`, GIF as `image/png`, JPEG as `image/svg+xml`, WebP as
    `application/octet-stream` are served as `image/png`/`gif`/`jpeg`/`webp` with `nosniff` and
    `default-src 'none'; sandbox`, bytes intact. Plus `Only_well_formed_raster_images_without_markup_are_recognised`.
    *Red:* the site's `Content-Type` served ("text/html" vs "image/png"); `nosniff` removed ("The given header was not
    found"); the upstream type as a fallback (HTML served, 200).
  - **Gap found and fixed:** `OgImageRules.RasterType` checked only the signature, so the polyglot (and the script in a
    text chunk) was served as `image/png` (red before the fix: "Expected RasterType(Polyglot) to be null … but found
    image/png", the proxy answered 200). It now also requires a file well formed to its end (PNG: a chunk chain that
    fits, IHDR first, an IDAT, IEND last with nothing after; JPEG: EOI last; GIF: the trailer last; WebP: the RIFF size
    equal to the file's, chunks that fit, VP8/VP8L/VP8X first) and no `<script`, `<html`, `<svg`, `<!doctype`, `<head`,
    `<body`, `<iframe`, `<object` or `<embed` anywhere, case-insensitively. Served images were already safe to render
    (nosniff, the image type, the sandbox CSP); this refuses them outright, as asked. The existing test's truncated PNG
    fixture became a real 1×1 PNG.
  - *Runs (in a worktree):* the new tests and `OpenGraphCardTests` green; **the full SeoTester.Tests 210/210** (3 min
    53 s, on `3f246566`); Spark.Tests `SafeHttp*` 122/122.

### Q-D56a (2026-10-11): the Docker spike of the secured RavenDB

Closes Q-D56a. The owner started Docker Engine (29.8.1); the spike of `docs/code-coverage/raven-tls-migration.md` §8 ran on this machine, local and
throwaway: the committed compose files copied with their global network names prefixed `spike-` (the owner's `web`
was never joined), RavenDB `7.1.10-ubuntu.22.04-x64`, the licence one-shot (`curlimages/curl:8.11.1`) and the relay
(`boky/postfix:v4.3.0`) with their real settings, the apps, fetcher and Chromium as `alpine` listeners. Certificates
from the runbook's §4.2 commands verbatim. Afterwards every `spike-*` container, volume and network, the two images
pulled for it, the certificates, the licence copy and the backups were removed; nothing else of the owner's was touched.

**Measured:**
- **Scoping on 7.1.10** (Spark's `SparkRavenDbSecurity` in a Linux container): unsecured `http://` no answer, no
  client certificate 403; `codecoverage` (Admin on `Coverage`) listed only `Coverage`, wrote, read, deployed an
  index, configured expiration, received a subscription batch over TLS (TCP 38888); refused reading or writing
  `SeoTester`, creating or deleting a database, registering a certificate (`AuthorizationException`), listing
  certificates and activating a licence (403); `seotester` refused on `Coverage`. The runbook's §4.8 curl checks:
  `000`, 403, 200.
- **The runbook steps** §4.5 (secured start: `Server available on: https://coverage-raven:8080`), §4.6 (four PUTs, 201
  each; the listing shows `codecoverage`, `seotester` and "Server Certificate", not the well-known operator, so the
  runbook comment was corrected), §4.7, the README's TLS backup commands (stats and export, exit 0), §4.4's cold copy.
- **The licence one-shot, exactly as composed:** with the 825-day server certificate the **Developer** licence was
  refused (curl exit 22, HTTP 500, "server certificate total duration is greater than 4 months"); with a 90-day one,
  exit 0 and `/license/status` Commercial / Developer / not expired. The licence file was only mounted, never printed.
- **Container uids:** RavenDB 999 (`ravendb`), curl 100 (`curl_user`, group 101), the relay root (mounts no
  certificate), the CodeCoverage app 1654 (`app`). The runbook's §3 now states them.
- **Rollback:** after running secured, master's unsecured compose file on the same volume came up healthy, read the
  document written while secured over `http://`, and kept the licence; restoring the cold copy with §4.9's command and
  going secured again, the app certificate still read it (registrations survive both).
- **Network matrix** (TCP from inside each container): exactly D55's table. RavenDB reaches only the two apps and
  itself and has no internet; the licence one-shot reaches only RavenDB and coverage-app; the relay reaches the apps
  and the internet, never RavenDB; Chromium reaches only seotester-app; the fetcher reaches only seotester-app and the
  internet; coverage-app never reaches Chromium or the fetcher. Found, not new: the two apps reach each other over
  `web` (Traefik's network, shared by every public stack, older than D55); recorded in the runbook §1.

**Cited, not measured:** the Community licence's certificate rules. `Raven.Server` 7.1's
`SecretProtection.ValidateExpiration` (decompiled, both overloads) limits server-certificate duration only when
`licenseType == LicenseType.Developer`; nothing applies to Community, so production keeps the 2-year certificates.
Also not measured: host file modes (Docker Desktop for Windows ignores bind-mount ownership).

**Runbook changes:** DRAFT banner removed; §1 the measured matrix note; §3 the measured uids and the relay; §4.6 the
listing and 201s; §7 rewritten (measured vs cited); §8 replaced by these results.

**NuGet bump (owner, 2026-10-11: "update packages solution-wide to the latest version").** The CS8785 the sweep found
(`GenericMethodSourceGenerator`, `InvalidCastException` on `SparkQuota`, a record) is fixed upstream (Dotnet.Tools
#193/#194, PR #195, `MintPlayer.SourceGenerators*`/`ValueComparerGenerator*` 12.2.0) but **not adopted yet**: 12.2.0
breaks the build. *Evidence:* `MPSG001 ... MissingMethodException: Method not found: '...SymbolExtensions.OpenPathSpec(IndentedTextWriter, PathSpec)'`
from the generator in `MintPlayer.AspNetCore.Endpoints` 11.4.0-rc.0 (the newest), which ships its own Tools 12.1.0;
PR #195 gave `OpenPathSpec` optional parameters (a binary break), and the compiler loads one `MintPlayer.SourceGenerators.Tools`,
the newer, so every endpoint project fails (CS0115/CS0535). The family stays on 12.1.1 until Tools restores the
two-argument overload or Endpoints is republished against 12.2.x; CS8785 on Settings is still there.
Adopting 12.2.0 also needs three base-constructor swaps: its `[Inject]` constructor follows declaration order, so
`SparkSubscriptionWorker<T>`'s becomes `(IDocumentStore, ILoggerFactory)` (was `(ILoggerFactory, IDocumentStore)`;
CS1503 at `MessageFeeder.cs:73`, `MessageSubscriptionWorker.cs:45`, and `SparkSubscriptionWorkerTests.cs:31`). That is a
breaking change for any hand-written subclass constructor and belongs in that release's notes.
Moved: `MintPlayer.Assertions` 11.0.0-rc.5 → rc.6, `AngleSharp` 1.7.2 → 1.8.4, `xunit.runner.visualstudio` 4.0.0 → 4.0.1.
Kept: the rest is already latest (Microsoft.* on 11.0 rc.1; `RavenDB.Client`/`TestDriver` 7.2.6 is the latest stable,
only 7.2.7-rc is newer; `Newtonsoft.Json` 13.0.4 stays stable over 14.0.1-beta2). Build 0 errors, no new warnings in
the changed projects; SourceGenerators.Tests 631/631, Spark.Tests (SubscriptionWorker, Messaging, Settings, Endpoints)
626/626, SeoTester.Tests Checks 40/40.

**12.2.0 adopted (2026-10-11; supersedes "not adopted yet" above).** Upstream republished Endpoints against Tools 12.2.0
(MintPlayer.AspNetCore.Tools #42, PR #43, `MintPlayer.AspNetCore.Endpoints`/`.Abstractions`/`.Generator` 11.4.1-rc.0;
that batch bumped no other package we use: SpaServices and SpaServices.Xsrf stay 11.0.0-rc.3). Moved:
`MintPlayer.SourceGenerators`, `.Attributes`, `.Tools`, `ValueComparerGenerator`, `.Attributes` 12.1.1 → 12.2.0;
`MintPlayer.AspNetCore.Endpoints` 11.4.0-rc.0 → 11.4.1-rc.0. *Evidence:* full solution build, 0 errors, no MPSG001,
and **CS8785 is gone** (`MintPlayer.Spark.Settings` rebuilt, no `GenericMethodSourceGenerator` warning).
Constructor order, as predicted: the three base-call swaps above, plus a fourth the note missed,
`SyncActionSubscriptionWorkerE2ETests.cs:120`, which constructs `SyncActionSubscriptionWorker` positionally (CS1503).
The README of `MintPlayer.Spark.SubscriptionWorker` now states the break. Audit for silent swaps (same-type or `null`
arguments compile either way): 12.2.0 keeps declaration order, moves optional (nullable) `[Inject]` parameters last
and puts the auto-injected `IConfiguration` first; the only class mixing `[Inject]` properties and fields with a
property first is `SparkSubscriptionWorker<T>`, and every positional `null` passed to an `[Inject]` constructor
(`ReferenceResolver`, `RowSecurity`) still lands on the same parameter. **New generator defect (upstream):** 12.2.0
de-duplicates a base parameter only when type *and* name match textually, so a derived class declaring
`[Inject] IHttpContextAccessor httpContextAccessor` over `DefaultPersistentObjectActions<T>`'s optional
`IHttpContextAccessor? httpContextAccessor` got the parameter twice (CS0100, 7 sites: `ApiTokenActions`, `CarActions`,
`OidcApplicationActions`, `OidcResourceActions`, the three `NotificationActions` classes). Worked around by renaming
the derived field `contextAccessor` (both now get the accessor from DI; 12.1.1 passed one to both). Warnings: nothing
new from the bump; every warning absent from `baseline-build.log` is in a project that log built incrementally and
shows in earlier full 12.1.1 logs (CS0436 `EndpointContractAttribute` in IdentityProvider, CS0618
`UseOptimisticConcurrency`, CS8602/CS8618, ASP0014, SPARK024, RS10xx/RS2008 in the generator projects).
Tests: SourceGenerators.Tests 631/631; Spark.Tests (SubscriptionWorker incl. `SyncActionSubscriptionWorkerE2ETests`,
Messaging, Settings, Endpoints, Notification, OidcApplication, OidcResource) 733/733; SeoTester.Tests Fetcher 15/15.

**13.0.0 adopted (2026-10-11; supersedes the `contextAccessor` workaround above).** Upstream fixed #196 in
MintPlayer.Dotnet.Tools PR #197: the `[Inject]` constructor has one parameter per service type (type symbol,
nullability ignored; required wins), and `base(...)` fills each base slot by type. Moved every `MintPlayer.SourceGenerators*`
reference 12.2.0 → 13.0.0 and renamed the 7 derived fields back to `httpContextAccessor`; base and derived member now
share the one, required, parameter. 13.0.0's other break (members of one type share one parameter and one instance) hits
nothing here: a scan of every `[Inject]` member found no class with two members of the same type under different names.
The Tools assembly changed no API, so Endpoints 11.4.1-rc.0 (built against 12.2.0) still loads.

### L6 in the sweep (2026-10-11): a page read before its index caught up was never read again

The third local sweep failed `LiveSpikeTests.L6_…(waitForIndexes: True)` once (passed in the sweep before and alone):
`of 20 writes, 13 pushes fresh, 2 stale, 5 never pushed (stale evaluation diffed as unchanged)`.

**What the numbers meant (the test's accounting was wrong).** The page holds all 50 seeded runs and each write
changes one of them, so every evaluation sends a frame, stale or not: a stale read loads the row's new values from
the document and takes its place from the index, a values-only `patch` with no `order` (seen:
`While paused: patch top=(order unchanged)`). "Never pushed, diffed as unchanged" cannot happen in L6. "No frame in
2 s" meant an evaluation that took over 2 s; the test then wrote again without its 550 ms pause and counted the late
frame against the next write ("stale"). The sweep recorded no timings, so why 5 evaluations took over 2 s in it is
**not located**. Not reproduced in 2 runs of the full Spark.Tests (4 727) with CodeCoverage.Tests (1 213) in
parallel, nor in 5 L6 runs during CodeCoverage.Tests: 20/20 fresh every time, index wait ≤ 68 ms (11 ms max under
load), slowest write-to-top 471 ms. The test now prints, per write, every frame with its time, and the engine's own
log line per evaluation (how long the index wait took and whether it caught up), so a recurrence names the slow step.

**The real defect it pointed at.** `SparkIndexWait` waits on the right indexes and the right cutoff: every stale index (not disabled,
errored or a side-by-side copy) whose collections include the changed one (a superset of what the page reads), each with
`WaitForNonStaleResults`, whose server cutoff is the collection's last etag when the query starts, which is at or
after the write (`OnAfterSaveChanges` and the Changes notification both follow the commit). But when its 5 s bound
ran out, the live engine passed `unfinished: null`, so it **proceeded silently**, read the page stale, diffed it, and
never looked again: the rows' values arrived, their new order did not, and nothing re-ran the page until the next
write or `Revalidate` (1 min; 10 min in the test). *Red:* new `L6_a_page_read_before_its_index_caught_up_is_pushed_again_once_it_has`
pauses the auto index (`StopIndexOperation`), `IndexWait` 300 ms, writes: a values-only patch, then after
`StartIndexOperation` **no frame in 10 s** (`Expected top to be "SpRuns/…" …, but found <null>`).

**Fix.** `SparkIndexWait.WaitAsync` returns whether every index caught up. A live query evaluation whose wait ran
out logs a warning ("did not catch up within N ms; the page is read now and read again once they have") and
schedules a re-read of the same collections (`LiveSubscription.ScheduleStaleRetry`): after `Throttle` (≥ 100 ms),
doubling while re-reads stay stale with no new change in between, and none once the back-off reaches `Revalidate`,
which takes over. A wait that caught up clears it; a new change starts the back-off over. The worker sleeps no longer
than the next re-read. *Green:* the frame with the row on top arrives **842–875 ms** after the index is resumed. A
first version doubled the back-off across new writes too; L6 with the wait off (every read may be stale) then left 4
of 20 rows off the top for 3 s, because the back-off had grown to seconds. Fixed by resetting on a new change; now
20/20.

**L6 now asserts** what the design promises, not a latency: every written row reaches the top within 15 s (both
modes), and with the wait on, a first push is stale only when the wait reported running out (`corrected ≤ ran out`).
The old 2 s window is gone because it misattributed late frames, not to absorb slow evaluations: their time is printed.

**Runs** (`RAVENDB_LICENSE` = Developer licence): `tests/MintPlayer.Spark.Tests/Live/` 5× 49/49; full Spark.Tests
4 727/4 727 with CodeCoverage.Tests 1 213/1 213 in parallel. Guide: `docs/guide-live-updates.md`, `IndexWait` row.
