# Iconic: plan

The PRD is [iconic_PRD.md](iconic_PRD.md). Everything happens in one PR, #699, on the branch `feat/iconic` (D24). Each milestone ends type-checked and demoable. Tests run once, at the end.

### Log

- 2026-11-20:
  - Investigation of DrawIt, how a Spark app fits, and the file formats (PRD §2).
  - Two grill rounds, PRD and plan drafted, draft PR #699.
- 2026-11-20, grill round 3 (Q1–Q13, PRD §8), which led to D14–D24:
  - our own solver package
  - derived points with Split
  - design-pixel units
  - stroke mode per output size
  - full phone editing
  - unlisted sharing
  - 5 MB scene quota with patch saves
  - point keyframes driving the solver
  - snapshots
  - live text rendered as outlines
  - one PR
- 2026-11-20, implementation (owner: "Run all spikes, update the PRD, implement all milestones, create the pull-request"):
  - S1: `@mintplayer/constraint-solver`, published like `@mintplayer/webhooks-receiver` (commit e1888fce1).
  - Stage 0: the server, `/api/iconic` (commit f099325fb).
  - Stages 1–4: the client (core, formats, editor, pages), and the list and delete endpoints.
  - Spike results are in PRD §5.1. Departures from the plan are D25–D29: the own API, export on the main thread,
    point and opacity tracks only, no orient-to-tangent, and the saver's `active` guard.
  - **NEXT:** the owner reviews the UX and runs the manual checks below. Then deploy.

## Stage 0: solver and skeleton

- [x] **S1** `libs/node_packages/constraint-solver` (`@mintplayer/constraint-solver`, D14). Result in PRD §5.1.
- [x] Publishing the solver: a `tsc` build, a `Push` step in `dotnet-build-master.yml`, and membership of "Angular suites" in `test.yml`.
- [x] `apps/Iconic/{Iconic,Iconic.Library,Iconic.Tests}`. Ports https 5019 and http 5020. Database `Iconic`.
- [x] Nx: the `project.json` files, `@spark-apps/iconic`, and the root `package.json` `workspaces`.
- [x] Accounts: `LocalCredentials.Full`, passkeys, plus the external providers from configuration.
- [x] `Project` and `ProjectScene` entities, owner-only row security, the D21 quotas, model and security synchronized.
- [x] Routes: `/home` (new drawing and the list), `/draft`, `/p/:key`, `/s/:token` and `/admin/takedown`, before `sparkRoutes()`. There is no `/new`: the home page creates a drawing.

## Stage 1: static vector

- [x] **S2** snapping and constraint UX, built into the editor. **Owner review pending** (PRD §5.1).
- [x] **S3** ICO/CUR/ANI writer and reader, with round-trip tests. **Manual checks pending:** Explorer, a Windows cursor, a CSS cursor.
- [x] **S4** Chromium versus Firefox at 16 and 32 px, measured (PRD §5.1).
- [x] **S5** document size, autosave as patches (fixed patch script, ops as `args`), and the attachment endpoints.
- [x] Core library: the model, a command and undo stack, derived points with Split (D15), and the solver adapter.
- [x] Edit renderer: the selection, handles, point glyphs (degrees of freedom, fixed, derived, conflict), and the touch loupe. It is a canvas, not SVG.
- [x] Tools: point, line, polyline, Bézier, arc, circle, ellipse, rectangle, polygon, star, closed curve, and text (opentype.js outlines, D23).
- [x] Line style and fill style panels, and a format painter (copy and paste style).
- [x] Layers panel: reorder, hide, lock, opacity. Guides are a per-shape flag, not a layer kind.
- [x] A responsive shell: a bottom offcanvas on phones, Pointer Events, pinch-zoom and pan (D18).
- [x] Save and load as patches, a quota meter, the `localStorage` draft for anonymous users, and snapshots (D22).
- [x] PNG and SVG export.

## Stage 2: icons and cursors

- [x] An outputs panel: the sizes, the entry encoding (PNG or BMP), the hotspot for cursors, and the stroke mode per size (D17).
- [x] Paint layers (pencil, eraser, bucket, picker), one bitmap per output width, stored as attachments (D2). Paint bitmaps are the per-size override; vector overrides per size are not built.
- [x] ICO and CUR export, and import of `.ico`, `.cur` and `.png`.

## Stage 3: animation

- [x] **S6** GIF (gifenc, a global palette, a matte option), APNG and WebP. On the main thread (D26).
- [x] **S7** each frame evaluated from the rest pose. **Owner check pending:** a mechanism at 30 fps in the preview.
- [x] A timeline with point keyframe tracks, layer opacity keys and easing presets (D27).
- [x] Motion paths bound to shapes (D28: no orient-to-tangent).
- [x] GIF, APNG, WebP and ANI export.

## Stage 4: production

- [x] Share links (`noindex`), Remix, report-by-email and takedown (D20).
- [x] The thumbnail, and the hand-off of a draft to the account ("Save to my account" on the home page and in the editor).
- [x] A `CLAUDE.md` entry marking Iconic as production.
- [ ] Deploy to the VPS: DNS, the reverse proxy and the secrets need the owner.
- [ ] A phone viewport in E2E. Iconic has no E2E host yet; the unit and service tests cover the core and the API.
- [x] Final test run (Iconic.Tests, the client core and formats, the solver) and a coverage check on the PR.
