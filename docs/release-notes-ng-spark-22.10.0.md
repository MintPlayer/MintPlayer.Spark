# Release notes — `@mintplayer/ng-spark` `22.10.0`

Client-only. **No NuGet change** — nothing in `MintPlayer.Spark*` moves, so apps stay on
`10.0.0-preview.70`.

One fix: **the [i] attribute-description tooltip now renders correctly in grid column headers and in
the reference picker** (#348, shipped incomplete in `22.9.0` — see the correction note in
[`release-notes-preview-70.md`](release-notes-preview-70.md)).

---

## What was wrong

`SparkAttributeDescriptionComponent` draws on three stylesheets that all live in `document.head`:
global Bootstrap (`btn btn-link p-0 ms-1`), `spark-icon`'s SVG sizing plus the `bootstrap-icons`
font, and its own component CSS. In a card or a form it is in the document tree and all three apply.
Inside a query grid it was mounted in `<mp-datatable>`'s **shadow root**, which document CSS cannot
cross — so all three went inert at once and the [i] rendered as unstyled default button chrome. The
markup was always correct.

The tooltip **popup** was never affected: `BsTooltipDirective` attaches through the CDK overlay to
`document.body`, so it always escaped the boundary. Only the trigger misrendered.

## What changed

`@mintplayer/ng-bootstrap` `22.18.0` drops the shadow root on the four components that adopt consumer
DOM — `mp-datatable`, `mp-treeview`, `mp-tree-select` and the `mp-query-builder` family — replacing it
with build-time attribute rescoping (`[data-mps]`, the same device as Angular's `_ngcontent`). The
other ~30 slot-based components keep their shadow roots.

No ng-spark API changed. The fix arrives purely from the dependency.

## Upgrading

**Both pins must move.** The light-DOM machinery ships in `@mintplayer/web-components`, which
ng-bootstrap declares only as a **peer** at `^2.0.0`. If your `web-components` pin is exact, bumping
ng-bootstrap alone installs `22.18.0` and leaves the shadow root — and the bug — in place, while the
version number says otherwise.

```jsonc
"@mintplayer/ng-bootstrap":   "^22.18.0",
"@mintplayer/web-components": "2.15.0"     // ← easy to miss; without it nothing changes
```

Confirm it actually landed:

```bash
grep -A2 'createRenderRoot() {' node_modules/@mintplayer/web-components/chunks/mp-datatable-*.mjs
# must print:  return this;
```

`@mintplayer/ng-spark` `22.10.0` declares `@mintplayer/ng-bootstrap` `^22.18.0` as a peer, so npm
will warn if you resolve an older one.

## What else this changes for you

Because the grid is now in the light DOM, **your page's CSS reaches content you render into grid
cells** — Bootstrap utilities (`text-muted`, `small`, `font-monospace`, `me-*`, `text-bg-*`), the
icon font, and your own component styles all apply inside `*bsRowTemplate` and `*bsDatatableColumn`
for the first time. Mostly this is the point. Two consequences worth checking:

- **Workarounds become redundant.** Inline styles written *because* a class could not reach the cell
  can go back to being classes. ng-spark did exactly this for its colour swatch and grid image.
- **Leak-in is real, and accepted.** Bare `table` / `th` / `td` / `input` rules in a global
  stylesheet now reach the datatable's internals. This is the same trade Angular's
  `ViewEncapsulation.Emulated` makes. Scope such rules if they cause trouble.

If you host one of the four converted components inside **your own** shadow root, mirror the light
tier into it with `adoptLightStyles` from `@mintplayer/web-components/light-dom`, or it renders
unstyled. Nothing in this repository needs that.

## Verified

Measured in a browser, not inferred:

- `mp-datatable.shadowRoot === null`; consumer DOM handed to the datatable carries **zero**
  `data-mps` stamps, and an unstamped decoy inside the grid stays unstyled — the boundary holds in
  both directions.
- Coverage ▸ Account: all six [i] on the page (2 in the card, 4 in the grid header) compute
  identically and measure 16 × 23 px in both places.
- Fleet ▸ Cars (`VirtualScrolling`, ~10k rows): header pinned, rows recycle, exactly one vertical
  scroller. No ng-spark CSS change was needed for virtual scroll.
- Bundles: `styles.css` unchanged; **+7.1 to +7.6 kB** raw per app, in JS, where the runtime-installed
  rescoped sheets belong.
