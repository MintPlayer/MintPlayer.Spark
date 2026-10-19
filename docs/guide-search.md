# Full-Text Search

How a search term travels from the query list to RavenDB, what it matches, and what it deliberately does not.

## Overview

Every query list has a search box. The term is sent as a `search` field in the request body, and the server
turns it into a RavenDB `search(...)` clause across the query type's text fields.

```
POST /spark/queries/execute
{ "queryId": "...", "search": "olkswag" }

→ from index 'Cars/Overview' where (search(LicensePlate, $p0, and) or search(Model, $p1, and))
```

**Nothing needs declaring.** Every text attribute of the query type is searchable — you do not opt a field in,
and `[Search]` is not required (see [Searchability is not `[Search]`](#searchability-is-not-search)). There is no
configuration, no per-query flag, and no client wiring: the search box, the `searchTerm` state and the request
field already exist in `@mintplayer/ng-spark`.

## What a term matches

The term is split on whitespace, each word is wrapped as `*word*`, and **all words must be present** in the same
field. So the behaviour is substring matching, word by word:

| term | matches `"Volkswagen Golf GTI"` | why |
|---|---|---|
| `olkswag` | yes | infix substring |
| `volks` | yes | prefix |
| `gti` | yes | suffix of the value's last word |
| `volks gti` | yes | both words present |
| `gti golf` | yes | **order does not matter** |
| `olf GT` | yes | each word matched independently |
| `volks octavia` | no | every word must match |
| `VOLKSWAGEN` | yes | case-insensitive in both directions |
| `volkswagon` | yes, when the field is searchable | typo-tolerant — see [Fuzzy by default](#fuzzy-by-default) |

Two consequences worth internalising:

- **Word order and adjacency do not matter.** `gti golf` finds `Volkswagen Golf GTI`. This makes search slightly
  more forgiving than a plain "contains this whole phrase" filter.
- **Every word narrows.** Adding a word can only reduce the result set, never widen it, which is what makes
  typing more characters feel like it should.

### Wildcards and empty terms

Wildcard characters typed by a user are **stripped, not honoured**. A bare `*` would otherwise match every
document in the collection, and RavenDB does not support `?` or a mid-word `*` anyway, so passing them through
could only mislead:

| input | effective term |
|---|---|
| `vol*ks` | `*volks*` |
| `*volks*` | `*volks*` |
| `*` | *no search* |
| `?` | *no search* |

An empty or whitespace-only term is **not a search at all** — it is skipped entirely, so clearing the search box
returns the unfiltered list. This is a deliberate guard rather than a formality: RavenDB's `search()` with an
empty term returns **zero rows** rather than acting as a no-op, so without it, clearing the box would empty
every grid.

## Multi-language fields

A `TranslatedString` is indexed as one field per language, and **all languages are searched**. A term matches
whichever language it happens to be written in, so a Dutch-language user still finds a record whose only
matching text is its French label. There is no dependency on the request's culture.

See [TranslatedString & i18n](guide-translated-strings.md) for how the per-language fields are generated.

## Searchability is not `[Search]`

`[Search]` marks a field for RavenDB's analyzer — it tokenizes the value and gives the field a `*Sort`
companion so it stays sortable (see
[Sorting on searchable text](guide-queries-and-sorting.md#sorting-on-searchable-text--why-sort-fields-exist)).
It is easy to assume `[Search]` is also what makes a field searchable. **It is not.**

A field with no `[Search]` keeps RavenDB's default indexing, which lower-cases the value but does not tokenize
it — the whole value stays a single term. That has a counter-intuitive consequence, measured on RavenDB 7.2.5:

| query against a plain, undeclared string field | result |
|---|---|
| `search(Trim, "volkswagen")` — a bare word | **no match** — no single term equals `volkswagen` |
| `search(Trim, "*olkswag*")` — a wildcard term | **match** |

Because Spark always wraps terms in wildcards, search works on every text field regardless of how it is indexed.
Scoping search to `[Search]` fields would narrow what users can find for no benefit, so the framework does not.

`[Search]` still earns its place — it enables token-level matching, analyzer behaviour, relevance and
**typo tolerance** ([Fuzzy by default](#fuzzy-by-default)) — it just is not the gate for search.

## What is not searchable

Three things, all inherent to running the filter in the database rather than in memory:

### Non-text attributes

Numbers, dates and the document id are excluded. Searching `42` will not find an entity whose `Age` is 42, and
searching `people/1` will not find that document. Only text fields participate.

### Reference display text (breadcrumbs)

An attribute's resolved display value — the breadcrumb shown for a `[Reference]` — is computed **after** the
query runs, so it is not an index term and cannot be searched.

Searching cars by their owner's name therefore does not work *through the reference*. The remedy is to
**denormalize the text into the index**, which is what a projection is for:

```csharp
Map = cars => from car in cars
              let owner = LoadDocument<Person>(car.OwnerId)
              select new
              {
                  car.LicensePlate,
                  OwnerFullName = owner != null ? owner.FirstName + " " + owner.LastName : null,
              };
```

Once `OwnerFullName` is a field on the index entity, it is searchable like any other text field — and sortable,
given a companion. See [Reference Attributes](guide-reference-attributes.md).

### Sort companions

A `*Sort` companion holds the same text as the field it shadows, so searching both would double the clauses to
find the same rows. Companions are `[IgnoreProperty]`, and that is the signal used to exclude them.

## Searching, sorting and security together

The three compose in a fixed order, and the order matters:

```
row-security filter  →  search  →  sort  →  page
```

- **Search and sort are independent.** Search narrows, sorting orders, and the redirect to `*Sort` companions
  applies exactly as it does without a search term. A search does **not** rank by relevance: the requested sort
  columns, or the query's default order, always determine the order.
- **Row-level security is unaffected.** The security predicate is composed *before* the search clauses and is
  ANDed with them — a row you may not see is not surfaced by a matching search term. See
  [Row-level security](guide-row-security.md).
- **`TotalRecords` is search-aware.** The count reflects the filtered set, so paging a search result behaves.

> **Implementation note, for anyone modifying this.** The framework never passes `SearchOptions` explicitly.
> RavenDB groups consecutive `search` clauses and ANDs that group with its neighbours **only** while the option
> is left at its default. Passing `SearchOptions.Or` makes the option leak onto the *adjacent* clause — and the
> adjacent clause here is the row-security predicate, which would silently become an alternative rather than a
> requirement. The emitted RQL shape is pinned by a test for exactly this reason, because the wrong shape
> returns plausible extra rows instead of erroring.

## Fallbacks

Two shapes cannot push the search into the database, and both fall back to filtering in memory after
materialization:

- a `Custom.` query whose method does not return an `IRavenQueryable<T>`;
- a query type with no text field at all.

The fallback is correct but reads the whole result set, so prefer an index-backed query for anything large. It is
also the only path that still matches breadcrumb text, since it runs after mapping.

## Fuzzy by default

Search is **typo-tolerant** on searchable fields (commerce X18; owner decision 2026-10-19): `volkswagon` finds
`Volkswagen`, `brusels` finds `Brussels`. Nothing needs declaring.

- **Which fields.** A *searchable* field is one its index declares `FieldIndexing.Search`: a `[Search]` property
  (its generated `{Name}Search` companion), each per-language companion of a `[Search]` `TranslatedString`, a
  library-declared index's `[Search]` result property, or a hand-written `Index(x => x.F, FieldIndexing.Search)`.
  Other text fields keep the substring matching above only.
- **How a word matches.** Each word still matches as a substring (`*word*`), **or** within an edit budget: one edit
  for words of four to seven letters, two from eight. Words shorter than four letters match by substring only, since
  one edit on three letters matches noise. Every word must still match the same field, either way. Wildcards and
  fuzzy cannot share a term (a `*` counts as edits), so they are separate arms of one clause:

  ```
  where (boost(search(ModelSearch, $sparkWildcard0), 2) or fuzzy(ModelSearch = $sparkFuzzy0, 0.74))
  ```

  The substring arm is boosted so an exact or substring hit outranks a typo match wherever results are ordered by
  score. The grid orders by its sort columns, so there the boost changes nothing (see
  [No relevance ranking](#no-relevance-ranking)). The user's words travel as query parameters only.
- **The engine follows.** Corax, the default on Community and Developer servers, refuses fuzzy queries
  (`Method 'Fuzzy' is not supported`), so **every index with a searchable field runs on Lucene**, which every
  licence allows per index. A generated index (`[GenerateIndex]`, or a library-declared `[SparkIndexMap]` one) sets
  `SearchEngineType = SearchEngineType.Lucene` in its constructor; a hand-written one gets it from Spark's index
  registration (`UseSpark`, and `RavenIndexHelper` in tests). Indexes without a searchable field keep the server
  default: nothing is pinned to Corax.
- **Never on Corax.** Spark adds the fuzzy arm only to an index the server *confirms* running Lucene (index
  statistics, remembered once confirmed, asked again after 30 s otherwise). A dynamic query's auto index, an index
  that pins Corax, and an index whose Lucene rebuild has not swapped in yet are searched by substring only, never
  with an error. The in-memory [fallback](#fallbacks) is substring-only too.

### Opting a field out: `[Fuzzy(false)]`

A field where a near miss is wrong (a code, a licence plate, a SKU) opts out per field:

```csharp
[GenerateIndex]
public class Car
{
    public string? Id { get; set; }
    [Search] public string Model { get; set; } = string.Empty;           // typo-tolerant
    [Search, Fuzzy(false)] public string LicensePlate { get; set; } = string.Empty; // substring only
}
```

The opt-out goes on the entity property of a `[GenerateIndex]` entity (the generator copies it onto the projection
and the companion), or on a hand-written index's `[FromIndex]` projection or mapped document. On `Name` it covers the
`NameSearch` companion and every `Name_{lang}` field. It changes the search only: the index still runs Lucene. There
is no search configuration in the model JSON, so the attribute is the only place to say it. To keep a whole index on
Corax, pin `SearchEngineType = SearchEngineType.Corax` in a hand-written index's constructor; Spark keeps the pin,
and its search on that index is substring-only.

### Your own fuzzy queries: `[Fuzzy]`

`Fuzzy(...)` in your own document queries works on any index Spark deploys on Lucene, so on every searchable one. To
use it on a field that is *not* searchable (a whole-value fuzzy match on a plain field), opt the field in with
`[Fuzzy]`; the index then runs Lucene too:

```csharp
[GenerateIndex]
public class Place
{
    public string? Id { get; set; }
    [Fuzzy] public string Name { get; set; } = string.Empty;
}

var hits = session.Advanced.DocumentQuery<Place, Places_Overview>()
    .WhereEquals(nameof(Place.Name), "brusels")
    .Fuzzy(0.8m)
    .ToList();
```

- A hand-written index that pins `SearchEngineType.Corax` while a field of it is `[Fuzzy]` **stops startup**: every
  fuzzy query on it would fail.
- **SPARK063** flags `Fuzzy(...)` on a `DocumentQuery<T, TIndex>()` (or `DocumentQuery<T>("Index/Name")`) whose
  index has no searchable or `[Fuzzy]` field, or pins Corax, and on a dynamic query, which runs on an auto index. It
  follows one fluent chain only; a query passed in from elsewhere is not followed, and raw RQL is not inspected.
- `[Fuzzy]` on a non-text property still switches the engine but is reported (SPARK_INDEX_014): edit distance means
  nothing on a number.

### Upgrading: search indexes rebuild once

Moving an index to Lucene changes its definition. On the first deploy of this version, **every existing index with
a searchable field rebuilds side by side, production apps included**: RavenDB builds `ReplacementOf/{name}` while the
old index keeps serving, and swaps it in when it catches up. RavenDB applies an engine only to a new or reset index,
so after deployment Spark reads each Lucene-declared index's statistics and, if it still runs another engine with no
replacement under way, resets it side by side (`SparkSearchEngine.Reconcile`). Until the swap, search on that index
stays substring-only; nothing fails. Expect indexing load proportional to the collections behind those indexes,
once.

## Known limits

### No relevance ranking

Results are ordered by the query's sort columns, never by match quality.

### Live queries search like any other query

A live query's subscription carries the same `search` as `/spark/queries/execute`, and every push re-runs that
read. A RavenDB-backed live query is therefore searched in the database exactly as described above. An app stream
(a live `Custom.*` method yielding rows) is searched in memory by the server over each yield, before sorting and
paging. See [live updates](guide-live-updates.md#rows-that-are-not-in-ravendb-app-streams).

### `Exact`-indexed string fields match case-sensitively

A string field that a **hand-written** index declares `FieldIndexing.Exact` is still searched, but matches
case-sensitively — the CLR property carries no trace of the index's field options, so the runtime cannot exclude
it. Generated indexes only ever apply `Exact` to `DateTimeOffset`, which is excluded by type, so this only
affects hand-written indexes that deliberately declare `Exact` on text.

## Related

- [Queries & Sorting](guide-queries-and-sorting.md) — index-based queries, projections, and why `*Sort` fields exist
- [Row-level security](guide-row-security.md) — how the security predicate is composed
- [TranslatedString & i18n](guide-translated-strings.md) — per-language fields
- [Reference Attributes](guide-reference-attributes.md) — references and breadcrumbs

## Where a column filter sits in the clause order (#431)

The composition order is fixed, and the position of the column filters is load-bearing:

```
row-security filter → column filters → search → restrictToIds → sort → page
```

Column filters compose as plain `Equal` comparisons, never through `Search`. That is not a style
preference: RavenDB groups consecutive `Search` clauses, and an explicit `SearchOptions` leaks onto
the **adjacent** clause — which here is the row-security predicate. Turning a security filter into an
alternative returns plausible rows and nothing reports it.

Equality resolves through the `{Name}Sort` companion, exactly as ordering does. A `[Search]`-analyzed
field is indexed as separate lower-cased terms, so an equality comparison against the display field
matches **nothing** for any multi-word value — silently.

⚠️ Free-text `search` still matches every readable string property, including columns that are off
the query surface. Column filters do not: they are gated on `ShowedOn.Query` like sorting. The
difference is deliberate for now and tracked as F1 in the #431 plan.
