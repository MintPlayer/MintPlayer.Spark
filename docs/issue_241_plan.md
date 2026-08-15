# Plan: renderer value for AsDetail attributes (#241) + row context (#245)

Single PR on branch `feat/issue-241-asdetail-renderer-value`. See `issue_241_PRD.md` for the
full design. One commit per milestone.

## Milestones

| # | Deliverable | Status |
|---|---|---|
| M1 | `renderers/src/renderer-inputs.ts` (`withDeclaredInputs` + `rendererValue`) + export from entry point + `renderer-inputs.spec.ts` (filters undeclared, keeps declared, caches per type; `rendererValue` prefers value → object → objects, undefined attr → undefined) — first spec in the `renderers/` entry point | ✅ |
| M2 | Contracts all-optional + `item` (`spark-attribute-renderer.ts`); registration slots nullable (`spark-attribute-renderer-registry.ts`) | ✅ |
| M3 | Sites 1–3: `rendererValue` fallback + po-detail formData-loop fix + `item` + `withDeclaredInputs` filter (builders take resolved component type as first arg; templates updated) | ✅ |
| M4 | Sites 4–7: `item` (sites 4/6/7) + filter (all four, incl. po-form edit at site 5) — no value change | ✅ |
| M5 | Component specs: query-list/sub-query — AsDetail-bound column renderer receives nested PO; renderer declaring `item` receives row PO; renderer declaring only `value` renders without throwing (pins the latent bug). po-detail — detail renderer gets nested PO as `value` AND in `formData`; AsDetail cell renderer gets flat row as `item`. po-form — B3 test mechanically updated to the new builder signature (DummyEditor became a real component declaring `valueChange`), in-place mutation still pinned; minimal edit renderer without `valueChange` renders | ✅ |
| M6 | `docs/guide-custom-attribute-renderers.md`: contract example blocks; input matrix (add `item`, note passed-only-when-declared); new "AsDetail values" + "Row context" sections; fix root-import examples → `@mintplayer/ng-spark/renderers` / `/models`; `valueChange`-optional note under Key points | ✅ |
| M7 | Version bump: `@mintplayer/ng-spark` 22.0.10 → 22.0.11 (client-only; nothing else moves in lockstep — .NET packages and ng-spark-auth untouched) | ✅ |

## Verification

- Spike: `renderer-inputs.spec.ts` run in isolation after M1 — validated
  `reflectComponentType` filtering under vitest (note: signal-input *aliases* aren't reflected
  in the JIT test environment; the contracts use none, and filter + NgComponentOutlet always
  read the same component definition so they stay consistent).
- `tsc --noEmit -p tsconfig.spec.json` clean after M5.
- Full ng-spark vitest suite after M7: **20 files / 232 tests, all passing**.
- DemoApp: `address-card` detail-only renderer registered on the AsDetail `Person.Address`
  attribute (nested-PO `value` + Person `item`, nullable registration slots in practice);
  client type-checks clean (only TS6059 rootDir noise inherent to source-based lib consumption
  under raw tsc).
- No server change: `libs/spark/**` untouched.

## Build-config hardening (post-CI-failure follow-up)

The first CI run failed on `Object.fromEntries`: the `build` targets passed no `tsConfig` to
`@nx/angular:package`, so ng-packagr compiled with its own default tsconfig (lib < ES2019) and
both `tsconfig.lib.json` files were dormant. Fixed twofold:
- `renderer-inputs.ts` briefly used a plain loop as a stopgap; reverted to `Object.fromEntries` once the lib tsconfig was wired.
- Both ng-spark and ng-spark-auth `build` targets now pass their `tsconfig.lib.json`
  (ES2022 lib + strict flags active), with `"compilationMode": "partial"` added — wiring a
  custom tsconfig drops ng-packagr's default, and published Angular libraries must ship
  partial-Ivy output. Verified: both packages build in partial mode locally.

## Site map (verified against master `32d03c3`)

1. `query-list/src/spark-query-list.component.ts` `getColumnRendererInputs` (~:317) + template :147-148
2. `po-detail/src/spark-sub-query.component.ts` `getColumnRendererInputs` (~:155) + template :47-48
3. `po-detail/src/spark-po-detail.component.ts` `getDetailRendererInputs` (~:164) + template :88-89
4. `po-detail/src/spark-po-detail.component.ts` `getAsDetailCellRendererInputs` (~:184) + template :104-105
5. `po-form/src/spark-po-form.component.ts` `getEditRendererInputs` (~:319) + template :328-329
6. `po-form/src/spark-po-form.component.ts` `getAsDetailCellRendererInputs` (~:338) + template :280-281
7. `po-form/src/spark-po-form.component.ts` `getAsDetailCellEditRendererInputs` (~:352) + template :111
