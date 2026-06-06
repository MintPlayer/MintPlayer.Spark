# Product Requirements Document: Angular 22 + ng-bootstrap 22 workspace upgrade (ng-spark republish)

**Issue**: #178
**Title**: fix: ng-spark build broken against ng-bootstrap 22 (removed virtual-datatable + toggle-button entry points)
**Status**: Draft
**Created**: 2026-06-06
**Last Updated**: 2026-06-06

---

## Summary

The published ng-spark / ng-spark-auth libs import ng-bootstrap v22-removed entry points (`virtual-datatable`, `toggle-button`), so any Angular-22 app that consumes them fails to bundle. The fix cannot be a localized component edit: this monorepo is still on Angular 21.1.6 + ng-bootstrap 21.18.0, the v22 replacements (`/checkbox`, fetch-driven `/datatable`) don't exist in v21, and ng-bootstrap 22 peer-requires Angular 22. With one root `node_modules` and CI/publish jobs that both compile the libs, the workspace **must move to Angular 22 + ng-bootstrap 22 in a single PR** (apps and libs together), absorb the datatable/checkbox API changes in the three ng-spark components **and** the ng-spark-auth login, keep the four demo apps compiling, and republish both libs as **22.0.0**.

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
- [ ] **FR-2**: ng-spark po-form/query-list/po-detail migrated to `<bs-checkbox>` + fetch-driven `<bs-datatable>`; no removed-API imports.
- [ ] **FR-3**: ng-spark-auth login migrated `bs-toggle-button` → `bs-checkbox` (preserve `formControlName="rememberMe"`).
- [ ] **FR-4**: Behavior preserved — server paging+sorting, virtual scrolling (`renderMode==='VirtualScrolling'`), query-list streaming, search, custom actions, permissions, lookup/reference rendering, per-cell renderer/link content.
- [ ] **FR-5**: All 4 demo ClientApps build under Angular 22.
- [ ] **FR-6**: ng-spark + ng-spark-auth republished as 22.0.0 with ^22 peer ranges, to npmjs.com + GitHub Packages.

### Should Have (P1)
- [ ] **FR-7**: Trim now-redundant manual pagination math where the new datatable paginates client-side (e.g. po-form `applyReferenceFilter`), if low-risk.

---

## Timeline & Milestones

### Milestone 1: Workspace upgrade to Angular 22 + ng-bootstrap 22
- [x] manifest bumps (Angular 22 + ng-bootstrap 22.2.0, TS 6.0.3, players ^20, Analog 2.6.0) + `overrides` for Nx-capped tooling + lib peer ranges → ^22 + clean `npm install`

### Milestone 2: ng-spark component migration
- [ ] po-form, query-list, po-detail + version/peer bump

### Milestone 3: ng-spark-auth migration
- [ ] login + version/peer bump

### Milestone 4: Demo apps build-green under Angular 22
- [ ] DemoApp, Fleet, HR, WebhooksDemo

### Milestone 5: Verify + publish
- [ ] build/test all, PR, CI green, merge, confirm 22.0.0 published to both registries

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
