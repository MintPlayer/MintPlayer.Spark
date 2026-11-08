# Song page header: large thumbnail with a play button — PRD and plan

Status: **implemented** (2026-11-08, M1–M6; refinements found while building in §4c; M7 results in §5). Grilled
2026-11-08 (§4b D9–D22; spikes S1–S5 in §4a). Issue: #601, PR #603. One pull request (ng-spark + apps/MintPlayer), per the
one-PR rule. Release note: [release-notes-601.md](release-notes-601.md).

## 1. Goal

The song detail page on mintplayer.com (`/po/song/{id}/{slug}`) gets the hero header the legacy site had: a large
thumbnail of the song's first playable medium, with a play button centred over it. Clicking it queues the song in the
player dock. Below the header, the song's tabs, groups and attributes are rendered by Spark, through a new reusable
presenter component.

## 2. What exists today (evidence)

### Legacy (`C:\Repos\MintPlayer\MintPlayer.Web\ClientApp\src\app\pages\song\show`)

- `show.component.html:23-24`: a fixed **480×360** `div` (`mw-100 mx-auto`) with `background-image: url(playerInfos[0].imageUrl)`
  and `<youtube-play-button (click)="doAddToPlaylist(null)">` inside. Not rendered when `playerInfos` is empty.
  No `background-size` is set anywhere (the `yt-image` class has no CSS).
- `components/youtube-play-button`: a 68×48 inline SVG (YouTube-shaped rounded rect `#212121` @ 0.8 + white triangle),
  centred via 50%/negative margins; on hover the rect turns red, 0.2 s transition.
- Thumbnail URL computed **server-side**, `MintPlayer.Data/Helpers/SongHelper.cs:52-100` (`GetPlayerInfos`): regex per
  provider → YouTube `https://i.ytimg.com/vi/{id}/hqdefault.jpg`, Dailymotion `https://www.dailymotion.com/thumbnail/video/{id}`,
  Vimeo and SoundCloud a hard-coded placeholder image. Only `playerInfos[0]` is used.
- Click → `addToPlaylist` subject → `playlistControl.addToPlaylist` (`app.component.ts:268-269, 347-348`): **appends to
  the queue**, plays `playerInfos[0].url`.
- Page order (`show.component.html:4-122`): title row (h1, Edit/Remove, like) → hero → "General information" card →
  Lyrics card → Media card.
- SEO: og:image / twitter:image / JSON-LD `image`+`thumbnailUrl` all from `song.youtubeId` hqdefault (`show.component.ts:105,152,248,265`).

### Spark (`libs/node_packages/ng-spark`)

- `spark-po-detail` (`po-detail/src/spark-po-detail.component.ts:69`) loads the PO itself from the route's `:type`/`:id`
  (:238-251), or from the `objectType`/`objectId` inputs when embedded.
- Tabs → groups → attribute rows are **inlined in one template** (`.html:81-294`, with roughly 15 helper methods). There is no
  reusable presenter component.
- Besides rendering attributes, the page also handles: loading, rights, the action bar with custom actions
  (`SPARK_DETAIL_ACTIONS`), detail layouts (`SPARK_DETAIL_LAYOUTS`), panels (`SPARK_DETAIL_PANELS`), sub-queries, the deleted
  view, the slug (`writeSlug` :393-399) and SEO (`writeSeo` :464-475).
- `sparkRoutes({ poDetail })` (`routes/src/spark-routes.ts:7,22`) can swap the page. CodeCoverage and SeoTester wrap
  `<spark-po-detail>` that way.
- ~~Slugs are tied to `spark-po-detail` today; generalising them is #602.~~ *Superseded by PR #605 (2026-11-08):* the
  slug/canonical/SEO **services** are generic and root-provided (`SparkObjectUrlService.apply`,
  `services/src/spark-object-url.service.ts:15-27`; `SparkSeoService.forObject`/`noIndex`/`reset`, `spark-seo.service.ts:103,160,166`),
  and `?lang` works on any page (`SparkLanguageService`). But only `spark-po-detail` **calls** them for an object
  (`spark-po-detail.component.ts:268,396,454,468,471`; other pages only call `noIndex`/`reset`). `sparkObjectRoute({ type, path,
  loadComponent? })` adds the two routes + `data.sparkType`, and #602's planned `sparkObjectResolver` was not built
  (`docs/spark_generic_slugs_PRD.md`, Outcome M4). So a custom page on `po/song/:id(/:slug)` today has to load the object and
  call those services itself.

### apps/MintPlayer

- `Song` (`MintPlayer.Library/Entities/Song.cs`) : `Subject`; media are `Subject.Media: List<Medium>` with a full URL in
  `Medium.Value` + `TypeId` (no provider ids). `Song.json`: no tabs/groups; Media is an AsDetail attribute (order 6);
  `Medium.json:34` gives `Value` the `media-player` renderer.
- Routes (`ClientApp/src/app/app.routes.ts`): `...sparkRoutes({ slugs: true, notFound: true })` last; `home` is the only
  dedicated lazy page.
- Player: `PlayerService.add({url, title, subtitle, ownerId})` (`player/player.service.ts:42`) appends and starts
  playing when idle; the only caller is `renderers/media-play-button.component.ts:47-53` (same semantics as legacy).
- `VideoApis.isPlayable(url)` (`player/video-apis.ts`) is false until the player plugins' lazy chunks load, so it is
  **always false during a server render**.
- SSR is on (`withHydration()`, `withSeo(mintPlayerSeo)`); `seo.ts:15` uses a static `/icons/icon-512x512.png` for every
  page. No per-song og:image. No CSP `img-src` in the app.

## 3. Decisions

| # | Decision | Evidence / reason |
|---|---|---|
| D0 | **#602 (generic slugs) lands first, in its own pull request.** | Owner, 2026-11-08: slug handling "is a whole work of its own … interesting for SEO purposes", so it gets its own ticket and is picked up first. The dedicated song page then gets slugs from the framework instead of copying `writeSlug`. |
| D1 | **A dedicated, lazy-loaded song page** on the same URLs (`po/song/:id`, `po/song/:id/:slug`), placed before `sparkRoutes()` in `app.routes.ts`. | Owner, 2026-11-08. The URLs stay the same, so existing links, the legacy 301s (`LegacyRedirects.cs`) and the sitemap still work. |
| D2 | **Extract a reusable presenter component from `spark-po-detail`** (working name `spark-po-presenter`). It takes a loaded `PersistentObject` + `EntityType` and renders the tabs, groups and attribute rows (html:81-294 + helpers, renderers included). `spark-po-detail` itself switches to it, so there is one implementation. | Owner, 2026-11-08: "will need to be built sooner or later anyway". |
| D3 | **No `headerTemplate` input and no header registry.** | Owner, 2026-11-08: it "would unnecessarily add a new mechanism to the framework". *Supersedes the first draft's D2 (`headerTemplate`) and the grill's option B (`SPARK_DETAIL_HEADERS`).* |
| D4 | *Superseded by §4b D9–D16 (resolved asynchronously on the server and stored in `MediaThumbnails`; the SSR reason stands, because the value now comes with the loaded object).* **Thumbnail derived from the first medium whose URL a synchronous regex recognises** (YouTube, Dailymotion; Vimeo/SoundCloud per S3). No header when none matches, as legacy. | Must render during SSR: gating on `VideoApis.isPlayable` would render no header on the server (no LCP image, and a hydration mismatch). |
| D5 | **Click = `PlayerService.add(...)`** with the same item shape as `MediaPlayButtonComponent` (append; start when idle), built by one shared helper. | Same as legacy and as the Media table's play button. A shared helper keeps the two buttons from drifting apart. *Open: Q2.* |
| D6 | **Responsive `<img>`, not a CSS background**: `aspect-ratio: 16/9`, `object-fit: cover`, `width`/`height` attributes, `alt` = song title, `fetchpriority="high"`, no lazy loading. The button is a real `<button>` with an accessible name. | Legacy's fixed 480×360 box overflows on phones and has no alt text. The hero is the page's LCP element. |
| D7 | **Per-song og:image / JSON-LD image** from the same thumbnail; the mechanism comes from S4 (*resolved: D11*). | Legacy had it (`show.component.ts:105,152,248,265`); today every song shares the app icon. |
| D8 | Play-button look: **open** (Q1). | Legacy copies the YouTube logo shape; a neutral round play icon avoids implying every medium is YouTube. |

## 4. Spikes

Each spike is time-boxed (≤ 1 h), ends with a decision recorded here, and changes nothing in production on its own.

- **S1 — what the dedicated page needs besides attributes.** Go through everything `spark-po-detail` does besides rendering
  attributes (load, rights, action bar + custom actions, `SPARK_DETAIL_ACTIONS`/`PANELS`/`LAYOUTS`, sub-queries, deleted view,
  SEO, retry handling) and decide which of it the song page needs (Edit/Delete at least; history panel?). Decide whether those
  pieces are also extracted (e.g. an action-bar component) or whether the song page keeps them minimal. **This sets the
  presenter's boundary**: one component for attributes only, or a page "shell" plus the presenter.
- **S2 — presenter API.** Inputs (`item`, `entityType`, `compact`, renderer overrides?), outputs, and how renderers that need
  the page context (AsDetail rows, `owner`) get it. Confirm the existing po-detail tests pass unchanged after the switch.
- **S3 — thumbnail sources.** YouTube `maxresdefault.jpg` (not always present → 120×90 grey placeholder, no 404) versus
  `hqdefault.jpg` (always present, 4:3 letterboxed); Dailymotion's redirect; Vimeo/SoundCloud: oEmbed (a server call; cacheable)
  or a placeholder like legacy. Output: provider table + client-side or server-side derivation.
- **S4 — per-song SEO image.** Can Spark's `seo` block in `Song.json` take a per-object image (computed attribute, e.g.
  `Thumbnail`), or does the app set it through `mintPlayerSeo`? If the server must compute it, D4's helper moves to C#,
  so there is a single source of truth.
- **S5 — Dailymotion.** It does not play yet (bug in `@mintplayer/dailymotion-player`). Probably skip Dailymotion media
  when picking the hero until that is fixed.

## 4a. Spike results (2026-11-08, read-only investigation after #605 merged)

**S1 — what the page needs besides attributes.** `spark-po-detail` also does (PD = `po-detail/src/spark-po-detail.component.ts`,
H = its `.html`): loading from route or inputs, dropping a load overtaken by navigation (PD:238-252, 408-458); rights →
Edit/Delete/custom actions (`deriveActions` PD:478-515); the action bar with Back/Edit/Delete/custom/`extraActionsTemplate`/
`SPARK_DETAIL_ACTIONS` (H:16-57, PD:742-798); `SPARK_DETAIL_PANELS` (H:312-318) and `SPARK_DETAIL_LAYOUTS` (H:66-68);
sub-query cards (H:300-307); the deleted view (`?deleted=only`, PD:183-186, 384-386); live updates and server patches
(PD:258-264, 286-377); page vs action errors (H:2-15, no retry button); URL/SEO calls (PD:268, 396, 454-471). The page is
**read-only**: Edit navigates to `/po/:type/:id/edit` (PD:784). Song page needs: load, header, attributes, Edit/Delete
(rights-gated), the sub-query cards if Song has any (`spark-query-card` already stands alone), slug/canonical/SEO. Not now:
panels, layouts, deleted view, live updates.

**S2 — presenter API.** Rendering is H:70-295 (tabs H:81-88, group cards H:91-124, row template H:126-294, compact H:71-79)
with helpers PD:521-692. State it needs: `item`, `entityType`, `allEntityTypes`, `lookupReferenceOptions`, `asDetailTypes`,
`asDetailReferenceOptions`, the renderer registry, `compact`. Proposed `spark-po-presenter`: inputs `item` (required),
`entityType` (required), `compact` (default false), optional `allEntityTypes` (avoid a second fetch); no outputs. It owns
`loadLookupReferenceOptions`/`loadAsDetailTypes`, driven by an effect, **keeping the `PendingTasks` wrap** for SSR (PD:413-417).
Specs to keep green: `spark-po-detail.component.spec.ts` (63 tests), `.extensions.spec.ts`, `.live.spec.ts`. They read
`visibleAttributes`, `asDetailTypes`, `asDetailReferenceOptions`, `lookupReferenceOptions`, `getDetailRendererComponent/Inputs`
on the page, so they move to a new presenter spec (no delegating getters: no backward compat in preview).

**S3 — thumbnail sources** (live probes 2026-11-08 on two public videos):
| Provider | Thumbnail | Notes |
|---|---|---|
| YouTube | `https://i.ytimg.com/vi/{id}/hqdefault.jpg` | 200 for old and new videos; 480×360 (4:3, letterboxed → `object-fit: cover` at 16:9 crops the bars). `maxresdefault`/`sddefault`/`hq720` returned **404** (body = 120×90 grey JPEG) on an old video. *Corrects S3's "no 404" note.* |
| Dailymotion | `https://www.dailymotion.com/thumbnail/video/{id}` | Works (302 → `s2.dmcdn.net/…/x240`, 240 px); unknown id → 404. Skipped per S5. |
| Vimeo, SoundCloud | oEmbed `thumbnail_url` only | A network call; no synchronous URL. |
Existing id regexes to reuse: `YouTubeUrl.VideoIdPattern` (`apps/MintPlayer/MintPlayer/YouTube/YouTubeUrl.cs:23`, strict 11-char id,
already used by `Songs_ByYouTubeId`) and the plugins' patterns copied in `MintPlayer.Migration/Transform/Playability.cs:19-36`.
No CSP in the app (`Program.cs`, `index.html`), so `i.ytimg.com` images are not blocked.

**S4 — per-song SEO image: already supported, no framework change.** `EntityTypeSeo.Image` names the attribute holding the
og:image URL (`MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs:305`); `spark-seo.service.ts:112` reads it (falling back to the
site image), :251 writes `og:image` (+ `twitter:card=summary_large_image`), :131 the JSON-LD `image`. `Song.json:11-17` has no
`image`, so every song gets `/icons/icon-512x512.png` (`seo.ts:15`). *(The spike's "get-only property" route was superseded by
§4b D15: virtual attributes filled in `OnLoadAsync`.)* The server render runs the same service, so og:image
lands in the SSR HTML (`Program.cs:61,169`). Not written: an explicit `twitter:image` (X falls back to og:image).

**S5 — Dailymotion still does not play.** `@mintplayer/dailymotion-player` is still `^20.1.0` (lockfile 20.1.0); the bug is the
one in `docs/mintplayer_website_plan.md` (embed frame without the video id, refused by `frame-ancestors`). Not re-tested in a browser.


## 4b. Grilled decisions (owner, 2026-11-08)

The spikes' first proposal (a get-only `Song.Thumbnail` computed from a URL regex, YouTube only) is *superseded* by the
owner's design: thumbnails are resolved **asynchronously on the server** and cached in their own collection, so platforms
that need a web request (oEmbed) are covered too.

| # | Decision | Evidence / reason |
|---|---|---|
| D9 | **A `MediaThumbnails` collection**, one document per platform video, natural id `mediathumbnails/{platform}/{platformId}` → `{ Platform, PlatformId, ThumbnailUrl, CheckedAt }`. Nothing new is stored on `Medium`: the song keeps exactly what the editor typed. *Supersedes the first D9 (get-only property) and D4's client regex.* | Owner (G1–G3). The key is platform-wide, not song-specific: one video has one thumbnail whichever subject links it, so the name is `MediaThumbnails`, not `SongThumbnails` (owner, G6). |
| D10 | **`MediaUrl.TryParse(url) → (Platform, Id)?` in `MintPlayer.Library`**, promoted from `MintPlayer.Migration/Transform/Playability.cs:19-36` (regexes copied verbatim from the player plugins' `urlRegexes`, platform names as in `mintplayer-ng-video-player`), extended with the platform name and the plugins' `match2id` logic (Spotify: `track:{id}` / `episode:{id}`). Migration's `IsPlayable` uses it too. | Owner (G4). The recipient is C#, the plugin library is TypeScript (`libs/video-player/src/lib/player-type-finder.ts`); `Playability` is the one existing C# copy. **Trap:** it drifts when the library adds or changes a platform; an unknown URL just gets no thumbnail. |
| D11 | **Trigger: a hand-written message + `IRecipient`.** An `IBeforeSave<Song>` enqueues `MediaUrlsAdded(string[] Urls)` through `IMessageBus.EnqueueAsync(context.Session, …)`, so the message commits with the save and is dropped with it (`SparkInterceptors.cs:73, 244-247`; `MessageBus.cs:90`). The recipient parses each URL, batch-loads the existing ids, and resolves only the missing ones. Only Song saves enqueue in this PR. | Owner (G7: B over `IAfterSaveCommitted<Song>`, which was the alternative offered: `SparkCommittedInterceptors.cs:3-27`). The message carries URLs, not a song id, so it is reusable for Artist/Person later and needs no load. Messaging is already on (`Program.cs:52`). |
| D12 | **Failures:** a failed request (timeout, 5xx, 429) **throws**, so Messaging retries it. A definitive "no" (404, private, platform refuses) stores the document with **`ThumbnailUrl = null`** + `CheckedAt`, so it is never asked again. | Owner (G8, G8b). **Trap:** the null is permanent; a video that becomes public later needs its document deleted. `CheckedAt` keeps a future re-check sweep possible (not in this PR). |
| D13 | **Resolvers, one `IMediaThumbnailResolver` per platform:** YouTube `https://i.ytimg.com/vi/{id}/hqdefault.jpg` and Dailymotion `https://www.dailymotion.com/thumbnail/video/{id}` (derived, no HTTP); Vimeo, SoundCloud and **Spotify** via oEmbed `thumbnail_url` (typed `HttpClient`). A parsed platform without a resolver gets no document. | Owner (G10: B, Spotify included). S3 probes. oEmbed tests use WireMock with **real recorded bodies** (real-fixtures rule). |
| D14 | **Backfill: an `ISparkMigration`** that streams all songs, collects the distinct parseable URLs and enqueues `MediaUrlsAdded` in batches of ~100. It does not resolve inline. | Owner (G9). `ISparkMigration.cs:3-38` (once per database, at startup); `Program.cs:30` already calls `AddMigrations()`. A throwing migration would abort startup, so outbound HTTP stays in the recipient. |
| D15 | **The song page gets the hero through `SongActions.OnLoadAsync`** (decorate the base result): parse the media, one batched `LoadAsync` of the computed ids, then set two **JSON-only virtual attributes** in `Song.json`, `Thumbnail` and `ThumbnailMedium` (the URL the button queues). `GetDefaultIncludes` cannot help: it only follows ids stored in the document (`DefaultPersistentObjectActions.cs:210-218`). | Owner (G2: A). The detail load reads the document, not an index (`DatabaseAccess.cs:95-120`). Virtual attributes survive synchronization (#253), the mapper never writes them, and `OnLoadAsync` returns the `PersistentObject`, so #261 no longer blocks this. |
| D16 | **Hero rule:** the first medium in **array order** whose thumbnail is non-null, whose `MediumType.Visible` is true, and whose platform is not in an "unplayable" set (today only `dailymotion`, S5). The same for every visitor; it does not follow the Media table. | Owner (G11; closes Q3). Legacy took `playerInfos[0]`. `MediumType` is a `[Reference]`, so `MaterializeAsync` already includes it. **Trap:** the unplayable set must be emptied by hand when the Dailymotion player is fixed. |
| D17 | **No index in this PR.** A Songs list with a thumbnail column would need one (`[DefaultIndex(typeof(Song))]` + `[FromIndex]` view, map building the ids from `MediaUrl`'s `const` patterns like `Songs_ByYouTubeId.cs:27-31`, `LoadDocument` the thumbnail): one query instead of N+1. That is a separate change. | Owner (G5, G5b). **Trap:** a default index changes every Songs query (sort, search, filters), so it gets its own grill. |
| D18 | **SEO:** `Song.json` seo block `"image": "Thumbnail"` and `properties.thumbnailUrl: "Thumbnail"`. *Resolves D7.* | S4: `EntityTypeSeo.Image` (`EntityTypeDefinition.cs:305`), read at `spark-seo.service.ts:112`. |
| D19 | **Object-page lifecycle in ng-spark** (e.g. `sparkObjectPage()`): load by type/id from the route or `data.sparkType` (dropping overtaken loads, `PendingTasks`), then `objectUrl.apply` + `seo.forObject`, `noIndex` on refusal, `reset` on destroy. `spark-po-detail` and the song page both use it; embedding (`objectType`/`objectId` inputs, no SEO), live updates, patches and the deleted view stay in po-detail. | Owner (G12; closes Q4). S1: those calls live only in po-detail (PD:268, 396, 454-471), and #602's tier-(a) resolver was not built. **Trap:** the riskiest framework change here; the po-detail specs are the safety net. |
| D20 | **Presenter: attributes only** (`spark-po-presenter`, API per S2). The song page renders its own Edit/Delete gated on `item.can`, using po-detail's delete flow, and `spark-query-card` for sub-queries if Song has any. No action-bar extraction. | Owner (G13). **Trap:** a future Song custom action is not shown on the song page until the action bar is extracted. |
| D21 | **Play button: neutral round icon** (circle + triangle, scales with the image, accent colour on hover/focus, visible focus ring, `<button aria-label="Play {title}">`). *Closes Q1/D8.* | Owner (G14). With Vimeo/SoundCloud/Spotify heroes, a YouTube-shaped button would be wrong for most platforms. |
| D22 | **Click appends** (`PlayerService.add`, through the shared queue-item helper of D5), no "added to queue" confirmation. *Closes Q2.* | Owner (G15). Same as the Media table (`media-play-button.component.ts:47-53`) and legacy. |

## 4c. Refinements made while implementing (2026-11-08)

Each one narrows or corrects a §4b decision; the §4b text stays as the grilled record.

| # | Refinement | Evidence |
|---|---|---|
| R1 (D11) | The interceptor enqueues through **`IMessageOutbox.EnqueueAsync(session, …)`** (`MintPlayer.Spark.Messaging`), the session-bound half of the bus, and only the URLs **added** by this save (`SongActions.AddedMediaUrls(context.Before, entity)`); an unchanged save enqueues nothing. | `SongActions.cs` `OnBeforeSaveAsync`; tests `SongHeroTests.AddedMediaUrls_*`. Re-sending known URLs would only cost a batched load, but an edit of the title should not wake the recipient. |
| R2 (D12) | The recipient stores whatever resolved **before** rethrowing (`finally` + `SaveChangesAsync(CancellationToken.None)`), so a retry after a 503 on the third URL does not ask the first two again. A 429 throws `RetryAfterException` with the `Retry-After` delay (delta or date, else 1 min); 400/401/403/404 are the definitive "no". | `MediaUrlsAddedRecipient.cs`, `MediaThumbnailResolvers.cs` `OEmbedClient`; tests `MediaUrlsAddedRecipientTests.Keeps_answers_when_a_later_platform_throws`, `MediaThumbnailResolverTests.Too_many_requests_*`. |
| R3 (D13) | Vimeo's oEmbed is asked with **`width=1280`**: without it the thumbnail is 295×166, too small for the header; with it, 1280×720. A `thumbnail_url` that is not absolute `https` is treated as none (mixed content on the page). | curl of `https://vimeo.com/api/oembed.json?url=https://vimeo.com/76979871` with and without `width=1280` (2026-11-08); recorded bodies in `MintPlayer.Tests/Fixtures/oembed/`. |
| R4 (D16) | The hero platforms are an **allow-list, `{youtube, vimeo, soundcloud}`**, not "all but Dailymotion": the site's player loads only the YouTube, Vimeo, SoundCloud and Dailymotion plugins (`ClientApp/src/app/player/video-apis.ts`), Dailymotion does not play (S5), and Spotify and plain files have no plugin. Spotify and Dailymotion thumbnails are still resolved and stored (D13), for a later list or player. | `SongHero.Platforms`; test `SongHeroTests.Pick_skips_platforms_the_player_cannot_play`. **Trap:** add a platform here when its plugin is added to `video-apis.ts`. |
| R5 (D15) | Overriding `OnLoadAsync` turns off `SupportsBatchedLoad` for Song, so a multi-id load of songs runs `OnLoadAsync` per id instead of one batch. Accepted: correct by design (the framework does this for every decorating actions class), and the song page loads one song. | `DatabaseAccess.cs:151-168`, `DefaultPersistentObjectActions.cs:76`. |
| R6 (D20) | Edit/Delete visibility is one shared function, **`detailBuiltInActions`** (`@mintplayer/ng-spark/models`), used by both `spark-po-detail` and the song page, instead of a copy of po-detail's rules in the app. | `query-actions.ts`; `spark-po-detail.component.ts` `deriveActions`. |
| R7 (new) | **`sparkObjectRoute` swallowed the edit page** (a #602 defect, latent until a custom page sat on Spark's own `po/<alias>` path): its `:id/:slug` route matched `po/song/<id>/edit`. The slug route now has a `canMatch` that refuses a last segment `edit`. | `spark-routes.ts` `notTheEditPage`; spec "leaves the edit page to sparkRoutes() on Spark's own po/<alias> path". |
| R8 (D20) | Known gap, as the D20 trap said: the song page shows no add-on panels (e.g. History) and no custom actions. Song has no custom actions today. | `song-page.component.html`. |
| R9 (D9) | Document ids are written `MediaThumbnails/{platform}/{platformId}` (RavenDB ids are case-insensitive, so this is the D9 id); a Spotify id is `track:{id}` / `episode:{id}`. | `MediaThumbnail.DocumentId`. |

## 5. Milestones (one PR; #602 is merged)

Done: M1 (`5d505e17b`), M2–M6 (this PR's later commits). M7: see the end of this section.

1. **M1 — ng-spark:** `spark-po-presenter` (D20) and the object-page lifecycle (D19); `spark-po-detail` uses both; po-detail
   specs rewired (rendering assertions to a presenter spec, no delegating getters). Release note (minor).
2. **M2 — `MediaUrl`** (D10) in `MintPlayer.Library`, Migration switched to it; unit tests with real URL shapes (no personal
   data): YouTube watch/m./youtu.be/shorts/live, Vimeo, Dailymotion, SoundCloud, Spotify track/episode, Wikipedia → null.
3. **M3 — `MediaThumbnails` + resolvers** (D9, D12, D13): entity, the five resolvers, the `IRecipient<MediaUrlsAdded>`; tests
   for skip-existing, null on 404/private, throw on 5xx/timeout (WireMock, real oEmbed bodies).
4. **M4 — trigger + backfill** (D11, D14): `IBeforeSave<Song>` enqueue; the `ISparkMigration`.
5. **M5 — load + SEO** (D15, D16, D18): `SongActions.OnLoadAsync`, the two virtual attributes, the seo block; tests for the hero
   rule (order, hidden type, Dailymotion skipped, null thumbnail skipped).
6. **M6 — client:** `SongHeaderComponent` (D21) + shared queue-item helper (D22) also used by `MediaPlayButtonComponent`; the
   lazy song page via `sparkObjectRoute({ type: 'song', loadComponent })` before `sparkRoutes()`, with the presenter and
   Edit/Delete (D20).
7. **M7 — verify once at the end:** ng-spark + MintPlayer client tests, MintPlayer C# tests, then a browser check on a running
   host (YouTube song, Vimeo song once resolved, song without media, Dailymotion-only song, phone width, dark mode) and the
   SSR HTML (`<img>`, og:image, canonical with `?lang`).

Release: the ng-spark change ships to npm once the merged PR is backported to `release/angular-22.x` (`backport` label).
The app uses the workspace package, so mintplayer.com deploys from master without waiting for that.

## 6. Open questions

None. Q1–Q5 were closed by the grill of 2026-11-08 (§4b, G1–G15).
