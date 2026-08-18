# Full-Text Search

How a search term travels from the query list to RavenDB, what it matches, and what it deliberately does not.

## Overview

Every query list has a search box. The term is sent as `?search=`, and the server turns it into a RavenDB
`search(...)` clause across the query type's text fields.

```
GET /spark/queries/{id}/execute?search=olkswag
→ from index 'Cars/Overview' where (search(LicensePlate, $p0, and) or search(Model, $p1, and))
```

**Nothing needs declaring.** Every text attribute of the query type is searchable — you do not opt a field in,
and `[Search]` is not required (see [Searchability is not `[Search]`](#searchability-is-not-search)). There is no
configuration, no per-query flag, and no client wiring: the search box, the `searchTerm` state and the query
parameter already exist in `@mintplayer/ng-spark`.

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
| `volkswagon` | no | no fuzzy matching — see [below](#no-fuzzy-matching) |

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

`[Search]` still earns its place — it enables token-level matching, analyzer behaviour, and relevance — it just
is not the gate for search.

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

## Known limits

### No fuzzy matching

`volkswagon` does not find `Volkswagen`. This was measured and deliberately not implemented: RavenDB's `Fuzzy`
is **unsupported by Corax**, the default search engine and the one generated indexes use — it fails server-side
with `Method 'Fuzzy' is not supported`. It is therefore an index-definition decision (`SearchEngineType.Lucene`)
rather than a query feature. It is also mutually exclusive with the wildcard wrapping above, because wildcards
count as literal edits and silently consume the edit-distance budget.

### No relevance ranking

Results are ordered by the query's sort columns, never by match quality.

### Streaming queries filter client-side

The WebSocket streaming path takes no search term; the client filters streamed rows locally. The asymmetry with
the paged path is known.

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
