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
 ├─ constraints[]                      between points/shapes (solver, D4)
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
| D4 | **Shared points and a full geometric constraint solver.** Shapes refer to shared points (DrawIt). Constraints include coincident, horizontal, vertical, parallel, perpendicular, equal length, fixed length or angle, tangent, point-on-curve, midpoint and symmetric. They are solved live while dragging. The solver choice is spike **S1**. | Owner, grill 2026-11-20 (chose the solver over "shared points + smart snaps") |
| D5 | **Animation: keyframes and motion paths.** A timeline has fps, duration and loop. Keyframe tracks per shape cover position, rotation, scale, opacity, stroke color and fill color, with linear or cubic-bezier easing. A shape can follow a path on a guide layer, optionally oriented along its tangent. Export samples `scene(t)` per frame. | Owner, grill 2026-11-20 |
| D6 | **Own accounts plus external logins.** `LocalCredentials.Full` with Google, Microsoft and GitHub, as `apps/MintPlayer` does. No SparkId. | Owner, grill 2026-11-20 |
| D7 | **Exports in v1:** ICO, CUR, ANI, GIF, APNG, animated WebP, single-size PNG and SVG. **Import in v1:** `.ico`, `.cur` and `.png`, which become paint layers. | Owner, grill 2026-11-20 |
| D8 | **Name: Iconic.** The app is `apps/Iconic`, the RavenDB database is `Iconic`, the host is `iconic.mintplayer.com`, and the ports are https 5019 and http 5020. | Owner (name), §2.2 (ports) |
| D9 | **All rendering and encoding happen client-side.** The server stores projects and never rasterizes. One TypeScript scene model drives two renderers: SVG DOM for editing (hit-testing, handles, crisp zoom) and Canvas2D/OffscreenCanvas for raster output, which is deterministic and happens at the output size. Each size is drawn natively, never downscaled from 256. | Proposed; the research recommends it (§2.3) |
| D10 | **Storage.** The scene graph (points, constraints, layers, shapes, timeline) is one JSON string attribute on the `Project` document. Spark has no JSON attribute type (§2.2), and the editor is a custom page, so Spark never edits it field by field. Paint-layer bitmaps and the project thumbnail are RavenDB attachments behind an app endpoint, following the pattern of `CommitGraphAvatars.cs`. Project metadata (name, kind, sizes, updated, thumbnail) stays as normal Spark attributes so the generic project query works. | Proposed; spike S5 confirms the size limits |
| D11 | **The editor core is a framework-free TypeScript library** (`libs/iconic-core`, or inside the ClientApp at first). It holds the model, commands and undo, the solver adapter, the renderers, the animation sampler and the encoders. It is unit-tested with vitest. The Angular code is only the shell. This avoids DrawIt's god-class. | Proposed |
| D12 | **Undo is command-based over the model**, with an inverse for every command. A point merge and a solver result are single undoable steps; DrawIt got both wrong. | Proposed |
| D13 | **Zoneless Angular**, ng-bootstrap components and `<spark-icon>`, as in every app in this repo. | Repo rules (memory) |

## 5. Spikes

Each spike ends in a short note in this PRD (§5.x result) with evidence (numbers, file:line, or a demo), and a go/no-go.

| # | Spike | Question | Done when |
|---|---|---|---|
| **S1** | **Constraint solver** | Do we adopt an existing solver or build one? Candidates: **planegcs** (FreeCAD's GCS compiled to WASM, `@salusoft89/planegcs`, LGPL-2.1: check the licence for a client bundle), a hand-written Newton–Raphson / Levenberg–Marquardt solver over point coordinates, or `cassowary`/kiwi.js (linear only, so no tangents or circles). | Ten points with eight mixed constraints solve while dragging at ≥ 60 fps on a mid-range laptop. Over-constrained input is reported, never hung. Bundle size and licence are recorded. |
| S2 | **Snapping and constraint UX** | How does a user add constraints without a CAD learning curve? Proposed: snapping *creates* constraints (drop on a point gives coincident; on a midpoint gives a midpoint constraint; near horizontal gives horizontal). They show as small glyphs that can be deleted. A toolbar offers explicit constraints. Hold Alt to place a point without snapping. | A clickable prototype; the owner decides. |
| S3 | **ICO/CUR/ANI writer and reader** | A byte-exact writer for PNG and 32-bit BMP entries with an AND mask, the CUR hotspot, ANI, and a reader for import. | Files open in Windows Explorer, in a Windows cursor applied through Settings, and as a CSS `cursor:` in Chrome, Edge and Firefox. Round trip: write, read, compare pixels. |
| S4 | **Pixel-exact small-size rendering** | Does Canvas2D at 16×16 with pixel-grid snapping give acceptable icons? How do the browsers differ in anti-aliasing? | The same scene rendered in Chrome and Firefox. The pixel differences are measured and recorded, and decide whether golden-image tests need a tolerance. |
| S5 | **Storage shape** | Scene JSON as a string attribute versus `AsDetail` nesting. The size of a realistic project (500 shapes, 30 keyframe tracks). Attachment endpoints and authorization for paint layers. | A measured document size, plus the save/load round trip through a Spark custom endpoint or action. |
| S6 | **Animation export** | gifenc with a global palette and transparency (matte versus threshold). APNG encoder choice. WebP muxing. Encoding time for 64×64 × 60 frames in a worker. | Three files from one scene, viewed in browsers. Timings are recorded. |
| S7 | **Constraints × animation** | When a keyframe moves a constrained point, is the solver run per frame (keyframes are drivers), or are constraints frozen during playback? | A decision with a demo. Proposed: keyframes animate *driver* values, the solver runs per sampled frame, and the solve is deterministic. |

## 6. Scope by stage

The stages are milestones inside **one PR** (owner rule: one PR per task; splitting needs the owner's word).

- **Stage 0, skeleton.** Create `apps/Iconic` (host, Library, Tests, ClientApp) and register it in Nx. Accounts (D6). A `Project` entity with the generic query page. An empty editor route.
- **Stage 1, static vector.** Spikes S1–S5. Points, constraints and snapping. All line and plane shapes with line and fill styles. Layers (shape and guide). Undo. Save and load. PNG and SVG export.
- **Stage 2, icons and cursors.** Output sizes with per-size overrides. Paint layers. ICO and CUR export with a hotspot editor. Import of `.ico`, `.cur` and `.png`.
- **Stage 3, animation.** Spikes S6 and S7. The timeline UI, keyframes and motion paths. GIF, APNG, WebP and ANI export.
- **Stage 4, production.** Share links (D3). The thumbnail. Hand off localStorage drafts at sign-in. Deployment to the VPS beside the other apps. A `CLAUDE.md` entry marking Iconic as production.

**Out of scope:** real-time collaboration, text shapes (they need a font-embedding decision), boolean path operations (union and subtract), SVG import, and Lottie or SMIL export.

## 7. Risks

- **The constraint solver is the largest unknown (S1).** A WASM solver adds bundle size and possibly an LGPL obligation. A hand-written solver risks convergence bugs. Mitigation: S1 comes first, and its fallback is D4-lite (shared points plus snapping, no solver). Falling back needs the owner's word.
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

## 9. Open questions for the owner

1. **Quotas.** Is there a cap on projects per user, document size or attachment size? Proposed: 200 projects and 5 MB per project, adjustable later.
2. **Touch and mobile.** Should the editor work on tablets, with pointer events and pinch-zoom, or only on desktop?
3. **Gallery.** Should public share links feed a browsable public gallery, or stay unlisted?
4. **Solver fallback.** If S1 finds no solver that meets the bar, do you accept D4-lite (shared points plus snapping) for v1?
5. **Licence.** Is an LGPL WASM dependency (planegcs) acceptable in the client bundle?
6. **Units.** Should the canvas be in pixels of the largest output size, or in abstract units (for example 0–1000)? Proposed: abstract units, with the canvas aspect fixed per project.
