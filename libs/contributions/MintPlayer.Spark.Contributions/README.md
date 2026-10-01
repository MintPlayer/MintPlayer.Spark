# MintPlayer.Spark.Contributions (preview)

> ⚠️ **Preview, not functional yet.** This release carries the attribute API, the contracts and the
> packaging. The source generator emits nothing yet and `AddContributions()` registers a placeholder;
> the generated types and the runtime follow.

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
