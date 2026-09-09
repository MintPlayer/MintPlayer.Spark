# Issue #384 — implementation plan

**PRD:** [issue_384_PRD.md](issue_384_PRD.md)
**Status:** M1–M3 done; S1, S2, M4 (app run) and M5 outstanding

Worked red/green: every behavioural change gets a test that **fails first for the stated reason**,
and the production edit is what turns it green. The gate has survived since June 2026 precisely
because the only tests near it were green either way — so "the test fails before the fix" is the
deliverable here, not a formality.

## Status

| | |
|---|---|
| **S1** does a stored `Person.Address` carry a key? | **not started** — spike, decides whether DemoApp is affected in practice |
| **S2** a shape that actually displays a keyed embedded breadcrumb | **not started** — spike, supplies the end-to-end verification R2 says we lack |
| **M1** RED — a keyed embedded row fails to render its template | **Done** — 5 facts in `EntityMapperKeyedAsDetailBreadcrumbTests`; 3 failed RED with `found "KeyedCredit"`, the CLR type name, which is the right reason |
| **M2** GREEN — drop the `IsNullOrEmpty(po.Id)` term | **Done** — one term, plus the comment that carried the false premise |
| **M3** RED/GREEN — the placeholder leaks through two client pipes | **Done** — and it was **three** leaks, not two; see below |
| **M4** verify against the workspace and production | **Partly** — build clean, 2033/2033 .NET, 443/443 vitest. App run + `--spark-verify-model` still outstanding |
| **M5** docs | **not started** |

### What M3 turned out to be

Two pipes were predicted; a third leak surfaced when the first two were fixed, and it was **caused
by fixing them**. `formatAsDetailValue` joins `Object.values(dict)`, and `nestedPoToDict` stashes the
row key and the row's own breadcrumb in that same dict. Previously unreachable-with-data: the only
route to the join was the server sending no breadcrumb, which is exactly when nothing is stashed.
Filtering the placeholder opened a route in with both reserved keys populated, so the fallback
printed `Voorbeeldstraat 1, 1000, Address` and a row guid.

Fixed by `isReservedAsDetailKey` in `as-detail-conversions.ts`, used by the join. Worth recording as
a shape rather than an incident: **a value that was safe only because its branch was unreachable.**
Two reviewers reading the join would have called it correct, because with the old control flow it
was.

### Housekeeping done alongside

Nine files carried two real personal addresses as fixture data — in specs, a production pipe
comment, three PRDs and a plan. Replaced throughout with obviously-fictional values
(`Voorbeeldstraat 1, 1000 Brussel`, `Voorbeeldlaan 2`). Checked for phone numbers too — the only
match is `+15551234`, a reserved fictional number, left alone. Not part of #384; it was in files
this work touched and does not belong in a public repository.

The real values are deliberately not quoted here: naming them to record their removal would put
them back into the repository in the document that reports the cleanup. `git log -p` has them if
anyone needs to verify the sweep.

## S1 — is DemoApp's `Address` actually keyed at rest?

**Question.** `Address` has `public string? Id { get; set; }`, documented as "assigned automatically
when it is saved". `Address` is *also* a root collection with its own actions and query, so a root
`Address` document certainly has an id. What is unknown is whether the **embedded** copy on
`Person.Address` carries a non-empty `Id` in stored JSON — nothing in `DemoApp.Library` or
`AddressActions` assigns one, and grep finds no `Id =` for it.

**Why it matters.** It decides whether the PRD's correction 1 ("`Address` has been broken since
#186") is true of real data or only of the type declaration. If the stored `Id` is null, `Address`
was never affected and correction 1 narrows to "any type that owns a populated `Id`".

**Method.** Query the DemoApp database directly (`dcg:ravendb` skill, or a `SparkTestDriver` test
that seeds a `Person` with an `Address` through the real save path and reads the raw JSON back).
Inspect the `Address` sub-document for an `Id` property.

**Output.** One paragraph appended here, and correction 1 in the PRD adjusted to match. **Not a
blocker** for M1/M2 — the fix and its test stand regardless.

## S2 — find a shape that displays a keyed embedded row's breadcrumb

**Question.** The PRD establishes that no shipped screen currently renders one (arrays discard it,
DemoApp's `Address` is bypassed by `address-card`, and every displayed single-AsDetail type is
keyless). So what shape *would* show it, and can we stand one up to verify the fix end to end?

**Why it matters.** This is R2. Without it, M1's unit test is the only evidence the fix works, and
the last three months are a demonstration of what happens when mapper behaviour is only ever checked
one layer away from where a user sees it.

**Method.** Construct the missing intersection — a **single, non-array** AsDetail attribute whose
type is a `[ValueObject]` with a `[ValueKey]` and a breadcrumb template, with **no** renderer, shown
on a detail page (`attribute-value.pipe.ts:29`) and in a query grid (`query-cell.pipe.ts:33`).
Cheapest home is a DemoApp or Fleet entity, added as a fixture rather than as a product feature.

**Decide before building.** If standing it up means inventing a demo entity that exists only to hold
a test, prefer an E2E test over the real API (`MintPlayer.Spark.E2E.Tests`) or a
`SparkTestDriver`-backed assertion on the projected `QueryResultItemValue.Breadcrumb` — one layer
below the pixel, but over the real save/load/project path. Record the choice and the reason here;
do not silently downgrade to a unit test and call R2 handled.

## M1 — RED: a keyed embedded row does not render its template

`tests/MintPlayer.Spark.Tests/EntityMapperAsDetailBreadcrumbTests.cs`, beside the existing
`Embedded_row_renders_its_own_breadcrumb_template_not_the_clr_type_name` — which is the test that
should have caught this and does not, because its `SongArtist` fixture is keyless
(`EntityMapperAsDetailBreadcrumbTests.cs:19`).

Add a **keyed** fixture. The registration idiom is `AsDetailRowIdentityRoundTripTests.cs:55-56`:

```csharp
static EntityMapperAsDetailBreadcrumbTests()
    => SparkValueObjects.Register(typeof(KeyedSongArtist), "Id", row => ((KeyedSongArtist)row).Id);
```

with `KeyedSongArtist` carrying `public string Id { get; set; } = "abc123";` alongside the same
`ArtistId` reference, an `EntityTypeDefinition` whose `Breadcrumb` is `"{ArtistId}"`, and a
`BreadcrumbResult` that contains the referenced artist ids but **not** the row key.

| Case | Expectation |
|---|---|
| Keyed embedded row, template `{ArtistId}` | renders the resolved artist name (**FR1**) |
| Keyed embedded row, scalar template `{Role}` | renders the scalar — proves it is not just reference resolution |
| Keyless embedded row (the existing test) | still renders (**FR2**) — regression guard on the fix itself |
| Root PO whose id **is** in the result | still takes the pre-resolved value, not a re-render (**FR3**) |
| Root PO with a denied reference | still shows `RedactedPlaceholder` (**FR3**, no redaction bypass) |

**Verify RED first, and read the failure.** The first two must fail with the row's `Breadcrumb`
equal to `"KeyedSongArtist"` — the CLR type name. A failure with any other message means the fixture
is wrong (most likely the key never registered, leaving `po.Id` null and the old path firing), and
that is a false red which would go green on its own. Record the observed failure message here.

## M2 — GREEN: drop the term

`libs/spark/MintPlayer.Spark/Services/EntityMapper.cs:218`

```diff
-if (string.IsNullOrWhiteSpace(breadcrumb) && breadcrumbs is not null && string.IsNullOrEmpty(po.Id))
+if (string.IsNullOrWhiteSpace(breadcrumb) && breadcrumbs is not null)
```

And rewrite the comment above it, which is the actual carrier of the defect — it asserts embedded
objects have no id, which is what made the term look correct to three subsequent readers. It should
say the gate is on **the lookup missing**: an embedded row is absent from `BreadcrumbResult` because
that map holds document ids, and a row key is not one.

**Verify:** M1's five cases go green with no other edit. If FR3's two cases move, stop — the fix has
reached root objects and the PRD's safety argument is wrong somewhere.

## M3 — RED/GREEN: the placeholder leaks through two client pipes

The adjacent defect (PRD, "An adjacent defect"). Live today, and unlike #384 it is reproducible
against shipped data with a **keyless** type.

**RED.** In `libs/node_packages/ng-spark/pipes/`, two vitest cases:

- `attribute-value.pipe` given a single AsDetail whose `object.breadcrumb` is the short type name
  → must not return it. Fails today: returns `"BuildFeedback"`.
- `query-cell.pipe` given a cell whose `breadcrumb` is the short type name → must not return it.
  Fails today: returns `"GateSettings"`.

Each needs the type name in scope to compare against — check what `attr.asDetailType` /
the column descriptor actually carries at each call site before writing the fixture, since
`selfBreadcrumb` takes short-or-full and returns null only when it can compare.

**GREEN.** Route both through `selfBreadcrumb` (`as-detail-conversions.ts:97`) rather than
reimplementing the comparison. Keep each pipe's existing fallback chain for the null case, so the
cell degrades to whatever it shows for an absent breadcrumb rather than to the placeholder.

**Guard:** a case per pipe asserting a *real* breadcrumb still passes through untouched. The failure
mode of a too-eager filter is a blank cell where a legitimate value happened to match a type name.

## M4 — verify against the workspace and production

- `dotnet build`, then the full .NET suite and the ng-spark vitest suite — **one sweep at the end**,
  after M1–M3 are all in.
- `--spark-verify-model` on `apps/CodeCoverage`, `apps/DemoApp`, `apps/Fleet`, `apps/HR` — no model
  change is intended here, so all four must still exit 0.
- Run the app that S2 chose and confirm the breadcrumb renders, plus one keyless single-AsDetail
  screen (`Build` detail, or HR `Person`) to confirm M3 turned a leaked `BuildFeedback` into an
  empty cell rather than into a new placeholder.
- Coverage baseline for comparison: **82.6%** on `4f9e9319` (24365/29482 lines, 691 files), read
  from coverage.mintplayer.com. The PR should not drop it; M1 and M3 add covered lines.

## M5 — docs

- `docs/guide-asdetail-attributes.md` — the invariant: an embedded row's breadcrumb comes from its
  own template because it is *absent from the resolved map*, not because it is unkeyed. That
  sentence is the one whose absence cost three months.
- A short note wherever the row-identity work from #382 is written up, recording that giving every
  `[ValueObject]` a key silently changed the meaning of an existing `IsNullOrEmpty(po.Id)` test —
  the class of breakage worth naming, since #382 also rewrote the three lines directly above it.
- `docs/issue_384_PRD.md` — fold in S1's answer and S2's chosen verification.

## Deliberately not doing

- **Suppressing the placeholder server-side** (PRD, Design). One-point fix, four-path blast radius,
  and it would invert the client contract `selfBreadcrumb` is written against.
- **Closing or reviving PR #381.** Out of scope per the PRD; `10f2a1cc`'s other half is already on
  master, so nothing there is load-bearing for this fix.
- **Splitting M3 into its own PR.** Same subject — the placeholder escaping to a user — and the
  repository lands related fixes together.
