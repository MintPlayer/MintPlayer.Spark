# Plan — `spark-sub-query` becomes embeddable

**PRD:** `docs/issue_308_309_PRD.md`
**Issues:** #308 (implemented), #309 (items 1–4)
**PR:** #308, grown to cover both
**Branch:** `fix/parentless-sub-query`
**Base:** `master` @ `7ad2e30`
**Release:** `@mintplayer/ng-spark@22.3.0` + `10.0.0-preview.61` (M5 ships — decided 2026-08-21)

---

## Milestones

| M | Title | Issue | Blocking Coverage? |
|---|---|---|---|
| M0 | Spikes | — | — |
| M1 | Template restructure: three states, one gate | #309(3), F2 | prerequisite |
| M2 | Chrome: projected header + `showCard` | #309(1) | **yes** |
| M3 | Error surface: signal, alert, output | #309(3) | no |
| M4 | Refresh: `reload()` + `reloadToken` | #309(2) | no (hack exists) |
| M5 | `RowsNavigable`, all three link sites | #309(4) | no (but invalid HTML today) |
| M6 | Version, lock, release notes, docs | F8 | **yes** — nothing ships without it |

M1 is a prerequisite for M2 and M3 and must land first. M5 is the only milestone touching
the server; it is deliberately last so it can be cut without disturbing M1–M4.

**#308 is already implemented and green on this branch** (235 specs). It is not re-planned
here — it only needs the version bump it never carried (M6).

---

## Spikes

Each spike is a question that can come back "no". Run all of M0 before writing M1.

### S1 — Does hoisting the loading/error states out of the `query()` gate change what `spark-po-detail` renders?

The sole in-repo consumer stacks N sub-queries and relies on the card's `margin: 1rem 0` as
the only separator. M1 moves the spinner and adds an alert outside the gate.

**Method:** run DemoApp or HR, open a PO detail with ≥2 sub-queries, screenshot before and
after M1. **Pass:** spacing and card boundaries identical once loaded; a spinner now appears
during the initial load where previously nothing did.
**If it fails:** keep the margin on a wrapper element that renders in every state.

### S2 — Does `reloadToken` in a second effect actually preserve page and sort?

R7 is the whole point of splitting data-level from metadata-level refresh, and the
ng-bootstrap `fetch` setter *defeats its own dedupe* by resetting `_initialFetchDone`
(`mp-datatable.ts:344-357`). Reassigning `fetchFn` may therefore reset the datatable's
internal paging state even though our `settings` signal is untouched.

**Method:** spec — load, go to page 2, sort by a second column, bump `reloadToken`, assert
`executeQuery` was called again with the **same** `skip`/`take`/`sortColumns`.
**Pass:** identical params, one extra call. **Fail:** the datatable re-fetches page 1 →
fall back to exposing `reload()` only, and document that the token resets paging.

### S3 — Does `<ng-content>` default content give us the caption fallback?

D1 depends on `<ng-content select="[subQueryHeader]">{{ caption }}</ng-content>` rendering
the caption when nothing is projected. Angular's default-content support is real but has
edge cases under OnPush and with structural directives on the projected node.

**Method:** two specs — one with a projected header, one without — asserting the rendered
header text. **Pass:** caption when absent, projected content when present, no duplication.
**If it fails:** probe with `contentChild(SubQueryHeaderDirective)` and `@if` on it; costs a
directive export on the entry point.

### S4 — Does a hand-set `rowsNavigable: false` survive `--spark-synchronize-model`?

Synchronize must be a **fixed point**: read-modify-write that drops a hand-authored field is
a known bug class in this repo. `ModelSynchronizer.cs:122` claims `Custom.*` queries are
never touched and `:136`/`:829` scope stamping to `Database.`, so preservation should be
free — **assert it, do not assume it**.

**Method:** set `rowsNavigable: false` on a `Custom.*` query in a demo model file, run
synchronize twice, diff the file after each run.
**Pass:** byte-identical both times. **Fail:** add explicit preservation, as #274 did for
`showedOn`.

### S5 — Reproduce the nested anchor

F6 is the evidence that #309's stated workaround does not work, and AC 9 depends on the fix.

**Method:** a spec on a query whose first attribute has a renderer that emits an `<a>`;
assert `querySelectorAll('a a').length === 1` today, `0` after M5.
**Pass:** the nested anchor is observed before the fix. **If it is not observed**, F6 is
wrong and M5's justification needs revisiting before any server field is added.

### S6 — Do the M-3 security tests still pass untouched?

R10. M3 changes how the client *renders* a 404; nothing server-side should move.

**Method:** run `NotFoundVsForbiddenTests` and `MetadataEndpointAuthTests` unmodified.
**Pass:** green, with no edits to either file. **Any required edit is a stop-and-ask**, not
a fix.

### Not spiked: whether omitting `bs-card` breaks the datatable

Already answered by reading ng-bootstrap: `BsCardComponent`/`BsCardHeaderComponent` are pure
content projection, `.card-header` is a **global class rule** rather than `::slotted`, and
`bs-datatable` is `:host{display:block;width:100%}` with no card dependency. The only
difference is the loss of `overflow:hidden` clipping on a wide responsive table — covered by
S1's eyeball, not worth a spike.

### Not spiked: the 404-vs-403 decision

F5 settled it from four independent sources (the comment, the audit finding, the commit that
introduced it, and two tests). There is nothing left to measure.

---

## M1 — Template restructure

`spark-sub-query.component.html`, `.ts`.

Replace the single `@if (query(); as q)` gate with explicit states:

```
[error alert — outside every gate]
@if (loading())      { spinner }
@else if (query())   { body }
@else if (errorMessage()) { card shell + message }
```

- Move the spinner out of the gate so it is reachable on first load (F2).
- `loadData` resets `query` alongside `resultCount`/`fetchFn` at `.ts:86-87`, so a failed
  re-load stops leaving stale chrome (F2).
- Delete `resultCount` (F3), including its spec assertion at `.spec.ts:122`.

**Behaviour change, intended:** a first load now shows a spinner; a failed load now shows
something. Both were previously invisible.

**Verify:** S1; existing 235 specs stay green.

## M2 — Chrome

- `<ng-content select="[subQueryHeader]">` inside `<bs-card-header>`, with
  `{{ (q.description | resolveTranslation) || q.name }}` as default content (S3).
- `showCard = input(true)`; when false, emit the body div and the spinner, no card.
- Keep `margin: 1rem 0` with the card only — in bare mode spacing is the host's, by design.

**Specs** (nested `describe`, mirroring the `describe('without a parent')` precedent):
projected header replaces the caption; absent projection keeps it; `showCard=false` has no
`bs-card` but still renders `bs-datatable`; `showCard=false` still shows the spinner while
loading. DOM assertions — these are the first specs in this file to need them.

**Verify:** AC 2, 3, 4, 10.

## M3 — Error surface

- `errorMessage = signal<string | null>(null)`; both catches bind their error.
- Extraction `e.error?.error || e.message || t(fallback)`; **404 → generic message** (F5).
- `error = output<HttpErrorResponse>()`.
- Alert rendered outside every gate (M1 made this possible).
- Clear on `loadData` entry and on fetch success.

**Specs:** first-load 404 renders an alert and emits the output; a fetch rejection renders an
alert instead of an empty grid; a recovering fetch clears it.

**Verify:** AC 5, 6, 7; S6.

## M4 — Refresh

- `reload(): void` — data-level, `fetchFn.set(this.makeFetch(...))`, mirroring
  `spark-query-list.component.ts:268-276`.
- `reloadToken = input<unknown>(null)` in a **second** effect that calls `reload()` and skips
  its first run. The existing effect must not read it (R7).

**Specs:** `reload()` re-fetches with unchanged params; a token bump does the same; the
token's initial value does **not** double-fetch on mount.

**Verify:** AC 8; S2.

## M5 — `RowsNavigable` *(server; ships — but stays cuttable if S4/S5 fail)*

- `SparkQuery.RowsNavigable` (`bool?`) + `rowsNavigable?: boolean` in `spark-query.ts`.
- Default in the query-resolution path: `Database.*` → true; `Custom.*` → true unless
  explicitly false.
- Guard becomes `first && canRead() && rowsNavigable()` in **all three** sites:
  `spark-sub-query.component.html:27-30`, `spark-query-list.component.html:92-95` and
  `:124-127`.
- Set `rowsNavigable: false` on `Stock.json` (`Custom.StreamItems`) and `ProjectColumn.json`
  (`Custom.GetProjectColumns`) — both currently render dead links, so the demos carry the
  evidence.

**Verify:** AC 9; S4; S5. Fleet's `Stolen_Cars`/`Recent_Cars` links still work.

## M6 — Release

1. `libs/node_packages/ng-spark/package.json` → **22.3.0**. Peer ranges unchanged.
2. `npm install` **from the repo root** — the lock records a stale `22.0.8`. Commit it.
3. `docs/release-notes-preview-61.md` if M5 ships (server change → 20 `.csproj` bumps to
   `preview.61`); otherwise a client-only note stating the npm version explicitly. Since the
   new policy makes even breaks minors, the notes must say plainly which category this is.
4. Update the sub-query section of the component docs with the projection slot and the new
   inputs.
5. **Review the version diff before merging.** `npm-publish@v4` no-ops on an existing
   version, so a forgotten bump is a *green run that publishes nothing* (F8).

**Verify:** AC 12 — `npm view @mintplayer/ng-spark version` reports `22.3.0` after merge.

---

## Verification

- `nx run @mintplayer/ng-spark:test` — 235 specs today, plus ~11 new. Full suite, once, at
  the end.
- If M5 ships: the .NET suite, with `NotFoundVsForbiddenTests` and `MetadataEndpointAuthTests`
  **unmodified** (S6).
- Manual: DemoApp/HR PO detail for S1; a bare `showCard=false` grid for the responsive-table
  overflow eyeball.
- Do **not** run `ng serve`/`ng build` against a demo ClientApp — the ASP.NET host owns the
  dev server.

## Open questions

1. ~~**Does Coverage need M5, or only M1–M3?**~~ **Decided 2026-08-21: M5 ships in this PR.**
   Nested anchors are invalid HTML and the dead `/po/` links are live in two demos, so this
   is a real bug rather than a polish item. It is the only milestone touching the server, so
   the release becomes `10.0.0-preview.61` + `@mintplayer/ng-spark@22.3.0`, and M6 must bump
   all 20 `.csproj` files. If M5 turns out to be blocked (S4 or S5 failing), cut it and
   downgrade to a client-only release rather than holding M1–M4 behind it.
2. **`reloadToken` vs `reload()` for Coverage.** The page holds `gridEpoch` already, so the
   token is a one-line swap. If S2 fails, the token is the half that gets dropped.
3. **Should M2's projection slot be exported as a directive?** Only if S3 fails and the
   `contentChild` probe is needed.

## Outcome

_(filled in as milestones land — deviations, and why)_
