# Dates & sort companions — what you write

Background and evidence: [PRD](raven_datetimeoffset_and_sort_companions_PRD.md) ·
[plan](raven_datetimeoffset_and_sort_companions_plan.md) ·
[summary](raven_datetimeoffset_and_sort_companions_summary.md).

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

row.Starts   // 2026-03-09T10:00:00+02:00 — offset intact

appointment.Starts = newValue;
await client.SaveAsync(appointment);   // persisted
```

Both used to be wrong: a projected `DateTimeOffset` came back as `08:00+00:00` with the offset
destroyed, and an edit was silently discarded while the save reported success.

⚠️ **When you assert on a `DateTimeOffset`, compare the `.Offset` or use `EqualsExact` — never `==`.**
Equality compares the *instant*, so `10:00+02:00 == 08:00+00:00` is `true`. The defect preserved the
instant and destroyed only the offset, which is exactly why it survived a 2436-test suite: an ordinary
equality assertion passes against the broken value. The first draft of the regression tests for this
fix passed while the pipeline was provably returning `08:00+00:00`.

---

## How to display one

**Show it in the viewer's timezone.** That is the default and it needs no work: the grid hands the value
to Angular's `DatePipe` with no timezone argument, which renders in the browser's own zone. Leave it
alone.

This works because **the instant was never the thing that broke.** RavenDB flattened the offset but kept
`UtcTicks`, so `2026-03-09T08:00:00Z` and `2026-03-09T10:00:00+02:00` are the same moment and render
*identically* in a viewer-local grid. A UI that shows viewer-local time never displayed this defect —
which is a large part of why it went unnoticed for years.

⚠️ **But viewer-local is not the stored wall clock, and never was.** Three different things are in play,
and conflating them is the fastest route back into this confusion:

| | example |
|---|---|
| the **instant** | `2026-03-09T08:00:00Z` — always correct, never broken |
| what the **viewer sees** | `09/03/2026, 09:00` — the instant, in their zone, *on that date* |
| the **stored** wall clock + offset | `2026-03-09T10:00:00+02:00` — what the fix restores |

Measured in `Europe/Brussels`: a value stored as `10:00+02:00` displays as **09:00**, because Brussels is
CET (`+01:00`) on 9 March even though it is CEST (`+02:00`) in September. The viewer's *current* offset is
irrelevant — the zone's offset **at the value's own date** is what applies. A value and a viewer can sit in
the same country and still disagree, purely because of DST.

So "the grid looks right" has never meant "the grid shows the document". If you need the originating wall
clock — an export, an audit trail, "which country's morning was this?" — you need the offset, and before
this fix it was not on the wire to be had.

So what is the offset for? Anything that needs the *originating* local time rather than the viewer's:
server-side business logic (`.Offset`, "which country's morning was this?"), an export that must reproduce
the local wall clock, or writing the value back unchanged.

⚠️ **The `offset-datetime` renderer in `apps/Fleet` is a demonstration device, not a pattern to copy.** It
prints three lines per cell -- `stored` (the wall clock and offset from the document, recovered through the
wrapper), `your time` (the same instant in the viewer.s zone, which is what the detail page and every other
grid show), and `index only` (what the projection returned before the fix, derived from the instant) -- so
the difference between them is visible rather than something a reader has to work out. A real app shows one
of those, the middle one.

That is also why the Fleet demo's grid and detail page deliberately disagree: the grid shows
`2026-12-31 23:59 -08:00` (the originating wall clock, via the custom renderer) while the detail page shows
`01/01/2027, 08:59` (the same instant, in your zone, via the default). Seeing both side by side is the
clearest illustration of what the offset is *for*. Everywhere else in the framework they agree.

### The detail page used to print raw ISO strings

Until this change, a `datetime` attribute on a detail page fell through to the generic value renderer and
printed the wire value verbatim — `2026-12-31T23:59:00-08:00` in a definition list — while the grid
formatted the same document as `01/01/2027, 08:59`. Not a cosmetic difference: a value whose offset
differs from the viewer's can disagree on the **date**, so the two pages named different days for one car.
Both now parse through the same `parsedDate` pipe and format identically.

## How to edit one

**Nothing to do — the form handles it.** A `date` or `datetime` attribute is edited through a native
`<input type="date">` / `<input type="datetime-local">`, and ng-spark converts in both directions around
it: the stored instant is shown as a wall clock in **your** zone, and what you type is sent back as a
complete ISO-8601 string carrying the offset for **the date you entered**.

```
document   2026-12-31T23:59:00-08:00
   ↓  shown in the control (viewer in Brussels)
           2027-01-01T08:59
   ↓  saved back
           2027-01-01T08:59:00+01:00     ← same instant, viewer's offset
```

The value takes the **viewer's** zone on save, never the record's original offset — see
[the semantics decision](raven_datetimeoffset_and_sort_companions_plan.md). Under Spark's rules the
originating offset is not business data, so there is nothing to preserve. If your app genuinely needs
"which country's morning was this?", model that as its own field.

⚠️ **Never assign a wire timestamp straight to a date control.** `<input type="datetime-local">` accepts
only `yyyy-MM-ddTHH:mm`; hand it `2026-12-31T23:59:00-08:00` and it does not throw, does not warn, and
renders **blank** — and saving the untouched form then writes that blank back over the stored value. If
you build a custom editor, go through `toDateInputValue` / `fromDateInputValue` from
`@mintplayer/ng-spark/models` rather than rolling the conversion again.

⚠️ **Compare edited timestamps by instant, not by text.** The round trip legitimately rewrites the
offset, so a string comparison marks every untouched date as changed. Use `wireDatesEqual`.

### Daylight saving: the one hour that is genuinely ambiguous

Two wall clocks a year are not ordinary values, and both are reachable by someone typing into a form:

| | example (Brussels) | what happens |
|---|---|---|
| **autumn fold** | `2026-10-25T02:30` | happens **twice** — two valid instants an hour apart |
| **spring gap** | `2026-03-29T02:30` | happens **never** — the clock jumps 02:00 → 03:00 |

The browser resolves both (it takes the daylight offset in each case), so an edit always produces a
definite instant. What you should know:

- **A wall clock cannot round-trip losslessly through the fold**, on any platform. It names two instants
  and the form can only show one. This is not a bug to report; it is what a wall clock is. The offset in
  the document is what disambiguates, which is the read path the wrapper already fixes.
- **Server-side, do not use `TimeZoneInfo.ConvertTimeToUtc`** on user input — it *throws* inside the gap.
  Use `IRequestTimeZoneResolver.ToViewerDateTimeOffset`, which handles both cases and deliberately
  reproduces the browser's choice so the two sides cannot disagree.
- ⚠️ **.NET's own default disagrees with the browser at the fold** — `GetUtcOffset` returns the *standard*
  offset where a browser returns the *daylight* one. Measured across four zones in both hemispheres. If
  you convert a wall clock yourself anywhere, you will land an hour off once a year.

### The viewer's timezone on the server

The client sends `X-Spark-Timezone: Europe/Brussels` on every same-origin request, and
`IRequestTimeZoneResolver` reads it.

```csharp
[Inject] private readonly IRequestTimeZoneResolver timeZones;

var zone = timeZones.GetViewerTimeZone();                              // TimeZoneInfo, UTC if unknown
var starts = timeZones.ToViewerDateTimeOffset(new DateTime(2026, 7, 4, 9, 0, 0));
```

**This is for server-initiated work only** — a scheduled export, a notification email, a "today" filter
evaluated server-side. Reads and writes do not use it and must not: the browser's timezone rules are kept
current by the OS, while a container's are frozen at image build time, so a stale image that converted
server-side would store a permanently wrong instant with nothing to show for it.

An unknown zone id falls back to UTC rather than failing the request. The realistic cause is not a bad
actor but a **zone rename** between the browser's tzdata and the server's — `America/Godthab` became
`America/Nuuk`, and ICU still reports `Asia/Calcutta` for `Asia/Kolkata`.

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
