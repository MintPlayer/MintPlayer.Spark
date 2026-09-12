# Summary — `DateTimeOffset` fidelity and sort companions

**Status: implemented and verified in a browser** on `fix/datetimeoffset-fidelity` —
[PR #403](https://github.com/MintPlayer/MintPlayer.Spark/pull/403), 11 commits, open.
[PRD](raven_datetimeoffset_and_sort_companions_PRD.md) · [plan](raven_datetimeoffset_and_sort_companions_plan.md) ·
developer-facing: [guide](guide-dates-and-sorting.md). Everything below is measured, and every value
shown is a real observation from RavenDB 7.2.6 with the Fleet demo's 10,010 cars.

### Done

| | |
|---|---|
| **Read path** | `{Name}Raw` wrapper emitted automatically for every `DateTimeOffset`; `ProjectedOffsetRestorer` reads it back in `RowSecurityGate.ApplyAsync` |
| **Write path** | `DateTimeOffset` branch in `EntityMapper.SetPropertyValue` — editing one used to do nothing while reporting success |
| **Companions corrected** | `{Name}Sort` and `FieldIndexing.Exact` removed from `DateTimeOffset`; `[Search]` strings keep theirs; nothing else ever needed one |
| **Display consistency** | Detail page formats `datetime` through the new `parsedDate` pipe instead of printing a raw ISO string; grid and detail now agree |
| **Analyzer** | `SPARK005` left matching `Exact` (deliberately — see the plan); guard test pins CodeCoverage's index shape |
| **Demo** | `apps/Fleet` — `Car.RegisteredAt`, a scatter button, a paginated sorted grid, a three-line renderer |
| **Docs** | this file, the PRD, the plan, `guide-dates-and-sorting.md`, and a correction to `guide-queries-and-sorting.md` |
| **Versions** | 23 NuGet packages → `10.0.0-preview.81`; `ng-spark` → `22.18.0` |

Suites: `MintPlayer.Spark.Tests` 2143/2143 · `CodeCoverage.Tests` 438/438 · `SourceGenerators` 278/278 ·
`Client` 38/38 · `ng-spark` 459/459.

### Not done

| | |
|---|---|
| **The client write contract** | `<input type="datetime-local">` sends no offset, so editing a timestamp in the UI lands as UTC. The server half is done; `ng-spark` does not yet carry the original offset through the editor. **Blocked on a product question, not on effort** — see [§9](#9-what-is-still-open). |
| **Upstream + sideways** | A comment on [ravendb#17901](https://github.com/ravendb/ravendb/issues/17901), and handing the originating team the Defect C finding. Both outward-facing; the issue owner's to send. |

---

## The one-paragraph version

A `DateTimeOffset` is stored correctly in the collection. It is **not** stored correctly in the index:
RavenDB converts it to its UTC-equivalent `DateTime` the moment the value becomes a *scalar index field*.
So `session.Query<TIndexEntity, TIndex>().ProjectInto<T>()` hands back a different value from
`session.Load<TEntity>(id)` for the same document — same instant, wrong offset, wrong wall clock. The fix
carries the value through the index a second time inside a **nested object**, which RavenDB stores opaquely
and never touches, and reads it back from there. Separately, `DateTimeOffset` *writes* had never worked at
all.

---

## 1. The collection stores it correctly

```csharp
public class Car
{
    public DateTimeOffset RegisteredAt { get; set; }   // no attribute needed
}
```

```bash
curl ".../databases/SparkFleet/queries?query=from Cars where LicensePlate='2-TAN-135' select RegisteredAt"
```
```json
"RegisteredAt": "2026-12-31T04:15:00.0000000-08:00"
```

✅ Offset intact. Nothing was ever wrong here, and `session.Load<Car>(id)` returns exactly this.

## 2. The index does **not**

This is the part that surprises people, so it is worth being exact: it is not the client that flattens the
value, and it is not the projection call. The value is already wrong in the server's response, over raw
HTTP, with no .NET client involved:

```bash
curl ".../queries?query=from index 'Cars/Overview' where LicensePlate='2-TAN-135' select RegisteredAt, RegisteredAtRaw"
```
```json
"RegisteredAt":    "2026-12-31T12:15:00.0000000Z"           ← ❌ flattened by the server
"RegisteredAtRaw": { "V": "2026-12-31T04:15:00.0000000-08:00" }  ← ✅ the wrapper this fix adds
```

`04:15 -08:00` and `12:15 Z` are the **same instant**. Only the offset and the wall clock are lost — which
is precisely why nothing failed loudly: ordering, filtering, range queries and row counts all stayed
correct, and 2120 tests passed straight over it for years.

**Where it happens:** at index time, when the value becomes a *scalar* index field. Not at query time, not
on serialization. That is why nothing on the read side can undo it — the information is already gone.
Upstream: [ravendb#17901](https://github.com/ravendb/ravendb/issues/17901), open since 2023-12, both Corax
and Lucene, no fix in any 7.1.x or 7.2.x changelog.

## 3. So the two read paths disagree

```csharp
// ✅ correct — reads the document
var loaded = await session.LoadAsync<Car>("cars/1-A");
loaded.RegisteredAt;           // 2026-12-31T04:15:00-08:00

// ❌ wrong — reads the index's stored fields
var projected = await session.Query<VCar, Cars_Overview>()
    .ProjectInto<VCar>()
    .FirstAsync();
projected.RegisteredAt;        // 2026-12-31T12:15:00+00:00
```

Two values for one document, differing by eight hours of wall clock and an offset. And because `Spark`
answers grids from indexes and detail pages from documents, that discrepancy was **visible in the app** —
a grid and a detail page naming different days for the same car.

> ⚠️ **`==` will not catch this.** `DateTimeOffset.Equals` compares the *instant*, so
> `projected.RegisteredAt == loaded.RegisteredAt` is `true` while the offsets differ. Assert on `.Offset`
> or use `EqualsExact`. The first draft of the regression tests for this fix passed against the broken
> value for exactly this reason.

## 4. The fix: carry it through the index in a nested object

A value **nested inside a complex object** is stored by RavenDB as an opaque sub-document and is never
decomposed into a typed field — so nothing converts it. That, not the indexing mode, is the lever.
(A scalar field loses its offset even at `FieldIndexing.No`; a nested one keeps it even at default
indexing.)

The generator emits this automatically for every `DateTimeOffset` — the developer writes nothing:

```csharp
public sealed class SparkIndexValue<T> { public T V { get; set; } = default!; }   // shipped in Abstractions

// generated into the index:
RegisteredAt    = car.RegisteredAt,                                              // ordering, filtering
RegisteredAtRaw = new SparkIndexValue<DateTimeOffset> { V = car.RegisteredAt },  // fidelity
Index(nameof(VCar.RegisteredAtRaw), FieldIndexing.No);
StoreAllFields(FieldStorage.Yes);
```

`ProjectedOffsetRestorer` then overwrites the flattened scalar from the wrapper, in
`RowSecurityGate.ApplyAsync` — the single choke point every row-returning path passes through, and one
`session.Load` never reaches. It is an assignment, not arithmetic: the wrapper holds the original whole, so
applying it twice changes nothing.

> ⚠️ `FieldIndexing.No` on the wrapper is **mandatory**. Without it Corax deploys the index cleanly and
> then parks it at `state=Error, entries=0`, so every query returns nothing. Lucene is unaffected — which
> is what makes it easy to miss, since both CI and production run Corax.

### Why not `ProjectionBehavior.FromDocument`?

It works — measured, it returns `04:15 -08:00`:

```csharp
.Customize(c => c.Projection(ProjectionBehavior.FromDocument))
```

And it is rejected anyway, on cost rather than correctness. It recovers the offset by making the server
stop answering from the index's stored fields and **read each matching document from storage instead** —
a per-row document read on exactly the path Spark uses for paging, undoing the whole reason
`StoreAllFields` is emitted. The wrapper buys the same fidelity for one extra stored field and no extra
reads. Pinned by a test so the trade-off is not re-litigated from memory.

### Why not a `{Name}Sort` companion, or `FieldIndexing.Exact`?

Both were there before this change, and both were measured to do nothing:

- A same-typed `{Name}Sort` copy orders **byte-identically** to the field it copies — *and* is flattened
  identically, so it could not have carried the offset either.
- `Exact` on a `DateTimeOffset`: 62 paired queries across both engines, identical index terms and identical
  equality / `in` / range / ordering. RavenDB reduces the value to a canonical UTC instant before any
  analyzer sees it, so `Exact` had nothing to act on.

Both removed. `[Search]` strings keep their sort companion — that one is measured necessary. `DateTime`,
numerics, `Guid`, `bool` and enums get nothing, and never needed anything.

## 5. `DateTime` is not affected

Measured, not assumed: a plain `DateTime` survives the same projection with its **ticks and its `Kind`**
intact. It has no offset to lose. So `session.Query<TIndexEntity, TIndex>()` mangles only
`DateTimeOffset`, and only the offset component.

(`DateTimeKind.Local` does not round-trip — it returns as `Unspecified` — but that is the JSON wire format,
true on `session.Load` too, and unrelated to indexes.)

---

## 6. What reaches the browser, and what the browser does with it

**The wire always carries the collection's value.** After this fix, both read paths send the same thing the
document holds:

```json
{ "key": "RegisteredAt", "value": "2026-12-31T04:15:00.0000000-08:00" }
```

**The Angular app then projects that into the viewer's timezone.** Both the grid and the detail page parse
it with `new Date(...)` — which collapses it to an instant — and format it with Angular's `DatePipe` with
no timezone argument, which renders in the browser's own zone:

```
wire:                    2026-12-31T04:15:00-08:00
browser (Europe/Brussels):   31/12/2026, 13:15
```

That is deliberate: a timestamp is shown to a reader as *the moment, in their time*. The offset stays in
the data for code that needs the originating wall clock — server-side logic reading `.Offset`, an export,
an audit trail — it simply is not what a viewer is shown.

⚠️ **So the displayed value is not the stored wall clock, and never was.** Three distinct things:

| | example |
|---|---|
| the **instant** | `2026-12-31T12:15:00Z` — always correct, never broken |
| what the **viewer sees** | `31/12/2026, 13:15` — the instant, in their zone, *on that date* |
| the **stored** wall clock + offset | `2026-12-31T04:15:00-08:00` — what this fix restores |

A value and a viewer can sit in the same country and still disagree, because the zone's offset **at the
value's own date** is what applies — a value stored `10:00+02:00` displays as `09:00` in Brussels on 9
March, when Brussels is on CET.

### The detail page used to disagree with the grid

Until this change a `datetime` attribute on a detail page had no formatting step at all and printed the
wire value verbatim — `2026-12-31T04:15:00-08:00` in a definition list — while the grid showed
`31/12/2026, 13:15` for the same document. Not cosmetic: when the offsets differ they can disagree on the
**date**. Both now parse through one `parsedDate` pipe and format identically.

The `offset-datetime` renderer in `apps/Fleet` is the deliberate exception: it prints the raw wall clock
and an offset badge so a reader can *see* the data survived the round trip. It is a demonstration device,
not a pattern to copy.

---

## 7. The other defect: writes were silently dropped

`EntityMapper.SetPropertyValue` had no `DateTimeOffset` branch, so a wire string fell through to
`Convert.ChangeType`, which throws `InvalidCastException` — `DateTimeOffset` does not implement
`IConvertible`, while `DateTime` does — and a bare `catch` swallowed it:

```csharp
catch { /* Skip properties that can't be converted */ }
```

**Editing a `DateTimeOffset` did nothing, while the save reported success.** Clearing a nullable one always
worked, because the null branch returns before the `try` — which is why the asymmetry went unnoticed.

Now parsed with `DateTimeStyles.AssumeUniversal` + `InvariantCulture`. Both that and `RoundtripKind`
preserve an offset the client sent, but an offset-less string is read as *local* time under
`RoundtripKind`, which would stamp the server's offset onto the value. An absent offset means UTC.
**Never `AdjustToUniversal`** — measured to flatten every offset to `+00:00`.

---

## 8. Deployment

**Every generated index containing a `DateTimeOffset` or a `[Search]` string changes shape, so RavenDB
rebuilds it from scratch on deploy.** Indexes with neither are untouched. During the rebuild window the
runtime reads a missing wrapper as "no information" and returns the un-restored value rather than throwing
— not a compatibility concession, but a live transient on every deploy.

`apps/CodeCoverage` is production and **is not corrupted**: `Commit` is the only entity there with a
`DateTimeOffset` and the only one without a generated index, so the `StoreAllFields` trigger never meets
the type. No migration. A guard test pins it, because that safety was one missing line rather than a design
decision.

All 23 NuGet packages bumped in lockstep to `10.0.0-preview.81`; `ng-spark` to `22.18.0`.

---

## 9. What is still open

**The client write contract**, and it is stuck on a product question rather than on work.

`<input type="datetime-local">` knows only a wall clock — it has no offset to send. The server already
parses whatever offset it is given and treats an absent one as UTC, so a save now persists (it silently
did nothing before). What `ng-spark` does not yet do is carry the value's *original* offset through the
editor and reattach it on submit.

**The semantics are now decided** (plan → *Timestamp semantics*), and they answer what used to be the open
question:

> **A `DateTimeOffset` in Spark means an instant. The originating offset is not business data.**

So an edited value takes the **viewer's** zone — there is no "preserve the record's original offset" case,
because there is no original-offset semantics to preserve. An app that genuinely needs "which country's
morning was this?" must model that itself.

What remains is implementation: **the browser computes the offset** for the entered date and sends a
complete ISO-8601 string; the server parses it and never infers one (already true as of this PR). The
browser rather than the server because its timezone rules are OS-maintained and current, while a
container's are frozen at build time — a stale image would compute a wrong offset after a rule change and
store a permanently wrong instant, silently.

A separate `X-Spark-Timezone` header is decided in shape but not built, and is **only** for work with no
browser in the loop — a server-side "today" filter, an export, a cron job. Reads and writes do not need it.

Until the client half lands, the Fleet demo seeds values server-side via the button rather than the editor.

---

## 10. Traps worth carrying forward

- **`DateTimeOffset.Equals` compares the instant.** Assert `.Offset` or `EqualsExact`, never `==`.
- **Nesting preserves; scalar index fields normalise.** `FieldIndexing.No` does not help. Neither does
  storing it as a string — RavenDB normalises date-shaped strings too.
- **`FieldIndexing.No` on a wrapper is mandatory on Corax**, and its absence fails *after* a clean deploy.
- **The client's query cache does not appear to key on `ProjectionBehavior`.** Running a `FromDocument`
  query first makes a subsequent default query return the cached — correct-looking — response. Use
  `NoCaching()` when comparing the two, or the measurement lies.
- **A bad index map fails late.** `PutIndexesOperation` succeeds and the index then sits at
  `state=Error, entries=0`. Map members bind at runtime, so no build validates them.
