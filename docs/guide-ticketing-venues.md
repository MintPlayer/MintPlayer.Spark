# Ticketing venues and layouts

`MintPlayer.Spark.Commerce.Ticketing.Venues` (alias `ticketing-venues`, ng-spark entry point `@mintplayer/ng-spark/ticketing/venues`)
holds the venues, the seating layouts and their editor, the snapshots a sale uses, and the buyer's seat map
(`docs/commerce_PRD.md` §4.11, D7, D8, D25, D32, default 3; plan M14 and spike S7).

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>();
    spark.AddTicketingVenues();   // adds Inventory (M5) and Approvals (M3)
});
```

## Venues (D8, §4.18)

A `Venue` is a shared catalogue entry: a name, a description and an **address from reference data** (the
`MintPlayer.Spark.ReferenceData.Address` value object, edited with the `spark-address` renderer; free text stays possible).
The venue's IANA zone is copied from the address on save (D33). Nothing converts or formats zones here (K10).

Every save of a venue is an **Approvals document proposal**: modifications always, creations when
`Spark:Commerce:Ticketing:Venues:ApproveCreations` is on. The package sets `Spark:Approvals:Types:Venue` itself (as a
post-configuration, so configuration cannot switch the modification rule off). A holder of `ApproveProposal/Venue` saves
directly (Approvals' rule).

## Layouts (D8, D32)

A `VenueLayout` belongs to its creator. Its plans are separate documents (a 50k-seat plan is megabytes, never a form
attribute):

| Document | What |
|---|---|
| `VenueLayoutPlans/{layoutId}/draft` | the owner's working copy; each save names the revision it was made on (409 when stale) |
| `VenueLayoutPlans/{layoutId}/proposal` | a publication waiting for an approver, frozen, so later draft edits do not change what is approved |
| `VenueLayoutPlans/{layoutId}/v{n}` | published version *n*, created once (empty change vector), never written again |
| `VenueLayoutBackgrounds/{guid}` | an uploaded background image (PNG, JPEG, WebP, checked by its bytes; SVG refused), immutable |

**Publishing** (`POST /layouts/publish`): the first version publishes at once unless `ApproveCreations`; every later
version waits for an approver (D8: "modifications always need approval"). The request is the decision kind
`ticketing-venues.layout-publish`; its deciders are the holders of `ApproveProposal/VenueLayout`, who also publish their
own layouts without a request. Approving copies the frozen proposal to the next version; it goes stale when another
version was published meanwhile. Rejecting keeps the reason on the layout (`LastRejection`).

**Copy to edit** (`POST /layouts/copy`): anyone signed in copies a published layout (its latest version) into a new
private draft of their own. Only the layout's name is a form field; renaming is a proposal like any modification.

Endpoints (`/spark/ticketing/venues`, ids in query strings and bodies):

| Endpoint | Who |
|---|---|
| `POST /layouts/create`, `/layouts/copy` | signed in |
| `GET /layouts/plan?layoutId&version=draft\|proposal\|published\|{n}` | draft and proposal: owner and approvers; versions: signed in |
| `POST /layouts/draft`, `/layouts/csv/import`, `/layouts/publish`, `/layouts/background?layoutId` | owner |
| `GET /layouts/csv?layoutId&version`, `/layouts/diff?layoutId`, `/background?imageId` | as the plan |
| `GET /seatmap?snapshotId`, `/seatmap/state?snapshotId`, `/seatmap/background?snapshotId` | anonymous |

## The plan

Blocks are polygons (flat `x, y` lists in plan units) of three kinds (D7): `seatedNumbered` (rows of seats, each seat a
unit of the inventory engine), `seatedUnnumbered` and `standing` (a capacity, sold from a quota). Rows run front to
back; seats in a row run aisle to aisle, the order the single-empty-seat rule reads. Seat keys are unique in the whole
plan and match `^[A-Za-z0-9_.-]{1,64}$`, because they become unit ids. Decor (`polygon`, `stage`, `text`) sells
nothing. Price categories are keys with a name and a colour; the prices are the sale's (Ticketing.Events).

**Chains (D25).** A chain lists numbered blocks in fill order, each with its direction (`backToFront`, `reverseSeats`).
Best available seats a group in the next run along the chain, so groups sit together and the venue fills from the best
blocks outwards. A block is in one chain at most (the chain document is the engine's hot key; one chain per block group
is the contention lever, §4.3).

**Seat tags (default 3).** `accessible` (a wheelchair space), `companion` (the seat beside it, optionally naming it in
`companionOf`) and `restricted-view`. Accessible and companion seats are **never chain-filled**
(`ExcludedFromChains`); `AccessibleSeating.Check` tells checkout whether a ticket type may take a selection, and a
companion seat travels with its wheelchair space. Restricted view restricts nothing; it is shown, and usually priced
through a seat's own category.

**CSV (D32).** `block,row,seat,x,y,category,tags,companion_of,key` (RFC 4180, invariant numbers, tags `|`-separated).
Import replaces the rows of every block the file names and creates a missing block around its seats. Export guards
spreadsheet formula injection (a text cell starting with `= + - @` gets an apostrophe, removed again on import). The
browser editor reads and writes the same format with the File API and a Blob; the server has the same in `LayoutCsv`.

## Snapshots and the inventory

A date that goes on sale freezes a layout version (§4.11):

```csharp
var snapshotId = await venues.SnapshotAsync(layoutId, scope: date.Id);   // once per scope, never changed again
var map = await venues.ProvisionAsync(snapshotId);                       // chains + block quotas, idempotent
await venues.SetLiveAsync(snapshotId, saleEndsUtc);                      // live seat states until then

// checkout, from the buyer's choice:
new ClaimRequest(claimId, basketId, expiresAt, [map.Places("pit", 2)],
    BestAvailable: [map.BestAvailable("main", 4)],
    Picks: map.Picks(pickedSeatKeys));                                   // pick your seat, single-seat rule per row
```

Seats are the units `{snapshotId}/{seatKey}`, chains `{snapshotId}/chains/{chainKey}`, block quotas
`{snapshotId}/blocks/{blockKey}`. Republishing the layout changes nothing for a snapshot. Ticketing.Events (M13) stores
the snapshot id on its date; this package does not reference Events.

## Seat map and live states (S7, X2)

`GET /seatmap?snapshotId=…` answers the snapshot packed as parallel arrays (seats block by block, row by row), cached in
memory per snapshot. `GET /seatmap/state` and the public topic `ticketing-venues:{snapshotId}` carry the states as base64
of **2 bits per seat** in the same order (0 free, 1 held, 2 sold; about 17 KB for 50k seats) plus what is left per block.
Who holds a seat is never sent.

The frames are **pushed, not polled**. After every commit the inventory tells its `IInventoryCommitted` listeners which
units and quotas the commit touched (claim, adjust, convert, release, return, exchange, the sweeper's releases). Venues
maps them to their snapshots (`{snapshotId}/{seatKey}`, `{snapshotId}/blocks/{key}`). For each snapshot that is live
(`SetLiveAsync`) it schedules one push after `PushDelay` (250 ms). Commits on the same snapshot within that window
share the push, so a hot sale computes at most one frame per snapshot per delay. A push recomputes the frame from
`GetUnitStatesAsync` (batches of 1,024 compare-exchange reads) and publishes it when it changed. `SetLiveAsync` pushes
the first frame. Reads of `/seatmap/state` reuse a frame for `StateCacheDuration` (2 s). A hold that lapses frees its
seat without a commit until the inventory sweeper releases it; that release is a commit and is pushed. Topics are
in-process, so each instance pushes the commits it made itself.

## The Angular side (`@mintplayer/ng-spark/ticketing/venues`)

| Export | What |
|---|---|
| `<spark-seat-map>` | the buyer's map: canvas, live states, pan and zoom by mouse, touch (pinch) and keyboard, pick your seat; `[(selected)]` seat keys, `maxSelection`, `allowedTags` |
| `<spark-seat-picker>` | the map plus the **accessible list alternative** (block + quantity = best available; `(bestAvailable)`), and the picked seats as text with remove buttons (WCAG 2.1 AA, GA10) |
| `<spark-venue-layout-editor>` | the editor (below) |
| `<spark-venue-layout-review>` | the approver's visual diff: the proposal with added, moved and retagged seats outlined, and the counts (§4.13) |
| `SparkVenuesService` | the endpoints |
| `buildSeatMapModel`, `renderSeatMap`, `SeatMapLayer`, `SeatMapController` | the framework-free renderer, for a host's own page |

**The editor** edits the owner's draft: polygon blocks over an optional uploaded background, standing areas with a
capacity, decor, seat generation (rows × seats; straight, angled or curved; letter or number rows, from the front or
back; numbers, odd or even seats, from either end; rows widening further back), seat tags and categories on a
selection, price categories, chain order by dragging (or the arrow buttons), CSV import and export, undo and redo
(structural sharing, so a step costs only what it changed), snapping to a grid, touch (Pointer Events) and keyboard
(arrows move the selection, Delete, Escape, Enter closes a shape, Ctrl+Z/Ctrl+Y). Colours come from CSS variables
(`--spark-seat-*`) mapped to the Bootstrap theme, so dark mode needs nothing extra.

## S7: rendering 50k seats

**Design (K11: Canvas 2D, no library).** Three levels of detail by how many pixels a seat pitch covers: below 3 px every
block is one polygon shaded by what is left (no per-seat work); below 8 px seats are squares in one path per colour; above
that circles (sprites from 12 device pixels), with row labels from 11 px and seat numbers from 18 px. A uniform grid culls
and hit-tests. On top, **`SeatMapLayer`** renders a frame once into an `OffscreenCanvas` at a scale quantised to steps of √2,
over the view plus a 35 % margin per side, and every further frame of a pan or zoom stamps that bitmap with one `drawImage`;
it redraws when the scale leaves its step, the view leaves the margin, or the content changes.

**Measured** (2026-10-19, Chromium 155 via the Playwright MCP server on the owner's Windows 11 desktop, 1280 × 800 canvas at
DPR 1.25, synthetic stadium of 40 blocks × 25 rows × 50 seats = 50,000 seats, about 40 % sold and 10 % held; the harness
is `node tools/seat-map-perf/build-harness.mjs <out.html>`, served from a local static file, no app server):

| Scenario | Direct renderer, fps | With `SeatMapLayer`, fps (bitmap redraws) |
|---|---|---|
| whole stadium, pan (block level) | 220 | 210 (0) |
| zoom sweep from fit to 30 px/seat and back (all three levels) | 32 | **100** (15 in 240 frames) |
| pan at 4 px/seat, every seat on screen | 25 | **239** (0) |
| pan at 6 px/seat | 31 | **184** (4) |
| pan at 12 px/seat, circles + row labels | 34 | **121** (6) |
| pan at 24 px/seat, circles + seat numbers | 86 | **148** (8) |

Model build 72 ms, payload 33 ms, applying a state frame 10 ms. Criterion (≥ 50 fps pan and zoom, level of detail working):
**met with the layer cache**; the direct renderer alone is not enough below 24 px/seat (25–34 fps), because rasterising tens
of thousands of seats costs the GPU more than the JavaScript that issues them (median JavaScript 6–9 ms a frame). The
frame rates are `requestAnimationFrame` intervals on an uncapped headed browser; the machine was in normal use. No
low-end Android check (round 4). The Vitest render-budget tests (`seat-map/seat-map.spec.ts`) guard the level of detail,
the culling, the batching and the layer's reuse in jsdom; they are failure bounds, not measurements.
