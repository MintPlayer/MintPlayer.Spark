# SeoTester (#268): PRD and plan

Status: **spec consolidated after 8 grilling rounds** (2026-10-10); M1–M6 built. Issue: https://github.com/MintPlayer/MintPlayer.Spark/issues/268

§1–§11 are the current spec. §12 is the decision log (D1–D43 plus measurements): each decision with its evidence, in the order it was taken. Where the body and the log disagree, the log is the record and the body is wrong. Fix the body.

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
| Spikes S1–S13 | ⏳ (S1, S6, S8 (L5 open), S11, S12 (Chrome; Q-M6a open), S13 ✅ 2026-10-10, §12; S4, S5, S10 researched) |
| Milestones M0–M12 | ⏳ |
| M1 ✅ | 2026-10-10, commit `72c84933`: X2 SSRF-safe client in `libs/spark/MintPlayer.Spark/Http/`, adopted by the IdP (§12 "Spike S1") |
| M2 ✅ | 2026-10-10, commit `6df64fb3`: X1 document expiration in `libs/spark/MintPlayer.Spark/Expiration/` (`IExpiring`, `"expiresAfter"`, stamper, read filter, one 36 h configurator), adopted by Messaging, Mail, Moderation and the IdP (§12 "Spike S6"). ⚠️ `model.schema.json` gains `expiresAfter`: CI mints the next `schemas/v{n}` tag on merge (no tag created by hand) |
| M3 ✅ | 2026-10-10: X9 settings package `libs/settings/MintPlayer.Spark.Settings` (alias `settings`, `11.0.0-preview.104`) and X7 per-queue `HandlerTimeout` in Messaging (§12 "Spike S11"). Owner round 10 (D38–D41) committed separately before it |
| M4 ✅ | 2026-10-10: X11 organizations package `libs/organizations/MintPlayer.Spark.Organizations` (alias `organizations`, `11.0.0-preview.104`), the `ISparkSignedInHandler<TUser>` hook in Authorization (`preview.104`), ng-spark `/organizations` (`withOrganizationRoutes()`, `22.33.0`), and a startup refusal of unsupported verb combinations in `security.json` (§12 "Spike S13"). Guides `docs/guide-organizations.md` and `docs/guide-settings.md` (M3's). ~~Open: Q-M4a, Q-M4b (§11)~~ answered 2026-10-10: D42, D43 (§12 Round 11) |
| M6 ✅ | 2026-10-10 (§12 "Spike S12 and M6"): X10 notifications `libs/notifications/MintPlayer.Spark.Notifications` (alias `notifications`) with the hand-rolled Web Push library `libs/webpush/MintPlayer.Spark.WebPush` (both `11.0.0-preview.104`), preferences per organization (D44); X3 quotas (`[SparkQuota]`, `ISparkQuotas`, settings package; core `ISparkCustomActionGate`); X5 ng-spark score renderers (`@mintplayer/ng-spark/score-renderers`), CodeCoverage switched over with its DOM pinned; X6 share links in core (`ISparkShareable`, `AddShareLinks`, `ShareLink`/`RevokeShareLink`, `/spark/share`); ng-spark notifications/share UI; M5 follow-ups; S12 measured on Chrome (5/5 delivered). Guides: notifications, quotas, share links, score renderers. Open: Q-M6a–Q-M6d (§11) |
| M7 ✅ | 2026-10-10 (§12 "M7"): the SeoTester skeleton `apps/SeoTester` (host + SPA, `.Library`, `.Tests`; https 5013 / http 5014; database `SeoTester`), composed per §4.1 (external sign-in only with `ConfirmByEmail`, the four provider presets from configuration, MailManager, Messaging with the six crawl queues declared, SafeHttp, settings with every §6 gate and limit and the §7 weights, three quotas, notifications, organizations, the sharing switch); server-rendered `/bot`; Dockerfile; nx, solution and CI lists; `docs/seotester/README.md` (credentials to register). SparkId registers a Development `seotester` client. M6 leftovers: share-page headings, a clicked push is read. Open: Q-M7a, Q-M7b (§11) |
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
  - SeoTester joins the existing RavenDB network and a new **`seo-browser`** network.
  - A **Chromium container** joins **only** `seo-browser`, so it has no route to RavenDB (unsecured, internal) or postfix.
  - The goal is `internal: true` on `seo-browser` (no egress at all), which works if every browser request is fulfilled by SeoTester (§6); S3 proves it.
  - Memory limits and `shm_size` are set on Chromium. The compose file sets no resource limits today.
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
| F6 | **Page drill-down**: per-page findings for 7 days, then "page details expired" | D8 |
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

- **Projects:** `apps/SeoTester/SeoTester` (host + ClientApp), `SeoTester.Library` (entities, check implementations, catalogue seed JSON), `SeoTester.Tests`. Ports 5013/5014 (the next free pair).
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
| `PageResult` | **`@expires` 7 d** (D8) | ~1–3 KB | run, URL, status, depth, timings, title/description/h1 snapshot (previewers), findings `(checkId, status, params)`. Shown as a sub-query of `AuditRun`, not AsDetail |
| `CrawlFrontier` | `@expires` 2 d, deleted on completion | grows with the crawl | pending URLs + visited-set hashes, the checkpoint for page batches |
| `LookupCache` (RDAP, hstspreload, robots, PSI) | `@expires` 1–7 d | small | natural ids (`rdap/example.com`) |
| `TargetCooldown` | `@expires` = cool-down | tiny | per-target throttle across **all** users for unverified targets (D17) |
| `CheckDefinition` | permanent | small | admin-editable catalogue (D32) |
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

The stages run independently. Each one sets its flag on the run when it finishes, and the last one enqueues the aggregate with `BroadcastOnceAsync(runId)`. Page work is idempotent: a `PageResult` id is `{runId}/pages/{urlHash}`.

**Monitoring:** a cron sweep every 15 min selects verified sites with `NextRunAt <= now` (when the monitoring setting is on) and calls `BroadcastOnceAsync($"{siteId}:{slot}")`. This is the `ManifestScanScheduler` pattern.

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

- **SSRF, two layers:**
  1. Every server-side fetch goes through X2.
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
| S3 | Chromium container | image/RAM per page on Debian; `shm_size`; axe-core time; `ConnectAsync` + `RouteAsync` + host-resolver rules: **does any request escape?** Can `seo-browser` be `internal: true`? PDF render time | escape test red→green; numbers |
| S4 | RDAP coverage | expiry for .com/.net/.org/.eu/.nl/.be/.io; confirm .be has no RDAP or WHOIS expiry | TLD table |
| S5 | PSI + CrUX | real quota, latency, Lighthouse 13 insight ids, CrUX availability for small sites | real responses stored as fixtures |
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
- **Q-M5a (from M5, spike S8 L5): where is the real-proxy check (nginx/Traefik upgrade and idle timeout for `/spark/live`) run?** It needs a reverse proxy in front of a running Spark host. Not run in M5: Docker Desktop was not running on the build machine and no nginx/Traefik binary is installed, and probing production was not part of this milestone. What is known: production fronts the apps with Traefik (`apps/CodeCoverage/docker-compose.yml:221-233`), which forwards WebSocket upgrades by default; the server sends a `ping` frame after 30 s of silence (measured, `LiveSocketTests.A_quiet_socket_gets_a_ping_at_the_heartbeat`), below nginx's default 60 s `proxy_read_timeout`; the nginx recipe is in `docs/guide-docker-deployment.md`. Options: (a) start Docker locally and run nginx and Traefik in front of a test host; (b) measure on the production VPS with a throwaway container, as L1 was; (c) measure at the M12 deploy of SeoTester, which is the first app with a live type. **Recommendation: (c)**, because it is the real proxy and the real app, and nothing before M11 opens a live socket in production (Fleet and DemoApp are demos).
- **Q-M6a (from S12): where is delivery to Firefox, Edge and Safari measured?** Chrome 154 was measured (5/5 delivered and decrypted, §12 "Spike S12 and M6"). Edge refused to subscribe under automation (`Registration failed - permission denied`: its WNS channel is not reachable from an automation profile), and no Firefox or Apple device is reachable from the build machine. All three accept `aes128gcm` (RFC 8291) per their documentation, and the library is byte-exact against the RFC vectors. Options: (a) the owner subscribes a desktop Firefox, a desktop Edge and an iPhone with SeoTester installed to the Home Screen and the result is recorded; (b) a scripted run in a non-automation browser profile here; (c) do it at the M12 deploy, on the real origin over HTTPS. **Recommendation: (c)**, with (a)'s devices: iOS needs an installed PWA on a real HTTPS origin anyway, which only exists at M12.
- **Q-M6b (from M6): push subscriptions are documents of their own, not a list on `SparkUser`.** D30 says "stored per device on the SparkUser". Built as `PushSubscriptions/{userId}/{hash}` (read by id prefix), because a list on the user document would put every subscribe on Identity's concurrency stamp (a sign-in racing a subscribe fails one of them) and make Authorization know about push. Options: (a) keep separate documents; (b) move them onto `SparkUser`. **Recommendation: (a).**
- **Q-M6c (from M6): what a share link shows is declared in code** (`AddShareLinks(o => o.Share<Site>(attributes, queries))`, allow-lists) rather than in the model JSON. Options: (a) keep it in code; (b) a `"share"` block on the persistent object in the model (low-code, but it moves the model hash and the schema, and a model edit could widen a public link). **Recommendation: (a).**
- **Q-M7a (from M7): a feature gate has three values, not two.** D11 says the owner wants to "enable or disable" monitoring, intrusive probes, deep-crawl limits and sharing, with defaults "verified-only" for the first three and "allowed for unverified sites" for sharing. An on/off switch cannot express both, so each gate is built as `SeoFeatureScope` = `Off` / `VerifiedOnly` / `AllSites` (`SeoTester:Features:*`, defaults as D11). Options: (a) the three-valued gate; (b) a boolean per gate with "verified-only" fixed in code (then "intrusive probes on unverified sites" can never be switched on, which D11's legal note argues for); (c) two booleans per gate (enabled, unverified allowed). **Recommendation: (a)**: one setting per gate, every D11 default expressible, and `AllSites` for probes remains an explicit administrator choice.
- **Q-M7b (from M7): passkeys on SeoTester?** D2 says external sign-in only. CodeCoverage, also external-only, enables passkeys as a second, forge-independent way in. Built without them (the account page excludes them; the Authorization package ships its passkey rights to signed-in users regardless, as in every app; with passkeys off no passkey can be enrolled). Options: (a) keep them off (D2 as written); (b) enable them (`auth.Passkeys = SparkPasskeys.Enabled` and `PasskeyServerDomain = seotester.mintplayer.com`, pinned for life). **Recommendation: (a)** until the owner asks: a passkey's relying-party id cannot be moved later, so turning them on is a decision to make once, deliberately.
- **Q-M6d (from M6, X5): the renderers' look.** The letter-grade colours (A, B green; C, D amber; F red), `spark-delta`'s defaults being CodeCoverage's (1 decimal, small), and the gauge (a 48 px ring with a grade badge; no CodeCoverage precedent, not yet seen in a browser). **Recommendation:** keep them and review the gauge in SeoTester's UI (M11).


---

## 12. Decision log


### Round 1 (owner, 2026-10-10)

- **D1 (G1): production app.** `apps/SeoTester`, deployed on the same Debian VPS as CodeCoverage with its own compose service and deploy workflow. Treat changes as production changes from the first deploy. *Evidence: owner preference.*
- **D2 (G2): signed-in only.** External logins only (Microsoft, GitHub, X, Google); no local passwords, no anonymous audits. *Evidence: owner preference; abuse control, §6.*
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
  fresh; with the wait removed **19/20 fresh, 1 stale** — and a stale evaluation that diffs as unchanged sends
  nothing, leaving the viewer stale until the next change or `Revalidate`. **Decision: watch collections, not
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
