# PRD + Plan — Composed queries: let a virtual type own its rows

**Status: ✅ SUPERSEDED 2026-08-29 — shipped upstream as
[Spark PR #328](https://github.com/MintPlayer/MintPlayer.Spark/pull/328) in `10.0.0-preview.67` /
`@mintplayer/ng-spark@22.8.0`, squash-merged to master as `fd570906`.**

> **This file is the proposal record, kept for the reasoning chain — do not implement from it.** The
> authoritative design lives upstream in `docs/issue_327_PRD.md`, `docs/issue_327_plan.md` and
> `docs/release-notes-preview-67.md`. What shipped went **further** than this document proposed:
> rather than only making `clrType` optional on the query path (design C below), Spark separated the
> row type from the persistent object outright — a query now returns columns once plus lightweight
> `QueryResultItem` rows. Coverage's migration is [program-units-plan.md](program-units-plan.md) M6/M9.
>
> **Three claims below are wrong as written**, and the corrections are the part worth keeping:
>
> - **§3(B) understated the case.** Returning `PersistentObject` rows is rejected here as needing a CLR
>   type for actions resolution. True, but the deeper answer is that a row should never have been a
>   `PersistentObject` at all. The prior art (`QueryResultItem`) was the better model and this
>   document did not reach it.
> - **The `DistinctBy` framing is on the wrong axis.** This file says "keep it on the Raven path, drop
>   it on the custom path". The real condition is **indexed vs in-memory**, which cuts across both — a
>   `Custom.*` query can return a Raven queryable over a fan-out index. Spark's implementation is
>   conditional on `isRavenQueryable`, spelled out at both call sites.
> - **F5 ("the client needs no change") did not survive.** It was true of design C; the shipped
>   separation changes both the wire and the renderer contract. See M9 in the plan.

Successor to §7 item 3 of [program-units-PRD.md](program-units-PRD.md), which recorded the gap.
Owner direction at the time: **no backward compatibility required.**

Source references are to `C:\Repos\MintPlayer.Spark` @ `5ebfaa45` (master = `10.0.0-preview.65`) —
the baseline this was written against, **not** the shipped implementation.

---

## 1. The problem

PR #325 made a persistent-object type able to exist **without a CLR class** — `clrType` became optional, and
a JSON-only type's page is composed by `{Name}Actions.OnLoadAsync(string id, PersistentObject? parent)`,
resolved *by name*. That is a complete story for a page.

It is not a story for a **grid**. A `Custom.*` query on such a type returns an empty list with no
diagnostic, because `QueryExecutor` resolves the row `clrType` before it does anything else
(`QueryExecutor.cs:242-246`). So an app that wants a grid of computed rows on a composed page must invent a
CLR class that is not a document, is not on the `SparkContext`, and exists only to be shaped by the mapper —
plus a hand-authored model file to give that class a `clrType` to point at.

Virtual types are currently **a page feature with a query-shaped hole in it.**

## 2. Findings that decide the design

**F1 — Type-level authorization already runs before the CLR bail.** `ResolveEntityTypeDefinition` →
`EnsureAuthorizedAsync("Query", def.Name)` → *then* `ResolveClrType`. The `Query` right is anchored on the
**model type name**, never the CLR type. Making `clrType` optional in the query path costs the type-level
right nothing.

**F2 — The mapper already maps unregistered row classes.** `ToPersistentObject(entity, objectTypeId, …)`
scaffolds from the definition looked up by `objectTypeId` — which comes from `query.EntityType`, not from
the row's CLR type — then reflects the row **by attribute name**, leaving unmatched attributes null. Anonymous
types, records and ad-hoc classes all map today. Only the `ResolveClrType` guard and
`actionsResolver.ResolveForType(entityType)` stand in the way.

**F3 — Row security is document-shaped by construction.** `GetRowFilterAsync` returns
`Expression<Func<TEntity,bool>>`; `GetProtectedAttributesAsync` takes a `T entity`; `FilterAsync` and
`RedactAsync` reload base documents by id to judge a projection, and drop rows they cannot correlate —
*"unverifiable is not shown"*. A computed row has no document. **Row security is therefore a no-op for
composed rows under every design in this space.** That is a property of the row, not of any proposal.

**F4 — #325 already shipped this posture on the PO path.** `ExecuteCustomAction.cs:205`:
`if (rowIds.Length > 0 && clrType is not null && !await rowSecurity.AreAllowedAsync(...))` — per-row
authorization is **already skipped for a virtual type today**, and `LoadVirtualObjectViaActionsAsync`
compensates by forcing `obj.Can ??= { Edit = false, Delete = false }`. A composed grid is not a new
precedent; it is the query-path twin of one that already landed.

**F5 — The client needs no change.** Columns come from `entityType.attributes` filtered on
`isVisible && showedOn ⊇ Query`; cells match by attribute **name**; `canRead()` comes from the permissions
endpoint, not the rows; `row.can` is never read by the grid and `etag` appears nowhere in the TS client.
Every option here is server-only.

**F6 — Returning `IEnumerable<PersistentObject>` is already accepted, and is a silent failure today.**
`ExtractQueryableElementType` returns `typeof(PersistentObject)` and nothing rejects it. The rows then flow
into `ToPersistentObject`, which treats each PO **as an entity**: reflects `Id` (right by luck), then looks
for a CLR property per declared attribute, finds none, and silently skips. Result: correct row count, every
cell blank, no error, no log. Whatever else is decided, **this hole should be closed.**

**F7 — Custom actions on a virtual type already work.** Both `ExecuteCustomAction` and `ListCustomActions`
use `entityType.ClrType?.Split('.').Last() ?? entityType.Name`. The driving case's page-level `Resync`
needs nothing new.

### The silent-failure inventory is larger than the three we filed

| # | Failure | Site |
|---|---|---|
| S1 | Missing `Id` → `DistinctBy(po => po.Id)` collapses the grid to one row | `QueryExecutor.cs:223`, `:375` |
| S2 | Query on a virtual type → empty list, no diagnostic | `:244` |
| S3 | `sortColumns` silently ignored for a non-queryable result | `:333` |
| S4 | Query with no `entityType` → metadata 404 → empty card, no columns, no error | `Endpoints/Queries/Get.cs:24` |
| S5 | `ResolveEntityTypeDefinition` null → silent `([], false)` | `:376-388` |
| S6 | Projected row with no readable `Id` → `FilterAsync` returns `[]` (deliberate fail-closed, indistinguishable from "no rows") | `RowSecurity.cs` |
| S7 | `Database.*`: **six** separate silent `([], false)` bails | `:104-133` |

**`DistinctBy`'s provenance settles S1.** It was introduced to dedupe RavenDB **fan-out** index results — the
repo already corrected its own docs on this (`issue_210_PRD.md:52-56`: *"Duplicates come from fan-out maps,
not from the analyzer. The `DistinctBy` is still correct — it just guards a different hazard than the docs
say."*). It is meaningless on the in-memory path and actively destructive there, because
`Enumerable.DistinctBy` treats every `null` key as equal.

## 3. Designs considered

Eleven were evaluated. The four that matter:

**(A) `[SparkIgnore]` marker class — rejected, and the repo already rejected it.** When #324 needed a virtual
*page*, the marker-class design was the one that got cut (`issue_324_PRD.md:282`):

> "A virtual type needs **no CLR class at all** — F3's marker-class shape was itself boilerplate.
> `EntityTypeDefinition.ClrType` became optional… Fleet's `ConfirmDeleteCar` marker class was deleted on the
> same grounds."

It also fights the naming vocabulary. Every model-shaping attribute is **unprefixed** (`[IgnoreProperty]`,
`[Search]`, `[Sortable]`, `[Breadcrumb]`, `[Reference]`, `[GenerateIndex]`, `[DefaultIndex]`, `[FromIndex]`);
only the two that are *not* about the model carry a `Spark` prefix. And `Ignore` already means "drop this
**property** from the model", in two deliberately distinguished spellings. A class-level `Ignore*` would be a
third meaning of the same verb. If it must exist, `[SparkView]` at least names the thing rather than the
workaround. Four consumer files, negative depth, and a `Can` trap: the phantom type needs a `security.json`
entry, and getting it wrong renders Edit/Delete buttons that 404.

**(B) Return `PersistentObject` rows — does not work as specified.** `ExecuteCustomQueryAsync` locates the
method through `actionsResolver.ResolveForType(entityType)`; it needs a CLR type to find the actions class
*before* it ever inspects the return shape. Accepting PO rows removes the **mapping** requirement but not the
**resolution** requirement — you would still declare a CLR class, merely stop shaping it. B only works when
paired with by-name actions resolution, at which point it is (C). It is also the wrong seam on its own
merits: as a return-type overload it is a foot-gun for the mixed case, where a hook pre-maps POs from real
documents of a row-secured type and bypasses rules it should have obeyed.

**(C) Virtual query types — recommended.** Make `clrType` optional *throughout the query path*, symmetric
with what #325 did for the PO path. When it is null: resolve the actions class through the existing
`ResolveByEntityName` (`ActionsResolver.cs:77`, added by #325), skip row security because it has nothing to
evaluate, map rows by attribute name via the mapper's existing reflection, and square the per-row envelope
the way #325 squares the PO envelope. Two consumer files, no attribute, no new JSON key, no client change.

**(G) AsDetail array — complementary, ships today.** A virtual page *can* carry a grid right now, as an
`AsDetail` array: the parent may be virtual, the child model file supplies the columns, renderers dispatch
identically, and it renders in the **light DOM** so scoped SCSS actually applies (unlike query grids inside
`mp-datatable`'s shadow root). It loses sorting, paging, search, selection, per-row navigation, server-declared
actions and `totalItems`, and the whole collection is inlined into the parent payload with no cap. One
constraint that is easy to miss: **the child type must have a `clrType`** — both hosts resolve it by exact
string match (`types.find(t => t.clrType === attr.asDetailType)`), and a child without one renders a table
with zero columns and zero rows, silently. It also needs its **own `Query` grant** or the columns vanish
(DemoApp grants `Query/Address` for exactly this reason).

## 4. Recommendation

**Ship (C), with a `QueryRows` envelope and by-name method resolution. Document (G) as the answer for small
fixed lists.** (C) is the mechanism; (G) is the guide entry that stops people reaching for the mechanism when
they don't need it.

### The consumer writes two files

`Home.json` gains one query; `GitHubAccount.json` is a model file with **no `clrType`**, exactly like
`StartPage.json`. Then:

```csharp
public partial class GitHubAccountActions
{
    [Inject] private readonly IAsyncDocumentSession session;

    public async Task<QueryRows> GetAccountsAsync(QueryArgs args)
    {
        var accounts = await session.Query<Account, Accounts_Overview>()
            .ProjectInto<VAccount>().ToListAsync();

        return QueryRows.From(accounts.Select(a => new {
            a.Id, a.Login, Avatar = a.AvatarUrl, a.RepoCount,
            Coverage = a.CoveredLines * 100.0 / Math.Max(1, a.TotalLines),
            Installed = a.InstallationId is not null }));
    }
}
```

Anonymous rows work today (F2). No row class, no marker attribute, no `security.json` entry for a phantom
type. And `GitHubAccountActions` is the natural home for "how do GitHubAccount rows come to exist" — the same
class that would later gain `OnLoadAsync`, so the row link `/po/githubaccount/{id}` starts working with no
further change.

### Why `QueryRows`

Today the paged / sorted / total decisions are inferred from the runtime **shape** of the return value
(`isRavenQueryable` / `isQueryable` / `IEnumerable`) — which is precisely why S3 exists. Making the author
state it kills a class of shape-sniffing:

```csharp
return QueryRows.From(rows);                    // framework pages, sorts, searches, counts
return QueryRows.Page(rows, totalRecords: 812); // author paged; framework leaves it alone
return QueryRows.Empty;
```

### Names

| Thing | Name |
|---|---|
| Result envelope | `QueryRows` |
| Wire result (renamed, resolves the collision) | `QueryResult` → `QueryResponse` |
| Hook args (renamed) | `CustomQueryArgs` → `QueryArgs`, gaining `Skip`/`Take`/`Search`/`SortColumns` |
| Virtual row type | **no new key** — the absence of `clrType`, as #325 |
| Startup alarm toggle | `SparkOptions.WarnOnComposedQueries` (default `true`) |

Deliberately **not** a `"virtual": true` key and **not** a `Composed.*` third source scheme: #325 declined a
second spelling of the same fact for the same reason — two spellings are a chance for them to disagree, the
`rowsNavigable` mistake in miniature.

## 4b. Determinism: scaffold-and-fill beats reflection

A composed query'"'"'s row structure is **fully determined by the model**: the page'"'"'s `persistentObject.queries`
(a `string[]` of aliases, `EntityTypeDefinition.cs:59`) names queries whose `entityType` names a type whose
`attributes` are the columns. Nothing about the column set ever needed the CLR type — which is why (C) works
at all.

That determinism is not merely permissive; it is **enforceable**, and it splits (C) into two variants that
fail in opposite directions:

| | Hook returns | Name mismatch | Ceremony |
|---|---|---|---|
| **C1** | anonymous objects / records, reflected by name | **silent** — `PopulateAttributeValues` does `if (property is null) continue; // silent skip` (`EntityMapper.cs:222`) | one object literal per row |
| **C2** | `PersistentObject` rows scaffolded via `IManager.GetPersistentObject(name)` and filled with `row["X"].Value = …` | **loud, today** — the indexer throws `KeyNotFoundException: Attribute '''X''' not on PersistentObject '''Y'''` (`PersistentObject.cs:82-84`) | one assignment per attribute |

**C2 is the right default.** The definition is the contract and the hook is its only supplier, so an attribute
name the definition does not declare is an authoring error, not a projection-only column — the tolerance
`PopulateAttributeValues` extends to entity-backed projections is exactly wrong here. C2 also *is* the idiom
#325 shipped for pages (`GetPersistentObject` + `obj["X"].Value`) and the one dialog POs have always used, so
it adds no second convention. And because `AddAttribute` is framework-internal, a consumer **cannot** build a
row PO except through a model file — the deterministic structure is enforced by construction.

This adds an eighth silent failure to the inventory, and C2 removes it:

| # | Failure | Site |
|---|---|---|
| S8 | Row property name not matching a declared attribute → that column is blank forever, no error | `EntityMapper.cs:222` |

Consequence for §3(B): `IEnumerable<PersistentObject>` is the *natural* return type for C2 — so the original
"return PersistentObject rows" instinct was right about the shape and wrong only about the mechanism, which
is by-name actions resolution rather than return-type sniffing. Finding 6 (PO rows silently yielding blank
cells) is then not merely closed but **inverted**: the shape becomes the supported one, on virtual types only.

Keep C1 accepted for the ergonomic case, but only with the same validation applied — reflect by name, then
verify every declared `showedOn: Query` attribute was actually supplied, and throw naming the misses.

## 5. The strongest argument against

Spark holds an absolute invariant today: **every row a query returns is a document the framework can reload
and re-judge.** `FilterAsync` is so committed to it that it returns `[]` rather than show a projected row it
cannot correlate back to a document. (C) deliberately inverts that for composed rows: unverifiable **is**
shown, because the author said so.

The risk is not this home page. It is the next developer who reaches for a composed query because writing one
is easier than writing a row rule, and ends up with a grid over real, document-derived, sensitive data with
row filtering, redaction and per-row permissions silently absent — looking exactly like every other Spark
grid. No technical enforcement is available, because the rows genuinely are not documents.

The only mitigation is making the choice loud, which is why item 6 below is not optional. **If the startup
diagnostic is not shipped, (C) should not ship and (G) is the honest answer** — G's rows are unmistakably
page content; C's rows are unmistakably a grid.

The counter-argument I find decisive: **#325 already crossed this line on the PO path**, in shipped code, at
`ExecuteCustomAction.cs:205`. (C) makes the query path consistent with a decision already taken, rather than
taking a new one.

---

## 6. Plan

Two of these should ship regardless of which design wins, and are now free.

**M1 — Free fixes (ship independently).**
1. Drop `DistinctBy` from the custom path; keep it on the Raven path where fan-out actually happens. Kills S1.
2. Add the in-memory **sort** fallback beside the in-memory **search** fallback that already exists
   (`QueryExecutor.cs:55-67`). Kills S3.
3. Close F6: reject `PersistentObject` as an ordinary custom-query element type, loudly.

**M2 — `clrType` optional in the query path.** When null: `ResolveByEntityName(def.Name)` instead of
`ResolveForType`; skip `ComposeRowFilterAsync` / `FilterAsync` / `RedactAsync`; map by attribute name.
Per row, square the envelope: `po.Can ??= new PersistentObjectPermissions { Edit = false, Delete = false }`.
Require a non-null, unique `po.Id` and **reject** a duplicate or missing one loudly — the load path can
default an id because it was requested with one; a query cannot.

**M3 — Make every silent bail loud.** The seven `([], false)` returns in `ExecuteCustomQueryAsync` /
`ExecuteDatabaseQueryAsync` become thrown `InvalidOperationException`s naming the query and the fix. The
precedent is `LoadVirtualObjectViaActionsAsync`, which already throws on a shape mismatch rather than 404ing
(`DatabaseAccess.cs:479-489`). Fix the streaming twin at `StreamingQueryExecutor.cs:57-62`, whose message
currently interpolates an empty `ClrType` and reads `CLR type '' not found` — two paths, same cause, opposite
failure modes, both wrong.

**M4 — `QueryRows` + `QueryArgs`.** The envelope, and the args gaining `Skip`/`Take`/`Search`/`SortColumns`.
Blast radius across the demos: **5 true `Custom.*` methods in 4 apps** (2 further hits are streaming
overrides on a different hook).

**M5 — The alarm.** Once per composed query at startup:
`Query 'Accounts' on virtual type 'GitHubAccount' returns composed rows — row filtering, redaction and
per-row permissions are GitHubAccountActions' responsibility, not the framework's.`

**M6 — Docs.** A guide section on composed queries; an AsDetail-array subsection for small fixed lists,
naming the child-`clrType` requirement and the child `Query` grant; and a worked transient-row example.
Note that `modelHashes.json` already has the right slot — `ConfirmDeleteCar.json` appears under `files` with
no `entities` entry — so "modelled but not a class" is already expressible in the fingerprint.

**M7 — A demo.** DemoApp's `StartPage` gains a composed query. No virtual type has a query today
(`StartPage.json` and `ConfirmDeleteCar.json` both carry `"queries": []`), which is precisely why the gap
went unnoticed.

## 7. Runner-up, recorded not chosen

**(J) Clean slate** — delete `source` entirely; a query becomes a member of its type's actions class resolved
by name like `OnLoadAsync`, with `Database.Cars` as `DefaultPersistentObjectActions<Car>`'s default
implementation. One contract, S1–S7 all loud. It is the better design and is now permitted.

Not primary because its blast radius lands on `Database.*` — the one path carrying all four security layers,
exercised by every existing app — to fix a gap that exists only on the other path. Wrong risk shape even with
breaking changes free. Worked through against the security invariant, J converges on C anyway: if the hook
returned already-mapped POs for a CLR-backed type, the executor could no longer run filter/redact, converting
today's unescapable envelope into an opt-in one. J's honest form keeps that envelope in the executor and lets
the hook produce only the raw sequence — **which is C's seam.** So C costs nothing later; it is J minus the
naming convention.
