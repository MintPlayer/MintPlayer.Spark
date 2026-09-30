# PRD — `MintPlayer.Spark.Contributions`: per-user contributions with latest-wins

The order of work is in [contributions_plan.md](contributions_plan.md). This PRD was grilled with the
owner on 2026-09-30. Decisions marked **C#** are the owner's; **T#** are Claude's, with reasons
recorded.

**Goal:** let an application declare that a property of a persistent object is filled by user
**contributions**. Each user owns and overwrites only their own contribution, the **latest
non-hidden contribution per slot is current**, and moderators remove a bad contribution so the
previous one becomes current. It needs no RavenDB revisions, so it works on the Community licence (2
revisions, 45 days).

**First consumer: MintPlayer song lyrics.** One song has several versions, e.g. BTS "Mikrokosmos" in
Korean (Hangul), Korean romanized (Latin), a Cyrillic transliteration, and later an English
translation. They are rendered side by side and synced to playback. This replaces MintPlayer's
`PRD-Spark-Completion.md` **D29** (one shared `Song.Lyrics` text). That MintPlayer-side change is not
in this repository; see §7.

---

## 1. What exists (investigated 2026-09-30)

- **MintPlayer today:**
  - `Song.Lyrics` is one `string?` (`MintPlayer.Domain/Entities/Song.cs:23`).
  - `LyricsTimings` are kept per recording, as start times parallel to `Lyrics.Split('\n')`
    (`LyricsTiming.cs:45-50`).
  - Songs have RavenDB revisions enabled; that is the only lyrics history.
  - Legacy SQL had one lyrics row per `(SongId, UserId)` (authorship, not history).
- **Spark History** configures native revisions. On Community, merged limits above 2 revisions / 45
  days refuse startup (`libs/history/.../RevisionsConfigurator.cs:183-208`). Nothing collapses
  revisions by time.
  - RavenDB 7.2.6 has `DeleteRevisionsOperation` (by change vectors or a time window), but no
    coalescing.
- **Spark Moderation** (`libs/moderation`) moderates opt-in `IModeratable` entities:
  - it stamps an immutable `AuthorId` / `PostedAt`
  - flags and the review queue
  - reputation-gated privileges (groups in `security.json`)
  - locks (`ModerationLocks/{targetId}`)
  - suspension, the new-account throttle, and audit
  
  **It owns no content.** Its README says its documents are not persistent objects, and "suggested
  edits" are explicitly out of v1 (`docs/issue_460_PRD.md:259`). An upheld flag does not hide the
  target. Owner-only editing is app logic (QnA row filter, `apps/QnA/QnA/Actions/AnswerActions.cs:18-26`).
- **Natural ids** exist through `IHasNaturalId` (`libs/spark/MintPlayer.Spark.Abstractions/IHasNaturalId.cs`)
  and `UseNaturalIds()`.
- **Persistent-object pipeline, for a property that is not stored:**
  - `[Newtonsoft.Json.JsonIgnore]` keeps a property out of the RavenDB document (the store uses
    Newtonsoft, `SparkDocumentStoreConventions.cs:28`), while it stays a model attribute.
    `[IgnoreProperty]` would remove the attribute from the model entirely.
  - `IPersistentObjectInterceptor.OnBeforeSaveAsync` can store more documents in the request session,
    which commits atomically.
  - **Gap:** the save loads the *stored* entity (`DefaultPersistentObjectActions.cs:260`) and diffs
    AsDetail rows against it (`EntityMapper.cs:677-766`, `MatchStoredRows :811`). With the property
    not stored, every row looks new, Edit/Delete rights are never checked, removals are never
    detected, and `Before` (`DatabaseAccess.cs:305`) is empty.
- **RavenDB reads, measured 2026-09-30 (RavenDB.TestDriver 7.2.6):**
  - `LoadStartingWithAsync` defaults to **25** documents. It pages by id, not by date, and
    `matches`/`exclude` filter on the id only.
  - The prefix `"Songs/1"` also matched `Songs/12`, so **prefixes must end in `/`**.
  - A map-reduce "max-by" is correct on Corax and Lucene, but **stale right after a write**.
  - The 7.2.6 client has
    `IAsyncLazySessionOperations.LoadStartingWithAsync(idPrefix, matches, start, pageSize, exclude, startAfter, token)`,
    so a lazy `Load` and a lazy `LoadStartingWith` go out in one request.
- **Generator packaging precedent:** `libs/all_features/MintPlayer.Spark.AllFeatures/MintPlayer.Spark.AllFeatures.csproj:40-65`
  embeds generator DLLs through `<None Pack="true" PackagePath="analyzers/dotnet/cs">`, and
  `spark-allfeatures.targets:25-37` wires the analyzer `ProjectReference` in-repo. Only
  `LibraryGenerators/Generators/ValueObjectKeyGenerator.cs:41` uses `ForAttributeWithMetadataName`.
  Snapshot tests (Verify) live in `tests/MintPlayer.Spark.SourceGenerators.Tests/Snapshots/`.
- **`TriggersRefresh`** is `bool?` (`EntityTypeDefinition.cs:304`). The client already refreshes free
  text on blur and discrete editors immediately (`ng-spark/po-form/src/refresh-coordinator.ts:108`).
  It appears in only 3 model JSON files (Fleet `Car.json:403`, HR `CarreerJob.json:76`, CodeCoverage
  `GateSettings.json:118`) and is not part of the model hash.

---

## 2. Decisions

### Owner decisions (grilling 2026-09-30)
- **C1 — Per-user contribution documents, no revisions.**
  - Each user has exactly one document per slot and overwrites only that one.
  - Repeated saves by one user merge into one entry by construction; this is the "5-minute" rule
    without a timer.
  - Flooding can't evict other people's contributions.
  - Works on Community.
- **C2 — The slot key is language + script.** For lyrics that is `{languageCode}/{scriptCode}`, e.g.
  `ko/Kore`, `ko/Latn`, `en/Latn`. Translations are covered from day one.
- **C3 — Scripts come from a Spark lookup reference** of scripts only (ISO 15924: `Latn`, `Cyrl`,
  `Kore`, `Jpan`, `Hani`, `Arab`, `Grek`, `Hebr`, `Thai`, `Deva`, …), rendered as a `bs-select`.
  - The script is detected from the text. The client refreshes on blur (`triggersRefresh: "Auto"`),
    and `OnRefreshAsync` sets `Script`.
  - **The save re-detects on the server and is authoritative.** It fills an empty script and refuses a
    clear mismatch.
  - **Detection rules:** majority of letters by script, ignoring the Unicode Common script. Any kana →
    `Jpan`. Any Hangul → `Kore`. Only Han → `Hani`. .NET has no `\p{Script=}`, so the server uses
    Unicode ranges.
- **C4 — The original version defines the lines.** The other versions of the same song must match its
  line count, blank lines included; a save that doesn't match is refused with both counts. If the
  original's line count changes, the others are flagged out of sync but not blocked. Playback timings
  stay per recording and are shared by all versions.
- **C5 — A separate library: `MintPlayer.Spark.Contributions`** (+ `.Abstractions`). Not in Moderation,
  which owns no content and scopes out suggested edits. Contributions integrate with Moderation
  (`IModeratable`) and SoftDelete by interface.
- **C6 — The declaration API is an attribute on the target's property:**
  ```csharp
  public partial class Song {
      [Contribution]
      public List<Lyrics> Lyrics { get; set; } = new();   // also Lyrics, Lyrics[], IList<Lyrics>, …
  }
  public partial class Lyrics {
      [ContributionSlot] public string Language { get; set; } = "";
      [ContributionSlot] public string Script { get; set; } = "";
      public string Text { get; set; } = "";
  }
  ```
  - The generator derives the owning type, the contribution name (property name) and the element type
    from the property.
  - The slots are the element's `[ContributionSlot]` properties, in declaration order (which is the id
    order).
  - `[ContributionSlot]` is inert when no `[Contribution]` points at the type.
  - No partial properties (owner: hold off).
- **C7 — Source generator plus analyzer, embedded in the library's nupkg.**
  - A `MintPlayer.Spark.Contributions.SourceGenerators` project (`netstandard2.0`,
    `IsRoslynComponent=true`, **`IsPackable=false`**) is packed into `analyzers/dotnet/cs` of
    `MintPlayer.Spark.Contributions`, following the AllFeatures pattern.
  - It embeds **only its own** generator DLL.
  - It uses `ForAttributeWithMetadataName`, so records are seen.
  - A code fix may edit an element type in another project of the solution, as
    `MintPlayer.Dotnet.Tools`' `InterfaceImplementationAnalyzer.CodeFix` does. Where the element type
    exists only as compiled metadata, the diagnostic goes on the `[Contribution]` property.
- **C8 — Reads use a current document that holds the text (option X).**
  - `{target}/{name}/{slot…}` holds `{ <value properties>, ContributorId, ContributionId, UpdatedAt }`
    and is written in the **same transaction** as the contribution.
  - A song page is **one request**: a lazy `Load<Song>` plus a lazy `LoadStartingWith` over the
    current-document prefix.
  - The Song is **never** written by lyric edits: no re-index, no Song revision consumed, no
    contention with metadata edits.
  - The duplicate text is a cache the library owns and can rebuild.
  - Rejected:
    - pointers on the Song plus `Include` (Y): it rewrites the Song, which consumes its 2 Community
      revisions and re-indexes it
    - picking the latest client-side (Z): the payload grows with contributors on every view
    - a map-reduce index: stale after a write
- **C9 — `TriggersRefresh` becomes an enum** (preview, no backward compatibility):
  ```csharp
  [JsonConverter(typeof(JsonStringEnumConverter))]
  public enum ERefreshTrigger { None, Auto, ValueChanged, Blur }
  public ERefreshTrigger? TriggersRefresh { get; set; }   // absent = None
  ```
  - **`Auto`** is today's `true`: free text refreshes on blur; lookup, reference, boolean, date,
    datetime, enum and colour refresh immediately.
  - **`ValueChanged`** on free text is debounced (300 ms, like the search box) inside
    `RefreshCoordinator`, and blur or save flushes it.
  - **`Blur`** on a discrete editor acts as `ValueChanged`, and verify-model warns.
  - The 3 model JSON files are hand-edited to `"Auto"`, with no converter for `true`.
  - The wire value is PascalCase, like `referenceDisplayType`.
- **C10 — No backward compatibility** (the libraries are in preview).

### Technical decisions
- **T1 — Id scheme, with separate prefixes so no glob is needed:**
  - **contribution:** `{targetId}/{Name}Contributions/{slot1}/{slot2}/User/{userId}`, e.g.
    `Songs/1234/LyricsContributions/ko/Kore/User/567`
  - **current:** `{targetId}/{Name}/{slot1}/{slot2}`, e.g. `Songs/1234/Lyrics/ko/Kore`
  - The user id's own slashes (`MintPlayerUsers/{guid}`) are kept, because the user segment is always
    last.
  - Readers always use prefixes ending in `/`.
- **T2 — Slot types must have one stable, culture-independent text form.**
  - **Allowed:** `string`, any `enum` (formatted by **name**), `byte`/`sbyte`/`short`/`ushort`/`int`/
    `uint`/`long`/`ulong`, `Guid`, `bool`, and their nullable forms (with `null` refused at runtime).
  - **Refused by the analyzer:** `float`/`double`/`decimal` and `DateTime`/`DateTimeOffset`.
  - The generated `GetId` formats with `CultureInfo.InvariantCulture`.
  - **At runtime,** every formatted slot value must match `^[A-Za-z0-9-]{1,32}$`, or the save is
    refused; a `/` would shift the id structure.
- **T3 — Generated code, per `[Contribution]` property.** Shown here for `Song.Lyrics` with element
  `Lyrics`:
  - `partial class LyricsContribution : IContribution`: the element's value properties, the slots,
    `TargetId`, `ContributorId`, `UpdatedAt`, and a static `GetId(targetId, slots…, userId)` with an
    explicit `IHasNaturalId` member.
  - `partial class LyricsCurrent : ICurrentContribution`: the value properties, the slots,
    `ContributorId`, `ContributionId`, `UpdatedAt`, and a static `GetId(targetId, slots…)`.
  - A metadata class holding slot accessors, prefixes, the element ↔ contribution ↔ current mapping,
    and a lazy-load helper. There is no reflection at runtime.
  - **Registration:** `spark.AddContributions()` picks up every generated contribution in the
    assembly.
  - Both types are `partial`, so the app can add `IModeratable` or `ISoftDeletable` itself
    (`partial class LyricsContribution : IModeratable { … }`).
  - Both are ordinary persistent objects with model JSON (via synchronize) and `security.json` rights.
- **T4 — Analyzer rules:**
  - a slot type outside T2
  - a single-valued `[Contribution]` property whose element has slots (zero slots = one current per
    target, which is valid)
  - an element with no value properties
  - an owner that isn't a Spark persistent-object entity
  - `[Contribution]` on a property without `[Newtonsoft.Json.JsonIgnore]` (a code fix adds it)
- **T5 — New framework seam `IPersistentObjectInterceptor.OnAfterMaterializeAsync(MaterializeContext { Entity, Session, … })`.**
  - It runs on **every** path that loads an entity for persistent-object work: `MaterializeAsync`
    (`DefaultPersistentObjectActions.cs:236`), the save's `LoadAsync` (`:260`), and the `Before`
    side-session load (`DatabaseAccess.cs:305`).
  - Spike S-C2 lists any others.
  - With the entity hydrated, AsDetail row identity, the New/Edit/Delete row rights and the `Before`
    diff work unchanged.
  - It is generally useful for any satellite data.
- **T6 — Runtime interceptor behaviour** (`ContributionsInterceptor`):
  - **After materialize:** fill the `[Contribution]` property from the current documents, with a lazy
    prefix load batched with the entity load where the path allows.
  - **Before save:** diff `Before` against `Entity` per slot.
    - **Added or edited row:** upsert *the current user's* contribution (`UpdatedAt = now`, UTC
      `DateTime` rather than `DateTimeOffset`, per project memory on index projections), then set the
      current document from it.
    - **Removed row:** withdraw the current user's own contribution for that slot (delete, or
      soft-delete if the type is `ISoftDeletable`), then recompute current.
    - **Never** touch another user's contribution.
    - **Validation:** slot values (T2), script consistency (C3), line count (C4, through an app hook,
      not the library).
  - **Recompute current:** a prefix load of that slot's contributions with an explicit `pageSize`,
    excluding hidden ones, picking the max `UpdatedAt`. If none remain, delete the current document.
    This runs on hide, delete or unhide of a contribution, including moderator actions and SoftDelete.
  - **Concurrency:** optimistic concurrency on the current document, retried on
    `ConcurrencyException` (S-C4).
  - **Rebuild:** `IContributions.RebuildCurrentAsync(targetId)` for repair.
- **T7 — A single-slot app hook for domain rules.** `IContributionValidator<TElement>` supplies the
  script detection and line-count rules for lyrics. The library only calls it.

---

## 3. Requirements

- **R1.** An app declares contributions with C6 alone, plus `spark.AddContributions()`. No hand-written
  ids, current-document code or interceptors.
- **R2.** Loading a target is one database request, including all current versions. Measured with
  `session.Advanced.NumberOfRequests`.
- **R3.** Saving changes to one slot writes the user's contribution and the current document in one
  transaction. The target document is not modified when only contributions changed.
- **R4.** A user can only create, overwrite or withdraw their own contributions. Tampering with the
  user segment of an id, or with `ContributorId`, is refused.
- **R5.** Hiding or deleting the current contribution makes the previous non-hidden one current,
  immediately (no index lag).
- **R6.** The analyzer enforces T2 and T4. The generator output is snapshot-tested.
- **R7.** The `TriggersRefresh` enum (C9) replaces the `bool` across C#, TS, the 3 model JSON files,
  the docs and the tests.
- **R8.** Everything works on the RavenDB Community licence.

## 4. Spikes

- **S-C1 — Rights model** (owner question, see §5 Q1). Saving the Song persistent object requires
  `Edit/Song` (`DatabaseAccess.cs:212-220`). Find out whether Spark can let a user save *only* the
  contribution property: attribute-level rights, a dedicated custom action, or an `Edit` gate that
  only the interceptor evaluates. Otherwise every lyrics contributor must also be able to edit the
  song's title.
- **S-C2 — Every entity-load path for persistent-object work.** Enumerate them (custom actions,
  sub-queries, breadcrumbs, retry actions, replication, History revert) to scope T5.
- **S-C3 — AsDetail row identity for the element type.** Find out whether an AsDetail element needs a
  `[ValueKey]` property, and whether a generated key (the slot tuple, e.g. `"ko/Kore"`) can serve.
  See memories `project_spark_asdetail_row_identity.md` and `reference_raven_nested_id_initializer.md`.
- **S-C4 — Optimistic concurrency.** Check whether Spark's request session enables it. If not, check
  that setting a change vector on `StoreAsync` for the current document is enough, and what a
  `ConcurrencyException` looks like to the client.
- **S-C5 — Interceptor ordering with SoftDelete and Moderation.** A SoftDelete `Replace()` of a
  contribution delete must still trigger the recompute. Moderation's lock and suspension checks must
  run before contributions write anything.
- **S-C6 — Lazy batching.** Confirm that lazy `Load` + lazy `LoadStartingWith` really is one request
  (`NumberOfRequests == 1`), and check the payload at 20 versions.

## 5. Open questions for the owner

- **Q1 — Who may contribute?** This depends on S-C1. Recommended: contributing to `Song.Lyrics` must
  **not** require `Edit/Song`. Instead a `Contribute/Lyrics` right (or `New`/`Edit` on the contribution
  type) gates it, and Moderation's reputation groups grant that right.

## 6. Risks

- **T5 missing a load path** silently brings back "every row is new". There is one test per path.
- **Generator/runtime mismatch.** Both ship in one package at one version, which prevents it.
- **The current document is a cache.** A manual database edit can desync it; `RebuildCurrentAsync`
  repairs it.

## 7. Done elsewhere

- **MintPlayer's own code, in the MintPlayer repository, in a session running there** (a hook refuses
  cross-repo edits from this session). It covers:
  - the `Song`/`Lyrics` types
  - the scripts lookup
  - script detection and line-count validation (T7)
  - the side-by-side renderer
  - replacing D29
  - migrating `Song.Lyrics` into `ko/Kore`-style contributions attributed to a system user

  It is sequenced after this library is published.

## 8. Out of scope (genuinely not being done)

- **Auto-hiding a contribution when a flag is upheld** (a possible Moderation addition later).
- **Full-text search over contributions** (a separate index over the current documents, when needed).
