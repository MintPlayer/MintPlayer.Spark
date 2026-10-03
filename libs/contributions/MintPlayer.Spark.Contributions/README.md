# MintPlayer.Spark.Contributions (preview)

> ⚠️ **Preview.** Server and client work: loading, saving, recomputing the current version, rebuild,
> the generated model and contributions query, contributor names, `RevertContribution`, removing a
> whole version, the save notices, and the ng-spark pieces in `@mintplayer/ng-spark/contributions`
> (the attribution row renderer with the History link, the line-diff renderer, the revert action, and
> the conflict-merge rule for contribution rows) — see [Client](#client-ng-spark). The QnA sample
> (`apps/QnA`) is the demo consumer: `Question.Translations`.

Per-user contributions with **latest-wins** for MintPlayer.Spark. Mark a property of an entity with
`[Contribution]`: every user edits their own version of each slot, the latest non-hidden version is
shown, and every version stays in a moderatable history. The target entity is never written by a
contribution, so its revisions and indexes are untouched.

## Declaring a contribution

```csharp
using MintPlayer.Spark.Contributions;

public partial class Song
{
    [Contribution(Attribution = ContributionAttribution.Contributor | ContributionAttribution.UpdatedAt)]
    [Newtonsoft.Json.JsonIgnore] // stored in the contribution documents, never on the Song
    public List<Lyrics> Lyrics { get; set; } = new();
}

public partial class Lyrics
{
    [ContributionSlot] public string Language { get; set; } = "";
    [ContributionSlot] public string Script { get; set; } = "";
    public string Text { get; set; } = "";
}
```

- The **slots** (`Language`, `Script`) identify a row: one current version per slot combination,
  one contribution per user per slot combination.
- The generator creates `SongLyricsContribution` and `SongLyricsCurrent` (always target + property,
  no override). They are RavenDB collections and `security.json` resources, so renaming the target or
  the property later means migrating them.
- `Attribution` (default `None`) opts in to showing who wrote the current version, when, and the
  slot's history count.

The property may be `T`, `T[]`, `List<T>`, `IList<T>`, `IReadOnlyList<T>`, `ICollection<T>` or
`IEnumerable<T>`. A collection needs at least one slot; a single-valued property has none (one
current version per target). The owner need not be `partial`; the element must be (when the
generator adds anything to it) and must be declared in the same project. Records work; positional
records do not (no parameterless constructor). Do **not** mark the element `[ValueObject]`.

**Slot types** (T2): `string`, any enum (by name), `byte`/`sbyte`/`short`/`ushort`/`int`/`uint`/
`long`/`ulong`, `Guid` (32 hex digits, format `N`) and `bool` (`true`/`false`), and their nullable
forms. Every formatted value must match `^[A-Za-z0-9-]{1,32}$` (`ContributionSlotFormat.IsValid`).

## The generated API

For `Song.Lyrics` (types in `Song`'s namespace):

| Type | Members |
|---|---|
| `partial class SongLyricsContribution : IContribution, IHasNaturalId` (+ `ISoftDeletable` when MintPlayer.Spark.SoftDelete is referenced) | `Id`, `TargetId`, `ContributorId`, `UpdatedAt` (UTC `DateTime`), the slots, the value properties; `static GetId(targetId, language, script, userId)` → `Songs/1234/LyricsContributions/ko/Kore/User/MintPlayerUsers/abc` |
| `partial class SongLyricsCurrent : ICurrentContribution, IHasNaturalId` | `Id`, `TargetId`, `ContributorId`, `ContributionId`, `UpdatedAt`, `ContributionCount` (only with `History`), the slots, the values; `static GetId(targetId, language, script)` → `Songs/1234/Lyrics/ko/Kore` |
| `sealed class SongLyricsContributionMetadata : ContributionDescriptor<Song, Lyrics, SongLyricsContribution, SongLyricsCurrent>` | `Instance`; `Shape` (the shape hash) and `QueryName` (`SongLyricsContributions`) constants; prefixes (both end in `/`); `GetRows`/`SetRows`; `SlotKeyOf…`; `FindInvalidSlot`; `ContributionId`/`CurrentId`/`SlotContributionPrefix`; `CreateContribution`/`CopyValues`/`CreateCurrent`/`CreateRow` |
| `partial class Lyrics` (added) | `[ValueKey] public string Key => "{Language}/{Script}"` (get-only, deterministic: the AsDetail row identity); with attribution, read-only `ContributorName`, `UpdatedAt`, `ContributionCount` (rendering hint `contributionAttribution`) |

A single-valued declaration has no slot segments: `{targetId}/{Property}Contributions/User/{userId}`
and `{targetId}/{Property}`.

The generated documents are `partial`, so the app can add `IModeratable` (or `ISoftDeletable` itself
when it wants to own the members — the generator then leaves them out).

Each assembly gets one `[ModuleInitializer]` that registers every element key with
`SparkValueObjects` and every descriptor with `ContributionRegistry`; the runtime reads the registry
and scans nothing. Diagnostics SPARK025–SPARK029 and SPARK031–SPARK035 are listed in
[docs/diagnostics.md](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/diagnostics.md).

## Registration

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddContributions();
});
```

Domain rules for a row (e.g. script detection, line counts) go in an `IContributionValidator<TElement>`
registered in DI.

`AddContributions()` must see the assemblies that declare contributions, because the generated
registration is a module initializer, which runs at the first use of its assembly's code. Covered
without arguments: the entry assembly and those of its direct references that reference
`MintPlayer.Spark.Contributions.Abstractions`, and the assembly of every model entity type. When
neither reaches a declaring assembly (a test host, whose entry assembly is the test runner), name
it: `spark.AddContributions(typeof(Song).Assembly)`.

## Runtime behaviour

Everything below runs in persistence hooks (#482; formerly an interceptor), inside the request session,
after SoftDelete has decided whether a delete is replaced, so it
commits atomically with the save or delete that caused it, and a refused save writes none of it.

- **Load.** Every load of a target for persistent-object work (GET, refresh, the save's reload, the
  `Before` side load) fills the property from the current documents with **one** lazy
  `LoadStartingWith` over `{targetId}/{Property}/` (page size 1024; a single-valued property is a
  lazy point load).
- **Save** (create or edit of the target). The rows are diffed by slot against the stored ones, by
  value:
  - added or edited row → **your** contribution for that slot is created or overwritten
    (`UpdatedAt` = now, UTC), and the slot's current document is set from it;
  - removed row → **your own** contribution for that slot is withdrawn (soft-deleted with reason
    `withdrawn` when the type is `ISoftDeletable`, deleted otherwise) and the slot's current is
    recomputed. Removing a row that shows someone else's version withdraws nothing: it stays;
  - a changed slot value is withdraw-old plus add-new; an unchanged row writes nothing;
  - another user's contribution is never touched; the contributor is always the caller's Spark user
    id (the one History stamps), whatever was posted;
  - refused with a validation error (400): two rows on one slot, a slot value that is not a valid id
    segment, an `IContributionValidator<TElement>` error, a row whose own contribution a moderator
    hid, and an anonymous caller.
  - The target document itself is **not** rewritten: the property is `[JsonIgnore]`, and RavenDB
    skips an unchanged document.
  - A create stores the target first, to know its id; the id convention must be client-side (HiLo
    or a natural id).
  - Only the attribute as posted and writable counts: a save that does not post the property, or
    whose attribute-level rights drop it, leaves the contributions alone. Reverts, restores and syncs
    of the target never touch them.
- **Recompute.** A slot's current document is its latest (max `UpdatedAt`) contribution that is not
  soft-deleted, read with a prefix load of that slot's contributions. None left → the current
  document is deleted. `ContributionCount` (with `History`) counts the visible ones.
- **Concurrency.** Every current document is written with a pinned change vector: update → the
  version this session loaded, create → must not exist, delete → the loaded version. A contributor
  racing another one on the same slot gets **409**, and nothing of the refused save is written (the
  contribution and the current document commit together or not at all). There is no in-pipeline
  retry: the commit is the framework's single `SaveChangesAsync`, after every hook, so the
  client's conflict flow (re-fetch, merge, save) is the retry.
- **Moderation.** Contributions are not PO saves, so Moderation's hooks never see them. Before
  writing or withdrawing a contribution, the runtime asks every registered `ISatelliteWriteGuard`
  (Spark core contract); Moderation registers one that refuses a suspended account and a locked
  contribution (`IModeratable`, for those the lock binds). Not for the system context.
- **Hide, delete, restore of a contribution** (its own PO: SoftDelete's delete, a hard delete, a
  purge, a restore, a moderator's edit) recomputes the slot's current in the same commit — before the
  delete, never after it.
- **Deleting the target.** A real delete or purge also deletes every contribution and current
  document of the target, in the same commit (hard deletes: nothing is left to restore them to). A
  soft delete of the target (SoftDelete replaced it) keeps them, so a restore brings everything back.
- **Rebuild.** `IContributions.RebuildCurrentAsync(targetId)` recomputes every current document of the
  target from its contributions and deletes current documents nothing backs (its own session, up to 3
  attempts on a concurrent write). At startup, each declaration's generated `Shape` is compared with
  the marker `SparkContributions/Shapes/{QueryName}`; on a difference (or no marker) every target of
  that declaration is rebuilt in batches of 64 before the host serves requests, and the marker is
  written last.
- **System context** (a migration, a sync) writes as contributor `system` when there is no user, and
  skips the validators and the guards.

- **Save notices** (PRD Q3), in the save response's `operations` (`notify`, sent after the commit):
  - your row removed → "Your version of `en/Latn` was withdrawn; *Alice*'s version is shown now"
    (info), or "…no version of it is left";
  - a removed row (or a row whose slot you changed) that shows someone else's version → "`en/Latn`
    shows *Alice*'s version, which only its author or a moderator can remove: it stays" (warning).
  - Every language travels (`translatedMessage`): ng-spark shows the one its user picked, not the
    browser's `Accept-Language`, in which `message` is the fallback.
  - The other contributor is named only with `Attribution.Contributor` and a resolved name; otherwise
    the text says "another contributor". The slot is the row key (`en/Latn`), or the property name
    for a single-valued declaration. Texts are English/French/Dutch built in; an app overrides one by
    defining its key in `translations.json`: `contributions.withdrawnNowShowing` (`{0}` slot, `{1}`
    name), `contributions.withdrawnNowShowingAnother`, `contributions.withdrawnNoneLeft`,
    `contributions.notYoursStays` (`{0}`, `{1}`), `contributions.notYoursStaysAnother`.
- **Contributor names** (`Attribution.Contributor`) are resolved at read time through core's optional
  `ISparkUserNameResolver` (one batched call per loaded entity), never stored.
  `AddHistoryUserNameResolver<T>()` registers History's resolver there too; an app without History
  registers an `ISparkUserNameResolver` itself. Without one, `ContributorName` is empty.

## Model (no app hand-work)

Synchronize (`--spark-synchronize-model`) writes model files for the generated types as for any
entity, although they are not `SparkContext` properties: each declaration registers them as
**satellites** of the target (`SparkModelSatellites`, Spark.Abstractions), keyed by the target type.
A context that exposes `Song` therefore gets:

- `SongLyricsContribution.json` — the contribution type, with the query
  **`SongLyricsContributions`** (alias `songlyricscontributions`), source
  `Custom.SparkContributionsOfTarget`, sorted by `UpdatedAt` descending. Minted once; afterwards the
  file owns it (columns, `canSort`/`canFilter`, labels), like every query.
- `SongLyricsCurrent.json` — the current type (no query), so moderators can `Delete` a version.
- On the element's model file (`Lyrics.json`), the attribution attributes `ContributorName`,
  `UpdatedAt`, `ContributionCount` get `renderer: "contributionAttribution"` and the
  `rendererOptions` below — only when the attribute has no renderer yet; an authored renderer is
  never overwritten.

Synchronize stays a fixed point, and the model hash covers the satellites. The query is served by
the runtime's actions for the generated type (registered per declaration as
`IPersistentObjectActions<SongLyricsContribution>`). ⚠️ An app class named
`SongLyricsContributionActions` takes precedence and must then serve
`SparkContributionsOfTarget(CustomQueryArgs)` itself.

The query lists the contributions of its **parent** (the target, passed as `parentId`/`parentType`,
resolved and row-checked by the query endpoint), every slot, newest first, with `ContributorName`
filled. Without a parent of the target type it has no rows. Hidden contributions follow SoftDelete:
only `ViewDeleted` holders see them, with `deleted=include|only`. It can also be declared as a
sub-query on the target's model file (`"queries": [{ "query": "songlyricscontributions" }]`).

## Moderator actions

- **Hide / restore / purge one contribution:** `Delete`, SoftDelete's `Restore`/`Purge` on the
  contribution type (its own PO). The slot's current version is recomputed in the same commit.
- **Remove a whole version:** `Delete` on the current type (`POST /spark/po/delete` with the current
  document's id, e.g. `Songs/1/Lyrics/en/Latn`). Every visible contribution of that slot is hidden
  (reason `version-removed`; deleted when the type is not soft-deletable), and the current document
  is deleted, in one commit. Each hidden one can be restored separately, which brings the slot back.
- **Revert:** `POST /spark/po/revert-contribution { objectTypeId, id }` (the contribution type and the
  contribution to make current). Every *newer* visible contribution of its slot is hidden (reason
  `reverted`), atomically; no text is rewritten and nothing is re-attributed — the reverted-to
  version keeps its author and date. A hidden target is refused (400, "restore it first"): reverting
  to a hidden version is `Restore` first, then revert if something newer is still visible. A
  declaration without SoftDelete refuses reverts (400), because a revert would have to destroy other
  users' versions. Answers 200 with the contribution, 409 on a concurrent write, and Spark's
  indistinguishable 404 (401 anonymous) on a missing right, row or type.
- Both moderator actions ask every `ISatelliteWriteGuard` (Moderation: suspension, locks) before each
  hide, and are audited after the commit through every `ISatelliteAuditSink` (Spark.Abstractions);
  Moderation registers one that writes a `ModerationAuditEntry` (`RemoveContributionVersion` /
  `RevertContribution`, target = the current document / the contribution, details = the target and
  every hidden id).

## Rights

Contributors hold `Edit` on the target, narrowed by attribute-level rights; there is no `Contribute`
verb. The one new verb is `RevertContribution`. With `Song.Lyrics`:

| Who | Right | For |
|---|---|---|
| contributors | `Edit/Song` (+ attribute denies, or a per-row `GetProtectedAttributesAsync("Edit")`), and `New`/`Edit`/`Delete` on the row type (`EditNewDelete/Lyrics`) | adding, editing, withdrawing their own versions: adding and removing a row are judged by `New`/`Delete` on the row type, and the form offers **Add** only with `New` |
| readers of the history | `Query/SongLyricsContribution`, `Read/SongLyricsContribution` (and `Read/Song`: the query's parent) | the History link, opening a contribution |
| moderators | `Delete/SongLyricsContribution`, `Restore/…`, `ViewDeleted/…`, `Purge/…` | hiding, restoring, seeing and purging single contributions |
| moderators | `RevertContribution/SongLyricsContribution` (+ `Read/…`) | revert |
| moderators | `Read/SongLyricsCurrent` + `Delete/SongLyricsCurrent` | remove a whole version |

A target whose `Edit` is owner-only (a row rule) takes contributions from nobody else: the
contributor needs `Edit` on the target **row**. Open the row rule for `Edit` and keep the target's own
attributes to its owner with `GetProtectedAttributesAsync("Edit", entity)` — every attribute but the
contribution property. The save drops them silently, and the edit form leaves out what the loaded
object marks read-only (an `IAfterLoad` hook can mark them). QnA does exactly this
(`QuestionActions`, `QuestionTranslatorFormInterceptor`).

A contribution is reachable exactly when its target is: the generated actions judge every row of the
contribution type (a load by id, the history query, a moderator's action) by `Read` on the target type
and the target's own row rule, so the history of a row the caller may not see (a draft) stays hidden.
That is also the row rule Spark's startup check requires before a well-known group may `Read` the type.

Synchronize writes `ContributorId` on the contribution type as `showedOn: PersistentObject` and
`isVisible: false` when it creates the attribute (the resolved `ContributorName` is shown instead), so
raw user ids stay out of the history grid by default; an authored value in the model file wins.
Denying `Query/SongLyricsContribution/ContributorId` also works. The generated type names exist in the
model only after synchronize; until then SPARK012 warns about them, and both the attribute-level
analyzer SPARK014 and the runtime validator fall back to the generated CLR class (one build behind is
fine).

## Client (ng-spark)

The client pieces live in `@mintplayer/ng-spark/contributions` (no new npm package, no routes of its
own — PRD Q7):

```ts
providers: [
  provideSpark(...),
  provideSparkSoftDelete(),          // the Deleted toggle, Restore/Purge of single contributions
  provideSparkContributions(),       // "Revert to this version": row menu + contribution page
  provideSparkAttributeRenderers([...sparkContributionRenderers, ...yourRenderers]),
]
```

- `contributionAttribution` is an AsDetail **row** renderer (`rowComponent`, see
  `docs/guide-custom-attribute-renderers.md`): the attribution attributes are not drawn as columns but
  as one line under the row's first cell, on the target's detail page and in its edit form.
- The query page (`query/:queryId`) reads `parentId`/`parentType` and the slot filters from the URL,
  for any query, and shows them as removable chips.
- `lineDiff` is generic; on the contribution type it compares against the **target's row of the same
  slot** (whose value is the current version), so a history reader needs `Read/Song` — which the
  query's parent already requires — and no right on the current type. Synchronize seeds it on the
  contribution type's `string` value attributes (`ContributionDescriptor.LineDiffRendererOptions`:
  the target id and the row key are derived from the contribution id by a regular expression).
- Revert is offered for `RevertContribution/{type}` (`canRevertContribution` in
  `GET /spark/permissions/{type}`), on a generated contributions query (source
  `Custom.SparkContributionsOfTarget`) and its contribution pages, outside the recycle bin; it asks
  first, and the response carries a notice ("…newer version(s) were hidden").
- Conflict merge: each row of a `[Contribution]` property loaded through `po/load` carries
  `metadata.contribution.own` — whether the caller wrote the version it shows (a boolean, never the
  contributor id). After a 409, a row both sides touched whose version theirs shows is **another**
  contributor's is theirs-wins, with a notice; only the caller's own (a second tab) is a true conflict.

## Client contract

What the ng-spark client pieces implement against:

- **Attribution row renderer.** An AsDetail row attribute with `renderer: "contributionAttribution"`
  (`ContributionDescriptor.AttributionRenderingHint`). The row carries `ContributorName` (string or
  null), `UpdatedAt` (UTC) and `ContributionCount` (number) — only those the declaration asked for;
  `rendererOptions.attribution` lists which. Render "by *Alice* · 3 days ago · History (4)" once per
  row, hiding the separate cells. `rendererOptions` (the same on each of them):

  ```json
  {
    "contributionsQuery": "songlyricscontributions",
    "targetType": "Song",
    "property": "Lyrics",
    "slots": ["Language", "Script"],
    "attribution": ["ContributorName", "UpdatedAt", "ContributionCount"]
  }
  ```
- **History link.** `/query/{contributionsQuery}?parentId={owner PO id}&parentType={targetType}` plus
  one `{slot}={row value}` per entry of `slots` (e.g. `&Language=en&Script=Latn`). The query page
  executes `POST /spark/queries/execute` with `parentId`, `parentType` and one column filter per slot
  (`columns: [{ name: "Language", includes: ["en"] }, …]`). No new Angular route: `sparkRoutes()`'s
  `query/:queryId` (the query-list page then reads these query-string parameters).
- **Revert button** on the query's rows and the contribution page: `POST
  /spark/po/revert-contribution { objectTypeId, id }`, shown for `RevertContribution/{type}` holders
  (`canRevertContribution`). Answers with a `notify` saying how many newer versions it hid.
- **Line diff renderer** (`renderer: "lineDiff"`, seeded on the contribution type's string values): a
  contribution's text against the current version — the target's row of the same slot.
- **Conflict merge** (M1c): rows of the `[Contribution]` property conflict only between two edits by
  the same user (`metadata.contribution.own` on each loaded row); another user's change to a slot is
  theirs-wins with a notice.
- **Notices** arrive as ordinary `notify` operations on the save response.

## Packaging

The source generator and analyzer ship inside this package (`analyzers/dotnet/cs`). In this
repository, import `Targets/spark-contributions.targets` from the app's csproj to wire the generator,
since analyzer project references do not flow transitively.

The design is in [docs/contributions_PRD.md](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/contributions_PRD.md).
