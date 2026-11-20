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
  - **NEXT:** S1.

## Stage 0: solver and skeleton

- [ ] **S1** `libs/node_packages/constraint-solver` (`@mintplayer/constraint-solver`, D14):
  - the residuals and Jacobians for every D4 constraint type
  - damped Newton / Levenberg–Marquardt
  - connected components
  - Jacobian rank for conflicts and degrees of freedom
  - the degenerate-case corpus in vitest
  - a fps benchmark on a laptop and a phone
  - go/no-go recorded in the PRD
- [ ] Publishing the solver: a `tsc` build, a `Push` step in `dotnet-build-master.yml`, and membership of "Angular suites" plus the "Other suites" exclude in `test.yml`. Copy the `webhooks-receiver` setup.
- [ ] `apps/Iconic/{Iconic,Iconic.Library,Iconic.Tests}`, copied from `apps/MintPlayer`. Ports https 5019 and http 5020. Database `Iconic`.
- [ ] Nx: the `project.json` files, `@spark-apps/iconic`, and the root `package.json` `workspaces`.
- [ ] Accounts: `LocalCredentials.Full` plus Google, Microsoft and GitHub.
- [ ] `Project` entity:
  - fields: name, kind, design size, outputs, scene JSON, updated, owner, isPublic, isListed
  - row security: owner only
  - the D21 quotas
  - run `--spark-synchronize-model` and `--spark-synchronize-security`
- [ ] Routes: the editor `/p/:id` and `/new`, placed before `sparkRoutes()`.

## Stage 1: static vector

- [ ] **S2** prototype of snapping and constraint UX at desktop and phone widths. The owner reviews it.
- [ ] **S3** ICO/CUR/ANI writer and reader. Test in Explorer, as a Windows cursor and as a CSS cursor. Write a round-trip test.
- [ ] **S4** Canvas2D at 16 and 32 px with pixel snapping. Record the differences between Chrome and Firefox.
- [ ] **S5** document size, autosave as patches (parameterised, no RQL splicing), and the attachment endpoints.
- [ ] Core library: the model, a command and undo stack, derived points with Split (D15), and the solver adapter.
- [ ] SVG edit renderer: the selection, handles, constraint and derived-point glyphs, and the touch loupe.
- [ ] Tools: point, line, polyline, Bézier, arc, circle, ellipse, rectangle, polygon, star, closed curve, and text (opentype.js outlines, D23).
- [ ] Line style and fill style panels, and a format painter.
- [ ] Layers panel: shape and guide layers; reorder, hide and lock.
- [ ] A responsive shell: bottom sheets on phones, Pointer Events, pinch-zoom and pan (D18).
- [ ] Save and load as patches, a quota meter, the `localStorage` draft for anonymous users, and snapshots (D22).
- [ ] PNG and SVG export.

## Stage 2: icons and cursors

- [ ] An outputs panel: the sizes, the format (PNG or BMP), the hotspot for cursors, and the stroke mode and pixel snapping per size (D17).
- [ ] Per-size overrides and paint layers (pencil, eraser, bucket, picker), stored as attachments.
- [ ] ICO and CUR export, and import of `.ico`, `.cur` and `.png`.

## Stage 3: animation

- [ ] **S6** GIF (gifenc, a global palette, a matte option), APNG and WebP. Time a 60-frame encode in a worker.
- [ ] **S7** a solve per frame from the rest pose. Scrubbing must equal playback, and the export must equal the preview.
- [ ] A timeline UI with shape and point keyframe tracks and an easing editor.
- [ ] Motion paths bound to guide-layer shapes, with orient-to-tangent.
- [ ] GIF, APNG, WebP and ANI export, encoded in a worker.

## Stage 4: production

- [ ] Share links (`noindex`), Remix, report-by-email and takedown (D20).
- [ ] The thumbnail, and the hand-off of a draft at sign-in.
- [ ] Deploy to the VPS. A `CLAUDE.md` entry marking Iconic as production.
- [ ] Final test run, including a phone viewport in E2E, and a coverage check.
