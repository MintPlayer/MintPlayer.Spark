# Inputs that lost `.form-control`: missing `BsFormControlDirective` imports

**Status:** planned (2026-10-14). Fixes go in one PR.
**Trigger:** after PR #512 (commit `78e86a1e`), the "Organization" picker on SeoTester's Edit-Site page
(`/po/site/<id>/edit`) renders an unstyled `<input>`: browser-default inset border, `1px 2px` padding, no radius.

## 1. Root cause

In `@mintplayer/ng-bootstrap` 22.23.0, nothing in CSS and nothing in `<bs-form>` itself puts the
`form-control` class on an input. A directive does it:

- `BsFormControlDirective` (`node_modules/@mintplayer/ng-bootstrap/fesm2022/mintplayer-ng-bootstrap-form.mjs:44-51`)
  - selector: `bs-form input:not(.no-form-control), bs-form textarea:not(.no-form-control)`
  - host binding: `[class.form-control]`, set to `!!inject(BsFormComponent, { optional: true })`

The class appears only when **both** of these hold:

1. **The directive is imported by the component that owns the template containing the `<input>`.**
   Angular matches directives at compile time, against that template's own `imports`. A parent's
   imports don't count, even when the parent's `<bs-form>` wraps the child component.
2. **A `BsFormComponent` sits above the input in the element-injector tree.** This is a DI lookup, so
   it crosses child-component and modal boundaries.

The styles themselves are global: `::ng-deep .form-control{…}` from `BsFormComponent`'s stylesheet.
So once the class is missing there is no fallback, and `.form-control.is-invalid` (the red
validation border) breaks with it.

**What PR #512 changed** (`git show 78e86a1e -- libs/node_packages/ng-spark/po-form/src/spark-reference-picker.component.ts`):
- The modal's hand-written search box (`<bs-form><bs-grid><bs-input-group><input>`) was replaced by
  `<spark-search-box>`, which has its own `<bs-form>` and imports the directive itself
  (`grid/src/spark-search-box.component.ts:3,31,33`).
- After that change the form imports looked unused, so they were removed:
  `FormsModule, BsFormComponent, BsFormControlDirective`, plus the grid imports and `SparkIconComponent`.
- They were still needed. The picker's visible read-only trigger input
  (`spark-reference-picker.component.html:2-7`, unchanged by the commit) needs condition 1.

**Blast radius:**
- Every single-value `Reference` attribute in every app, since `spark-po-form.component.html:98-111`
  routes all non-array references to `<spark-reference-picker>`. This is not SeoTester-specific.
- Inline AsDetail reference cells. PR #512 widened that branch (`inlineRefUsesPicker`), so more cells
  now render the broken picker instead of a `bs-select`.
- Array references (`<bs-tree-select>`), lookup pickers and `<bs-select>` paths are not affected.

### Evidence
- Production DOM, inspected through Playwright: `<input type="text" readonly id="OrganizationId">`
  has no `class` attribute. The Website input on the same page has
  `class="… form-control …"` and computes `1px solid rgb(222,226,230)` / `6px 12px` / radius `6px`.
  The Organization input computes `2px inset` / `1px 2px` / `0px`.
- The `.form-control` rules are present on the page in both cases (one global inline `<style>`, not
  scoped). Only the class is missing. Screenshot: `.playwright-mcp/org-field-spa.png`.
- The owner confirms the hard load is broken too. One inspection saw a styled hard load, which was
  most likely a stale cached bundle; it is superseded.

## 2. Same defect elsewhere: codebase sweep

The sweep covered `libs/node_packages/ng-spark/**` and every `apps/*/ClientApp`. No component sets
`ViewEncapsulation`, so overlays and encapsulation are irrelevant. Only conditions 1 and 2 matter.

### A. Broken: fix in this PR
| Location | Problem | Origin |
|---|---|---|
| `libs/node_packages/ng-spark/po-form/src/spark-reference-picker.component.ts:41` (template `.html:2`) | Trigger input has no `form-control`; `[class.is-invalid]` has no visible effect | `78e86a1e` (#512) |
| `libs/node_packages/ng-spark/auth/account/src/spark-connected-agents.component.ts:42-45` (inputs at `:135`, `:139`) | Token-name and days inputs sit inside `<bs-form>` (`:132`), but the component imports `BsFormComponent` and not `BsFormControlDirective` | `31ab6ecb` (#509) |

### B. Verified fine
- `spark-po-form` (all inputs, textareas, `is-invalid`, `invalid-feedback`)
- `spark-lookup-picker` (same layout as the reference picker, still imports the directive)
- `spark-search-box`
- `client-operations/secret-dialog`
- every auth page (login, register, forgot/reset password, two-factor) and every account page
  (change-password, personal-data, two-factor-setup, profile)
- CodeCoverage's `repo-commit-graph-panel`
- Intentional exceptions:
  - `spark-column-filter-panel` has its own `.filter-search` styles, documented at `.ts:26` / `.scss:4-6`.
  - The disabled native checkboxes in `spark-grid-cell.html:7` and `spark-po-detail.html:176` are
    deliberately left unstyled.

### C. Fragile: decide in this PR
- `po-edit/src/spark-po-conflict-dialog.component.ts:112,119`
  - Its radios carry an explicit `class="form-check-input"` inside a `bs-modal`, with no `<bs-form>` of their own.
  - They are styled only because the edit page's `spark-po-form` keeps the global form stylesheet loaded.
  - **Plan:** wrap the dialog body in `<bs-form>` and import `BsFormComponent`, so the dialog no longer
    depends on a sibling being on the page.
- `apps/SeoTester/.../seo/renderers/check-results.component.html:15`
  - A bare `<input type="checkbox">` with no form context renders as a native checkbox.
  - **Plan:** check it in the browser, and switch it to `bs-checkbox` if the native look is unintended.

## 3. Plan (one PR)

| # | Change | File |
|---|---|---|
| F1 | Put `BsFormControlDirective` back into `imports` (`@mintplayer/ng-bootstrap/form`). Keep the trigger input's markup as is, so the `.no-form-control` opt-out convention still works. | `spark-reference-picker.component.ts` |
| F2 | Add `BsFormControlDirective` to `imports` | `spark-connected-agents.component.ts` |
| F3 | Give the conflict dialog its own `<bs-form>` | `spark-po-conflict-dialog.component.ts` |
| F4 | Verify the checkbox in the browser; switch to `bs-checkbox` if the native look is unintended | `check-results.component.html` |
| T1 | Spec: render the picker inside a `<bs-form>` host and assert the trigger input has `form-control`, and gets `is-invalid` when `isInvalid` is true. Must fail before F1. | `spark-reference-picker.component.spec.ts` |
| T2 | Spec: the connected-agents inputs have `form-control`. Must fail before F2. | connected-agents spec (new, or the existing account spec) |
| G1 | **Guard against the next cleanup** (see below) | new spec under `libs/node_packages/ng-spark` |

**G1, static guard.** Unused-import cleanups will keep happening, and a directive that matches a
plain `<input>` selector always looks unused. G1 is a vitest spec that does the following:
- Scan every `*.component.ts`, plus its `templateUrl` html, under `libs/node_packages/ng-spark` and `apps/*/ClientApp/src`.
- For every template containing `<input` or `<textarea`:
  - Require `BsFormControlDirective` in that component's `imports`, unless the element has
    `class="… no-form-control …"`, is `type="checkbox|radio|hidden"`, or the file is on an explicit
    allow-list with a one-line reason (column-filter panel, grid cell, po-detail).
- The spec must fail on today's tree (it would list F1 and F2) before the fixes go in.

**Verification:**
- Red→green for T1, T2 and G1.
- In the browser (playwright MCP, local SeoTester `dotnet run`):
  - Edit-Site Organization field: computed styles match the Website field, both on a hard load and on detail → Edit.
  - Account → Connected agents: the token inputs are styled.
  - A Fleet or HR edit page with a single reference picker is styled.
- Production check after deploy: same Edit-Site URL.

## 4. Out of scope
- None.
