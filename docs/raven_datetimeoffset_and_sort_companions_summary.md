# Summary — `DateTimeOffset` fidelity and sort companions

Short version of [PRD](raven_datetimeoffset_and_sort_companions_PRD.md) ·
[plan](raven_datetimeoffset_and_sort_companions_plan.md). Everything below is measured.

---

## The one-paragraph version

RavenDB converts a `DateTimeOffset` to its UTC-equivalent `DateTime` whenever it becomes a **scalar
index field**, destroying the offset. Values **nested inside a complex object** are stored opaquely
and survive intact. So the fix is to emit a small nested wrapper alongside the real field and read
the value back from there. Separately, `DateTimeOffset` **writes** have never worked, and the
`{Name}Sort` companion convention turns out to be unnecessary for everything except `[Search]`
strings.

---

## What is actually broken

| # | Defect | Visible symptom |
|---|---|---|
| A | A scalar `DateTimeOffset` read through an index projection loses its offset | timestamps off by the value's own offset; instant is correct |
| D | `DateTimeOffset` edits are silently discarded | save succeeds, value unchanged |
| B | `{Name}Sort` companions emitted where they do nothing | wasted index size and indexing throughput |

**Not broken:** RavenDB 7.2.6 (identical to 7.1.12), the client version, Corax vs Lucene,
`session.Load`, full-document index queries, and `DateTimeOffset` inside `[ValueObject]` children.

---

## The rule to remember

> **Nesting preserves. Scalar index fields normalise.**
> `FieldIndexing.No` does *not* help. Neither does `ProjectionBehavior`. Neither does storing the
> value as an ISO string — RavenDB normalises date-shaped strings too.

---

## The new system

### 1. Generated indexes gain a wrapper, and lose a useless companion

One generic box ships in `MintPlayer.Spark.Abstractions`:

```csharp
public sealed class SparkIndexValue<T> { public T V { get; set; } = default!; }
```

For a scalar or array `DateTimeOffset` the generator emits:

```csharp
// into the map
Starts    = e.Starts,                                   // real field — ordering, filtering
StartsRaw = new SparkIndexValue<DateTimeOffset> { V = e.Starts },   // wrapper — carries the true value, offset intact
// into the constructor — NOT optional
Index(x => x.StartsRaw, FieldIndexing.No);
```

**Why a typed box is safe:** RavenDB's expression-to-string converter **erases every type name** before
the index reaches the server, so `new SparkIndexValue<T> { V = x }` and `new { V = x }` are the *same bytes* server-side.
Class, struct, generic and cross-assembly all measured identical, raw wire **and** materialised CLR, on both
engines. No type metadata is stored — renaming the type changes nothing.

⚠️ **Omitting `FieldIndexing.No` kills the index on Corax**: it deploys clean, then sits at
`state=Error, entries=0, isInvalid=True`. Lucene is unaffected — so it fails only on the engine CI and
production actually run.

The wrapper is **additive**. It is never ordered or filtered on — a `FieldIndexing.No` field cannot
be filtered (on Corax a range predicate returns HTTP 500).

One wrapper carries a whole collection, per element, in order:
`{"V":["…+02:00","…-08:00","…+05:45"]}`.

### 2. Companion matrix

| Property kind | `{Name}Sort` | Wrapper |
|---|---|---|
| `[Search]` string | **yes** (keep — measured necessary) | no |
| `DateTimeOffset` scalar / array | **no** (drop — measured useless) | **yes** |
| `DateTimeOffset` in a `[ValueObject]` child or complex property | no | **no** — already correct |
| `DateTime`, numerics, `Guid`, `bool`, enum | no | no |

### 3. Runtime restores the value at one choke point

`RowSecurityGate.ApplyAsync`, between filtering and mapping. It is the only way rows become a result,
covers index + custom/composed + streaming paths, and never sees a `session.Load` row. Absent wrapper
degrades to today's behaviour — never throws, never half-restores.

It **must** run before `ToPersistentObject`: `EntityMapper.PopulateAttributeValues` resolves properties by
attribute name, so the `Starts` attribute reads the flattened `VCar.Starts`. Restore on the typed row first.

Prefer a **generator-emitted typed restore method** over reflection — `ProjectInto<T>()` materialises the box
as its real CLR type, so a generated index and its restorer cannot drift apart. Reflection is the fallback for
hand-written index entities.

### 4. Writes are fixed

A `DateTimeOffset` branch in `EntityMapper.SetPropertyValue`, parsing with `RoundtripKind` +
`InvariantCulture`. **Never `AdjustToUniversal`** — measured to flatten every offset to `00:00:00`.
`AssumeUniversal` is harmless and was wrongly flagged in an earlier draft.

### 5. The build stops you getting it wrong

- New diagnostic at **Error** severity: a stored scalar `DateTimeOffset` with no wrapper. Errors run
  at `dotnet build`; code fixes are IDE-only and can never be load-bearing. **Suppressible** via
  `<NoWarn>$(NoWarn);SPARK018</NoWarn>` for anyone who deliberately does not care about offsets.
- Scoped on **any `FieldStorage.Yes`**, not `StoreAllFields` — per-field `Store(...)` triggers the
  identical defect.
- **SPARK005 narrowed to `Search`** (it matched `Exact` by text suffix, which is how `DateTimeOffset`
  got swept in).
- **New CI index-health check.** A bad map expression *deploys successfully* and then sits at
  `state=Error, entries=0`. Map members bind at runtime, so no build validates them.

---

## What a developer writes

**Generated index — nothing extra, before or after:**

```csharp
[GenerateIndex]
public class Appointment : IDocument
{
    public string? Id { get; set; }
    [Search] public string Title { get; set; } = "";
    public DateTimeOffset Starts { get; set; }      // wrapper generated automatically
}
```

**Hand-written index — one line per `DateTimeOffset` in the map.** Irreducible: the generator can add
members to a partial class but not to an object initializer, and the value comes from the developer's
own expression. The Error diagnostic makes it non-optional; the IDE code fix types it for you.

---

## Deployment consequences

- **Every generated index containing a `DateTimeOffset` or a `[Search]` string changes shape, so
  RavenDB rebuilds it from scratch at every consumer.** Indexes with neither are untouched.
- During the rebuild window the new binary must read an old index as "no wrapper" and return today's
  value rather than half-restoring.
- ~~Gate on spike S2~~ — **resolved: no `DateTime` companion is needed**, so no second rebuild is coming
  from that direction.
- Version: minor/patch inside `10.0.0-preview.*` — the NuGet major tracks .NET, never an API break.

---

## Spikes — all resolved, none grows the scope

| Spike | Verdict |
|---|---|
| **S1** Is `FieldIndexing.Exact` on `DateTimeOffset` load-bearing? | **No — drop it.** 62 measurements, zero differences; the index term is the same UTC-normalised string either way. Both modes already match by instant. |
| **S2** Can mixed `DateTimeKind` invert `DateTime` ordering? | **No.** RavenDB re-serialises dates at index time to a fixed-width 7-digit fraction, so `Z` only ever breaks ties. **Ship no `DateTime` companion** — a ticks companion fixes nothing and can take the whole index to `state=Error`. |
| **S3** Was production data corrupted? | **No, and no migration.** `Commit` is the only entity with a `DateTimeOffset` and the only one without a generated index, so the `StoreAllFields` trigger never meets the type. |

## No backward-compatibility requirement

Confirmed by the issue owner. Consequences:

- `{Name}Sort` on `DateTimeOffset` is **removed outright** — no alias, no deprecation period.
- **SPARK005 is narrowed immediately**; the new missing-wrapper diagnostic ships at **Error** from day one.
- The wrapper stays **implicit** (triggered by the type, no opt-in attribute) — every existing
  `DateTimeOffset` index changes shape, and that is fine.
- No compatibility shims, no dual-read path, no staged rollout.

The one thing that still needs a tolerant read path is **not** compatibility but a deployment transient:
while RavenDB rebuilds an index, the wrapper field is genuinely absent, so the runtime must treat a
missing wrapper as "no information" and return the un-restored value rather than throwing or
half-restoring.

## Open decisions

Keep or drop the (measurement-neutral) `RavenDB.Client` 7.2.6 bump in this PR; whether to move
`CodeCoverage.Tests` off its deliberate `TestDriver` 7.2.1 pin; and the wire contract for
`<input type="datetime-local">`, which has no offset to send.
