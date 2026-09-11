# Dates & sort companions — what you write

> ⚠️ **Status: designed and measured, NOT YET IMPLEMENTED.**
> The "Today" columns below describe shipped behaviour. The "After" columns describe the planned fix
> and do not exist yet. Do not write code against the `After` shape until this banner is removed.
> Background and evidence: [PRD](raven_datetimeoffset_and_sort_companions_PRD.md) ·
> [plan](raven_datetimeoffset_and_sort_companions_plan.md) ·
> [summary](raven_datetimeoffset_and_sort_companions_summary.md).

---

## The short version

Two behaviours in RavenDB force the framework to emit extra index fields. You mostly don't write them.

| Problem | Extra index field | Do you write it? |
|---|---|---|
| A `[Search]` string is tokenised, so ordering on it is arbitrary | `{Name}Sort` | **No** — generated |
| A `DateTimeOffset` loses its offset when it becomes a scalar index field | `{Name}Raw` | **No** — generated |

And one rule that is **not** true, despite being widely believed:

> **You do NOT need a `*Sort` property for every sortable field.**
> Measured: for anything that isn't `FieldIndexing.Search`, a `{Name}Sort` companion produces a
> *byte-identical* ordering. `DateTime`, `int`, `decimal`, `Guid`, `bool` and enums need nothing.
> Adding companions "to be safe" costs index size and indexing throughput and buys zero correctness.

---

## Generated indexes — you write nothing extra

```csharp
[GenerateIndex]
public class Appointment : IDocument
{
    public string? Id { get; set; }
    [Search] public string Title { get; set; } = "";
    public DateTimeOffset Starts { get; set; }        // no attribute needed
}
```

That's the whole of it. `DateTimeOffset` is its own trigger — there is no opt-in attribute, no config,
and nothing to remember.

<details>
<summary>What the generator emits (you never open this file)</summary>

```csharp
[FromIndexAttribute(typeof(Appointments_Overview))]
public partial class VAppointment
{
    public string Title { get; set; } = default!;
    public DateTimeOffset Starts { get; set; }

    [IgnoreProperty] public string? TitleSort { get; set; }              // [Search] needs it
    [IgnoreProperty] public SparkIndexValue<DateTimeOffset>? StartsRaw { get; set; } // carries the offset
}

public partial class Appointments_Overview : AbstractIndexCreationTask<Appointment>
{
    public Appointments_Overview()
    {
        Map = appointments => from appointment in appointments
            select new VAppointment()
            {
                Title     = appointment.Title,
                Starts    = appointment.Starts,
                TitleSort = appointment.Title,
                StartsRaw = new SparkIndexValue<DateTimeOffset> { V = appointment.Starts },
            };
        Index(nameof(VAppointment.Title), FieldIndexing.Search);
        Index(x => x.StartsRaw, FieldIndexing.No);   // mandatory — see below
        StoreAllFields(FieldStorage.Yes);
        OnInitialize();
    }
}
```

</details>

---

## Hand-written indexes — one line per `DateTimeOffset`

The generator contributes the **property declaration** and the `FieldIndexing.No` call into your
partial class. You supply the **assignment**, because the value comes from your own expression:

```csharp
public partial class Commits_ByRepository : AbstractIndexCreationTask<Commit>
{
    [FromIndex(typeof(Commits_ByRepository))]
    public partial class Result
    {
        public DateTimeOffset? AuthoredAt { get; set; }
        // AuthoredAtRaw is contributed by the generator — do not declare it
    }

    public Commits_ByRepository()
    {
        Map = commits => from commit in commits
                         select new Result
                         {
                             AuthoredAt    = commit.AuthoredAt ?? commit.FirstSeenAtUtc,
                             AuthoredAtRaw = new SparkIndexValue<DateTimeOffset?> { V = commit.AuthoredAt ?? commit.FirstSeenAtUtc },
                         };
        IndexSearchFields();   // generated; carries the FieldIndexing.No call
    }
}
```

**Why this line can't be generated away:** a source generator can add members to a partial *class*, but
not to an *object initializer* — and only you know what the value should be (here, a coalesce). A build
**error** makes it non-optional, and the IDE code fix writes it for you.

<details>
<summary>Why a generated helper method doesn't save you anything (measured)</summary>

A static helper **is** callable from a map — RavenDB erases type names only for `new` expressions, not for
method calls — and shipping it via `AdditionalSources` works completely, including `SelectMany`, multi-map
and map-reduce. But the only shape that would have collapsed N date properties into one call,
`select Result.Complete(new Result { ... })`, is rejected by the server at PUT:

> `Could not extract any fields` — `FieldNamesValidator`

RavenDB reads an index's field list **syntactically** from the anonymous-object literal in the `select`,
so the select body must *be* that literal. A per-field helper call is the same typing as
`new SparkIndexValue<...> { V = ... }` while adding an invisible coupling and server-side failure messages, so the
plain form wins.

**Do not try to set index-entity fields from a constructor.** `new Result { ... }` with a constructor
assigning the field deploys as `new { }` — the field is silently **absent**, and the index reports
`state=Normal`. It is the one mistake in this area that fails quietly.

</details>

**When you don't need it at all:** only indexes that *store* fields are affected. An index with no
`StoreAllFields` / `Store(...)` projects straight from the document and is already correct — the
diagnostic is scoped accordingly and stays quiet.

---

## What you get

```csharp
// stored:  2026-03-09T10:00:00+02:00
var row = await client.QueryAsync<VAppointment>("Appointments");

row.Starts   // today:  2026-03-09T08:00:00+00:00   ← offset destroyed
             // after:  2026-03-09T10:00:00+02:00   ← correct

appointment.Starts = newValue;
await client.SaveAsync(appointment);
             // today:  silently discarded — the save reports success
             // after:  persisted
```

---

## Gotchas worth knowing

**`Index(x => x.XRaw, FieldIndexing.No)` is mandatory on a wrapper field.** Omit it and **Corax deploys
the index cleanly, then parks it at `state=Error, entries=0`** — every query against it returns nothing.
Lucene is unaffected, so this fails only on the engine CI and production actually run. The generator emits
it for you; don't delete it.

**A `DateTimeOffset` inside an `[ValueObject]` child collection is already correct** and needs no wrapper.
Nested objects are stored opaquely by RavenDB, so their inner values never go through the conversion.
Only scalars, `DateTimeOffset[]` / `IList<DateTimeOffset>`, and `SelectMany` fan-out are affected.

**Ordering and filtering were never broken.** RavenDB preserves the *instant*; only the offset and the
wall-clock reading are lost. Sorting, ranges and `where` clauses have always been correct — and comparing
an offset-bearing value against an index field works correctly too.

**Prefer `DateTime` (UTC) when you don't need an offset.** It has no offset for RavenDB to normalise away,
so it needs no wrapper at all. Note `DateTimeKind.Local` does **not** round-trip — it comes back as
`Unspecified` on every path, including `session.Load`. And a trap worth memorising:
`DateTime.Parse("…Z").Kind` is **`Local`, not `Utc`**.

**Don't store a date as a `string` to dodge this.** RavenDB normalises date-shaped strings too.

**Sorting numerics by field *name* is the one real ordering trap.** `OrderBy("Mileage")` — the
string-named overload — defaults to `OrderingType.String` and gives you `10, 100, 1000, 2, 20, 200, 5, 9`.
Use `OrderBy(name, OrderingType.Long)`. Spark's own query pipeline builds typed lambdas and is immune;
this only bites hand-written `DocumentQuery` code.

---

See also: [Queries & Sorting](guide-queries-and-sorting.md) · [Full-Text Search](guide-search.md)
