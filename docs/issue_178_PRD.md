# Product Requirements Document: Angular 22 + ng-bootstrap 22 workspace upgrade (ng-spark republish)

**Issue**: #178
**Title**: fix: ng-spark build broken against ng-bootstrap 22 (removed virtual-datatable + toggle-button entry points)
**Status**: In Progress (blocker MintPlayer/mintplayer-ng-bootstrap#385 resolved → shipped as ng-bootstrap 22.4.0)
**Created**: 2026-06-06
**Last Updated**: 2026-06-06

---

## Summary

**As built.** The whole workspace moved from Angular 21.1.6 + ng-bootstrap 21.18.0 to **Angular 22.0.0 + ng-bootstrap 22.2.0**, and ng-spark / ng-spark-auth were realigned to the v22 API and bumped to **22.0.0**. This had to be one PR (apps + libs together): the v22 datatable/checkbox API doesn't exist on v21, ng-bootstrap 22 peer-requires Angular 22, and a single root `node_modules` + CI/publish jobs compile the libs.

What landed:
- **M1 — upgrade** (`package.json`, lockfile, `tsconfig.base.json`): Angular → 22.0.0 (deps/devDeps/overrides incl. `@angular/cdk`); **TypeScript 5.9.3 → 6.0.3** (Angular 22 needs ≥6.0); ng-bootstrap 22.2.0 + ecosystem (ng-animations 22, players ^20, Analog 2.6.0). Nx 22.6.5 lacks Angular-22 support, so its Angular-tooling peers (`@angular/build`, `@schematics/angular`, `@angular-devkit/{core,schematics,build-angular}`, `ng-packagr`) are forced to 22.0.0 via `overrides`. TS 6.0 makes `baseUrl` an error → removed it and made `paths` relative (no `ignoreDeprecations`).
- **M2 — ng-spark**: po-form `<bs-toggle-button>`→`<bs-checkbox>` + reference-modal datatable to client-side `[data]`+`(rowClick)`; query-list and po-detail collapsed the virtual/normal dual path into one fetch-driven `<bs-datatable>` (`[fetch]`; `[virtualScroll]` flag; streaming via `[data]`). Dataless `*bsRowTemplate` with `@let row = $any(item)`. Removed two pre-existing dead imports.
- **M3 — ng-spark-auth**: login remember-me `<bs-toggle-button>`→`<bs-checkbox>`.
- **M4 — demos**: all four ClientApps build under Angular 22.

Load-bearing decisions: (1) **couple upgrade+migration** (they're mutually blocking) — chosen over splitting, which can't produce a buildable repo; (2) **lib version 22.0.0** tracking the Angular major — over a patch bump, to signal compatibility; (3) **`overrides` for Nx's capped peers** — over downgrading Angular or waiting for Nx, per the no-`nx-migrate` reality; (4) **remove `baseUrl`** — over `ignoreDeprecations` (developer preference; cleaner under TS 6).

**✅ UNBLOCKED.** The virtual-scroll eager-load (`runVirtualFetchAll`) was fixed in ng-bootstrap via the WC-owned lazy fetch loop (#385/#386), **shipped as ng-bootstrap 22.4.0** (web-components 2.0.0). This repo now consumes `^22.4.0` (root + ng-spark peer floor), so `[fetch]` + `[virtualScroll]` lazy-fetches the visible window page-by-page. One Spark-side follow-up was required: `query-list.onSearchChange()` now re-assigns `fetchFn` to force a refetch, because the 22.4 WC dedupes reloads by `{sortColumns, perPage, page}` (a page-1 reset with unchanged sort/perPage no longer refetches; reassigning the fetch callback resets the WC's `_lastReloadKey`). Also fixed: Fleet/HR demos pinned `ng-spark-auth ^0.0.8`, which stopped matching the workspace after the 22.0.0 bump — raised to `^22.0.0` to relink the local package (was pulling published 0.0.8 + a stray ng-bootstrap 21.47.0). M5 (PR → CI → merge → publish 22.0.0) is resuming; runtime eyeball of virtual scrolling pending.

Review outcome (`passes-with-fixes`): streaming coverage gap **fixed** (added a query-list streaming test); virtual-scroll eager-load → **blocker #385**; nits deferred (streaming double-sort harmless for string columns; po-form reference page-math dead code → FR-7; historical `docs/prd/*` v21 references left as archived history).

Traps for the reviewer: row-template `item` is `unknown` (the standalone `*bsRowTemplate` can't infer the datatable's generic) — cast via `@let row = $any(item)`, so cell content is no longer type-checked; **FR-4 behaviour preservation** (paging/sort/virtual/streaming/reference-pick) is build- and unit-test-green but needs real-app verification; po-form's `applyReferenceFilter` page-math is now partly redundant (the datatable paginates `[data]` client-side) but left in place to keep the diff minimal.

---

## Overview

A coupled framework major upgrade + component API migration, landed as one PR because the dependency bump and the component migration are mutually blocking (can't keep removed-API imports on v22; can't write v22 API on v21).

---

## Goals & Objectives

### Primary Goals
- Whole workspace on Angular 22 + ng-bootstrap 22.
- ng-spark + ng-spark-auth republished as 22.0.0, consumable by Angular-22 apps.

### Success Metrics
- Downstream `C:\Repos\MintPlayer` SPA bundles cleanly against `@mintplayer/ng-spark@22.0.0`.
- CI green; 22.0.0 live on npmjs.com + npm.pkg.github.com/MintPlayer.

---

## Chosen Design

Interface is defined by ng-bootstrap 22 (the merged fetch-driven `<bs-datatable>` and `<bs-checkbox>`). **Design fan-out not run** — no in-house interface to shape; the contracts are external and fixed. Per-component recipe captured in `docs/issue_178_plan.md` (M2/M3).

---

## Out of Scope

- **Downstream app update** (`C:\Repos\MintPlayer`, `npm i @mintplayer/ng-spark@22.0.0` + rebuild) — *Rationale: different repo; Session A drives it once 22.0.0 publishes.*
- **Refactoring ng-spark behavior beyond the API migration** (e.g., redesigning streaming, pagination UX, renderer system) — *Rationale: this PR preserves behavior; functional changes belong in their own issues.*
- **Changing ng-spark's public API surface / exports** — *Rationale: internal-implementation realignment, not an API change.*
- **Changing `@mintplayer/ng-bootstrap` source** (`C:\Repos\mintplayer-ng-bootstrap`) — *Rationale: the v22 datatable/checkbox API is correct; the Spark libs are what's stale.*
- **Demo app feature changes** — *Rationale: demos are touched only as far as needed to compile under Angular 22; no new demo functionality.*
- **Incidental diff churn** — *Rationale: the PR must contain only changes the upgrade + migration require; revert any schematic-introduced reformatting / unrelated edits so the diff stays minimal and reviewable.*

---

## Functional Requirements

### Must Have (P0)
- [x] **FR-1**: Workspace upgraded to Angular 22.0.0 + ng-bootstrap 22.2.0 (deps, devDeps; `overrides` force Nx-capped Angular tooling to 22; ng-bootstrap's new peers auto-resolved; TS → 6.0.3).
- [x] **FR-2**: ng-spark po-form/query-list/po-detail migrated to `<bs-checkbox>` + fetch-driven `<bs-datatable>`; no removed-API imports. (Also removed pre-existing dead `BsTableComponent`/`BsContainerComponent` imports.)
- [x] **FR-3**: ng-spark-auth login migrated `bs-toggle-button` → `bs-checkbox` (preserve `formControlName="rememberMe"`).
- [ ] **FR-4**: Behavior preserved — server paging+sorting, query-list streaming (tested), search (fixed for 22.4 dedup), custom actions, permissions, lookup/reference rendering, per-cell renderer/link content. Virtual scrolling now lazy-fetches via ng-bootstrap 22.4.0 (regression resolved). Builds green + unit-tested; pending end-to-end app eyeball.
- [x] **FR-5**: All 4 demo ClientApps build under Angular 22.
- [ ] **FR-6**: ng-spark + ng-spark-auth republished as 22.0.0 with ^22 peer ranges, to npmjs.com + GitHub Packages.

### Should Have (P1)
- [ ] **FR-7**: Trim now-redundant manual pagination math where the new datatable paginates client-side (e.g. po-form `applyReferenceFilter`), if low-risk.

---

## Timeline & Milestones

### Milestone 1: Workspace upgrade to Angular 22 + ng-bootstrap 22
- [x] manifest bumps (Angular 22 + ng-bootstrap 22.2.0, TS 6.0.3, players ^20, Analog 2.6.0) + `overrides` for Nx-capped tooling + lib peer ranges → ^22 + clean `npm install`

### Milestone 2: ng-spark component migration
- [x] po-form, query-list, po-detail migrated; version → 22.0.0 (peer ranges done in M1)

### Milestone 3: ng-spark-auth migration
- [x] login migrated; version → 22.0.0 (peer ranges done in M1)

### Milestone 4: Demo apps build-green under Angular 22
- [x] DemoApp, Fleet, HR, WebhooksDemo build (TS 6.0 `baseUrl` removed → relative `paths`)

### Milestone 5: Verify + publish — ✅ UNBLOCKED (ng-bootstrap 22.4.0 consumed)
- [x] build all (libs + 4 demos) + ng-spark/ng-spark-auth unit tests green (against 22.4.0)
- [x] pre-PR review+verify (`passes-with-fixes`); streaming test added
- [x] bump to ng-bootstrap ^22.4.0; `onSearchChange` refetch fix for 22.4 WC dedup; relink Fleet/HR demo ng-spark-auth
- [ ] end-to-end eyeball: VirtualScrolling query (Fleet `Car` / DemoApp `Stock`) lazy-fetches page-by-page; search refetches
- [ ] PR, CI green, merge, publish 22.0.0

---

## Open Questions

- None outstanding. Scope (couple upgrade + migration in #178), lib version (22.0.0), and that apps + libs move together were confirmed by the developer during planning.

---

## Technical Notes (Issue-Specific)

- ng-bootstrap 22 peers requiring explicit add: `@mintplayer/ng-click-outside`, `@mintplayer/ng-focus-on-load`, `@mintplayer/ng-swiper` (22.0.0), `@mintplayer/web-components` (^1.6.0), `lit` (^3.3.0).
- **Nx does not support Angular 22 yet** (2026-06) — do NOT `nx migrate`. Upgrade by hand-editing `package.json`, `npm install`, and adding `overrides` for any dependency stuck on an Angular `^21` peer. Keep `nx`/`@nx/*` at 22.6.5.
- Per CLAUDE.md: kill all `node.exe`, clear `.angular/` + demo `dist/` between dev-server restarts to avoid stale bundles.

---

## Related
- Issue #178
- Development plan: `docs/issue_178_plan.md`
- See CLAUDE.md / project memory for: Nx workspace layout, ng-bootstrap virtual-datatable internals, node.exe zombie-process caveat.
