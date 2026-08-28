# Plan — Program units: PO page targets, composed virtual PO pages, shipped shell

**PRD:** `docs/issue_324_PRD.md`
**Issue:** [#324](https://github.com/MintPlayer/MintPlayer.Spark/issues/324)
**Branch:** `feat/issue-324-program-units`
**Base:** `master` @ `7d8e0a68`
**Release:** next `10.0.0-preview.*`; `@mintplayer/ng-spark` minor; `@mintplayer/ng-spark-auth` minor

---

## Milestones

| M | Title | Breaking? |
|---|---|---|
| M0 | Spikes S1–S4 | — |
| M1 | Server schema: `ObjectId` + `Url`, loader canonicalization + validation, endpoint right-per-type | behaviour |
| M2 | Composed virtual PO read path: `OnComposeAsync` (+ id-redirect if S1 says it's free) | no |
| M3 | Client models + `RouterLinkPipe`: `objectId`, exact type matching, `url` units | no |
| M4 | `spark-shell` (slots) + `spark-program-units` + `spark-language-selector` + `SPARK_AUTH_STATE` token + `provideSparkAuth` wiring | no |
| M5 | Demos: four shells collapse to `<spark-shell>`; WebhooksDemo `programUnits.json`; DemoApp Start page dogfood | no |
| M6 | Test sweep (single batched run) | — |
| M7 | Docs: `guide-program-units.md`, release notes, PRD status flip | no |
| M8 | Version bumps (NuGet preview, npm minors) | — |
| M9 | Consumer app adoption — separate repo, after publish (S5 + implementation) | — |

M1 and M3 are the two halves of one contract change; they land together. M2 depends on M1
(the Start-page unit needs `objectId` to be routable). M4 depends on M3 (the component uses
the pipe). M5 depends on M2+M4. Tests are batched at M6 per repo convention — intermediate
milestones are verified by reading and type-checking only.

**One PR.** Everything M0–M8 in this repository ships in a single pull request. M9 is the
same unit of work in the consumer repository, sequenced after the packages publish — it gets
its own session there (cross-repo writes are blocked from this repo), driven by the handoff
notes in this plan.

---

## M0 — Spikes

Run all before M1. Each can come back "no" and reshape its milestone.

### S1 — Compose hook prototype (shapes M2)

On the branch, hack `DatabaseAccess.GetPersistentObjectAsync` to detect a virtual type and
return `manager.GetPersistentObject(objectTypeId)` with a couple of values set. Answer:

- Is "no context root" reliably detectable from what `DatabaseAccess` already holds
  (`EntityTypeDefinition`), or does virtual-ness need an explicit marker in the model JSON?
- What breaks downstream when CollectionGuard / RowSecurity / EntityMapper / Etag are
  skipped — does the wire shape of the PO (no Etag, forced `can`) upset the client?
- Where exactly does the hook sit so `OnLoadAsync`-based actions are untouched?
- Is the id-redirect variant (substitute the id string before step 2) genuinely one seam?
  If yes it rides along in M2; if it grows arms, it's out.

### S2 — Composed PO rendering (shapes M2/M5 acceptance)

Serve the S1 prototype PO to `spark-po-detail` in DemoApp. Answer: page title from
breadcrumb (yes/no — if no, that's an M3 fix, the title is the point of the feature);
edit/delete affordances hidden by absent `can`; what a long multi-line text attribute
renders as, and whether any markdown-ish renderer exists in `ng-spark/renderers` (if not,
the Start page uses plain text + separate attributes, and a markdown renderer is NOT added
in this PR — the demo content bends to what exists).

### S3 — Cross-package auth token (shapes M4)

Add `SPARK_AUTH_STATE` to ng-spark, provide it from `provideSparkAuth()`, consume it from a
scratch component in HR. Verify: optional injection is null without ng-spark-auth
(DemoApp), the effect re-fires on login/logout (HR), and ng-packagr builds both libs
without a dependency edge appearing.

### S4 — Packaged shell extraction (shapes M4)

Move the `--bs-*`/`::part`/`data-bs-theme` seams from HR's `shell.component.scss` into a
component-level stylesheet on the extracted markup and run HR. If component styles can't
reach (`:host` boundaries + Lit shadow DOM), fall back to a shipped SCSS partial under
`ng-spark/styles/` (existing precedent: `_actionbar.scss`) that hosts import — decide here,
not during M4. In the same run, verify the behavioral replacements: `bs-shell`'s
`dismissOnNavigate` closes the drawer on unit click while `data-no-dismiss` on accordion
group headers keeps expand/collapse from closing it (deleting `onMenuItemClick`), and the
`bs-navbar-toggler`↔shell-state mirroring works when owned once inside `spark-shell`
(the `statechange` output + hidden `::part(hamburger)` combination).

---

## M1 — Server schema + loader + endpoint

- `ProgramUnit.cs`: add `public string? ObjectId { get; set; }` and
  `public string? Url { get; set; }`. XML-doc the semantics table from PRD D1.
- `ProgramUnitsLoader`: on load, canonicalize `Type` to the exact strings
  `query` / `persistentObject` / `url` (case-insensitive in, exact out) and validate field
  combinations (`query` ⇒ `QueryId`, `url` ⇒ `Url`, `persistentObject` ⇒
  `PersistentObjectId`; unknown type ⇒ throw). Loud failure at load, mirroring
  `SecurityConfigurationValidator`'s philosophy. Keep the missing-file fail-soft.
- `Endpoints/ProgramUnits/Get.cs`: rights per type — `Query` for query units, `Read` for
  PO units (with or without `ObjectId`), `url` always visible. Empty-group dropping
  unchanged. Serialize the two new fields.
- Update `GetProgramUnitsEndpointTests` + `ProgramUnitsLoaderTests` expectations (written
  now, executed at M6).

## M2 — Virtual PO read path

**As built (see PRD D2's design-revision note — the ComposeArgs/OnComposeAsync sketch was
replaced by owner directive):**

- `EntityTypeDefinition.ClrType` becomes nullable; `ISparkTypeResolver.Resolve(null) → null`;
  the four private clrType-resolution helpers collapse into `SparkTypeResolver.ResolveClrType`.
- `DatabaseAccess.GetPersistentObjectAsync`: when the type has no CLR type, resolve
  `{Name}Actions` by name (`IActionsResolver.ResolveByEntityName`) and invoke its PO-shaped
  `Task OnLoadAsync(PersistentObject obj)` — scaffold in (Id = requested id), mutate in place,
  force `Can = { Edit: false, Delete: false }` unless set, return. No actions class / no hook →
  404; a wrong-shaped `OnLoadAsync` throws loudly. Entity-backed types byte-for-byte unchanged.
- No id-redirect seam shipped (nothing needed it).
- Comment discipline: the *why skipping the guards is sound* rationale from the PRD goes on
  the skip site, since the code can't show it.

## M3 — Client models + pipe

- `program-unit.ts`: `objectId?: string`, `url?: string`.
- `router-link.pipe.ts`: `persistentObject` + `objectId` → `['/po', alias ?? id, objectId]`;
  exact type comparison (loader canonicalizes); `url` returns `null` (component renders an
  anchor, the pipe only does router links — adjust signature to `string[] | null`).
- Whatever S2 flagged for the read-only composed rendering (title-from-breadcrumb at
  minimum, if missing).

## M4 — `spark-shell` + `spark-program-units` + auth token

- `libs/node_packages/ng-spark/shell/` entry point (folder + `ng-package.json` +
  `index.ts` + `src/`), doc-comment index in `src/public-api.ts` updated.
- Slot directives per PRD D4's table (`spark-shell-slots.ts`, mirroring
  `grid/src/spark-query-slots.ts`: `inject<TemplateRef<…>>(TemplateRef)`,
  `ngTemplateContextGuard`, one parallel `TemplateRef` input per slot on the component,
  omitted slot ⇒ default).
- `SparkShellComponent`: wraps `bs-shell` (`dismissOnNavigate`, forwarded `breakpoint`),
  emits `<div slot="topbar">` itself, owns the toggler↔state mirroring, inputs
  `title` / `breakpoint` / `sidebarTheme`, carries the shadow-seam styling per S4's
  verdict, embeds `spark-program-units`.
- `SparkProgramUnitsComponent`: fetch, sort groups **and** units by `order`, accordion
  markup (`data-no-dismiss` on group headers), `reload()`, `SPARK_AUTH_STATE` optional
  effect + `reloadToken` input.
- `SparkLanguageSelectorComponent`: the extracted `bs-select` over
  `SparkLanguageService`, self-hiding when ≤ 1 language; the `TopbarEnd` default.
- `SPARK_AUTH_STATE` token in ng-spark (root, next to `SPARK_CONFIG`).
- `provideSparkAuth()` provides it (`useFactory: () => inject(SparkAuthService).user`).
- Component specs beside the source (vitest), service-level fetch mocking; slot-directive
  specs cover default-vs-projected per region.

## M5 — Demos

- Four shells collapse to `<spark-shell>` + slots (PRD D5 maps each app's extras to its
  slot); delete the `shellState`/resize blocks, the four `bs-shell-topbar.directive.ts`
  copies, and the per-app shell SCSS that S4 moved into the library.
- WebhooksDemo: author `programUnits.json` covering its existing queries; verify against
  its `security.json`.
- DemoApp Start page: `StartPage` marker class (Library project, mirroring
  `ConfirmDeleteCar`'s placement), model JSON (read-only, greeting/text + one or two value
  attributes per S2's renderer verdict), `StartPageActions.OnComposeAsync` (greeting +
  live entity counts via session), `programUnits.json` unit
  (`persistentObject` + `objectId: "start"`), `Read/StartPage` grant in `security.json`,
  run `--spark-synchronize-model` / update `modelHashes.json` as the lifecycle requires.

## M6 — Test sweep

Single batched run at the end (repo convention):

- Server: loader validation cases (canonicalization, each invalid combination), endpoint
  rights-per-type (PO unit visible with `Read` only; query unit with `Query` only; `url`
  always; empty group dropped), compose path (200 with composed values for granted user,
  404/refusal without `Read`, `can` forced false, `OnLoadAsync` path untouched for
  non-virtual types), DemoApp Start page end-to-end through the test driver.
- Client: pipe cases (objectId, exact types, url → null), `spark-program-units` spec
  (sorting, auth-effect re-fetch, reloadToken), `spark-shell` slot specs (default vs
  projected per region).
- Full suite once; flaky-under-load caveat applies — re-run named tests in isolation
  before calling a regression.

## M7 — Docs

- `docs/guide-program-units.md`: the JSON schema + semantics table, the virtual/composed
  PO recipe (marker class + model JSON + `OnComposeAsync` + grant + unit), `spark-shell`
  usage with the slot table (incl. putting `spark-auth-bar` or custom auth in
  `*sparkShellTopbarEnd`, `SPARK_AUTH_STATE` for custom-auth apps, re-theming via `--bs-*`
  custom properties), and standalone `spark-program-units` for hosts that own their shell.
- Release notes for the preview; PRD status → Implemented.

## M8 — Versions

- NuGet: all `MintPlayer.Spark*` to the next `10.0.0-preview.*` (whatever is current at
  merge time — check the version diff in review; majors NEVER move).
- npm: `@mintplayer/ng-spark` minor (new entry point + token + model fields),
  `@mintplayer/ng-spark-auth` minor (`provideSparkAuth` addition).
- CI publishes on merge to master; no manual publishing.

## M9 — Consumer app (separate repo, after publish)

Handoff for a session rooted in the consumer repo (this repo's session cannot write there):

1. Bump `MintPlayer.Spark*` NuGets and `@mintplayer/ng-spark(-auth)` npms to the versions
   M8 published.
2. Run S5 there: map the current home page onto composed attributes + custom actions;
   resync becomes a custom action; the interactive account list either becomes a
   query-backed section or stays a client component — decide, don't fake.
3. `Home` marker class + model JSON + `HomeActions.OnComposeAsync`; `Read/Home` grant;
   `programUnits.json` (Home unit with `objectId`, plus the existing queries as query
   units); synchronize the model.
4. Shell: collapse to `<spark-shell sidebarTheme="light">` — hand-rolled GitHub auth into
   `*sparkShellTopbarEnd`, the login-error `bs-alert` into `*sparkShellMainHeader`, delete
   the hand-rolled `shellState`/resize block and the raw `slot="topbar"` div. The
   `poDetail` vanity-redirect override already falls through for unknown types.
5. Retire whatever of the old home component S5 made redundant; keep the rest explicitly.
