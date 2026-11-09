# Player polish and sortable AsDetail lists: PRD and plan

Status: **implemented and verified in the browser** (2026-11-09, M1 to M11). There is no issue; the work is PR #608. One
pull request covers it all (ng-spark plus apps/MintPlayer), per the one-PR rule. Requested by the owner on 2026-11-09,
in two messages of one session. Later the same day, follow-up requests in the same session added M8 to M11 (§1 items 6-8).

## 1. Goal

Five things the legacy mintplayer.com had (or that the owner asked for), brought to `apps/MintPlayer` and ng-spark:

1. A **progress bar** (`<bs-progress>`) in the queue sidebar, as in the legacy playlist sidebar.
2. On the song page, the **whole thumbnail** starts playback (adds the medium to the queue), not just the round button.
3. The song-page thumbnail is **horizontally centred**.
4. **Add a URL** to the queue through a `<bs-modal>`, as the legacy "Add URL" button did.
5. In `spark-po-edit`, an **AsDetail array can be reordered** by drag (angular/cdk/drag-drop) when the user has edit
   rights. The legacy song-edit page did this for media. The owner named these lists (2026-11-09):
   **Person.Media, Artist.Media, Artist.Members, Song.Artists, Song.Media**.

Follow-up requests (owner, 2026-11-09, same session):

6. A **volume slider and mute button** in the queue sidebar. **Add URL sits at the bottom** of the sidebar, and the
   list fills the space left over.
7. **Icons through `<spark-icon>`**, never `bi bi-…` classes: no bootstrap-icons styles are in scope in the components.
8. **Volume and mute are kept when the player moves to another site** (YouTube → Vimeo/SoundCloud), and the queue
   follows the player state on every medium. Both were library defects, fixed upstream (§2, "Upstream fixes").

## 2. What exists today (evidence)

### Legacy (`C:\Repos\MintPlayer\MintPlayer.Web\ClientApp\src\app`)

- `components/playlist-sidebar/playlist-sidebar.component.html`:
  - A row `mm:ss` | `<progress-bar [value]="currentTime / duration * 100">` | `mm:ss`, shown while `current !== null`.
  - It is followed by a marquee of the current title.
  - At the bottom is an "Add URL" button (`addVideoUrlClicked`).
- `app.component.ts:311-332` and `app.component.html:39-66`: the add-URL flow.
  - A `<modal>` inside a `<form>`, with a text input focused after 20 ms.
  - An `is-invalid` / `invalid-feedback` "URL not supported" message.
  - Submit → `playlistControl.addToPlaylist({ url })`.
- `app.component.ts:283-285`: `playerProgressChange(progress)` stores `{ currentTime, duration }` for the sidebar.

### apps/MintPlayer before this work

- `player/player-dock.component.html`: the queue `bs-offcanvas` has previous/play/next, shuffle/repeat and the list, but
  no progress and no add-URL. `(progressChange)` passed only `currentTime` to `PlayerService.progress` (for the
  controller's "previous restarts after 5 s").
- `player/video-apis.ts`: `VideoApis.isPlayable(url)` already matches a URL against the player plugins' own patterns.
- `pages/song/song-header.component.html` (#601): the `<img>` sat in a `div`, and only the centred `<button>` called
  `play()`. `.song-header` had `aspect-ratio: 16/9; max-height: 60vh` and full width. A height-capped header therefore
  lost its ratio instead of narrowing, so `.mx-auto` alone could not centre it.

### ng-spark: drag-reorder already existed

- `po-form/src/spark-po-form.component.html` (inline grid :248 and modal grid :334): `cdkDropList` / `cdkDrag` /
  `cdkDragHandle` with a grip icon, enabled by `attr.isSortable`. `onAsDetailReorder` (`.ts:1307`) does
  `moveItemInArray` on `formData`.
- `isSortable` comes from `[Sortable]` on the property (`ModelSynchronizer.cs:946`, AsDetail/primitive arrays only), and
  is written to `App_Data/Model/*.json`. Only `Playlist.Tracks` (MintPlayer) and HR's `Person` used it.
- **Gap 1:** none of the five lists the owner named had `[Sortable]`. `Media` is declared once, on the shared
  `Subject` base (`Entities/Subject.cs:19`), which covers Person, Artist and Song.
- **Gap 2:** the drag handle checked only `isSortable`, not any right. Cell editing is gated by `canEditDetailRow`
  (row type `Edit`, fail closed, `pipes/src/can-edit-detail-row.pipe.ts`).
- Read-only attributes never reach the form (`editableAttributes`, `.ts:419-423`), and the edit page itself needs
  `Edit` on the parent.

### Server behaviour for a reordered array (read-only investigation, 2026-11-09)

- `EntityMapper.PopulateObjectValuesAsync`, AsDetail array branch (`Services/EntityMapper.cs:784-866`):
  - It matches incoming rows to stored rows **by key** (`MatchStoredRows` :912-930, `SparkValueObjects.GetKey`), never
    by position.
  - It builds `items` in the **submitted order**.
  - **A pure reorder is therefore saved even without `Edit/{RowType}`.** Each row matches its own stored row, and the
    restored stored instances keep their submitted positions (:829).
- `AttributeWriteShield.ShieldRowsAsync` (`Services/AttributeWriteShield.cs:180-216`) matches by key the same way
  (`KeyRows` :241-253).
- The server **never reads `IsSortable` on save**. Its only uses are in `ModelSynchronizer.cs:1020,1114`. A client can
  therefore reorder any array.
- **Defect found (fixed in M7, D11):** without row `Edit`, *every* matched row is counted as `restored`
  (:823-831), changed or not. A save that only echoes the rows back (a reorder, or editing just the parent's title)
  warns "N {RowType} row(s) were left unchanged: you may not edit them." The client cannot help here:
  `as-detail-conversions.ts:287` sends every row attribute with `isValueChanged: true`.
- Tests: `AsDetailRowRightsTests.cs:229` (`An_unchanged_collection_needs_no_row_right`) checks the value but not the
  warning. `AsDetailRowIdentityRoundTripTests.cs:293` (`Reordering_lines_moves_the_keys_with_them`) covers reorder
  identity, but not rights.

### Upstream fixes in mintplayer-ng-video-player (2026-11-09)

- **#105, fixed by PR #106** (`@mintplayer/video-player` 20.2.0, vimeo-player 20.1.2, soundcloud-player 20.1.2):
  - Measured before the fix: the volume and mute set on YouTube were lost when the next medium played on another site.
  - Each new adapter started at its site's defaults.
  - Vimeo's `setVolume` also unmuted.
  - Since 20.2.0, `VideoPlayer` owns the volume and mute. It re-applies both to every new adapter and after every load,
    and ignores the site's own reports for `SETTLE_MS` = 2000 ms after a load. That is about 4× the ~500 ms of stray
    reports measured.
- **#107 (with #100), fixed by PR #108** (`@mintplayer/video-player` 20.2.1, `@mintplayer/ng-video-player` 22.3.0):
  - Measured before the fix: going from a *playing* YouTube video to a SoundCloud track never emitted `playing`.
  - `distinctUntilChanged` in `VideoPlayer` deduplicated states across media, so the queue showed "Play" while the
    track played.
  - Since 20.2.1, the state resets for every medium.
  - Since 22.3.0, the Angular wrapper no longer echoes values it reported back to the player (#100).

## 3. Decisions

| # | Decision | Evidence / reason |
|---|---|---|
| D1 | The progress row lives in the queue offcanvas, between the transport buttons and the list: elapsed, `<bs-progress><bs-progress-bar>`, total, then the current title (no marquee). | Legacy sidebar layout. `<bs-progress-bar>` takes `minimum/maximum/value/label/valueText` (ng-bootstrap `progress-bar` typings). |
| D2 | `PlayerService.position` is a signal of `{ currentTime, duration }`, set by `progress()`. The dock resets it to 0 on every new `play()` emission. | `(progressChange)` emits `PlayerProgress` (`@mintplayer/player-progress`). The service imports that type rather than declaring its own (owner, 2026-11-09; this supersedes the first version's structural `PlayerPosition`). |
| D3 | Time is shown as `m:ss`, or `h:mm:ss` from one hour up. | Legacy `date:'mm:ss'` wrapped after 60 minutes. |
| D4 | The whole thumbnail is one `<button>` (`.song-header-trigger`, `cursor: pointer`). The round icon is decoration (`aria-hidden`, `pointer-events: none`), and its hover/focus effect is driven from the button. The `img` gets `alt=""` because the button carries the label. Without a playable medium, the header stays a plain image. | Owner request (2). One focusable control, not two overlapping ones. |
| D5 | `.song-header` gets `max-width: calc(60vh * 16 / 9)` and `.mx-auto`. | `max-height` alone squashed the ratio at full width (see §2), so the header never got narrower. |
| D6 | Add URL: an "Add URL" button at the bottom of the queue opens a `<bs-modal>` with a `<bs-form>` input. Enter or "Add to the queue" validates with `VideoApis.isPlayable` (YouTube, Vimeo, SoundCloud, Dailymotion). An unsupported URL shows an error and keeps the modal open. A valid URL is queued as `{ url, title: url }`. | Legacy flow. It uses the same validation the media renderers use. ng-bootstrap components only (memory: no plain bootstrap classes). Enter is handled with `(keydown.enter)`, as `spark-board`'s rename modal does. The input is bound with FormsModule `[(ngModel)]` to the `urlToAdd` signal (owner, 2026-11-09). The error shows while the text equals the refused text (`rejectedUrl`), so editing the text hides it. |
| D12 | The queue sidebar has a mute button (`volume-mute`/`-down`/`-up-fill` icon) and a `<bs-range>` from 0 to 100 above the list. `PlayerService.volume`/`muted` are two-way bound to `<video-player>`'s `[(volume)]`/`[(mute)]`. Moving the slider unmutes, as the sites' own players do. The offcanvas content is a flex column: `.queue-list` grows and scrolls (`flex: 1 1 auto; min-height: 0; overflow-y: auto`), so Add URL sits at the bottom. | Owner request (6). `bs-range` is the ng-bootstrap component (memory: components, not classes). |
| D13 | Every icon is a `<spark-icon name>`. The SVGs come from `bootstrap-icons/icons/*.svg` and are registered once in `src/app/icons.ts` (`provideAppIcons()`, through `SparkIconRegistry`). `media-link`'s `icon()` returns names without the `bi-` prefix. | Owner, 2026-11-09: "`class="bi bi-` will never work, no styles exist globally". |
| D14 | The dock keeps no audio or state workarounds: no re-applying volume/mute on `capabilitiesChange`, and no reset of `state` to `unstarted` on a new medium or on `ended`. The video-player packages do both (§2, "Upstream fixes"). Versions: ng-video-player ^22.3.0, video-player ^20.2.1, vimeo-player ^20.1.2, soundcloud-player ^20.1.2. | The workarounds could not fix the defects anyway: the dedupe lived inside `VideoPlayer`, and Vimeo's `setVolume` unmuted (measured, §5). |
| D7 | The player UI strings stay hardcoded English. | The existing dock (`Queue`, `Previous`, …) is not translated. Translating it would be a separate change. |
| D8 | `[Sortable]` on `Subject.Media` (Person, Artist, Song), `Artist.Members` and `Song.Artists`. `"isSortable": true` goes into `Artist.json`, `Person.json` and `Song.json`, generated with `dotnet run --project apps/MintPlayer/MintPlayer -- --spark-synchronize-model`, which also updates `modelHashes.json`. ⚠️ A hand edit is not enough: the host refuses to start ("Spark model is out of sync with this build") when the hashes don't match. The two new properties carry `//` comments, not `///` summaries, because the synchronizer turns a summary into a user-visible attribute description in `translations.json`. | Owner, 2026-11-09: "Person.Media, Artist.Media, Artist.Members, Song.Artists and Song.media should all be sortable". |
| D9 | Reordering in `spark-po-form` needs `isSortable` **and** `canEditDetailRow` (row type `Edit`, fail closed). This applies to the inline and modal grids alike, and to the handle column, the drop list and the empty-row colspan. `onAsDetailReorder` re-checks the same condition. | Owner: "if the user has edit-permissions". Parent `Edit` and attribute writability are already implied by the form (§2). Row `Edit` is the remaining right, and it is the same gate the cells use. This is stricter than the server, which accepts a pure reorder without row `Edit` (§2), so the UI never offers a drag the server would then warn about (§6 Q1). |
| D11 | The "row(s) were left unchanged" warning counts only rows the caller **tried to change**. `EntityMapper.RowWouldChangeAsync` writes each attribute the populate would write onto a scratch instance, seeded with a JSON copy of the stored value (a TranslatedString merge mutates its target). It compares the results as JSON and compares nested AsDetail rows by order and key. The stored row is restored either way: the comparison only decides the warning, and every doubt (a conversion error, an uncopyable type, depth > 8) answers "changed", so the worst case is a needless warning, never a silent loss. | Owner go-ahead on Q1, 2026-11-09 ("y"). The client cannot supply the signal (`isValueChanged: true` on every row attribute, §2). Comparing on the server keeps the client untrusted. |
| D10 | No server-side `IsSortable` check on save. | Order is data the parent's `Edit` already covers. Refusing reorders of non-sortable arrays is a separate hardening question (§6 Q2). |

## 4. Milestones (one PR)

| M | Work | Status |
|---|---|---|
| M1 | Progress bar: `PlayerService.position` (`PlayerProgress`), `progress(position)`, reset on new play, offcanvas row (D1-D3). `player.service.ts`, `player-dock.component.{ts,html}`. | ✅ |
| M2 | Song header: whole-thumbnail trigger, centred (D4, D5). `song-header.component.{html,scss}`. | ✅ |
| M3 | Add-URL modal (D6). `player-dock.component.{ts,html}`. | ✅ |
| M4 | `[Sortable]` on the five lists, plus the model JSON (D8). `Subject.cs`, `Artist.cs`, `Song.cs`, `App_Data/Model/{Artist,Person,Song}.json`. | ✅ |
| M5 | ng-spark: reorder gated by row `Edit` (D9). `spark-po-form.component.{html,ts}`, plus specs: the existing direct-call tests now set row rights, a new test checks that a reorder is a no-op without `canEdit`, and new inline/modal tests check that no handle renders without `canEdit`. | ✅ |
| M7 | Server: warn only about rows that changed (D11). `Services/EntityMapper.cs` (`RowWouldChangeAsync`, `AsDetailWouldChangeAsync`, `TryCopyValue`, `SameValue`). Tests in `AsDetailRowRightsTests`: a reorder without Edit keeps its order and doesn't warn, unchanged rows don't warn, and a mixed save warns about only the changed row. | ✅ |
| M6 | Verification: run `spark-po-form.component.spec.ts`, type-check the MintPlayer client (`ngc -p tsconfig.app.json --noEmit`, done for M1-M3: no errors in `src/app`), build `MintPlayer.Library`. Check in the browser that the song page, queue and song edit page work (drag Media and Artists, then save and reload). | ✅ (§5) |
| M8 | Add-URL input on `[(ngModel)]`. Volume slider, mute button, and Add URL at the bottom (D6, D12). `player-dock.component.{ts,html,scss}`, `player.service.ts`. | ✅ |
| M9 | Icons through `<spark-icon>` (D13). `icons.ts`, `app.config.ts`, `player-dock`, `queue-toggler`, `media-play-button`, `media-link`. | ✅ |
| M10 | video-player 20.2.0 + the site packages (#105). Drop the dock's volume/mute re-apply (D14). | ✅ |
| M11 | ng-video-player 22.3.0 + video-player 20.2.1 (#100, #107). Drop the `unstarted` resets (D14). | ✅ |

## 5. Verification notes

- 2026-11-09: `ngc -p tsconfig.app.json --noEmit` on the MintPlayer client gave only the known TS6059 "not under
  rootDir" errors for the path-mapped `libs/node_packages/ng-spark` sources, and none in `src/app`.
- 2026-11-09: `vitest run po-form/src/spark-po-form.component.spec.ts`: 66 passed. `dotnet build
  MintPlayer.Library.csproj`: 0 errors.
- 2026-11-09 (M7): `dotnet test --filter AsDetailRowRightsTests|AsDetailRowIdentityRoundTripTests` passed 20 of 20,
  including the three new tests.
- 2026-11-09, browser (`apps/MintPlayer`, https 5015, local RavenDB `MintPlayer` on 8080, signed in as an
  Administrator):
  - **Song header:** it is a `<button>` with `cursor: pointer`. At 1600×700 it is 747×420 (16:9), with 263 px on each
    side, which is the 60vh cap, centred. Clicking 20 px from its top-left corner (the `<img>`, away from the icon)
    queued and played the song.
  - **Progress bar:** the queue offcanvas showed "0:53 ━━ 2:45" and the bar reported `aria-valuetext` "0:49 of 2:45".
    After *Next* it reset to the new medium's length ("0:10 of 2:54").
  - **Add URL:** the modal opened with the input focused and `.form-control`-styled. `https://example.com/not-a-video`
    plus Enter showed "This URL is not supported." and the modal stayed open. A YouTube URL plus "Add to the queue"
    closed the modal and appended the URL to the queue.
  - **Sortable lists:** `/po/song/Songs%2F30/edit` showed drag handles on Artists (2 rows) and Media (3 rows). I
    dragged the third medium to the top and swapped the two artists, then saved. The stored document has the new
    order (`Media[0]` = the "Live Performance" video, `Artists` = `[Artists/29, Artists/28]`).
  - Not checked in the browser: a user *without* row Edit. No local group has Edit on Song but not on Medium or
    SongArtist, and checking it would mean changing `security.json`. The spec tests cover that gate (M5), and
    `AsDetailRowRightsTests` covers the server side (M7).
- 2026-11-09, browser, follow-ups (M8-M11):
  - **Before the upstream fixes:**
    - YouTube at volume 30 and muted, then Next to Vimeo or SoundCloud, came back at the site's defaults.
    - Re-applying volume and mute in the dock did not fix Vimeo, because its `setVolume` unmuted.
    - From a *playing* YouTube video, SoundCloud emitted no `playerStateChange`. From a *paused* one, it emitted
      `playing` at 1099 ms.
  - **20.2.0 (M10):** YouTube at volume 30 and muted, then SoundCloud, stayed at 30 and muted. No `volumeChange` or
    `muteChange` was emitted on the switch.
  - **22.3.0/20.2.1 (M11):**
    - From YouTube *playing*, Next → SoundCloud reported `playing` after 1.4 s.
    - YouTube at volume 30 and muted, seeked to 3 s before its end (adapter `setProgress`):
      - On `ended`, the queue advanced to SoundCloud: `unstarted`, then `playing` at +2.6 s.
      - SoundCloud stayed muted at 30, and the position advanced.
  - `ngc -p tsconfig.app.json --noEmit`: only the known TS6059 noise.

## 6. Open questions

- ~~Q1: fix the spurious "rows were left unchanged" warning?~~ The owner said yes (2026-11-09), and it is done as D11 / M7.
- **Q2: refuse a reorder of a non-`IsSortable` array on the server?** Not needed for correctness (D10). Noted only.
