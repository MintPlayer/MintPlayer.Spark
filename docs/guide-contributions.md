# Guide: per-user contributions (`MintPlayer.Spark.Contributions`)

`MintPlayer.Spark.Contributions` lets several users each write **their own version** of part of an
entity, with the latest version shown. Mark a property with `[Contribution]`: every user edits their
own copy of each row ("slot"), the newest visible copy is what everyone sees, and every copy stays in a
history that moderators can hide, restore and revert. The entity itself is never written by a
contribution, so its revisions, etag and indexes do not move.

The package README (`libs/contributions/MintPlayer.Spark.Contributions/README.md`) is the reference,
including the exact client contract; this guide is the adoption path. The design and every decision
behind it are in [contributions_PRD.md](contributions_PRD.md).

> ⚠️ **Preview.** The live sample is `apps/QnA`: `Question.Translations`, one translation per
> language and script, written by any signed-in user.

---

## 1. When to use it

Use a contribution when a piece of content belongs to **many authors at once** and the owner of the
entity should not have to approve each change:

- song lyrics in several languages and scripts (`ko/Kore`, `ko/Latn`, `en/Latn`), the first consumer;
- community translations of a question (QnA);
- any "suggested text" where the latest good version wins and a bad one must be removable.

What you get compared with a plain `List<T>` property:

| Plain property | `[Contribution]` property |
|---|---|
| Last save overwrites everyone's rows | Each user overwrites only **their own** version of a slot |
| History needs RavenDB revisions (2 revisions / 45 days on Community) | History is ordinary documents; works on the Community licence |
| Every edit rewrites and re-indexes the entity | The entity is untouched; its revisions and indexes do not move |
| A vandal's edit replaces the text | A moderator hides the bad version and the previous one comes back |

---

## 2. Declaring a contribution

```csharp
using MintPlayer.Spark.Contributions;

public partial class Song
{
    public string? Id { get; set; }
    public string Title { get; set; } = "";

    [Contribution(Attribution = ContributionAttribution.Contributor
                              | ContributionAttribution.UpdatedAt
                              | ContributionAttribution.History)]
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

- **`[Contribution]`** goes on the property of the target entity. The property may be `T`, `T[]`,
  `List<T>`, `IList<T>`, `IReadOnlyList<T>`, `ICollection<T>` or `IEnumerable<T>`.
- **`[ContributionSlot]`** marks the element properties that identify a row. There is one current
  version per slot combination, and one contribution per user per slot combination. The slots, in
  declaration order, become segments of the document id.
  - A **collection** needs at least one slot. A **single-valued** property has none: one current
    version per target.
  - Allowed slot types: `string`, any enum (by name), the integral types, `Guid` (format `N`),
    `bool`, and their nullable forms. Every formatted value must match `^[A-Za-z0-9-]{1,32}$`, or the
    save is refused. Floating-point and date types are refused at build time: they have no single
    culture-independent text form.
- **`[Newtonsoft.Json.JsonIgnore]` is required** (SPARK029, with a code fix). Spark's document store
  uses Newtonsoft, so System.Text.Json's `[JsonIgnore]` does not count.
- **The element** must be `partial` (the generator adds members to it), declared in the same project,
  and have a parameterless constructor. Records work; positional records do not. Do **not** mark it
  `[ValueObject]`: the generator gives it its own row key.
- **`Attribution`** (default `None`) opts in to showing who wrote the current version
  (`Contributor`), when (`UpdatedAt`), and how many versions the slot has (`History`). Nothing extra
  is stored, loaded or shown for a flag you leave out.

### The generated types

For `Song.Lyrics` the generator produces, in `Song`'s namespace:

| Type | What it is |
|---|---|
| `SongLyricsContribution` | One user's version of one slot. `Id`, `TargetId`, `ContributorId`, `UpdatedAt` (UTC), the slots and the values. `ISoftDeletable` when `MintPlayer.Spark.SoftDelete` is referenced. |
| `SongLyricsCurrent` | The version currently shown for one slot: the values plus `ContributorId`, `ContributionId`, `UpdatedAt`, and `ContributionCount` (only with `History`). |
| `SongLyricsContributionMetadata` | The descriptor the runtime uses: ids, prefixes, the mapping between element, contribution and current. No reflection at runtime. |
| `Lyrics` (added members) | A get-only `[ValueKey] Key => "{Language}/{Script}"` (the row identity), and with attribution the read-only `ContributorName`, `UpdatedAt`, `ContributionCount`. |

Both document types are `partial`, so the app can add interfaces such as `IModeratable` (section 6).

⚠️ **The names are stored data.** They always come from target plus property, with no override, and
they become RavenDB collection names and `security.json` resources. Renaming `Song` or `Lyrics`
later means migrating the collections and the grants.

---

## 3. Document ids

Contributions and current versions use separate id prefixes, so every read is a prefix load with no
glob:

| Document | Id | Example |
|---|---|---|
| contribution | `{targetId}/{Property}Contributions/{slot1}/{slot2}/User/{userId}` | `Songs/1234/LyricsContributions/ko/Kore/User/MintPlayerUsers/abc` |
| current | `{targetId}/{Property}/{slot1}/{slot2}` | `Songs/1234/Lyrics/ko/Kore` |
| single-valued contribution | `{targetId}/{Property}Contributions/User/{userId}` | `Songs/1234/OriginalContributions/User/MintPlayerUsers/abc` |
| single-valued current | `{targetId}/{Property}` | `Songs/1234/Original` |

The user segment is always last, so a user id with slashes of its own (`MintPlayerUsers/{guid}`) is
kept as is. Prefixes always end in `/`: `Songs/1` must not match `Songs/12`.

Because a create must know the target's id before it can write contribution ids, the target's id
convention must be **client-side** (HiLo, or a natural id). A server-assigned convention (`Songs/` or
`Songs|`) is refused with an explicit error.

---

## 4. Server setup

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<SongsContext>();
    spark.AddAuthentication<SparkUser>();
    spark.AddSoftDelete();      // recommended: hidden versions can be restored, reverts are possible
    spark.AddHistory();         // optional: its user-name resolver names the contributors
    spark.AddModeration<SparkUser>(); // optional: suspension, locks, flags (section 6)
    spark.AddContributions();
});
```

`AddContributions()` finds the declarations through a module initializer that the generator emits in
each declaring assembly. It covers the entry assembly, its direct references that reference
`MintPlayer.Spark.Contributions.Abstractions`, and the assembly of every model entity. When none of
those reaches the declaring assembly (a test host, whose entry assembly is the test runner), name it:
`spark.AddContributions(typeof(Song).Assembly)`.

**Domain rules per row** (script detection, a matching line count) go in an
`IContributionValidator<TElement>` registered in DI. The library calls it for every added or edited
row; any error refuses the whole save:

```csharp
public class LyricsValidator : IContributionValidator<Lyrics>
{
    public ValueTask<IReadOnlyList<ValidationError>> ValidateAsync(
        string targetId, Lyrics element, CancellationToken cancellationToken = default)
    { /* … */ }
}

builder.Services.AddScoped<IContributionValidator<Lyrics>, LyricsValidator>();
```

**Packaging.** The NuGet package carries the generator and analyzer (`analyzers/dotnet/cs`). Inside
this repository, import `libs/contributions/MintPlayer.Spark.Contributions/Targets/spark-contributions.targets`
from the csproj that declares the contribution, because analyzer project references do not flow
transitively (see `apps/QnA/QnA.Library/QnA.Library.csproj`).

**Model.** Run `--spark-synchronize-model` as usual. The generated types are registered as
**satellites** of the target, so a context that exposes `Song` gets `SongLyricsContribution.json`
(with the query `SongLyricsContributions`, newest first) and `SongLyricsCurrent.json`, and the
element's attribution attributes get the `contributionAttribution` renderer. Synchronize never
overwrites a renderer or a query you authored, and the model hash covers the satellites.

---

## 5. How reads and writes work: the current document

The design keeps a **current document per slot** that holds a copy of the winning version's values.
That copy is a cache the library owns and can rebuild; the contributions are the truth.

### Read

Every load of the target for persistent-object work (open, refresh, the save's reload) fills the
property from the current documents with **one** lazy prefix load over `{targetId}/{Property}/`,
batched with the entity load. A page with lyrics is one request; nothing scans the contributions.

### Save

Rows are diffed per slot, by value, against what was loaded:

| You… | What happens |
|---|---|
| add or edit a row | **your** contribution for that slot is created or overwritten (`UpdatedAt` = now, UTC), and the slot's current document is set from it |
| leave a row unchanged | nothing is written |
| remove a row showing **your** version | your contribution is **withdrawn** (soft-deleted with reason `withdrawn`, or deleted without SoftDelete) and the slot is recomputed: the previous author's version comes back, or the slot disappears |
| remove a row showing **someone else's** version | nothing is withdrawn; the row stays, and the save says so |
| change a row's slot value (its language) | withdraw-old plus add-new |

- Another user's contribution is never touched. The contributor is always the caller's Spark user
  id, whatever the client posted.
- Repeated saves by one user overwrite one document, so flooding cannot push anyone else's version
  out of the history.
- The save is refused (400) for: two rows on one slot, an invalid slot value, a validator error, a
  row whose own contribution a moderator hid, and an anonymous caller.
- Only the attribute as **posted and writable** counts: a save that leaves out the property, or whose
  attribute-level rights drop it, leaves the contributions alone. Reverts, restores and syncs of the
  target never touch them.
- Everything is written in the request's session and commits atomically with the save. A refused
  save writes none of it.

**Save notices** arrive as ordinary `notify` operations on the save response, in every language
(ng-spark shows the user's chosen one): "Your version of `en/Latn` was withdrawn; *Alice*'s version
is shown now", or "`en/Latn` shows *Alice*'s version, which only its author or a moderator can remove:
it stays". The other contributor is named only with `Attribution.Contributor`. Override any text in
`translations.json` with the keys `contributions.withdrawnNowShowing`,
`contributions.withdrawnNowShowingAnother`, `contributions.withdrawnNoneLeft`,
`contributions.notYoursStays` and `contributions.notYoursStaysAnother`.

### Recompute

A slot's current document is its newest (max `UpdatedAt`) contribution that is not hidden. With none
left, the current document is deleted. Recompute runs on every save, withdrawal, hide, restore, purge
and moderator edit, in the same commit.

### Concurrency

Every current document is written with a pinned change vector. Two contributors racing on the same
slot: one wins, the other gets **409** and nothing of that save is written. The client's conflict flow
is the retry: ng-spark re-fetches, merges and lets the user save again. For contribution rows, only a
second tab of the **same** user is a real conflict; another user's change to a slot is taken
("theirs wins") with a notice. The whole flow is in [concurrent edits](guide-concurrency.md).

### Deleting the target, and rebuild

- A hard delete or purge of the target deletes all its contributions and current documents in the
  same commit. A soft delete keeps them, so a restore brings everything back.
- `IContributions.RebuildCurrentAsync(targetId)` recomputes every current document of a target from
  its contributions. At startup, each declaration's generated shape hash is compared with a stored
  marker; when it changed (for example `History` was turned on), every target of that declaration is
  rebuilt before the host serves requests.
- A system context (a migration, a sync) writes as contributor `system` and skips validators and
  guards.

---

## 6. Moderation

Contributions are not persistent-object saves, so Moderation's own persistence interceptors never see them.
Instead, before writing or withdrawing a contribution the runtime asks every registered
`ISatelliteWriteGuard` (a Spark core contract). Moderation registers one that refuses a **suspended
account** and a **locked** contribution.

Make the contribution type moderatable by adding `IModeratable` to the generated partial class. QnA
maps the author and date to the runtime-owned fields, read-only:

```csharp
public partial class QuestionTranslationsContribution : IModeratable
{
    string? IModeratable.AuthorId { get => ContributorId; set { } }
    // PostedAt likewise, from UpdatedAt
}
```

Flags then go to Moderation's normal review queue, and locks apply per contribution.

**Moderator actions** all run through generated standard screens (the contributions query, reached
from a row's "History (n)" link or as a sub-query on the target's page):

| Action | How | Effect |
|---|---|---|
| Hide / restore / purge one version | `Delete`, SoftDelete's `Restore` / `Purge` on the contribution type | the slot is recomputed in the same commit |
| Remove a whole version | `Delete` on the current type (e.g. `Songs/1/Lyrics/en/Latn`) | every visible contribution of the slot is hidden (reason `version-removed`), the current document is deleted; each one can be restored separately |
| Revert to a version | `POST /spark/po/revert-contribution { objectTypeId, id }` | every **newer** visible contribution of its slot is hidden (reason `reverted`), atomically. No text is rewritten and nobody is re-attributed |

- Revert needs SoftDelete: without it a revert would have to destroy other users' versions, so it is
  refused (400). Reverting to a hidden version is refused too: `Restore` it first.
- Both moderator actions ask the write guards before each hide and are audited after the commit
  through `ISatelliteAuditSink`; Moderation writes a `ModerationAuditEntry`
  (`RemoveContributionVersion` / `RevertContribution`).
- Without `MintPlayer.Spark.SoftDelete`, contributions are hard-deleted and SPARK033 warns.

---

## 7. Rights

There is **no `Contribute` verb**: editing a `[Contribution]` attribute *is* contributing. Contributors
hold `Edit` on the target, narrowed with [attribute-level rights](guide-authorization.md#attribute-level-rights-verbtypeattribute)
(core Spark). The one new verb is **`RevertContribution`**, a reserved verb, so no custom action may
use that name (SPARK023).

With `Song.Lyrics`:

| Who | Rights | For |
|---|---|---|
| contributors | `Edit/Song` plus attribute denies on the other `Song` attributes (or a per-row `GetProtectedAttributesAsync("Edit")`), and `EditNewDelete/Lyrics` on the row type | adding, editing and withdrawing their own versions; **Add** is offered only with `New/Lyrics` |
| history readers | `QueryRead/SongLyricsContribution` and `Read/Song` | the History link, opening a version |
| moderators | `Delete`, `Restore`, `ViewDeleted`, `Purge` on `SongLyricsContribution` | hiding, restoring, seeing and purging single versions |
| moderators | `RevertContribution/SongLyricsContribution` (+ `Read`) | revert |
| moderators | `Read/SongLyricsCurrent` + `Delete/SongLyricsCurrent` | remove a whole version |

Two rules from attribute-level rights matter here:

- **The type right is required.** An attribute grant without the type grant does nothing, so a
  contributor without `Edit/Song` cannot contribute, whatever `Edit/Song/Lyrics` says.
- **A static deny removes the attribute** (no Read: absent; no Edit: read-only and shielded on the
  server). Remember to deny each attribute you add to `Song` later; the analyzer and the security
  posture report warn when a group restricts some attributes of a type but not a new one.

**An owner-only target** (`Edit` limited by a row rule) takes contributions from nobody else: the
contributor needs `Edit` on the row. Open the row rule for `Edit`, then keep the target's own
attributes to the owner with `GetProtectedAttributesAsync("Edit", entity)`, returning every attribute
except the contribution property. QnA does exactly this (`QuestionActions`,
`QuestionTranslatorFormInterceptor`).

**A contribution is visible exactly when its target is.** The generated actions judge every
contribution (load by id, the history query, a moderator action) by `Read` on the target type and the
target's own row rule, so the history of a draft the caller may not see stays hidden. Hidden versions
follow SoftDelete: only `ViewDeleted` holders see them. Raw `ContributorId`s are hidden in the history
grid by default; the resolved `ContributorName` is shown instead.

---

## 8. Client (`@mintplayer/ng-spark/contributions`)

No new npm package and no routes of its own: the pieces are a secondary entry point of
`@mintplayer/ng-spark`, and the history uses `sparkRoutes()`' existing `query/:queryId` page.

```ts
import { provideSparkContributions, sparkContributionRenderers } from '@mintplayer/ng-spark/contributions';

providers: [
  provideSpark(...),
  provideSparkSoftDelete(),          // the Deleted toggle, Restore/Purge of single versions
  provideSparkContributions(),       // "Revert to this version": row menu and contribution page
  provideSparkAttributeRenderers([...sparkContributionRenderers, ...yourRenderers]),
],
```

What that gives you:

- **`contributionAttribution`**: an AsDetail **row** renderer. Instead of three extra columns, each row
  gets one line under its first cell: "by *Alice* · 3 days ago · History (4)". History opens the
  slot's contributions query filtered by `parentId`, `parentType` and the slot values; the query page
  shows those filters as removable chips.
- **`lineDiff`**: a contribution's text against the current version of the same slot, seeded by
  synchronize on the contribution type's string values.
- **Revert**: offered on the contributions query rows and the contribution page to holders of
  `RevertContribution/{type}` (`canRevertContribution` in `GET /spark/permissions/{type}`). It asks
  first, and the response says how many newer versions it hid.
- **Conflict merge**: each loaded contribution row carries `metadata.contribution.own` (a boolean,
  never the contributor id), so the 409 merge treats another contributor's change as theirs-wins.

---

## 9. Diagnostics

All are raised by `ContributionsAnalyzer`; the full table is in [diagnostics.md](diagnostics.md).

| Id | Severity | Raised when |
|---|---|---|
| SPARK025 | Error | a `[ContributionSlot]` type has no stable text form |
| SPARK026 | Error | cardinality does not match the slots: single-valued with slots, or a collection without |
| SPARK027 | Error | the element has no value (non-slot, public settable) properties |
| SPARK028 | Warning | the owner has no public `string Id`, so Spark would not load it as a persistent object |
| SPARK029 | Error | the property lacks Newtonsoft `[JsonIgnore]` (code fix adds it) |
| SPARK031 | Error | the element, or a type containing it, is not `partial` (code fix adds it) |
| SPARK032 | Warning | the element is declared in another assembly; nothing is generated |
| SPARK033 | Warning | SoftDelete is not referenced, so contributions are hard-deleted and reverts are refused |
| SPARK034 | Error | an unsupported declaration (generic owner, static or indexer property, unknown collection type, no parameterless constructor, slot without getter and setter, …) |
| SPARK035 | Error | the element clashes with a generated member (`Key`, `Id`, `TargetId`, `ContributorId`, `UpdatedAt`, …), or carries `[ValueKey]` / `[ValueObject]` |

SPARK030 sits in this range but is unrelated (an MSBuild check of the SPA's `package.json` for
`@mintplayer/ng-spark-auth`). Until you synchronize, SPARK012 warns that the generated type names are
not in the model yet; attribute-level rights checks fall back to the generated CLR class, so being one
build behind is fine.

---

## 10. Limitations

- **The target's id must be assigned client-side** (HiLo or natural id); server-assigned conventions
  are refused.
- **Hydration loads at most 1024 current documents** in the batched request; more slots page with
  further requests.
- **Validator errors are reported as one validation error**: the messages joined, on the first
  attribute.
- **A version a moderator hid cannot be re-saved by its author** (400); only their own withdrawal can
  be undone by saving again.
- **Reverts need SoftDelete.** Without it, hiding means deleting, and reverts are refused.
- **The single-valued (no slots) form** is covered by the generator tests only, not by an HTTP test.
- **A deferred `Delete(id, cv)` of a current document** (on withdrawal or recompute) cannot be taken
  back by a later refusal in the same request; it only commits if a later `SaveChangesAsync` in that
  request runs.
- **No auto-hide on an upheld flag**: a moderator hides the version.
- **No full-text search over contributions.** The current documents are not part of the target's
  index; a separate index would be needed.
- **Renaming the target or the property** renames the generated collections, query and rights:
  migrate the data and the grants.
- An app class named `SongLyricsContributionActions` takes precedence over the generated actions and
  must then serve the `SparkContributionsOfTarget` custom query itself.

## See also

- [Authorization](guide-authorization.md): attribute-level rights, precedence, the per-row hook.
- [Moderation](guide-moderation.md): flags, locks, suspension, the review queue.
- [Soft Delete](../libs/soft_delete/MintPlayer.Spark.SoftDelete/README.md) and
  [History](../libs/history/MintPlayer.Spark.History/README.md).
- [Custom Attribute Renderers](guide-custom-attribute-renderers.md): AsDetail row renderers.
- [Package README](../libs/contributions/MintPlayer.Spark.Contributions/README.md): the full reference
  and the client contract.
