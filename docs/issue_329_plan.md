# Plan — Restore the nested object on a single-child `AsDetail` query cell

**PRD:** `docs/issue_329_PRD.md`
**Issue:** [#329](https://github.com/MintPlayer/MintPlayer.Spark/issues/329)
**Branch:** `fix/issue-329-asdetail-renderer-value`
**Base:** `master` @ `fd570906`
**Release:** `10.0.0-preview.68`; npm packages unchanged at `22.8.0`

---

## Milestones

| M | Title | Breaking? |
|---|---|---|
| M1 | `QueryResultProjector`: single child projects `asDetail.Object` | no (restores documented behaviour) |
| M2 | Regression tests — object level **and** wire level | — |
| M3 | Comment/doc truth-up: client extractors, `issue_327_PRD.md` amendment | no |
| M4 | Release: NuGet `preview.68` bump ×22, release notes | — |

Small and linear; no dependencies worth drawing. Tests are batched into one run at M2/M4 per repo
convention. **One PR** — everything M1–M4 ships together.

## Investigation approach

No subagent team. The issue arrived with the defect localised to a single line, the wire evidence
captured from a live app, and both sides of the contract quoted — a fan-out would have re-derived a
conclusion already in hand at the cost of a cold context per agent. What *did* need first-hand checking
was serialisation safety (PRD §"Serialisation safety"), which is three greps in this repo.

## M1 — The fix

`libs/spark/MintPlayer.Spark/Services/QueryResultProjector.cs`, in `ToValue`:

```csharp
Value = column.IsArray ? asDetail.Objects?.Count ?? 0 : asDetail.Object,
```

The comment above the branch is rewritten: it previously justified the null, and now has to say why an
array projects a count while a single child projects the child, and why `Breadcrumb` below it is what
keeps rendererless cells unaffected. That comment is the artefact that stops the null being
reintroduced by someone reading the #327 rationale.

Pre-flight, before writing the line (all three confirmed):

- STJ serialises an `object`-declared property by **runtime** type → the PO is not flattened to `{}`.
- `PersistentObjectAttributeJsonConverter.WriteSharedFields` omits `Parent` → no attribute→PO cycle.
- `PersistentObject.Parent` is assigned only in `Refresh.cs` → a projected child has no parent pointer.

## M2 — Tests

New `tests/MintPlayer.Spark.Tests/Services/QueryResultProjectorAsDetailTests.cs` (7 facts):

| Fact | Pins |
|---|---|
| `Single_child_projects_the_nested_object_as_the_cell_value` | AC1 |
| `Single_child_keeps_its_resolved_breadcrumb_beside_the_object` | AC3 |
| `Single_child_absent_projects_null_rather_than_a_scaffold` | no scaffold leaks into a row |
| `Array_projects_the_child_count` | AC4 / #327 unchanged |
| `Array_with_no_children_projects_zero` | AC4 |
| `Single_child_serialises_to_the_shape_a_detail_page_sends` | AC2 |
| `No_populated_column_carrying_a_renderer_projects_a_null_value` | AC5, generalised past `AsDetail` |

The **wire** assertion is the one that matters. The bug type-checked — `Value` is `object?`, so `null`
is as valid as an object — and an object-level assertion alone would still pass if the value serialised
to `{}`. It serialises the cell with the endpoint's own `JsonSerializerOptions` and reads
`value.attributes[]`.

The last fact is the cheap general guard: a renderer's only grid input is the cell value, so a null
there is a blank column by construction, whatever the data type.

## M3 — Truth-up

- `renderer-inputs.ts` — the `cellValue` doc claimed "a grid row carries no nested objects to fall back
  to". Now: the cell is a single channel, `value` already *is* the nested object for a single-child
  `AsDetail`, and the two extractors stay separate so a renderer written against `attr.object` fails
  loudly rather than silently.
- `query-cell.pipe.ts` — the `AsDetail` display branch comment now distinguishes the array cell (count,
  pluralised here where the language is) from the single-child cell (object for a renderer; displayed by
  the breadcrumb above, never stringified here).
- `docs/issue_327_PRD.md` — a blockquote amendment under the paragraph that documented the null as
  intentional. Deleting it would erase the reasoning; leaving it unmarked would keep a false statement
  in the design record.
- `docs/guide-custom-attribute-renderers.md` — **no edit**: it becomes true again.

## M4 — Release

- `10.0.0-preview.67` → `.68` in all 22 publishable `.csproj` files. Major stays `10` — the targeted
  platform (`net10.0`) has not moved.
- npm untouched: only comments changed on the client, so `@mintplayer/ng-spark@22.8.0` stands.
- `docs/release-notes-preview-68.md`.

## Verification

- `dotnet build` clean (0 errors).
- **Red/green pinned by reverting the fix.** With `: null,` restored, exactly the three facts that
  assert the new behaviour fail — `Single_child_projects_the_nested_object_as_the_cell_value`,
  `Single_child_serialises_to_the_shape_a_detail_page_sends`,
  `No_populated_column_carrying_a_renderer_projects_a_null_value` — and the other four pass, which is
  the point: breadcrumb, absent child and both array facts are invariants that must hold on *both*
  sides of the change, so a test of theirs that flipped would mean the fix moved something it
  shouldn't. Fix reapplied: 7/7.
- Targeted run: 7/7 new facts.
- Batched sweep over the related surface — projector, row shape, `AsDetail`, query executor, query
  endpoints: **72/72 passing**.
- Not verified against the live consumer app: production runs `.67`, so it can only reproduce the
  defect, not confirm the fix. The wire assertion in M2 stands in for it, and the app confirms after
  it bumps to `.68`.

## Follow-through

Consumer adoption is a package-reference bump in that app's repository once CI publishes `.68` on merge
— no code change there, since the renderer was always written against the documented shape.
