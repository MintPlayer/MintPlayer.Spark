# Iconic: vector icon, cursor and animation editor on Spark: PRD

Status: draft (2026-11-20). Plan and log: [iconic_plan.md](iconic_plan.md).

## 1. Goal

Iconic is a public web app, planned for `iconic.mintplayer.com`. Users draw on a canvas of fixed size and download the result as:

- icons (`.ico`)
- cursors (`.cur` and animated `.ani`)
- images (`.png`, `.svg`)
- animations (`.gif`, APNG, animated WebP)

Users with an account store their projects on the server. Anonymous users can draw and export, but they cannot save.

Iconic is a Spark app in `apps/Iconic`. Like CodeCoverage, SeoTester and MintPlayer, it is a **production** app once it is deployed.

## 2. Where we start from (investigation, 2026-11-20)

### 2.1 DrawIt (`C:\Repos\DrawIt`): the owner's earlier desktop tool

DrawIt is a C# WinForms (.NET 4.5) geometric drawing tool. It has a single commit, and its identifiers are Dutch: Punt is a point, Rechte a straight line, Vlak a filled plane and Vorm a shape. The engine is in `DrawIt/Tekenen/`.

**What we keep (the ideas, not the code):**
- **Points are first-class, shared objects.** A shape refers to points instead of holding coordinates of its own: `Rechte` has `Punt1` and `Punt2` (`Rechte.cs:30-43`), and `Cirkel` has a collection of `Punt` (`Cirkel.cs:49`). Geometry is recomputed from the points on every draw.
- **Merging points by reference.** Dropping a dragged point on another point re-targets the dragged one (`Tekening.cs:1555-1559`, `Punt.cs:35,290`), so every shape that used it follows.
- **The style model:**
  - A line style is color, width, a dash style or custom dash pattern, and a dash cap (`Lijn.cs:17-83`).
  - A fill style has two colors and a type: none, solid, hatch, linear or radial. It also holds a hatch style and a gradient angle (`Vlak.cs:68-138`).
  - A format painter copies styles from one shape to another (`Lijn.cs:97`).
- **Stable ids in the file.** Points are written first, and shapes refer to them by id (`Tekening.cs:1149-1164`).

**What we don't repeat:**
- **Snapping is point-to-point only.** It uses a fixed ±3 px box (`Tekening.cs:1494-1501`). There are no midpoint, intersection or on-path snaps, and no constraints.
- **Undo is broken in two places.**
  - A merge cannot be undone.
  - The `Punt.X/Y` setters record their undo under the property name "Coordinaat" (`Punt.cs:56`).
- **Slow hit-testing.** It renders a full bitmap and reads back the pixel under the cursor (`Tekening.cs:1505-1509`).
- **A 2,098-line god-class control** (`Tekening.cs`).
- **An encrypted save format with a hard-coded key** (`Tekening.cs:845-851`).
- **An unfinished icon editor.** `IconEditor/Pictogram.Save` is empty (`Pictogram.cs:42`).

### 2.2 What Spark gives the app today

- **No generator for in-repo apps.** In-repo apps are copied by hand from `apps/MintPlayer`. The `create-spark-app` template from #690 (`libs/templates/MintPlayer.Spark.Templates/content/SparkApp/`) produces standalone apps.
  - The layout is `apps/Iconic/Iconic` (the host, `SpaRoot=ClientApp\`), `apps/Iconic/Iconic.Library` and `apps/Iconic/Iconic.Tests`.
  - Nx registration needs three things:
    - `project.json` files for the host and the ClientApp
    - the npm name `@spark-apps/iconic`
    - the ClientApp added to the root `package.json` `workspaces`
- **Ports.** https 5003–5015 are taken, and Bothmeet claims 5017/5018. **Iconic takes 5019 (https) and 5020 (http).**
- **Custom pages.** An app can register custom Angular routes before `sparkRoutes()` (`apps/MintPlayer/MintPlayer/ClientApp/src/app/app.routes.ts`). Those pages use `SparkService` for data. The editor is such a page; the project list can be a generic Spark query page.
- **Nested data.** `AsDetail` embeds objects and arrays inside the parent document. Spark has no attribute type for arbitrary JSON.
- **Binary data.** Spark has no attachment API. Apps call RavenDB attachments directly behind their own endpoints: `apps/CodeCoverage/CodeCoverage/CommitGraph/CommitGraphAvatars.cs` and `apps/SeoTester/SeoTester/Reports/ReportPdf.cs`.
- **Accounts.** `spark.AddAuthentication<SparkUser>` with `LocalCredentials.Full` and `AddExternalProviders`, as in `apps/MintPlayer/MintPlayer/Program.cs:36-54`.

### 2.3 File formats (research, 2026-11-20)

Facts marked (k) come from background knowledge and are not verified against a source. Spike S3 verifies them.

**ICO and CUR** ([Wikipedia: ICO](https://en.wikipedia.org/wiki/ICO_(file_format))):
- **Layout.** The file starts with a 6-byte `ICONDIR`: reserved, a type (1 for ICO, 2 for CUR) and a count. A 16-byte `ICONDIRENTRY` per image follows, then the image data. All values are little-endian.
- **Size 256.** A width or height of 256 is stored as 0.
- **Cursor hotspot.** In a CUR entry, the planes and bitCount fields hold the hotspot's x and y.
- **PNG entries** are complete PNG files.
- **BMP entries** are a 40-byte `BITMAPINFOHEADER` with no file header, followed by the pixel data:
  - The header states twice the image's height.
  - The data is a 32-bit BGRA bitmap stored bottom-up, followed by a 1-bit AND mask.
  - Every row is padded to 4 bytes.
- **Safe default (k).** Store the 256 entry as PNG and the 16–48 entries as BMP.
- **Browsers (k).** CSS `cursor: url(x.cur) x y` accepts sizes up to 128 px, but 32 px is the safe maximum.

**ANI** is RIFF `ACON`, which holds:
- an `anih` header: frames, steps, size and a rate in 1/60 s
- optional `rate` and `seq ` chunks
- a `LIST fram` with one complete `.cur` file per frame

It is about 100 lines on top of the CUR writer. Only Windows desktops use it; browsers ignore it.

**GIF:**
- **Encoder.** [gifenc](https://github.com/mattdesl/gifenc) is MIT-licensed, about 9 KB, and fast. It has a PnnQuant quantizer, runs in workers and supports a transparent palette index.
- **Rejected encoders.**
  - gif.js needs a separately served worker and is unmaintained.
  - modern-gif is slower.
  - omggif has no quantizer.
- **Transparency is 1-bit.** An anti-aliased edge either gets a matte color, or its alpha is thresholded with un-premultiplied RGB.
- **One global palette for all frames.** This avoids the flicker that per-frame palettes cause.

**APNG and animated WebP** carry full 8-bit alpha. APNG needs a small encoder (UPNG.js or our own). WebP can use `canvas.toBlob('image/webp')` plus a muxer.

## 3. Domain model

The vocabulary below is the owner's, with the hierarchy fixed.

```
Project                      (a stored document; one per user creation)
 ├─ kind: Icon | Cursor | Image | Animation
 ├─ canvas: { width, height }          the design space, in "units" (vector)
 ├─ points[]                           shared, first-class (DrawIt idea)
 ├─ constraints[]                      between points/shapes (solver, D4/D14)
 │                                     points[] also holds derived points (D15)
 ├─ layers[]                           ordered, bottom → top
 │   ├─ ShapeLayer { shapes[] }
 │   ├─ PaintLayer { per-size bitmaps } pixel art, one PNG per output size (D2)
 │   └─ GuideLayer { shapes[] }        never rendered; motion paths, construction lines
 ├─ outputs[]                          the images inside the icon/cursor
 │   └─ { size, format: Png|Bmp, bitDepth, hotspot?, overrides? }
 └─ timeline?                          { fps, duration, loop, tracks[] } (animation)
```

**Shapes** refer to point ids. They don't hold coordinates.

- **Line shapes** have a line style:
  - straight line (two points)
  - polyline
  - cubic Bézier, using control points that are ordinary points
  - circular arc (center, start and end; or three points)
  - elliptical arc
- **Plane shapes** have a line style **and** a fill style:
  - circle (center and radius point, or three points)
  - ellipse
  - rectangle, which is a polygon with constraints
  - polygon
  - closed curve (closed Bézier path)
  - regular polygon or star, generated from a center, a radius point and a count
- **Text** (D23) stores a string, font, size, spacing and alignment. It has a fill style and a line style, and is rendered from glyph outlines.
- **Line style:** color with alpha, width, dash pattern, dash cap, line join and line cap.
- **Fill style:**
  - Types: none, solid, linear gradient, radial gradient, or pattern (hatch).
  - Fields: color1, color2 and further gradient stops, angle, hatch kind.
- **Images:**
  - **One drawing, many sizes (D1).** The vector layers render at every output size.
  - **Per-size overrides.** An output size can override per layer: hide a layer at that size, or add a paint layer that exists at that size only. This is how a designer hand-tunes 16×16.
  - **"Snap to pixel grid" per output size.** Before rasterizing, odd-width strokes are offset by 0.5 px and vertices are rounded to whole pixels.

## 4. Decisions

| # | Decision | Source |
|---|---|---|
| D1 | **One drawing, many sizes.** The vector source renders at every output size. Each size can carry overrides (hidden layers, its own paint layer). | Owner, grill 2026-11-20 |
| D2 | **A paint layer is a pixel bitmap per output size.** Tools: pencil, eraser, fill bucket, color picker. It is stored as PNG. A paint layer is never scaled; a size without a bitmap shows nothing for that layer. | Owner, grill 2026-11-20 |
| D3 | **Public app.** Anyone signs up. Projects are private by default, and a public read-only share link is optional. Anonymous users can draw and export but not save. Their work stays in `localStorage` and is offered for import after sign-in. | Owner, grill 2026-11-20 |
| D4 | **Shared points and a full geometric constraint solver.** Shapes refer to shared points (DrawIt). Constraints include coincident, horizontal, vertical, parallel, perpendicular, equal length, fixed length or angle, tangent, point-on-curve, midpoint and symmetric. They are solved live while dragging. **We write the solver ourselves** (D14). If S1 misses its bar, v1 ships with snapping and derived points only (D15); the model keeps `constraints[]`, so a solver can be added later without migrating projects. | Owner, grill 2026-11-20 (rounds 2 and 3, Q1 and Q5) |
| D5 | **Animation: keyframes and motion paths.** A timeline has fps, duration and loop. A keyframe track targets a **shape** (position, rotation, scale, opacity, stroke color, fill color) or a **point** (position). Easing is linear or cubic-bezier. A shape can follow a path on a guide layer, optionally oriented along its tangent. Export samples `scene(t)` per frame. Point keyframes drive the geometry, and constraints (D4) and derived points (D15) are re-evaluated on every frame, so connected shapes follow and mechanisms work (D19). | Owner, grill 2026-11-20 (Q10: B) |
| D6 | **Own accounts plus external logins.** `LocalCredentials.Full` with Google, Microsoft and GitHub, as `apps/MintPlayer` does. No SparkId. | Owner, grill 2026-11-20 |
| D7 | **Exports in v1:** ICO, CUR, ANI, GIF, APNG, animated WebP, single-size PNG and SVG. **Import in v1:** `.ico`, `.cur` and `.png`, which become paint layers. | Owner, grill 2026-11-20 |
| D8 | **Name: Iconic.** The app is `apps/Iconic`, the RavenDB database is `Iconic`, the host is `iconic.mintplayer.com`, and the ports are https 5019 and http 5020. | Owner (name), §2.2 (ports) |
| D9 | **All rendering and encoding happen client-side.** The server stores projects and never rasterizes. One TypeScript scene model drives two renderers: SVG DOM for editing (hit-testing, handles, crisp zoom) and Canvas2D/OffscreenCanvas for raster output, which is deterministic and happens at the output size. Each size is drawn natively, never downscaled from 256. | Proposed; the research recommends it (§2.3) |
| D10 | **Storage.** The scene graph (points, constraints, layers, shapes, timeline) is one JSON string attribute on the `Project` document. Spark has no JSON attribute type (§2.2), and the editor is a custom page, so Spark never edits it field by field. Paint-layer bitmaps and the project thumbnail are RavenDB attachments behind an app endpoint, following the pattern of `CommitGraphAvatars.cs`. Project metadata (name, kind, sizes, updated, thumbnail) stays as normal Spark attributes so the generic project query works. | Proposed; spike S5 confirms the size limits |
| D11 | **The editor core is a framework-free TypeScript library** (`libs/iconic-core`, or inside the ClientApp at first). It holds the model, commands and undo, the solver adapter, the renderers, the animation sampler and the encoders. It is unit-tested with vitest. The Angular code is only the shell. This avoids DrawIt's god-class. | Proposed |
| D12 | **Undo is command-based over the model**, with an inverse for every command. A point merge and a solver result are single undoable steps; DrawIt got both wrong. | Proposed |
| D13 | **Zoneless Angular**, ng-bootstrap components and `<spark-icon>`, as in every app in this repo. | Repo rules (memory) |
| D14 | **We write the constraint solver ourselves, as the published package `@mintplayer/constraint-solver`** (`libs/node_packages/constraint-solver`). It is framework-free TypeScript with no WASM and no dependencies.<br>**How it works:** each constraint is a residual with an analytic derivative. The solver is damped Newton / Levenberg–Marquardt starting from the current positions, so it picks the nearest solution and shapes don't flip to their mirror image. A drag is a soft, low-weight constraint. Independent groups (connected components) are solved separately.<br>**Where it should beat planegcs:** rank analysis of the Jacobian names the exact constraints that conflict; each point shows how many degrees of freedom it has left; the same input always gives the same output.<br>**Releasing:** it has no Angular dependency, so it has its own semver from 1.0.0 (CLAUDE.md, "versioning"). It copies the `@mintplayer/webhooks-receiver` setup: a `tsc` build, a `Push` step in `dotnet-build-master.yml`, and membership of "Angular suites" in `test.yml`.<br>**Fallback:** only permissively licensed solvers (MIT, BSD, Apache). **Planegcs (LGPL) is out.** | Owner, grill Q5 and Q6 |
| D15 | **Derived points.** Snapping to a midpoint, an intersection or a spot on a path creates a point that stores *how* it is computed (`midpoint(p1,p2)`, `intersect(a,b)`, `onPath(shape,t)`). Derived points are recomputed in dependency order, in one pass with no solver.<br>**Split:** one click detaches any snapped or derived point into a free point. It is available from the context menu, with a shortcut, and on the snap glyph.<br>**Rules:** a snap that would create a cycle is refused, with an explanation. An intersection that no longer exists keeps its last position and is marked broken; it never throws.<br>**With or without the solver:** derived points work either way. If S1 passes, the solver runs after the derived-point pass. | Owner, grill Q2 (B + "easy way to split") |
| D16 | **Units are design pixels.** A project has a design size: 256×256 by default for icons and cursors, any W×H for an image. Coordinates are floats in those pixels. Each output size scales from it, so the pixel grid for a 48 px output is 5.33 units; the snap engine handles grids that aren't whole numbers. **All exports are anti-aliased.** Canvas2D cannot turn anti-aliasing off for paths, so a hard-edged pixel look comes only from paint layers (D2). | Owner, grill Q3 |
| D17 | **Stroke behaviour per output size.** Each size has a mode: *scale*, *scale with a minimum* (the user sets the minimum in px) or *fixed pixels*. The default is scale with a 1 px minimum. It sits beside that size's pixel-grid snapping. | Owner, grill Q4 |
| D18 | **Full mobile editing, phones included.**<br>Pointer Events everywhere, so mouse, pen and touch use one code path. Pinch-zoom, two-finger pan and long-press for the context menu.<br>On phones, panels become bottom sheets and the toolbar collapses.<br>Touch has no hover, so it gets an **offset loupe** showing the snap target.<br>Snap tolerance is in screen pixels and depends on the input type.<br>The E2E tests include a phone viewport. | Owner, grill Q7 (C) |
| D19 | **Frames are deterministic.** Frame *t* is solved from the **rest pose**, never warm-started from frame *t−1*. Scrubbing, playback and every export therefore produce the same pixels. | Proposed, a consequence of D5 |
| D20 | **Sharing.** `isPublic` (it has a share link) and `isListed` (it appears in the gallery) are separate fields from day one.<br>v1 has unlisted links only: an unguessable URL with `noindex`, plus view, export and **Remix** (copy into my projects).<br>Shared pages carry a "report this" link that emails the owner, and the owner can take a share down.<br>The public gallery is a later stage, built on `libs/moderation`. | Owner, grill Q8 (C) |
| D21 | **Quotas, enforced on the server:**<ul><li>200 projects per user</li><li>**5 MB of scene JSON** per project, which is RavenDB's huge-document threshold, so the server never warns</li><li>10 MB of attachments per project</li><li>50 saves a minute</li></ul>The editor shows usage live. A save over a limit fails with a clear message, and the work stays in the editor and in `localStorage`. Autosave sends **patches**, not the whole scene (S5). | Owner, grill Q9 (A, scene limit 5 MB) |
| D22 | **History is named snapshots.** "Save version" stores an explicit snapshot, at most 20 per project, counted in the attachment quota, with restore. Autosave overwrites without creating a revision. Restore first snapshots the current state, so restoring never loses work. Undo during a session is the in-memory command stack (D12). | Owner, grill Q11 (B) |
| D23 | **Text is live and rendered as outlines.** A text shape stores its string, font, size, spacing and alignment, and stays editable. **Every renderer draws it from glyph outlines produced by [opentype.js](https://github.com/opentypejs/opentype.js) (MIT)**: the on-screen editor, the raster exports and SVG. That means no `fillText`, no text-rendering differences between browsers, and no font embedding in SVG. "Convert to shapes" turns the text into ordinary plane shapes with shared points. Fonts come from a curated set (OFL or Apache) or a user upload (.ttf/.otf, counted in the attachment quota; the uploader is responsible for its licence). | Owner, grill Q12 (C), with outline rendering as my addition, so that C's editing gets B's identical output everywhere |
| D24 | **One PR (#699)** with every stage as a milestone. Each milestone ends type-checked and demoable, and the tests run once, at the end (global rule 6). | Owner, grill Q13 (A) |
| D25 | **The editor talks to its own minimal API (`/api/iconic`), not to Spark's generic persistent-object endpoints.** Scene patches, attachments with magic-byte checks, snapshots, quotas and share tokens don't fit the generic CRUD. Spark still provides accounts, antiforgery, the shell, the `Project` query page and the owner-only row filter (`ProjectActions`). Besides the planned calls, there are `GET /projects` (the home page's list) and `DELETE /projects/{key}` (the project and its scene, owner only). The routes are generator endpoint classes (`[MemberOf<…Group>]`, typed bodies, `[RouteParam]`), as `EndpointConventionsTests` R3 requires of every app. The one untyped body is the attachment upload: its raw bytes are read by `ProjectService` under a cap, after the name check. | Mine, implementation; `Endpoints/IconicEndpoints.cs`, `Iconic.Tests/RouteSnapshots/Iconic.txt` |
| D26 | **Export runs on the main thread, yielding between frames, not in a worker** (departs from S6's worker plan). The text renderer (opentype.js) and the paint bitmaps are already loaded in the page, and a worker would need both shipped again. 64×64 × 60 frames is short enough that the yielding busy state is acceptable. Revisit if users export large animations. | Mine, S6 |
| D27 | **Animation tracks are point tracks and layer opacity tracks** (departs from D5's per-shape rotation, scale and colour tracks). A shape moves, turns and scales by keying its points, which the solver already connects (D19). Layer opacity is keyed as a factor of the layer's own opacity. Easing is a preset (linear, ease in, ease out, ease in-out, step), not a cubic-bezier editor. Shape colour tracks are later. | Mine, Stage 3 |
| D28 | **No orient-to-tangent on motion paths** (departs from D5). Points have no orientation, so a point that follows a path just moves along it. A shape that should turn with the path gets two points on the path, a small offset apart. | Mine, Stage 3 |
| D29 | **The autosaver writes nothing until a drawing is opened.** It has an `active` flag that `startOwned`/`startDraft` set and `stop()` clears. Without it, the editor's initial empty scene could overwrite an anonymous draft in `localStorage`, and a drawing being handed off to an account could be written back on destroy. | Mine, found in review of Stage 4 |

## 5. Spikes

Each spike ends in a short note in this PRD (§5.x result) with evidence (numbers, file:line, or a demo), and a go/no-go.

| # | Spike | Question | Done when |
|---|---|---|---|
| **S1** | **Our own constraint solver** (D14) | Can a hand-written damped Newton / Levenberg–Marquardt solver in TypeScript meet the bar? Scope: a residual for every constraint type in D4, solving connected components separately, conflict detection by Jacobian rank, and degrees of freedom per point. | Three conditions:<ul><li>200 points with 150 mixed constraints solve while dragging at ≥ 60 fps on a mid-range laptop **and on a mid-range phone** (D18)</li><li>over-constrained input names the conflicting constraints and never hangs</li><li>a corpus of degenerate cases passes: coinciding parallel lines, zero-length segments, tangent at radius 0, and a near-singular Jacobian</li></ul>**No-go** means v1 ships with D15 only (Q1). |
| S2 | **Snapping and constraint UX, desktop and phone** | How does a user add constraints without a CAD learning curve? Proposed: snapping *creates* constraints or derived points (drop on a point gives coincident; on a midpoint gives a derived midpoint; near horizontal gives horizontal). They show as small glyphs that can be deleted, and a **Split** action is on the glyph (D15). A toolbar offers explicit constraints. Hold Alt to place a point without snapping. On touch, the offset loupe (D18) does the job of hover previews. | A clickable prototype at both desktop and phone widths; the owner decides. |
| S3 | **ICO/CUR/ANI writer and reader** | A byte-exact writer for PNG and 32-bit BMP entries with an AND mask, the CUR hotspot, ANI, and a reader for import. | Files open in Windows Explorer, in a Windows cursor applied through Settings, and as a CSS `cursor:` in Chrome, Edge and Firefox. Round trip: write, read, compare pixels. |
| S4 | **Pixel-exact small-size rendering** | Does Canvas2D at 16×16 with pixel-grid snapping give acceptable icons? How do the browsers differ in anti-aliasing? | The same scene rendered in Chrome and Firefox. The pixel differences are measured and recorded, and decide whether golden-image tests need a tolerance. |
| S5 | **Storage shape** | Scene JSON as a string attribute versus `AsDetail` nesting. The size of a realistic project (500 shapes, 30 keyframe tracks). **Autosave as patches** (D21): JSON Patch applied server-side through a parameterised patch script, never by splicing strings into RQL (CLAUDE.md). Attachment endpoints and authorization for paint layers, snapshots (D22) and fonts (D23). | A measured document size, the save/load round trip, and patch payload sizes for typical edits. |
| S6 | **Animation export** | gifenc with a global palette and transparency (matte versus threshold). APNG encoder choice. WebP muxing. Encoding time for 64×64 × 60 frames in a worker. | Three files from one scene, viewed in browsers. Timings are recorded. |
| S7 | **Constraints × animation** (D5, D19) | Point keyframes drive the geometry, and the solver plus derived points run on every frame, starting from the rest pose. Is that fast enough for live preview, and is it deterministic? | Three conditions:<ul><li>a swinging-arm mechanism plays at 30 fps in the preview</li><li>scrubbing to frame *t* and playing to frame *t* produce identical pixels</li><li>the GIF export equals the preview</li></ul> |

### 5.1 Spike results (2026-11-20)

| # | Result | Evidence | Verdict |
|---|---|---|---|
| S1 | Damped Gauss-Newton projection per connected group, with analytic gradients checked against central differences. Conflicts and redundancy come from Gram-Schmidt over the Jacobian rows, plus degrees of freedom per point. A drag frame of the worst case, **200 points in one group with 199 mixed constraints**, takes a median of **7.2 ms**, p95 10.9 ms and max 45 ms. A typical icon, **40 rectangles (160 points, 160 constraints)**, takes a median of **0.5 ms** and p95 1.3 ms. Both are within the 16.7 ms frame of 60 fps. | `npm run bench` in `libs/node_packages/constraint-solver` (`bench/drag.bench.ts`), 200 drag frames, Node 24.15 on the development machine, 2026-11-20; the degenerate-case corpus is in the package's vitest suite | **Go.** Not measured on a phone. A phone is plausibly 3–4× slower, which keeps the typical icon far inside budget but puts the worst-case single group near it. Check on a mid-range phone during the owner's review. |
| S2 | Built rather than prototyped. A drop on a point **merges** it (a shared point, D4). A snap to a midpoint or a spot on an outline creates a **derived point** (D15). The grid comes last. Hold **Alt** to place freely. On touch, an offset loupe replaces hover. Explicit constraints come from the Constraints tab, which offers only those that apply to the selection (`editor/selection.ts`). **Split**, **Detach** and **Fix** are in the Style tab. Snapping to an *intersection* is not offered yet; the model has the `intersect` derivation (`core/model.ts:43`). | `core/snap.ts` (point, then midpoint, then outline, then grid); `core.spec.ts` "snaps to a point first…", "merging makes two shapes share a point; Split gives one back its own" | Go. The owner reviews the UX on the PR. |
| S3 | ICO and CUR writer and reader for PNG and 32-bit BMP entries (AND mask, bottom-up BGRA, doubled height; 256 px written as 0). The CUR hotspot goes in the planes and bit-count fields. ANI is RIFF `ACON` with `anih` and a `fram` list of `.cur` frames. | `core/formats/formats.spec.ts`: directory layout, hotspot, DIB layout, write→read round trip for PNG and BMP entries, ANI chunks | Go for the format. **Not yet done:** the manual checks in Explorer, as a Windows cursor and as a CSS `cursor:`. They are listed for the owner in the PR. |
| S4 | The same Canvas2D drawing (a filled square, a 16-unit circle stroke, a diagonal and a Bézier, scaled from 256) differs between browsers. Chromium (Playwright) and Firefox (Playwright) on this machine: at **16 px, 148 of 256 pixels differ** (max channel 122, mean 16.4). At **32 px, 229 of 1024** differ (max 80, mean 2.1). | Measured 2026-11-20 with `getImageData` in both browsers, same script | Go, with a consequence: an export depends on the browser that made it. **Golden-image tests run in one browser (Chromium) only**, and never compare across browsers. Per-size paint layers (D2) are the answer for a user who needs exact pixels at 16 px. |
| S5 | The scene is one JSON document (`ProjectScene`), not `AsDetail` nesting. 500 shapes plus 30 point tracks of 10 keys stay **below 1 MB**, against the 5 MB quota. Moving one point gives a **patch under 200 bytes**. The patch is applied by one fixed RavenDB patch script, with the ops as `args`, and a version check (409). | `core.spec.ts` "keeps a realistic project far below the 5 MB scene quota…"; `Iconic.Tests/ProjectServiceTests.cs` (patch, version conflict, quota 413, attachments) | Go |
| S6 | GIF with gifenc, one global palette over all frames, a transparent index, and a matte colour for semi-transparent edges. APNG is our own writer (`acTL`, `fcTL`, `fdAT`). Animated WebP is a mux of the browser's own still WebP frames into `ANMF` chunks under `VP8X`. | `formats.spec.ts` (GIF89a loop and transparency, APNG chunks, WebP mux) | Go. **Departure:** encoding runs on the main thread, yielding between frames, not in a worker (D26). |
| S7 | Every frame is evaluated from the rest pose (D19), so evaluation order does not matter, and the preview, scrubbing and the export share one function (`core/evaluate.ts`). | `core.spec.ts` "evaluates every frame from the rest pose, so frame order does not matter (S7)" | Go. The 30 fps preview of a mechanism is for the owner to check on the PR. |

## 6. Scope by stage

The stages are milestones inside **one PR** (owner rule: one PR per task; splitting needs the owner's word).

- **Stage 0, solver and skeleton.**
  - Spike S1 and the `@mintplayer/constraint-solver` package (D14).
  - Create `apps/Iconic` (host, Library, Tests, ClientApp) and register it in Nx.
  - Accounts (D6).
  - A `Project` entity with the generic query page, and quotas (D21).
  - An empty editor route.
- **Stage 1, static vector.**
  - Spikes S2–S5.
  - Points, derived points with Split (D15), constraints and snapping.
  - All line and plane shapes, with line and fill styles.
  - Text (D23).
  - Layers: shape and guide.
  - Undo.
  - Save and load as patches.
  - Snapshots (D22).
  - PNG and SVG export.
  - A responsive shell for desktop and phone (D18).
- **Stage 2, icons and cursors.**
  - Output sizes with per-size overrides and stroke modes (D17).
  - Paint layers.
  - ICO and CUR export, with a hotspot editor.
  - Import of `.ico`, `.cur` and `.png`.
- **Stage 3, animation.**
  - Spikes S6 and S7.
  - The timeline UI, with shape and point keyframes and motion paths.
  - GIF, APNG, WebP and ANI export.
- **Stage 4, production.**
  - Share links, Remix, and report/takedown (D20).
  - The thumbnail.
  - Hand off `localStorage` drafts at sign-in.
  - Deployment to the VPS beside the other apps.
  - A `CLAUDE.md` entry marking Iconic as production.
- **Later, not in v1:**
  - the public gallery with moderation (D20)
  - a solver if S1 failed (D4)

**Out of scope:**
- real-time collaboration
- boolean path operations (union and subtract)
- SVG import
- Lottie or SMIL export

## 7. Risks

- **The constraint solver is the largest unknown (S1).** A hand-written solver risks convergence bugs in degenerate cases. Mitigation: S1 comes first and has a degenerate-case corpus. Its fallback is D15 (snapping and derived points), and animation still works without the solver (D5).
- **Phone editing (D18)** widens S1's performance bar and S2's UX work. The solver has to hold 60 fps on a mid-range phone.
- **A long-lived single PR (D24).** Integration problems surface late, because the tests run once. Mitigation: every milestone ends type-checked and demoable.
- **Browser differences in anti-aliasing (S4).** These make golden-image tests brittle.
- **GIF's 1-bit alpha** gives ugly fringes on cursors and icons. Mitigation: a matte-color option and APNG/WebP as alternatives.
- **Document size** for large animated projects (S5).

## 8. Grill decisions (owner, 2026-11-20)

| Question | Answer |
|---|---|
| How do vector shapes relate to the pixel sizes inside an icon? | One drawing, many sizes, with per-size overrides (D1) |
| What is a paint layer? | A pixel bitmap per output size (D2) |
| Audience and sharing | Public; anyone signs up; anonymous users draw and export but cannot save (D3) |
| How far does animation go in v1? | Keyframes and motion paths (D5) |
| Points and snapping | **Full constraint solver** (D4) |
| Sign-in | Own accounts plus external logins (D6) |
| Exports beyond ico, cur and gif | PNG/SVG, ANI, APNG/WebP, and import of ico/png: all four (D7) |
| Name | Iconic (D8) |

### Grill round 3 (owner, 2026-11-20)

The owner said the answers are guidance, and the final call is mine where something needs to differ. Where I departed from or extended an answer, the decision row says so.

| # | Question | Answer | Decision |
|---|---|---|---|
| Q1 | What happens if no solver meets the bar? | Try the solver first; fall back to snapping | D4 |
| Q2 | What does a snap leave behind? | Derived points, with an easy way to split them | D15 |
| Q3 | Units | Design pixels; the output is anti-aliased | D16 |
| Q4 | Strokes at small sizes | The user chooses per output size | D17 |
| Q5 | Solver licence | Write our own; otherwise permissive licences only | D14 |
| Q6 | Where does the solver live? | Its own package, published | D14 |
| Q7 | Touch and mobile | Full mobile editing, phones included | D18 |
| Q8 | Gallery | Unlisted links now; fields ready for a gallery later | D20 |
| Q9 | Quotas | Fixed limits; scene JSON limit 5 MB | D21 |
| Q10 | Animation × constraints | Point keyframes drive the solver | D5, D19 |
| Q11 | History | Named snapshots | D22 |
| Q12 | Text | Live, editable text | D23 (rendered as outlines) |
| Q13 | One PR? | One PR | D24 |

## 9. Open questions for the owner

None open. The first-draft questions were answered in round 3: quotas (Q9), touch (Q7), gallery (Q8), the solver fallback (Q1), the licence (Q5) and units (Q3).
