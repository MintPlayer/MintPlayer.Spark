# Song page header: large thumbnail with a play button — PRD and plan

Status: **planned, blocked by #602** (2026-11-08). Issue: #601. One pull request (ng-spark + apps/MintPlayer), per the one-PR rule.

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
- Slugs are tied to `spark-po-detail` today; generalising them is #602.

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
| D4 | **Thumbnail derived from the first medium whose URL a synchronous regex recognises** (YouTube, Dailymotion; Vimeo/SoundCloud per S3). No header when none matches, as legacy. | Must render during SSR: gating on `VideoApis.isPlayable` would render no header on the server (no LCP image, and a hydration mismatch). |
| D5 | **Click = `PlayerService.add(...)`** with the same item shape as `MediaPlayButtonComponent` (append; start when idle), built by one shared helper. | Same as legacy and as the Media table's play button. A shared helper keeps the two buttons from drifting apart. *Open: Q2.* |
| D6 | **Responsive `<img>`, not a CSS background**: `aspect-ratio: 16/9`, `object-fit: cover`, `width`/`height` attributes, `alt` = song title, `fetchpriority="high"`, no lazy loading. The button is a real `<button>` with an accessible name. | Legacy's fixed 480×360 box overflows on phones and has no alt text. The hero is the page's LCP element. |
| D7 | **Per-song og:image / JSON-LD image** from the same thumbnail; the mechanism comes from S4. | Legacy had it (`show.component.ts:105,152,248,265`); today every song shares the app icon. |
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

## 5. Milestones (one PR, after #602 is merged)

1. **M1 — extract the presenter** (per S1/S2) into ng-spark; `spark-po-detail` uses it; existing tests stay green, plus
   presenter unit tests. Release note (minor).
2. **M2 — thumbnail helper** (where per S3/S4) + unit tests with real media URL shapes (no personal data): YouTube
   watch/short/youtu.be, Dailymotion, unknown URL → null, first match wins.
3. **M3 — `SongHeaderComponent`** (image, centred play button, hover/focus, dark mode) + the shared queue-item helper also
   used by `MediaPlayButtonComponent`.
4. **M4 — dedicated song page + route** (`pages/song/song-detail-page.component.ts`, lazy): load the song, header, presenter,
   the pieces S1 keeps, slugs/SEO through #602's mechanism.
5. **M5 — SEO image** per S4.
6. **M6 — verify once at the end:** ng-spark + MintPlayer client tests, then a browser check on a running host (YouTube song,
   song without media, phone width, dark mode), and check the SSR HTML has the `<img>` and og:image.

Release: the ng-spark change ships to npm once the merged PR is backported to `release/angular-22.x` (`backport` label).
The app uses the workspace package, so mintplayer.com deploys from master without waiting for that.

## 6. Open questions for the owner

- **Q1** Play-button look: keep the legacy YouTube-shaped button, or a neutral round play icon (D8)?
- **Q2** Click behaviour: append to the queue (legacy, D5), or **play now**?
- **Q3** Thumbnail: always the first medium, or follow a medium chosen in the Media table?
