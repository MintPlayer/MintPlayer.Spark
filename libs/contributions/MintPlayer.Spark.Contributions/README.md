# MintPlayer.Spark.Contributions (preview)

> ⚠️ **Preview.** The runtime (loading, saving, recomputing the current version, rebuild) works. Not
> yet: the attribution rendering hint in the model and contributor-name lookup (`ContributorName` is
> always empty), the generated contributions query and its model JSON, the `RevertContribution`
> action, the "withdrawn; now showing X" notice, and ng-spark's conflict-merge rule for contribution
> rows.

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

Everything below runs in a persistent-object interceptor ordered **after** SoftDelete, History and
Moderation (`PersistentObjectInterceptorOrder.Contributions`), inside the request session, so it
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
  retry: the commit is the base `OnSaveAsync`'s single `SaveChangesAsync`, after every hook, so the
  client's conflict flow (re-fetch, merge, save) is the retry.
- **Moderation.** Contributions are not PO saves, so Moderation's interceptor never sees them. Before
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

## Rights

Contributors hold `Edit` on the target, narrowed by attribute-level rights; there is no `Contribute`
verb. The one new verb is `RevertContribution/<Type>Contribution`. History, hiding and restoring reuse
`Query`/`Read`, `Delete` and SoftDelete's `Restore`/`ViewDeleted`/`Purge`.

## Packaging

The source generator and analyzer ship inside this package (`analyzers/dotnet/cs`). In this
repository, import `Targets/spark-contributions.targets` from the app's csproj to wire the generator,
since analyzer project references do not flow transitively.

The design is in [docs/contributions_PRD.md](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/contributions_PRD.md).
