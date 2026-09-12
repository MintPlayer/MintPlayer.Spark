# PRD — `DateTimeOffset` loses its offset through index projections, and the `*Sort` companion convention is mostly cargo cult

**Status:** **IMPLEMENTED and verified in a browser.** Twelve agents across four rounds. Every claim below
is **measured on live servers** or cited upstream. Nothing rests on folklore.
**Branch:** `fix/datetimeoffset-fidelity` (18 commits, pushed) · **PR:** [#403](https://github.com/MintPlayer/MintPlayer.Spark/pull/403), open.
**Issues:** none — implemented directly; the PR references this PRD.
For what is done vs. still open, see the [plan](raven_datetimeoffset_and_sort_companions_plan.md) or the
[summary](raven_datetimeoffset_and_sort_companions_summary.md).

> **The framework's timestamp semantics were settled while this shipped** and are recorded in the plan
> under *Timestamp semantics*: a `DateTimeOffset` in Spark means an **instant**, the originating offset is
> not business data, the **browser** converts in both directions, and a viewer-zone request header exists
> only for server-initiated work. Read that before extending anything date-shaped — several decisions here
> only make sense against it.

---

## Executive summary

Commissioned on two beliefs. Measurement refuted one, confirmed the other but relocated its cause,
and turned up two further defects nobody was looking for.

| # | Belief / finding | Verdict |
|---|---|---|
| 1 | Ordering is wrong unless every sortable property has a `{Name}Sort` companion | **Refuted** except for `FieldIndexing.Search` strings |
| 2 | RavenDB 7.2.6 breaks the workaround | **Refuted** — 7.1.12 and 7.2.6 bit-identical across 151 assertions |
| 3 | `DateTimeOffset` comes back shifted ~2h | **Confirmed**, cause relocated — index-time UTC normalisation, always present |
| 4 | Vidyano ships a fix for the shift | **Refuted** — no `DateTimeOffset` handling anywhere in it |
| 5 | *(new)* `DateTimeOffset` **writes are silently dropped** | **Confirmed by code reading** — a separate, live defect |
| 6 | *(new)* A date-shaped **`string`** property is normalised too | **Confirmed** — destroys the obvious workaround |

There is **no regression to roll back and no version to avoid**. There are two long-standing
data-fidelity defects Spark is fully exposed to, and a convention that costs index size and
indexing throughput while buying nothing outside one narrow case.

---

## Measurement basis

Round 1 — matrix {server 7.1.12, 7.2.6} × {Corax, Lucene} × {client 7.1.12, 7.2.5, 7.2.6} = 12 cells,
151 assertions each. Harness `scratchpad/raven-repro/`.
Round 2 — encoding/carrier matrix on 7.2.6, both engines, `PASS=146 FAIL=57` identically.
Harness `scratchpad/dto-encoding/`.

**Every round-1 cell produced PASS=119, FAIL=32.** The only difference across 12 logs is *which*
wrong order the analyzed string returns (Corax and Lucene disagree on their wrongness) — which also
proves the per-database engine setting took effect.

---

## Defect A — `DateTimeOffset` loses its offset through an index projection

### The mechanism is NESTING, not indexing

Layer isolation (round 1): the document JSON is correct (`10:00:00+02:00`), the index terms are
mutated (`08:00:00Z`), and a raw `GET /queries` already returns `08:00Z` — **no client is involved**,
so no serializer or converter fix is possible.

Round 2 tested the natural hypothesis that `FieldIndexing.No` (which Spark already applies to complex
fields since #273) would prevent it. **It does not:**

| Shape | Offset preserved? |
|---|---|
| scalar `DateTimeOffset`, `FieldIndexing.No` + stored | **NO** — terms empty, projection still `Z` |
| `DateTimeOffset` **nested inside a complex object**, stored | **YES**, both engines |
| same, complex object at *default* indexing (Lucene) | **YES** |

**A complex/nested object is stored as an opaque blittable sub-document and never decomposed into a
typed index field, so its inner date strings survive verbatim. Anything that becomes a scalar index
field is converted to its UTC-equivalent `DateTime` — whether it is indexed or not.**
`FieldIndexing.No` is only needed to stop Corax faulting; it contributes nothing to fidelity.

### The "2 hours" is an artefact of the data

The shift equals **each document's own offset**: `+05:30` → −5h30, `-08:00` → **+8h later**,
`TimeSpan.Zero` round-trips perfectly. Nothing reads `TimeZoneInfo.Local`. `UtcTicks` is always
preserved — only the offset dies and the wall clock is re-expressed in UTC. Everything that
compares, filters, orders or converts to UTC stays correct; only wall-clock reads (`.DateTime`,
`.Hour`, `.ToString()`) and `.Offset` are wrong. **That is why it hides.**

### Trigger

Any **`FieldStorage.Yes` on a scalar `DateTimeOffset` index field**, read back through a projection.
Measured: `StoreAllFields`, `Store(x => x.Single, ...)` and `Store(nameof(...), ...)` all trigger it
identically. An index with **no** stored fields projects perfectly, because the server falls back to
the document.

Correct paths, measured: `session.Load`, full-document index queries, and projections from an index
with no stored fields.

### Spark is fully exposed

`StoreAllFields(FieldStorage.Yes)` is unconditional in every generated index
(`GenerateIndexGenerator.Producer.cs:281`), and the query path calls `ProjectInto<T>()`. It reaches
production at coverage.mintplayer.com (`Commit.AuthoredAt`, `FirstSeenAtUtc`, `Date`).

Undetected because **no test anywhere asserts a `DateTimeOffset` value read back** — a 2-hour shift
passes the entire suite today.

### Collections — the feared gap mostly does not exist

| Shape | Preserved? | Needs a carrier? |
|---|---|---|
| `DateTimeOffset` on an embedded child collection (`[ValueObject]`) | **YES** | **no** — already correct |
| `DateTimeOffset` in a complex/JSON object indexed whole | **YES** | **no** |
| `DateTimeOffset[]` / `IList<DateTimeOffset>` direct property | NO | yes |
| fan-out (`SelectMany`) projecting a child `DateTimeOffset` | NO — becomes a scalar field | yes |

Because Spark already stores complex properties as nested objects, **the `[ValueObject]` child-collection
case the first design flagged as its unsolved gap is already correct.** Emitting a companion there
would be pure index churn.

---

## Defect B — the `{Name}Sort` convention is unnecessary outside `FieldIndexing.Search`

Identical values, one field plain and one `{X}Sort`:

```
IDENTICAL  OrderBy(Mileage)       vs OrderBy(MileageSort)      both -> [6,4,1,3,7,2,8,5]
IDENTICAL  OrderBy(Appointment)   vs OrderBy(AppointmentSort)  both -> [7,2,1,3,4,5,6,8]
IDENTICAL  OrderBy(Starts)        vs OrderBy(StartsSort)       both -> [7,2,1,3,4,5,6,8]
DIFFERENT  OrderBy(Name) [Search] vs OrderBy(NameSort)         wrong vs correct
```

It is the **analyzer**, not the name: `PlainIdx.Name` — same name, no `Index(...)` call — sorts
correctly. `NameSort` works only because nobody called `Index(..., FieldIndexing.Search)` on it.
Dates survive a bare `order by` because ISO-8601 terms are lexically monotonic.

**Corroboration:** Vidyano's generator emits a companion for exactly `[Search]` strings and
`DateTimeOffset` — plain `DateTime`/`int`/`decimal` get none. Spark's `SparkModelSymbols.cs:70-77`
documents the same narrow rule from a 15/15-vs-22/22 reference corpus. **The blanket "always write a
`*Sort` property" habit in the originating codebase is wider than either framework and is cargo cult.**

**The `DateTimeOffset` sort companion is doubly pointless** — it neither improves ordering (identical)
nor protects the value (both shift), and it is typed the same as the base field so it *cannot* carry
the offset.

---

## Defect C — the real origin of the folklore

`DocumentQuery.OrderBy("Mileage")` — the **string-named** overload — defaults to
`OrderingType.String` and returns `10, 100, 1000, 2, 20, 200, 5, 9`
([ravendb#15631](https://github.com/ravendb/ravendb/issues/15631)). Fix is
`OrderBy(name, OrderingType.Long)`. **Spark is immune** — `ApplySorting` builds a typed lambda, so
the client writes `as long`/`as double` into the RQL. Pass to the originating team; it is their actual bug and
no companion fixes it.

---

## Defect D — `DateTimeOffset` writes are silently dropped *(new, live)*

`EntityMapper.SetPropertyValue` (`EntityMapper.cs:1108-1141`) branches on `string`, `Guid`,
`DateTime`, `DateOnly`, `Color` and enums — **no `DateTimeOffset`**. A wire value arrives as a string,
falls through to `Convert.ChangeType(value, targetType)`, which throws `InvalidCastException`
(`DateTimeOffset` is not reachable through `IConvertible`), and the exception is swallowed:

```csharp
catch { /* Skip properties that can't be converted */ }
```

**Editing a `DateTimeOffset` through the save pipeline silently does nothing today** — no error, no
400, the save reports success.

Grim symmetry: the read-side corruption **cannot currently be persisted, only because the write never
happens**. Fixing writes without fixing reads would let an inline grid edit over a projected row start
persisting the wrong offset. Both must land together.

Separately, the Angular client binds `<input type="datetime-local">`, which has no offset. Parsing an
offset-less string yields the *server's* offset, so round-tripping an unedited form would rewrite a
`+02:00` value. That is a contract decision, not a `SetPropertyValue` fix.

---

## Design

### Carrier: a nested wrapper field

RavenDB will normalise any scalar `DateTimeOffset` index field; that is not negotiable and upstream
has not fixed it in two years ([ravendb#17901](https://github.com/ravendb/ravendb/issues/17901)). But
**nested objects are stored opaquely and survive intact**. So the carrier is a nested wrapper emitted
into the map:

```csharp
StartsRaw = new { V = e.Starts },          // + Index("StartsRaw", FieldIndexing.No)
```

Measured: `"StartsRaw":{"V":"2026-03-09T10:00:00.0000000+02:00"}` — and for a collection,
`{"V":["…+02:00","…+00:00","…-08:00","…+05:45"]}`, **the whole collection with per-element offsets, in
order, in one field**. Null stays null.

### The wrapper must be a TYPED box, and that is provably equivalent to the anonymous one

A generated index projects into a declared class (`select new VCar { ... }`), and **an anonymous type
cannot be a property type** — so the generator must emit a real type. That turns out to be free:

**RavenDB's expression-to-string converter erases every type name before the index reaches the server.**
Verbatim from `GET /indexes?name=TypedIdx`:

```
docs.Cars.Select(car => new {
    WAnon    = new { V = car.Starts },   // anonymous
    WClass   = new { V = car.Starts },   // was: new BoxClass { ... }
    WGeneric = new { V = car.Starts },   // was: new SparkIndexValue<DateTimeOffset> { ... }
    WExternal= new { V = car.Starts },   // was: new ExternalBox { ... }, OTHER ASSEMBLY
})
```

The typed and anonymous cases are **the same bytes on the server**, so the known-good baseline transfers
by identity, not analogy. Class, struct, generic and cross-assembly all behave identically — **the
declaring assembly is erased at C# compile time**, so a single shipped generic type is safe.

Measured across all variants, on Corax and Lucene, raw wire **and** materialised CLR (`PASS=41 FAIL=3`;
the 3 are the deliberate bare-scalar control): **no discrepancy anywhere.** The Newtonsoft fear did not
materialise — a nested object deserialises from its ISO string with the offset intact. Arity is
irrelevant, nullable inner values round-trip as null, arrays and `IList<>` keep order and per-element
offsets.

**No type metadata is emitted.** Scanning stored rows for `$type`, `@Raven-Clr-Type`, `__type` and every
box type name: all absent. Stored bytes are exactly `{"V":"…"}`. **Renaming the box changes nothing in
the index**, and `TypeNameHandling` is not in play.

`[IgnoreProperty]` is inert at the Raven level — the index entity is never serialised by Raven, it is
compiled to JS. Note the asymmetry: on a *source entity* property it excludes the property from the index
entirely; on an *index entity* property it only removes it from the model.

### ⚠️ `Index(x => x.XRaw, FieldIndexing.No)` is MANDATORY per wrapper, not decorative

Measured without it, on **Corax**: the index deploys successfully and then sits at

```
entries=0  mapAttempts=3  mapErrors=3  state=Error  isInvalid=True
NotSupportedInCoraxException: The value of 'WAnon' field is a complex object…
The field is supposed to have 'Indexing' option set to 'No'
```

On Lucene the same index is `state=Normal`. A Corax-only, silent-at-deploy, **total index failure**.
This is the single most dangerous thing to get wrong in the implementation.

**Why this beats the `{Name}OffsetMinutes` companion** (the round-1 proposal): one mechanism covers
scalars *and* collections; no reassembly; no parallel-array order dependence; and **no
null-companion hazard** — a missing `int?` companion silently reads as `+00:00`, which is
indistinguishable from a genuine UTC value. The wrapper is either present and whole, or absent.

The wrapper is **additive, never a replacement**: a `FieldIndexing.No` field cannot be filtered
(on Corax a range predicate returns **HTTP 500 `NullReferenceException`** from
`TermNumericRangeProvider..ctor` — a server-side crash) and its ordering is undefined. The real typed
field stays for ordering and filtering, where the UTC-normalised instant is *chronologically correct*.

### Emit the wrapper only where it is needed

- scalar `DateTimeOffset` / `DateTimeOffset?` property → **wrapper**
- `DateTimeOffset[]` / `IList<DateTimeOffset>` → **wrapper**
- `DateTimeOffset` inside a `[ValueObject]` child collection or complex property → **nothing**, already correct
- fan-out (`SelectMany`) projecting a child `DateTimeOffset` → **wrapper**
- `DateTime`, numerics, `Guid`, `bool`, enums → nothing

And: **drop** `{Name}Sort` for `DateTimeOffset` (measured useless); **keep** it for `[Search]` strings.

### Why the hand-written line is irreducible — measured, not assumed

The obvious simplification is to have the generator emit a helper the map calls, so the developer stops
repeating their own expression. **Static methods in a map do work** — but only in the form that saves
nothing, and the form that would have collapsed N properties into one call is rejected by the server.

**Type names are erased only for object-creation expressions.** `new SparkIndexValue<T> { V = x }` deploys as
`new { V = x }`, but a *method call* keeps its receiver type and arguments verbatim:

```
AuthoredAtRaw = IdxHelper.MakeRaw((((DateTimeOffset)(c.AuthoredAt ?? c.FirstSeenAtUtc))))
```

With the helper shipped via `AdditionalSources` this **works completely**: `state=Normal`, offsets
byte-identical to the inline baseline on the wire and in the CLR, and it survives `SelectMany` fan-out,
multi-map, and map-reduce (in both map and reduce). Generics, reflection and multi-file sources all work.

**But the whole-object form is impossible.** `select Result.Complete(new Result { ... })` fails at PUT:

```
Could not extract any fields from 'foreach(var c in docs){ yield return IdxHelper.CompleteDict(new {...}); }'
 ---> System.InvalidOperationException: Could not extract any fields
   at Raven.Server.Documents.Indexes.Static.Roslyn.FieldNamesValidator.Validate(...)
```

`FieldNamesValidator` reads the index's field list **syntactically** from the anonymous-object literal in
the `select`. Any other select body — a method call, a `Dictionary`, an `ExpandoObject` — yields zero
fields and is rejected. This is structural, not a missing feature.

⇒ **Rejected.** The per-field call (`IdxHelper.MakeRaw(expr)`) is the same amount of typing as
`new SparkIndexValue<DateTimeOffset?> { V = expr }` and adds: a second invisible coupling (the index compiles only if
`AdditionalSources` carries helper text the generator wrote, and the client-side stand-in is a *different
declaration* from the one that runs); helper source embedded verbatim in **every** index definition that
ships it, so reformatting it would change every such index and force a fleet-wide re-index; and faults
that surface as server-side Roslyn messages instead of client compile errors.

Failure modes are at least loud — every mistake throws `IndexCompilationException` at PUT and the index is
not created. Deploy latency is unchanged (35 ms → 42 ms).

**Decision (issue owner): do not use `AdditionalSources`** unless the shipped source is completely generic
and fully embedded in the framework. That bar cannot pay off here even when met. A single fixed,
framework-owned helper would remove the per-consumer variation — but it would **not** remove the two costs
that matter: the helper text is still embedded verbatim in every index definition that ships it (so any
edit to it in a Spark release changes every such index and forces a fleet-wide re-index), and the map is
still bound to a declaration that exists only as a string on the server. And the benefit remains zero,
because `FieldNamesValidator` blocks the collapsing shape regardless of how generic the helper is. Generic
embedding removes a downside; it cannot create the upside. **`AdditionalSources` is out of scope for this
work**, recorded here so it is not re-proposed.

### ⚠️ New trap found here: a constructor-assigned index-entity field vanishes SILENTLY

`new CtorResult { ... }` where the constructor sets a field deploys as `new { }` — the field is simply
**absent** from the map, the wire JSON has no such key, and the index sits at `state=Normal, entries=3`.
No error anywhere. Constructors are unusable in index entities, and unlike every other mistake in this
area, getting it wrong is silent.

### Runtime hook: `RowSecurityGate.ApplyAsync`

Between `kept` (`RowSecurityGate.cs:188-193`) and `mapped` (`:199`). It is the single architectural
choke point — `SecuredRows` has a private constructor and every row-carrying shape demands one, so a
future row path cannot bypass it without a signature change. It covers the index path, custom/composed
queries and streaming, and **never sees a `session.Load` row**.

Rejected: hooking at the `ProjectInto` sites (misses an author's own projection inside a custom query);
`EntityMapper` (fixes `attribute.Value` but leaves the CLR row lying to everything else);
`QueryResultProjector` (structurally impossible — no row instance by then).

### Double application is structurally impossible

Restoration is **re-representation, never arithmetic** — `value.ToOffset(...)` preserves `UtcTicks` by
definition, so it is idempotent and a no-op on an already-correct value.

> **Never** implement this as `AddMinutes`/`AddHours`, or as `new DateTimeOffset(value.DateTime, offset)`.
> Those reinterpret the wall clock and *are* accumulative. Pin it with a test that applies the restorer
> twice and asserts equality.

With a wrapper carrier the value is taken whole, so even re-labelling arithmetic is absent.

### Rejected carriers — measured, not argued

| Option | Why rejected |
|---|---|
| Serializer convention | Impossible — the offset never reaches the wire. |
| `ProjectionBehavior` (all 5 values) | **No effect whatsoever.** `FromDocument` does not override a stored index field: `scalarShifted=4, collectionWrong=3` identically for `Default`, `FromIndex`, `FromIndexOrThrow`, `FromDocument`, `FromDocumentOrThrow`. |
| ISO `"O"` string on the **document** | **Destroyed.** RavenDB normalises a date-shaped *string* too — `"2026-03-09T10:00+02:00"` returns as `Z`. Only survives with a non-date prefix (`"dto:…"`). |
| Index-computed `ToString("O")` string field | Works (stored value intact), but scalar-only; a collection needs a parallel string array. Second choice. |
| Typed field + `int` offset companion | Works and is exactly lossless, but needs reassembly, a parallel array for collections, and carries the null-companion hazard. Third choice. |
| Drop `StoreAllFields` | Correct but costs a document load per row and breaks index-only/computed fields. |

### On `TotalMinutes` vs `TotalSeconds` *(resolved, now moot)*

`TotalMinutes` as `int` is **exactly lossless** and `TotalSeconds` is **strictly redundant**. .NET
rejects any offset that is not a whole number of minutes (`ArgumentException: Offset must be specified
in whole minutes`) and any outside ±14:00 (inclusive bounds); `ToOffset` and `Parse` enforce the same.
141 system time zones × 7 dates spanning 1850–2026 produced **zero** sub-minute offsets — Windows TZ
data is minute-granular. Range −840…840, so `short` suffices. Moot under the wrapper design, which
carries the value whole.

---

## Spikes — all three RESOLVED

### S1 — `FieldIndexing.Exact` on `DateTimeOffset`: **DROP it**

31 paired queries × 2 engines = 62 measurements, **zero behavioural differences** between
`Index(x => x.Starts, FieldIndexing.Exact)` and no `Index(...)` call at all. Equality, `in`, range,
ordering and null handling are identical on Corax and Lucene under 7.2.6.

**The decisive measurement:** the index term is the *same UTC-normalised string* in both modes —
`2026-03-01T08:00:00.0000000Z` for a document storing `2026-03-01T10:00:00.0000000+02:00`. **RavenDB
reduces the value to a canonical instant before any analyzer would see it, so `Exact` has nothing to
act on.** A date field is not analyzed in either mode; the only difference is the index *definition*
(`Fields.Starts.Indexing` present vs absent).

**The spike's premise was false.** 7.2.6 does match `DateTimeOffset` by instant for equality, `in` and
range — including shortened fractional seconds and a different UTC spelling — but that is **not**
something `Exact` supplies. Both modes do it.

Related, easy to mistake for an `Exact` effect: RavenDB **de-duplicates `DateTimeOffset` terms by
instant**, so two documents with the same instant in different offsets share one term and a terms dump
has fewer entries than documents.

⇒ Remove the `isDateTimeOffset ? "Exact"` arm from `GenerateIndexGenerator.cs:379`. It changes the
index definition and therefore forces a rebuild, so it **must ship in the same release** as the
`{Name}Sort` removal — deferring buys only a second rebuild.

### S2 — mixed `DateTimeKind`: **NO inversion. Ship no `DateTime` companion.**

The lexical argument is wrong. **RavenDB re-parses and re-serialises every date at index time into a
fixed-width 7-digit-fraction ISO term.** A `Z`-suffixed and a non-`Z` term are then byte-identical in
layout for their first 27 characters, so comparison always resolves on a digit *before* reaching the
`Z`. `Z` can only break a tie between two identical wall-clock readings; it can never reorder distinct
ones. The adversarial pair (`14:00:00.0000000Z` vs `14:00:00.5000000`) ordered correctly on both
engines.

`SparkModelSymbols.cs:70-77` giving `DateTime` no companion is **correct as it stands**. A ticks
companion would be worse on three counts, all measured:
- `x.When.Ticks` returns **exactly the same order** as `order by When` — it restates the wall clock as a
  number, ignoring `Kind` exactly as the ISO term does. Fixes nothing.
- `x.When.ToUniversalTime().Ticks` returns a **different** order — it shifts every non-`Utc` value by the
  **indexing node's** local offset, making the index non-deterministic across nodes and across DST, and
  re-interpreting `Unspecified` as local time.
- It converts a tolerable single-document oddity into a **whole-index outage**:
  `'Sparrow.Json.LazyStringValue' does not contain a definition for 'Ticks'` → `state=Error,
  mapErrors=2`, 18.18% error rate over the 15% threshold, and `GET /indexes/terms` returns HTTP 500.
  The identical index without the companion stayed `Normal`.

If date ordering ever needs hardening the remedy is **normalising `Kind` on write** — a companion
cannot recover information the document does not carry.

This also confirms *why* `DateTimeOffset` is immune to this particular issue: its terms are uniformly
`Z`-suffixed, so the `Z` never participates in a comparison. That uniformity is what `DateTime` lacks.

### S3 — production at coverage.mintplayer.com: **not corrupted, no migration**

The exposure is structurally empty. **`Commit` is the only entity in the app with a `DateTimeOffset`
(`AuthoredAt`, `FirstSeenAtUtc`, `Date`), and it is the one entity without a generated index.** Generated
indexes always emit `StoreAllFields`; `Commits_ByRepository` (the only index over `Commit`, hand-written)
declares **no stored fields at all**. Trigger and type never meet. Every other date in the app is plain
`DateTime`.

Reads are safe twice over: every query over that index ends in `.OfType<Commit>()`, returning documents.
Proof rather than assertion — `BrowseController.cs:106` reads `c.Coverage` and `c.Message`, fields *not in
the index*; if the server answered from stored fields they would render blank.

**Nothing was ever persisted shifted.** Exactly three writes of a `DateTimeOffset` exist in the app: two
`DateTimeOffset.UtcNow` and one straight off a GitHub webhook payload. No code copies a date between
commits or reads one from a projection. Defect D is unreachable here — `security.json` makes `Commit`
read-only for every group, and none of the five editable types owns a `DateTimeOffset`.

Measured, and it could have made the exposure larger: `CommitAssembler.cs:415` passes an offset-bearing
`DateTimeOffset` as a query parameter against an index field held as UTC. `where AuthoredAt <= "…+02:00"`
returns exactly the same rows as the same bound written `10:00:00Z` — the comparison is offset-aware, so
base-commit selection and Δ-restamping are not skewed.

**No production query is proposed, and none would help.** There is no discriminating signature: the only
candidate is a zero-offset `AuthoredAt`, which is indistinguishable from a genuinely-UTC author timestamp
(`FirstSeenAtUtc` is zero-offset by construction).

⚠️ **The protection is one missing line.** Adding `[GenerateIndex]` to `Commit`, or a
`StoreAllFields`/`Store(...)`/`indexName` binding, flips this to a live display defect silently. Worth a
guard test asserting `Commits_ByRepository` declares no stored fields.

---

## Traps discovered — must reach the docs

1. **A bad map expression fails LATE.** `PutIndexesOperation` with a non-existent member **succeeds**;
   the index then sits at `state=Error, mapErrors=N, entries=0`. RavenDB binds map members dynamically
   at runtime, so a generator-emitted expression is **not** validated by a build. **CI needs an
   index-health check**, not just a successful deploy.
2. **An analyzer scoped to `StoreAllFields` is incomplete** — per-field `Store(...)` triggers the
   identical defect. Scope on any `FieldStorage.Yes` reaching a `DateTimeOffset`.
3. **A date-shaped `string` document property is normalised.** Surprising, and it kills the obvious
   "just store it as text" workaround.
4. **#273's Corax workaround is still load-bearing on 7.2.6** — a complex field at default indexing gives
   `mapErrors=3`, `NotSupportedInCoraxException`, only 2 of 5 documents indexed. Lucene: `mapErrors=0`.
5. **`FieldIndexing.No` fields cannot be filtered on Corax** — HTTP 500 `NullReferenceException`, not a
   clean error.
6. **`DatabaseAccess.QueryEntitiesWithIncludesAsync` is dead code.** Only other reference is a
   doc-comment in `DatabaseAccessIntegrationTests.cs:237` claiming to drive it. Delete both.
7. **`EntityMapper.GetDataType` (`:1161`) is dead** and maps `DateTimeOffset` → `"string"` (not
   `AsDetail`). The live mapping is `SparkModelShape.cs:200` → `"datetime"`. Delete it.

---

## Out of scope

- Fixing `ravendb#17901` upstream. File our layer isolation as a comment; do not block.
- `DateTime.Kind=Local` → `Unspecified` round-trip loss. Measured on **every** path including
  `session.Load`; inherent to JSON. Document it — and note it makes `DateTime` the *safer* type, since
  there is no offset to normalise away.
- Migrating the originating codebase off the wider companion convention. Their call; Defect C is their actual bug.

---

## What must be true when this is done

1. A `DateTimeOffset` written, indexed and read back through a Spark query returns the **same offset and
   the same wall clock** — asserted by a test that fails today.
2. A `DateTimeOffset` **edited and saved** persists — Defect D closed.
3. The `{Name}Sort` companion exists only where measurement says it is needed.
4. `guide-queries-and-sorting.md` states the real mechanism, so the folklore stops propagating.
5. No claim in the docs about sorting or dates is unsourced.
