# MintPlayer.Spark.Contributions (preview)

> ⚠️ **Preview, not functional yet.** This release carries the attribute API, the contracts, the
> source generator and the analyzer. `AddContributions()` still registers a placeholder; the runtime
> (loading, saving, recomputing the current version) follows.

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

## Rights

Contributors hold `Edit` on the target, narrowed by attribute-level rights; there is no `Contribute`
verb. The one new verb is `RevertContribution/<Type>Contribution`. History, hiding and restoring reuse
`Query`/`Read`, `Delete` and SoftDelete's `Restore`/`ViewDeleted`/`Purge`.

## Packaging

The source generator and analyzer ship inside this package (`analyzers/dotnet/cs`). In this
repository, import `Targets/spark-contributions.targets` from the app's csproj to wire the generator,
since analyzer project references do not flow transitively.

The design is in [docs/contributions_PRD.md](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/contributions_PRD.md).
