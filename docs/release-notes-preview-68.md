# Release notes — `10.0.0-preview.68`

A single fix: **a single-child `AsDetail` column hands its renderer the nested object again** (#329).

Server-only. No client package changes, no API changes, nothing to migrate — apps on
`preview.67` upgrade the NuGet references and the column comes back.

---

## The regression

Since `preview.67` (#327), a query row projected an `AsDetail` cell as:

```jsonc
{ "key": "Coverage", "value": null, "breadcrumb": "1422" }
```

for `isArray: false`. That is right for a **text** cell — the client's cell pipe prints the
server-resolved breadcrumb and never looks at `value` — and useless for a **renderer**, whose only
input on a grid *is* `value`. Every renderer's null fallback painted, so a column with a custom
renderer on a single-child `AsDetail` attribute silently went blank: no error, no console warning,
green build, green `--spark-verify-model`.

The array case (`3 items`) and the detail page were never affected.

## The fix

`QueryResultProjector` now projects the child itself:

```jsonc
{ "key": "Coverage",
  "value": { "attributes": [ { "name": "LinesCovered", "value": 1422 }, … ], "breadcrumb": "1422" },
  "breadcrumb": "1422" }
```

This is the same `PersistentObject` the detail path hands a renderer, so
`docs/guide-custom-attribute-renderers.md` — "in query-list, sub-query and po-detail field hosts,
`value` is the nested `PersistentObject`" — is true on all three hosts again.

`breadcrumb` is untouched, which is what keeps rendererless cells identical: the client tests it
before the `AsDetail` branch. The array branch is untouched too.

## Pinned

`QueryResultProjectorAsDetailTests` covers both branches at the object level *and* at the wire —
the bug type-checked (`Value` is `object?`), so the assertion that would have caught it is the one
that serialises the cell and reads `value.attributes`. A general guard asserts no column carrying a
`renderer` ever projects a null value from a populated attribute.
