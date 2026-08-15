# Plan: renderer value for AsDetail attributes (#241) + row context (#245)

Single PR on branch `feat/issue-241-asdetail-renderer-value`. See `issue_241_PRD.md` for the
full design. One commit per milestone.

## Milestones

| # | Deliverable | Status |
|---|---|---|
| M1 | `renderers/src/renderer-inputs.ts` (`withDeclaredInputs` + `rendererValue`) + export from entry point + `renderer-inputs.spec.ts` (filters undeclared, keeps declared, caches per type; `rendererValue` prefers value → object → objects, undefined attr → undefined) — first spec in the `renderers/` entry point | ☐ |
| M2 | Contracts all-optional + `item` (`spark-attribute-renderer.ts`); registration slots nullable (`spark-attribute-renderer-registry.ts`) | ☐ |
| M3 | Sites 1–3: `rendererValue` fallback + po-detail formData-loop fix + `item` + `withDeclaredInputs` filter (builders take resolved component type as first arg; templates updated) | ☐ |
| M4 | Sites 4–7: `item` (sites 4/6/7) + filter (all four, incl. po-form edit at site 5) — no value change | ☐ |
| M5 | Component specs: query-list/sub-query — AsDetail-bound column renderer receives nested PO; renderer declaring `item` receives row PO; renderer declaring only `value` renders without throwing (pins the latent bug). po-detail — detail renderer gets nested PO as `value` AND in `formData`; AsDetail cell renderer gets flat row as `item`. po-form — existing B3 tests pass unmodified (in-place mutation pinned); minimal edit renderer without `valueChange` renders | ☐ |
| M6 | `docs/guide-custom-attribute-renderers.md`: contract example blocks; input matrix (add `item`, note passed-only-when-declared); new "AsDetail values" + "Row context" sections; fix root-import examples → `@mintplayer/ng-spark/renderers` / `/models`; `valueChange`-optional note under Key points | ☐ |
| M7 | Version bump: `@mintplayer/ng-spark` 22.0.10 → 22.0.11 (client-only; nothing else moves in lockstep — .NET packages and ng-spark-auth untouched) | ☐ |

## Verification

- Type-check per milestone (read + `tsc`/build implicitly via test config); full
  `nx run ng-spark:test` (vitest) once after M7 — tests are batched at the end per working
  agreement.
- No server change: `libs/spark/**` untouched.

## Site map (verified against master `32d03c3`)

1. `query-list/src/spark-query-list.component.ts` `getColumnRendererInputs` (~:317) + template :147-148
2. `po-detail/src/spark-sub-query.component.ts` `getColumnRendererInputs` (~:155) + template :47-48
3. `po-detail/src/spark-po-detail.component.ts` `getDetailRendererInputs` (~:164) + template :88-89
4. `po-detail/src/spark-po-detail.component.ts` `getAsDetailCellRendererInputs` (~:184) + template :104-105
5. `po-form/src/spark-po-form.component.ts` `getEditRendererInputs` (~:319) + template :328-329
6. `po-form/src/spark-po-form.component.ts` `getAsDetailCellRendererInputs` (~:338) + template :280-281
7. `po-form/src/spark-po-form.component.ts` `getAsDetailCellEditRendererInputs` (~:352) + template :111
