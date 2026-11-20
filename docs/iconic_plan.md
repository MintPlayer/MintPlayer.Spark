# Iconic: plan

The PRD is [iconic_PRD.md](iconic_PRD.md). All stages are milestones in one PR, on the branch `feat/iconic`. Tests run once, at the end. Between milestones, check the work by type-checking and reading the code.

### Log

- 2026-11-20:
  - Investigation done: DrawIt, how a Spark app fits, and file formats (PRD §2).
  - Two grill rounds (PRD §8).
  - PRD and plan drafted.
  - NEXT: the owner answers PRD §9, then S1.

## Spikes (first, in this order)

- [ ] **S1** constraint solver: planegcs (WASM) versus a hand-written Newton/LM solver. Measure fps, bundle size and licence. Give a go/no-go.
- [ ] **S2** snapping-creates-constraints prototype. The owner reviews it.
- [ ] **S3** ICO/CUR/ANI writer and reader. Test in Explorer, as a Windows cursor and as a CSS cursor. Write a round-trip test.
- [ ] **S4** Canvas2D at 16 and 32 px with pixel snapping. Record the differences between Chrome and Firefox.
- [ ] **S5** project document size and save/load round trip, and the attachment endpoint for paint layers.
- [ ] **S6** GIF (gifenc, global palette, matte) plus APNG and WebP. Time a 60-frame encode in a worker.
- [ ] **S7** constraints × animation: keyframes as drivers, with a solve per frame.

## Stage 0: skeleton

- [ ] `apps/Iconic/{Iconic,Iconic.Library,Iconic.Tests}`, copied from `apps/MintPlayer`. Ports https 5019 and http 5020. Database `Iconic`.
- [ ] Nx: the `project.json` files, `@spark-apps/iconic`, and the root `package.json` `workspaces`.
- [ ] Accounts: `LocalCredentials.Full` plus Google, Microsoft and GitHub.
- [ ] `Project` entity (name, kind, sizes, scene JSON, updated, owner, isPublic). Row security: owner only.
- [ ] Run `--spark-synchronize-model` and `--spark-synchronize-security`.
- [ ] Routes: the editor `/p/:id` and `/new`, placed before `sparkRoutes()`.

## Stage 1: static vector

- [ ] Core library: the model, a command and undo stack, and the solver adapter. Vitest tests.
- [ ] SVG edit renderer: the selection, handles and constraint glyphs.
- [ ] Tools: point, line, polyline, Bézier, arc, circle, ellipse, rectangle, polygon, star and closed curve.
- [ ] Line style and fill style panels, and a format painter.
- [ ] Layers panel: shape and guide layers; reorder, hide and lock.
- [ ] Save and load, the localStorage draft for anonymous users, and PNG and SVG export.

## Stage 2: icons and cursors

- [ ] An outputs panel listing the sizes, the format (PNG or BMP), and the hotspot for cursors.
- [ ] Per-size overrides and paint layers (pencil, eraser, bucket, picker), stored as attachments.
- [ ] ICO and CUR export, and import of `.ico`, `.cur` and `.png`.

## Stage 3: animation

- [ ] A timeline UI with keyframe tracks and an easing editor.
- [ ] Motion paths bound to guide-layer shapes, with orient-to-tangent.
- [ ] GIF, APNG, WebP and ANI export, encoded in a worker.

## Stage 4: production

- [ ] Share links, the thumbnail, and the hand-off of a draft at sign-in.
- [ ] Deploy to the VPS. A `CLAUDE.md` entry marking Iconic as production.
- [ ] Final test run and coverage check.
