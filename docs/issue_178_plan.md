# Development Plan: Issue #178

**Issue**: #178
**Title**: fix: ng-spark build broken against ng-bootstrap 22 (removed virtual-datatable + toggle-button entry points)
**Type**: Chore / Refactor (framework major upgrade) — *reclassified from `fix:` once the real scope surfaced*
**Priority**: High (blocks the downstream MintPlayer Spark-migration app)

## Executive Summary

`@mintplayer/ng-spark` (and `@mintplayer/ng-spark-auth`) cannot be consumed by an Angular-22 / ng-bootstrap-22 app because they import ng-bootstrap entry points that were removed in v22 (`virtual-datatable`, `toggle-button`). The naive expectation was a 3-file fix. Investigation showed it cannot be: this monorepo is still on **Angular 21.1.6 + ng-bootstrap 21.18.0**, the migration targets (`/checkbox`, fetch-driven `/datatable`) **do not exist in ng-bootstrap 21**, and ng-bootstrap 22 **peer-requires Angular 22**. With a single root `node_modules` and CI/publish jobs that both compile the libs, the only way to author, build, and publish 22-compatible libs from this repo is to **upgrade the entire workspace to Angular 22 + ng-bootstrap 22 in one PR** — apps and libs together — then migrate the affected components and republish.

---

## Problem Statement

### Current Behavior
- This monorepo builds fine today on Angular 21.1.6 + ng-bootstrap 21.18.0.
- The published `@mintplayer/ng-spark@0.0.8` / `@mintplayer/ng-spark-auth@0.0.8` import `@mintplayer/ng-bootstrap/virtual-datatable` and `/toggle-button`.
- A **downstream** app on ng-bootstrap 22 (`C:\Repos\MintPlayer`, `MintPlayer.Web/ClientApp`, Angular 22) fails to bundle: `Could not resolve "@mintplayer/ng-bootstrap/virtual-datatable"` / `.../toggle-button`. It cannot be worked around app-side — the broken imports live inside the published fesm.

### Expected Behavior
- The whole workspace (apps + libs) runs on Angular 22 + ng-bootstrap 22.
- ng-spark + ng-spark-auth build cleanly with zero unresolved `@mintplayer/ng-bootstrap/*` imports and are republished as **22.0.0**.
- The downstream app can `npm i @mintplayer/ng-spark@22.0.0` and bundle cleanly.

### Impact
Blocks the downstream MintPlayer Spark-migration app from building its SPA at all.

---

## Technical Analysis

### Root cause (no hypothesis needed — fully established)
The libs reference ng-bootstrap v22-removed entry points. The fix can only be authored against ng-bootstrap 22, which drags Angular 22 across the single-`node_modules` workspace (CI `nx affected --target=build` and the master publish job both compile the libs).

### ng-bootstrap 22 API deltas to absorb
- `virtual-datatable` **merged into** `datatable`. New `<bs-datatable>` is fetch-driven:
  - `[fetch]: BsDatatableFetch<T> = (req: BsDatatableFetchRequest) => Promise<PaginationResponse<T>>` (server-side; mutually exclusive with `[data]`).
  - `[data]: T[]` for client-side (paginates client-side).
  - `[virtualScroll]: boolean`, `[itemSize]`, `[isResponsive]`, `[(settings)]: DatatableSettings`, `[rowKey]`.
  - `*bsDatatableColumn="'Prop'; sortable: true"` headers; `*bsRowTemplate="let item"` rows — **no `of` microsyntax** (the old `*bsRowTemplate="let item of paginationResponse()"` form is gone). `$implicit` is the row.
- `toggle-button` removed → `BsCheckboxComponent` (`@mintplayer/ng-bootstrap/checkbox`, selector `bs-checkbox`, CVA over a boolean; supports `formControlName` and `[(ngModel)]`).
- `PaginationRequest = { perPage, page, sortColumns: SortColumn[] }`; `PaginationResponse<T> = { perPage, page, data, totalRecords, totalPages }`; `SortColumn = { property, direction: 'ascending'|'descending' }`.

### Files to Modify
**Dependency manifests / config**
- `package.json` (root) — Angular 21→22 (deps, devDeps, `overrides`), ng-bootstrap → ^22.2.0, add ng-bootstrap's new `@mintplayer/ng-*` peers, ng-animations → 22, ng-packagr → 22, nx/@nx/* → 22.7.5, ng-video-player → ^22.
- `libs/node_packages/ng-spark/package.json` — `version` 0.0.8 → **22.0.0**; peer ranges `@angular/* ^22`, `@mintplayer/ng-bootstrap ^22.2.0`.
- `libs/node_packages/ng-spark-auth/package.json` — same version + peer bump.
- Any `angular.json` / `project.json` / `tsconfig*` changes the Angular 22 migration schematics produce.

**Component migration (ng-spark)**
- `libs/node_packages/ng-spark/po-form/src/spark-po-form.component.{ts,html}` — `BsToggleButtonComponent` → `BsCheckboxComponent`; both `<bs-toggle-button>` → `<bs-checkbox>` (bindings verbatim); reference-modal `<bs-datatable>` → `[data]="referenceModalPagination()?.data ?? []"` + dataless `*bsRowTemplate="let item"`.
- `libs/node_packages/ng-spark/query-list/src/spark-query-list.component.{ts,html}` — drop virtual-datatable; collapse dual data path into one fetch-driven `<bs-datatable>`; streaming path binds `[data]="streamItems()"`.
- `libs/node_packages/ng-spark/po-detail/src/spark-sub-query.component.{ts,html}` — same fetch-driven collapse (with `parentId`/`parentType`), no streaming, keep `loading()` spinner.

**Component migration (ng-spark-auth) — NOT in the original handoff; discovered during planning**
- `libs/node_packages/ng-spark-auth/login/src/spark-login.component.{ts,html}` — `<bs-toggle-button [type]="'checkbox'" formControlName="rememberMe">` → `<bs-checkbox formControlName="rememberMe">`; swap the TS import.

**Demo apps (build-green only)**
- `Demo/{DemoApp,Fleet,HR,WebhooksDemo}/.../ClientApp` — no direct use of the removed APIs (verified by grep), but must compile under Angular 22 + ng-bootstrap 22; fix any schematic-missed breakages.

### Dependencies / Risks
- **@nx/angular ↔ Angular 22 compatibility**: confirm 22.7.5 drives the Angular 22 migration (`nx migrate`). Likely the gating tool version.
- **TypeScript / zone.js / tsconfig**: Angular 22 may require a TS bump or `tsconfig` target changes — let migration schematics drive this; verify `typescript 5.9.3` is in range.
- **ng-bootstrap 22 new peers** must be added explicitly (`ng-click-outside`, `ng-focus-on-load`, `ng-swiper`, `web-components`, `lit`) or installs/builds 404 on peer resolution.
- Standalone-API / control-flow / signal migration schematics may rewrite app code beyond the libs.

### Architecture Considerations
No new load-bearing interface — this is a prescribed framework upgrade plus a mechanical (but careful) component API migration. **Design fan-out not run** (Step 4F): the datatable/checkbox contracts are defined by ng-bootstrap 22; there is no second plausible shape to explore.

### PR hygiene (explicit constraint)
Keep the PR to **only the changes the upgrade + migration require**. `nx migrate` schematics and tooling can produce incidental churn (reformatting, comment rewrites, dependency reordering, config touch-ups unrelated to Angular 22). Review the full diff and **revert anything not needed** for the upgrade so the PR stays reviewable and free of unnecessary changes.

---

## Implementation Plan

### Milestone 1: Upgrade the workspace to Angular 22 + ng-bootstrap 22
1. `npx nx migrate latest` (pin Angular + @nx/angular to 22 targets); review `migrations.json`.
2. Update root `package.json`: Angular `*` → 22.0.0 (deps + devDeps + `overrides`, incl. `@angular/cdk`), `@angular/cli`/`build`/`build-angular`/`compiler-cli` → 22.0.0, `ng-packagr` → 22.0.0, `nx`/`@nx/*` → 22.7.5, `@mintplayer/ng-bootstrap` → ^22.2.0, `@mintplayer/ng-animations` → 22.0.0, add `@mintplayer/ng-click-outside`/`ng-focus-on-load`/`ng-swiper` 22.0.0 + `@mintplayer/web-components` ^1.6.0 + `lit` ^3.3.0, `@mintplayer/ng-video-player` → ^22.
3. `npm install` from repo root; `npx nx migrate --run-migrations`.
4. Kill stale `node.exe`, clear `.angular/` + demo `dist/` (per CLAUDE.md zombie-process note).
5. Outcome gate: tooling resolves; lib + app builds may still fail on the removed APIs — that's M2–M4.

### Milestone 2: Migrate ng-spark components
1. po-form: checkbox swap + reference-modal datatable to `[data]`.
2. query-list: single fetch-driven datatable (+ `makeFetch` factory, `settings`/`fetchFn` signals, streaming via `[data]`), preserve search/sort/virtual-scroll flag/custom actions/permissions/renderers.
3. po-detail sub-query: same fetch-driven collapse with parent context; keep spinner + empty state.
4. Bump `ng-spark/package.json` → 22.0.0 + peer ranges ^22.

### Milestone 3: Migrate ng-spark-auth
1. login: `bs-toggle-button` → `bs-checkbox`, preserve `formControlName="rememberMe"`.
2. Bump `ng-spark-auth/package.json` → 22.0.0 + peer ranges ^22.

### Milestone 4: Fix demo apps under Angular 22
1. Build each demo ClientApp; resolve any Angular-22 / ng-bootstrap-22 breakages.

### Milestone 5: Verify, publish, confirm
1. `npx nx run-many --target=build` (all) — zero unresolved `@mintplayer/ng-bootstrap/*`.
2. `npx nx run-many --target=test`.
3. ng-packagr `dist/` builds for both libs.
4. Open PR → wait for `pull-request` CI green → merge to master.
5. `publish-release` workflow builds + publishes ng-spark + ng-spark-auth **22.0.0** to npmjs.com and npm.pkg.github.com/MintPlayer; confirm both registries serve 22.0.0.

### Downstream (separate, Session A drives — out of scope for this repo)
`cd C:\Repos\MintPlayer\MintPlayer.Web\ClientApp && npm i @mintplayer/ng-spark@22.0.0 @mintplayer/ng-spark-auth@22.0.0 && npm run build`.

---

## Test Scenarios

### Scenario 1: query-list server paging + sorting
- **Given**: a non-streaming query with sortable columns.
- **When**: the page renders, the user sorts/pages.
- **Then**: `[fetch]` callback hits `executeQuery` with the right skip/take/sortColumns; rows render via the dataless row template; first-column link + permissions intact.

### Scenario 2: query-list virtual scrolling
- **Given**: a query with `renderMode === 'VirtualScrolling'`.
- **When**: rendered.
- **Then**: same datatable with `[virtualScroll]="true"` + `[isResponsive]`; server-side paging still driven by `[fetch]`.

### Scenario 3: query-list streaming (LIVE)
- **Given**: `isStreamingQuery`.
- **When**: snapshot/patch messages arrive.
- **Then**: `[data]="streamItems()"` updates; client-side search/sort still apply; no `[fetch]` used while streaming.

### Scenario 4: po-form boolean editor + reference modal
- **Given**: a boolean attribute and a Reference attribute.
- **When**: editing the boolean and opening the reference selector.
- **Then**: `<bs-checkbox>` round-trips the boolean via ngModel; reference modal lists filtered items client-side and selection sets the id.

### Scenario 5: po-detail sub-query with parent context
- **Given**: a detail page child query.
- **When**: rendered.
- **Then**: `[fetch]` passes `parentId`/`parentType`; spinner during load; empty state when 0 records.

### Scenario 6: ng-spark-auth login "remember me"
- **Given**: the login form.
- **When**: toggling remember-me.
- **Then**: `<bs-checkbox formControlName="rememberMe">` binds the reactive control as before.

---

## Acceptance Criteria

- [ ] Workspace builds on Angular 22 + ng-bootstrap 22 (`nx run-many --target=build` green).
- [ ] Zero `@mintplayer/ng-bootstrap/{virtual-datatable,toggle-button}` references remain anywhere.
- [ ] `nx run-many --target=test` green.
- [ ] All 4 demo ClientApps build.
- [ ] ng-spark + ng-spark-auth `dist/` build via ng-packagr; both at version 22.0.0 with ^22 peer ranges.
- [ ] Behavior preserved: server paging+sorting, virtual scrolling, streaming, search, custom actions, permissions, lookup/reference rendering, per-cell renderer/link content, login remember-me.
- [ ] PR CI green, merged, and 22.0.0 published to npmjs.com + npm.pkg.github.com/MintPlayer.

---

## Build & Test Commands

```bash
# Upgrade
npx nx migrate latest
npm install
npx nx migrate --run-migrations

# Build / test
npx nx run-many --target=build
npx nx run-many --target=test

# Lib dist (publish artifact shape)
npx nx run-many --target=build --projects=@mintplayer/ng-spark,@mintplayer/ng-spark-auth
```

---

## Related Files
- `package.json` (root)
- `libs/node_packages/ng-spark/{package.json, po-form, query-list, po-detail}`
- `libs/node_packages/ng-spark-auth/{package.json, login}`
- `Demo/{DemoApp,Fleet,HR,WebhooksDemo}/.../ClientApp`
- `.github/workflows/{pull-request,dotnet-build-master}.yml`
- PRD: `docs/issue_178_PRD.md`
