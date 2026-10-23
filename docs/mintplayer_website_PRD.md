# MintPlayer website on Spark: PRD

Status: draft, 2026-10-22. Execution plan: [mintplayer_website_plan.md](mintplayer_website_plan.md).

## 1. Goal

Rewrite https://mintplayer.com as a Spark application, `apps/MintPlayer`, in this repository, deploy it to
the VPS beside CodeCoverage and SeoTester, and retire the Foxxl hosting. After the cutover, the
`MintPlayer/MintPlayer` repository is archived and moved out of the org. Everything in it apart from the
website (`MintPlayer.RestClient`, `MintPlayer.Client`, `Fetcher`, `Crawler`, …) is abandoned (owner,
2026-10-22).

`apps/MintPlayer` is a **production** app, like CodeCoverage and SeoTester. Add it to the "two of them are
production" paragraph in `CLAUDE.md` when it is created.

## 2. Where we start from (investigation, 2026-10-22)

### 2.1 The live site (`C:\Repos\MintPlayer`, `master`)

The live site is ASP.NET Core with SQL Server (EF), and **Angular 13.3** with Universal SSR, a service worker
and plain Bootstrap CSS. It does **not** use `@mintplayer/ng-bootstrap` today, so "full ng-bootstrap usage"
means writing a new UI, not migrating one.

| Area | What exists |
|---|---|
| Domain | Abstract `Subject` (soft delete, audit fields, Media, Likes, Tags) with three subtypes: Person (FirstName, LastName, Born, Died), Artist (Name, YearStarted, YearQuit, members via ArtistPerson) and Song (Title, Released, Description, artists via ArtistSong). Medium/MediumType covers YouTube, SoundCloud, Vimeo and Dailymotion. Lyrics are per song and user, with a timeline of `List<double>`. Likes (DoesLike). Tag has a hierarchy, and TagCategory carries a color. Playlist is per user, has public/private accessibility and holds PlaylistSong. BlogPost. ElasticSearchIndexJob. |
| Server | SPA API `web/v3/*`. Public API `api/v1/*` (JWT). `/sitemap` (an index plus per-subject pages), `robots.txt`, OpenSearch, an AMP song page, Elasticsearch indexing, HSTS middleware, and SMTP mail through Razor views. |
| Auth | Register, email confirmation, login, password reset. External logins: Facebook, Microsoft, Google and Twitter, with LinkedIn configured but not wired. TOTP 2FA with recovery codes, plus a per-user "bypass 2FA for external login" flag. WebAuthn through Fido2NetLib, though production has **no** credential table. Roles. |
| Client routes | `/`, `/search[/:term]`, `/account/{login,register,profile,two-factor,password/reset}`. `/person`, `/artist`, `/song` and `/playlist` each have list, create, `:id/:name`, `:id/:name/edit` and `favorite`. Songs add `:id/:title/sync` (the lyrics timeline editor) and playlists add `public`. Also `/mediumtype`, `/tag/category/...`, `/community/blog` and `/gdpr/{privacy-policy,terms-of-service}`. |
| Client libraries | ng-video-player, ng-playlist-controller, ng-player-progress, ng-{youtube,soundcloud,vimeo,dailymotion}-api, ng-share-buttons, ng-json-ld, ng-datatables, ng-select2, ng-pagination, ng-click-outside, … i18n is @ngx-translate with en, nl and fr (about 355 keys). |

**Production data is small** (S2 on the old branch). There are 752 users, of whom only 109 were ever
active, plus 141 songs, 138 artists, 9 people, 507 media, 16 playlists, 10 blog posts and 156 likes from 3
users. 2 users have 2FA enabled, and there are no WebAuthn credentials.

### 2.2 The earlier port (`C:\Repos\MintPlayer`, branch `feature/spark-migration`): how reliable is it?

The verdict is mixed. **The backend foundations are reliable. The website isn't built, and the branch is
stale against current Spark.**

| Reliable, verified by a spike | Not done, or stale |
|---|---|
| The Raven domain model and its `App_Data/Model`, security.json and translations | It pins Spark **10.0.0-preview.41** on net10. Current Spark is 11.0.0-preview.* on net11. |
| `MintPlayer.Migration`, the SQL `.bak` to RavenDB tool (S5): reconciler green with 1176 refs, 0 unresolved, in 10.6 s; 51 tests | It references `@mintplayer/ng-spark-auth`, which **no longer exists**; it is now `@mintplayer/ng-spark/auth/*`. |
| Karaoke timelines (S6): 0 mismatches over 507k samples | The ClientApp is 49 files: home, the song page, a player sidebar and rooms. There is no search, list, blog, GDPR or account UI. |
| `api/v1` compatibility (S7): 39/39 diff cases pass | There are no external logins, passkeys or Moderator group. |
| YouTube playlist import (S10), listen-together rooms (S9) | S8 (deploy) was local only and never reached the VPS. |
| SSR prototype (S1), at about 30 ms TTFB | `PRD-Feature-Parity.md` claims "done" and contradicts the later `PRD-Spark-Completion.md`. Trust the Completion doc, and treat both as history. |

The S3 blocker, where "email login fails for 750/752 users", **is resolved in current Spark**.
`SparkSignInManager` now resolves an email identifier through `FindByEmailAsync`
(`libs/authorization/MintPlayer.Spark.Authorization/Identity/SparkSignInManager.cs:94`). Username login was
already working and rehashing legacy hashes. Spike W2 re-confirms this against real data.

**Decisions carried over** from `PRD-Spark-Completion.md` §8. They need no new grilling unless the owner
says otherwise:
- D9: migrate from the SQL `.bak`.
- D10: no scraping.
- D11: drop AMP; `/amp/song/{id}` returns a 301 to `/song/{id}`.
- D12: lyrics are merged, keeping the most complete timeline.
- D15: keep `MediumType.Visible`.
- D16: Editor becomes Moderator.
- D17: keep LinkedIn login.
- D18: migrate all 752 users, with no announcement mail.
- D19: legacy times are Europe/Amsterdam.
- D23: RavenDB backups.
- D29: one shared lyrics text, with revisions.
- D30: the cutover shows a maintenance page.
- D31: rooms are open to anonymous listeners.
- D33: localized timestamps.
- D34: GDPR export and delete.

**Decisions superseded:**
- **D32, "two Spark PRs".** The one-PR rule applies. The website, any Spark changes it needs, and the
  migration all land in one PR here.
- **The NuGet pins.** `apps/MintPlayer` uses project references, like the other apps.
- **D24/D25, durable mail on MJML templates.** Use Spark's existing `AddMailManager()` and the auth mails
  as they are. MJML is not needed for stage 1.

### 2.3 What Spark gives the app today

- **Auth** (`spark.AddAuthentication<SparkUser>`, see `apps/SeoTester/SeoTester/Program.cs:52-66`):
  - `SparkLocalCredentials.Full` covers login, register, email confirmation and forgot/reset password.
  - TOTP and recovery codes.
  - `SparkPasskeys.Enabled` with `PasskeyServerDomain`. Pin it to `mintplayer.com` from day one: it is the
    relying-party id for life.
  - `ExternalLoginTwoFactor` and `ExternalLoginLinking`.
  - All five legacy providers exist: `AddGoogle`, `AddMicrosoftAccount`, `AddFacebook`, `AddTwitter` (OAuth
    2.0 + PKCE; the legacy site used OAuth 1.0a keys, so W2 checks the user-id match) and `AddLinkedIn`. They
    use the callback paths `/signin-<provider>` (`libs/authorization/MintPlayer.Spark.Authorization/README.md:298-304`).
    A provider without a ClientId is simply not offered.
- **Client auth**: `sparkAuthRoutes(withExternalLogin(...), withAccount())` from `@mintplayer/ng-spark/auth/*`,
  as in SeoTester's `app.routes.ts`.
- **Shell and pages**: `<spark-shell>`, the generic PO list, detail and edit pages, `sparkRoutes()` last, the
  `t` pipe, `translations.json`, `culture.json` and `<spark-language-selector>`. Custom public routes come
  before the shell route, following the `withShareRoutes()` pattern.
- **Server pages**: SeoTester's `Pages/ServerPage.cs` and `Legal/LegalPages.cs` render no-script HTML with
  nl/en culture selection. That is the pattern for bot-facing and SEO pages.
- **Not in Spark**: generators for sitemap, robots and OpenSearch. These are written in the app as minimal
  `MapGet` endpoints placed before the SPA fallback.

### 2.4 Your libraries, checked against Angular 22 (npm, 2026-10-22)

| Package | Latest | Angular peer | Use |
|---|---|---|---|
| `@mintplayer/ng-video-player` | 22.1.0 | ^22 | ✅ Use. Built on `@mintplayer/video-player`, `player-progress` and `player-provider` ^20. These replace the per-provider `ng-*-api` packages. |
| `@mintplayer/ng-share-buttons` | 22.1.0 | ^22 | ✅ Use |
| `@mintplayer/ng-click-outside` | 22.2.0 | ^22 | ✅ Use if needed |
| `@mintplayer/ng-bootstrap` | ^22.25 (in workspace) | ^22 | ✅ The whole UI |
| `@mintplayer/ng-playlist-controller` | 17.0.0 | ^17 | ❌ Peer conflict. Replace it **in the app** with a small signal-based queue service, using CDK drag-drop to reorder. |
| `@mintplayer/ng-player-progress` | 17.0.0 | ^17 | ❌ Superseded by `@mintplayer/player-progress` through ng-video-player |
| `@mintplayer/ng-json-ld` | 18.0.0 | ^18 | ❌ Emit JSON-LD in the app (a `<script type="application/ld+json">` from the server page or a tiny directive) |
| `@mintplayer/ng-{youtube,soundcloud,vimeo,dailymotion}-api` | 16.0.0 | ^16 | ❌ Superseded by `@mintplayer/video-player` |
| ng-datatables, ng-select2, ng-pagination | n/a | n/a | ❌ Replaced by ng-bootstrap and the Spark grid |

**Result: stage 1 needs no change to any library outside this workspace**, which is what the owner prefers.
The workarounds stay inside `apps/MintPlayer`. A library change, if spike S1 finds one, goes into one batched
release, listed in §7.

## 3. Scope by stage

### Stage 0: a basic site live on the VPS (target: today, 2026-10-22)

The goal is to get the data and the domain off Foxxl. It covers:
- The `apps/MintPlayer` scaffold, with Spark core and Authorization (`AddSpark` plus `AddAuthentication`).
- Entities, security.json and generic Spark CRUD pages for Person, Artist, Song, MediumType, Tag and
  TagCategory. Anonymous users can read; the Moderator and Administrators groups can write.
- A song detail page with `ng-video-player` that plays the song's media.
- Local login and register, TOTP and passkeys, all of which come with Spark for free. External providers
  only where a ClientId/secret is ready today.
- The data migrated from the Foxxl SQL backup (catalog and users), when a `.bak` is in hand today. See the
  fallback in the plan.
- Docker, compose, a Traefik router for `mintplayer.com` and `www`, the deploy workflow, a RavenDB database and
  client certificate, and the DNS cutover.

### Stage 1: full parity of the core site

- **Public UI.** A custom public layout built on `@mintplayer/ng-bootstrap`, with the Angular CDK where it
  helps (drag-drop for the queue, overlays). It covers home, the person, artist and song list and detail
  pages, the legacy URL shapes `:id/:name`, and search over a RavenDB index, which replaces Elasticsearch.
- **Player.** The play queue, playlists (public and private, with reordering), likes, favorites and tags with
  their hierarchy and colors.
- **Lyrics.** One shared text with revisions (D29), and the timeline sync editor at `/song/:id/:title/sync`.
- **Auth.** External logins (Google, Microsoft, Facebook, Twitter/X, LinkedIn: all are built into Spark), migration of
  the 2 users' TOTP, and the "bypass 2FA for external login" option, mapped to `ExternalLoginTwoFactor`
  (see W2).
- **Mail.** Email confirmation and password reset over the CodeCoverage SMTP relay, with DKIM for
  mintplayer.com.
- **i18n.** en, nl and fr in `translations.json`, using the legacy keys as the source.
- **Sharing and SEO.** Share buttons. sitemap.xml, robots.txt and the OpenSearch description. JSON-LD and
  OG/Twitter meta on server-rendered bot pages for songs, artists and people, following the SeoTester
  server-page pattern.
- **Smaller pages.** GDPR privacy and terms, and the blog (10 posts).
- **Redirects.** 301s for legacy URLs: `/amp/song/{id}` (D11) and any route whose shape changed.
- **Moderation.** A Moderator group (D16). Open editing with Moderation (D26/D27) is a stage 2 item unless
  #460 has already landed the seam.

### Stage 2: later

These are postponed:
- **SSR** (S1 on the old branch proved it).
- **The public `api/v1`** (S7 on the old branch). Port it only if a consumer still exists.
- **Listen-together rooms** (S9) and **YouTube playlist import** (S10).
- **Open editing and moderation**, and **GDPR export and delete** (D34).
- **Service worker and PWA install.**

### Out of scope

The rest of the MintPlayer repo, as listed in §1. AMP (D11). Scraping (D10). Elasticsearch, which is
replaced by a RavenDB index.

## 4. Architecture decisions (proposed)

| # | Decision | Why |
|---|---|---|
| A1 | `apps/MintPlayer/MintPlayer` (host) plus `ClientApp`, scaffolded from SeoTester. It uses project references to `libs/`. | It matches the other production apps, so there are no NuGet pins to drift. Name it **MintPlayer**, not "Coverage"-like: no `.gitignore` component clash. |
| A2 | Port the old branch's domain model, Actions, Indexes and `MintPlayer.Migration` (with its tests). Do not port its ClientApp. | The backend is spike-verified. The client is 49 files on a dead package. |
| A3 | The public site is custom Angular routes on ng-bootstrap, placed **before** `sparkRoutes()`. The generic Spark PO pages stay available for moderators and admins at their default routes. | The public look needs full control. CRUD for editors comes for free. |
| A4 | Bot-facing HTML (OG, JSON-LD, title, description) comes from server pages for `/song/{id}`, `/artist/{id}` and `/person/{id}`, chosen by User-Agent or a `?_escaped_fragment_`-free path, until SSR lands. | It gives most of the SEO value of SSR at a fraction of the risk. SeoTester's `Bot/BotPageEndpoint.cs` is the pattern. |
| A5 | Use the shared `coverage-raven` server (TLS, client cert), database `MintPlayer`, `EnsureDatabaseCreated=false`. | It has backups and nightly cert renewal. The alternative, a dedicated Raven container, is faster to set up today but adds a second server to operate. **The owner chooses: open question Q1.** |
| A6 | A mail sender domain on the existing `coverage-smtp` relay. | No new mail infrastructure. **Done 2026-10-23:** first `mail.mintplayer.com`, the same day the apex `noreply@mintplayer.com` (owner's preference, Foxxl dropped), on the relay, which moved out of CodeCoverage as `mail-relay` (apps/MailRelay; docs/mintplayer_mail_plan.md). |
| A7 | Passkey RP id `mintplayer.com`, never `www`. Traefik redirects `www` to the apex. | The RP id is permanent, and the host must stay the same for cookies. |

## 5. Spikes

Each spike is time-boxed, and its result goes into `docs/mintplayer_website_spikes/<id>.md`.

| Id | Question | Box | Blocks |
|---|---|---|---|
| **S1** | Do `ng-video-player` 22.1 and `@mintplayer/video-player` 20 play YouTube, SoundCloud, Vimeo and Dailymotion inside an Angular 22 zoneless Spark app, including the "next track" callback the queue needs? Can a queue service plus CDK drag-drop replace `ng-playlist-controller`? | 2 h | Stage 0 song page, stage 1 player |
| **S2** | Port `MintPlayer.Migration` to net11 and current Spark entities, then run it on a **fresh** production `.bak`. Is the reconciler still green? Does `Subject` inheritance map onto the Spark PO model and security.json? | 3 h | The stage 0 data import |
| **W2** | Identity against real data. Does the legacy hash rehash on email and username login through current `SparkSignInManager`? Do the 2 TOTP secrets migrate? Do external logins (provider + key) map onto `SparkUser` logins so existing Google/Facebook users keep their account? How does "bypass 2FA for external login" map? | 2 h | Stage 0 user import, stage 1 auth |
| **S3** | The public layout next to the Spark shell. A custom ng-bootstrap layout for anonymous visitors, the Spark shell for editors, one auth state. Can legacy URLs (`/song/:id/:name`) coexist with `sparkRoutes()` (route ordering)? | 2 h | Stage 1 UI |
| **S4** | Mail and DNS. Where does mail for `mintplayer.com` live today: MX at Foxxl? If Foxxl hosting is cancelled, does mail break? What SPF, DKIM and DMARC do we need for the relay? | 30 min, before the cutover | The stage 0 cutover |
| **S5** | Bot pages versus SSR. Do server-rendered bot pages (A4) with JSON-LD score at least as well as the live site in SeoTester? | 2 h | Stage 1 SEO |
| S6 | (Stage 2) Re-validate the old S1 SSR result on the current SpaServices and Angular 22. | — | Stage 2 |

## 6. Risks

| Risk | Mitigation |
|---|---|
| The Foxxl hosting ends before the data is exported | Take the SQL backup **first**, today (plan step 0.0). |
| The apex DNS change also moves mail | S4 before changing records. Keep the MX, SPF and other records as they are. |
| The passkey RP id is chosen wrong | A7: pin `mintplayer.com` from the first deploy. |
| Adding a network to the shared Raven means a CodeCoverage redeploy (production) | Make the compose change as small as possible and deploy when quiet. Or choose the dedicated Raven (Q1). |
| Legacy SEO URLs break | Keep the `/{type}/:id/:name` shapes. Add 301s for the rest. Run SeoTester before and after. |

## 7. Changes outside this workspace

The owner prefers as few as possible (2026-10-22). **None are needed for stages 0 and 1** (§2.4). If any
become necessary, they go into **one batched release**, published before the app PR merges:

- **Optional cleanup** (after stage 1, not blocking): deprecate on npm the packages this site no longer uses:
  ng-playlist-controller, ng-player-progress, the `ng-*-api` packages and ng-json-ld. Or bump them to 22 if
  other consumers exist.
- **Anything S1 finds** missing in `ng-video-player` / `@mintplayer/video-player`, such as an ended or next
  event, or a provider bug. Work around it in the app first.

Changes to Spark itself (`libs/`) are in-workspace and land in the same PR.

## 8. Grill decisions (owner, 2026-10-22)

These supersede anything above that conflicts. The plan has not yet been rewritten to match them.

| # | Decision | Evidence / note |
|---|---|---|
| G1 | **Full cutover today.** The apex `mintplayer.com` moves to the VPS with all data migrated. | Owner: "A" |
| G2 | RavenDB is the **shared `coverage-raven`**, database `MintPlayer`, with its own network and client cert. This needs a CodeCoverage redeploy. | Owner: "A". VPS RAM is 7.7 GB with about 2.7 GB available and no swap (checked 2026-10-22), so set memory limits in compose. |
| G3 | Mail is send-only, through the existing relay, from a subdomain sender (e.g. `noreply@mail.mintplayer.com`) with its own DKIM key. | Owner: "I can already send mails today". The apex SPF currently lists only `_spf.foxxl.com`. **Superseded 2026-10-23:** the sender is the apex, `noreply@mintplayer.com`, once the owner dropped Foxxl (docs/mintplayer_mail_plan.md, "Sender on the apex"). |
| G4 | **Nothing receives mail at `@mintplayer.com`.** Set a null MX, SPF for the relay only, and DMARC `p=reject`. Check every account's recovery address first. | Owner: "A". The current MX is Foxxl's spam-filter. |
| G5 | **The registration and DNS zone stay at Foxxl.** Only the records change, to A `188.245.190.60` and AAAA `2a01:4f8:c0c:f87c::`, as for `coverage.`. Only the web hosting and mail are cancelled. Confirm that the zone survives that cancellation. Claude edits the records in a `playwright_node` browser after the owner signs in. | Owner correction to Q5 |
| G6 | **The UI is the Spark shell for everything**, "similar to how MintPlayer's UI is built today". The legacy URLs `/song/:id/:name`, `/artist/...` and `/person/...` 301 to the Spark `/po/...` pages. Migrated ids keep the legacy numbers (`Songs/{id}`). | Owner: "A". Ids: old branch `MintPlayer.Migration/Transform/Primitives.cs:12` |
| G7 | **The player is a floating card** mounted outside `<spark-shell>`, dragged by a header handle with CDK `cdkDrag` bounded to the viewport, its position remembered per device, and snapped to a corner on phones. During a drag, a transparent overlay `<div>` covers the iframe (the legacy technique). It never goes below YouTube's 200×200 minimum. **The queue panel is a right-hand ng-bootstrap offcanvas** holding the queue (CDK drag-drop reorder) and the transport buttons, toggled from `sparkShellTopbarEnd`. It is docked open on wide screens. | Owner, on Q7 |
| G8 | The provider secrets are written into the VPS `.env` by **the owner**. Claude supplies `.env.example` with the key names only. Google, Facebook and Microsoft keep their existing registrations (same `/signin-*` callback paths on the same domain). Twitter needs a new OAuth 2.0 client; LinkedIn needs a registration. | Owner: "A" |
| G9 | **One PR** (`feat/mintplayer-website`), merged today, and deployed by a workflow on push to `master` copied from CodeCoverage's. | Owner, on Q9 |
| G10 | **Today's scope is stage 0 plus all of stage 1**, including the full database migration, en/nl/fr and all five external logins. | Owner, on Q10 |
| G11 | **The cut is at 22:00.** Whatever works then is merged and deployed, and the rest follows in a second PR. The owner said "22:00 GMT+1", which is 23:00 Brussels time before 25 October; Claude assumes 22:00 Brussels (20:00 UTC) unless corrected. | Owner, on Q11 |
| G12 | The source is the Plesk "Export dump" `mintplay_MintPlayer_2026-10-22_14-38-31`, a native SQL Server `.bak`. It is restored into LocalDB as `MintPlayer_Snapshot_20261022` (776 users, 145 artists, 15 people, 141 songs, 507 media, 16 playlists, soft-deleted rows included). **No re-export at cutover**: the owner accepts losing production changes made after 14:38. | Restore verified 2026-10-22 |

## 9. Open questions for the owner (as of the first draft; partly answered by §8)

- **Q1.** RavenDB: the shared `coverage-raven` (recommended, see A5) or a dedicated container to go live faster
  today?
- **Q2.** Can you produce a fresh SQL Server `.bak` from Foxxl today? If not, stage 0 launches with an empty
  catalog on a subdomain, or with the catalog alone imported from the public `api/v1`, which is the D9 cross-check
  path.
- **Q3.** Which external-provider app registrations (Google, Microsoft, Facebook, Twitter, LinkedIn) can be
  re-pointed at the new callback URLs today? Each one needs the redirect URI
  `https://mintplayer.com/signin-<provider>`. Twitter needs an OAuth 2.0 client and the "Request email"
  setting.
- **Q4.** Is mail for mintplayer.com hosted at Foxxl (S4)? If it is, it must be moved, or the MX records kept
  pointing at Foxxl, before you cancel.
