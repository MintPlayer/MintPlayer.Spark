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
  - **Why an enum at all** (owner, 2026-09-30): the client already refreshes free text on blur only
    (`refresh-coordinator.ts:108`, `spark-po-form.component.ts:646-678`), so the `bool` worked. The
    owner chose the enum anyway "while the framework is still in preview".
  - **A `[Flags]` enum was considered and deferred** (Claude's recommendation; the owner raised it and
    didn't push back):
    - `ValueChanged|Blur` equals `ValueChanged`, because a debounced change is flushed on blur anyway.
    - A discrete editor never fires blur.
    - `Auto` is "decide per editor", not a combination of events.
    - Revisit when a third independent trigger exists, e.g. `EnterKey`.
  - **Blast radius, measured:** 3 model JSON files, 2 C# readers and 5 TS reads. `triggersRefresh` is not
    in the model hash.
  - **As built:** `7014143a`. A stale boolean stops startup loudly (`6663391e`,
    `ModelLoaderTests.A_boolean_triggersRefresh_stops_the_process_instead_of_dropping_the_type`).
    Without that, `ModelLoader.cs:87-96` would have silently dropped the whole type.
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
  - `partial class SongLyricsContribution : IContribution`: the element's value properties, the slots,
    `TargetId`, `ContributorId`, `UpdatedAt`, and a static `GetId(targetId, slots…, userId)` with an
    explicit `IHasNaturalId` member.
  - `partial class SongLyricsCurrent : ICurrentContribution`: the value properties, the slots,
    `ContributorId`, `ContributionId`, `UpdatedAt`, and a static `GetId(targetId, slots…)`.
  - A metadata class holding slot accessors, prefixes, the element ↔ contribution ↔ current mapping,
    and a lazy-load helper. There is no reflection at runtime.
  - **Registration:** `spark.AddContributions()` picks up every generated contribution in the
    assembly.
  - Both types are `partial`, so the app can add `IModeratable` or `ISoftDeletable` itself
    (`partial class SongLyricsContribution : IModeratable { … }`).
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

### 4.1 Spike results (measured or read, 2026-09-30)

**S-C1 Rights: ❌ not possible today.**
- Every Song save first demands `Edit/Song` (`DatabaseAccess.cs:212-220`, called at `:280`). Only then
  do the row Edit gate (`:327`) and the disabled-action gate (`:345`) run.
- There are no per-attribute rights. `IsWritableBySchema` (`EntityMapper.cs:574-583`) only uses static
  model flags.
- The only per-caller write limit is the virtual `GetProtectedAttributesAsync` plus
  `ShieldProtectedAttributesAsync` (`DefaultPersistentObjectActions.cs:520-552`). It puts stored values
  back, but only on the Actions class, and only after `Edit/Song` has already passed.
- **The element side is already correct.** `Edit`, `New` and `Delete` on the element type are
  enforced per row (`EntityMapper.cs:720-753`).
- **Any verb can already be granted:** a right is just `"{action}/{target}"` (`PermissionService.cs:37-39`),
  so `Contribute/Song` needs no schema change. *(The `Contribute` verb itself was later SUPERSEDED by attribute-level rights, §5 Q11–Q13.)*
- **Rejected alternatives:**
  - A custom action plus a modal: its save still needs `Edit/Song`, or runs as the system context,
    which skips Moderation, WITH CHECK and the row gates.
  - Contributions as their own PO in a sub-query: its authorization is correct, but it drops C6.
- **Result → framework addition F4.**

**S-C2 Load paths: 16 sites, 4 need hydration.**
- **Needs hydration:**
  - `LoadManyAsync` right after `DefaultPersistentObjectActions.cs:121`. **Not** inside the virtual
    `MaterializeAsync`, because an app override would skip it. All 14 endpoint callers go through it
    (Get, Refresh, Update pre-read, Delete, SubQueryParent, New parent, DeleteMany, DeleteRow parent,
    CustomAction parent and query parent, History, SoftDelete, ModerationTargets).
  - `GetPersistentObjectsByIdAsync` (batched).
  - The save reload (`:260`).
  - The `Before` side-session load (`DatabaseAccess.cs:305`).
- **Must be idempotent.** `Update.cs:43` pre-reads in the same session, so `:260` gets back the tracked,
  already-hydrated instance.
- **Warn** when an app's `OnSaveAsync` override skips `:260`, following the `AnnounceBeforeSaveBypass`
  pattern.
- **❗ Two paths would silently withdraw contributions:**
  - **History revert** (`SparkHistory.cs:93`) posts every attribute of a revision, and its `Lyrics` is
    empty because it was never stored.
  - **A full replication sync** (`SyncActionHandler.cs:46/:113`) turns a missing `Lyrics` into `[]`
    (`EntityMapper.cs:704`).
  
  **Both must drop `[Contribution]` attributes** (framework addition F2), as partial sync already does
  (`:120`).
- **Queries** (`RowSecurityGate.cs:211`) need no hydration as long as the property isn't a grid column.

**S-C3 Row identity.**
- A key is required, but it doesn't have to be settable or a Guid. Registration only needs a getter
  (`SparkValueObjects.cs:35`), and `TryWriteId` (`EntityMapper.cs:585-597`) skips read-only keys.
- **It must be deterministic:** `[ValueKey] public string Key => $"{slot1}/{slot2}"` (get-only). A Guid
  initializer would mint a new key per load, so every save would become New+Delete (the trap in the
  memory note `reference_raven_nested_id_initializer`).
- **Changing a slot on an existing row** is an Edit in the mapper and withdraw-old plus add-new in the
  contributions diff, which is correct. Two rows landing on one slot are refused.
- **Generators can't see each other's output,** so the element must NOT rely on `[ValueObject]`: its
  generator would emit and register a Guid `Id`. The Contributions generator emits the key **and** its
  `SparkValueObjects.Register(...)` module initializer itself, and SPARK017 accepts `[Contribution]`
  elements (framework addition F3).

**S-C4 Concurrency.**
- The request session is plain `OpenAsyncSession()` (`SparkMiddleware.cs:183-184`), which is
  **last-write-wins**. Measured: 20 parallel read-modify-writes lost 19 updates. With a change vector
  and a retry, all 20 landed.
- **A change vector scoped to the current document works.**
  - Update: `StoreAsync(current, cv, id)`.
  - Create: `StoreAsync(current, "", id)`.
  - Delete: `Delete(id, cv)`.
  
  A contribution plus a stale current document in one `SaveChanges` rolls back both (atomic), so the
  whole unit can be retried (≤3 attempts). Session-wide optimistic concurrency is not used; it would
  change every other save in the request.
- **❗ Core bugs found along the way (F7):**
  - The PO save's etag check compares in a separate session, then writes **without** a change vector
    (`DatabaseAccess.cs:319-325`, `DefaultPersistentObjectActions.cs:283-284`). That's a race window,
    and the check is skipped entirely without an etag.
  - A raw `Raven.Client.Exceptions.ConcurrencyException` is caught nowhere in core, so it most likely
    becomes a 500 instead of the 409 envelope (`Update.cs:79-86`). This is inferred; an HTTP test
    settles it.
  - The RavenDB messages contain change vectors, so they must never reach the client.

**S-C5 Interceptor ordering.**
- There is no `Order`: before-hooks run in DI registration order and after-hooks in reverse
  (`PersistentObjectInterceptorPipeline.cs:50-84`). QnA registers SoftDelete → History → Moderation.
- **Required:** SoftDelete → History → Moderation → **Contributions last**, so a suspension or lock
  refuses the Song save before anything is written. This is enforced (F5).
- **The documents Contributions writes are not PO saves,** so Moderation never sees them.
  **Contributions checks suspension and locks itself** through the Moderation integration.
- **A soft delete of a contribution** fires `OnAfterDeleteAsync` with `WasReplaced`, but that happens
  after the commit, so it isn't atomic. **Recompute in before-delete instead,** registered after
  SoftDelete and treating the soon-deleted document as hidden, so the current document commits with
  the delete (`DatabaseAccess.cs:517-522`). Restore is a save with `Operation == Restore`, so it is
  recomputed in before-save.
- **❗ A refused save leaves interceptor-stored documents tracked.** WITH CHECK
  (`DefaultPersistentObjectActions.cs:282`) runs after the before-hooks, but on refusal only the target
  is evicted (`DatabaseAccess.cs:374-382`, `:541`). The next `SaveChangesAsync` in that request would
  commit the orphaned contribution and current documents (F6).

**S-C6 Lazy batching: ✅.**
- A lazy `Load` plus a lazy `LoadStartingWith` was **1 request**. Awaiting `.Value` without
  `ExecuteAllPending` also batches.
- The `Songs/1/Lyrics/` prefix returned exactly the current documents: 0 contributions and 0 from
  `Songs/10`. Without the trailing `/` it returned 221 or 243.
- The default page size is 25, so an explicit `pageSize` is needed.
- A missing target: 1 request, `null`.
- About 62.6 K characters of JSON for 21 versions of ~3 KB each.

**F7 as implemented (M1b, `6660f8b9`), red → green through `ConcurrentWriteRaceTests`:**
- **Before the fix,** 3 of 5 tests returned 200/204 and silently overwrote a concurrent write: saves
  with and without an etag, and a replaced (soft) delete.
- **Saves of existing documents** write with `StoreAsync(entity, expectedCv, id)`, where the expected
  change vector is the client's etag, or otherwise the version this save loaded. A RavenDB
  `ConcurrencyException` becomes `SparkConcurrencyException` → 409 in `DatabaseAccess`. Create,
  Delete, DeleteMany and SoftDelete restore map it too.
- **The early etag check is kept.** It gives a fast 409, and it protects `OnSaveAsync` overrides that
  never call the base.
- **Behaviour change:** saves without an etag, replication syncs and restores that race a write now
  get a **409 instead of last-write-wins**; Spark Messaging retries the sync.
- **Still unprotected: hard deletes.** The RavenDB 7.2.6 client ignores the check mode on
  `Delete(entity)`, and `Delete(id, cv)` leaves a deferred command behind after a failure.
  - A save that changes nothing sends no write, so it gets no check.

### 4.2 Framework additions the spikes require (same unit of work)

- **F1:** `OnAfterMaterializeAsync(MaterializeContext)`, idempotent, called at the four S-C2 sites,
  with a bypass warning for app `OnSaveAsync` overrides. Replaces T5's placement.
- **F2:** History revert and full replication sync drop `[Contribution]` attributes. Generic form:
  attributes flagged `IsSatellite`/non-stored are never posted by revert or sync.
- **F3:** Row-key registration comes from the Contributions generator, and SPARK017 accepts
  `[Contribution]` element types.
- **F4 (SUPERSEDED by attribute-level rights, see §5; kept for history):** A contribute-only save path. When the caller lacks `Edit/T` but holds `Contribute/T`, and
  `T` has `[Contribution]` properties, the save is allowed with **every non-contribution attribute
  shielded** (stored values restored, reusing `ShieldProtectedAttributesAsync`, with the list supplied
  by a registry and not only by the Actions class). The row gate and the disabled-action gate judge
  `Contribute`. Client `IsValueChanged` flags are never trusted. Element-type New/Edit/Delete stay as
  they are. **Depends on Q1.**
- **F5:** An ordering mechanism for interceptors (`Order`, or a startup check that Contributions is
  registered after SoftDelete and Moderation).
- **F6:** On a refused save or delete, `DatabaseAccess` evicts every document an interceptor stored
  during that operation, not only the target. The alternative is an "on failure" hook.
- **F7:** Core concurrency fixes. The PO save writes with the checked change vector
  (`StoreAsync(entity, cv, id)`) to close the race, and a RavenDB `ConcurrencyException` maps to the
  existing 409 envelope, with an HTTP regression test.

## 5. Owner decisions from the second grilling round (2026-09-30)

- **Q1 → A (SUPERSEDED by Q11–Q13: no `Contribute` verb; contributors hold `Edit/Song` plus attribute denies):** a contribute-only save (F4). `Contribute/<Target>` without `Edit/<Target>` is allowed,
  with every non-contribution attribute shielded. A reflection-driven test asserts that every
  attribute stays unchanged.
- **Q2 → A:** reserved verbs. `[assembly: SparkReservedActions(typeof(XRights))]` points at a class of
  `const string` verbs (read by the analyzer from referenced assemblies through
  `IFieldSymbol.ConstantValue`, and by a startup check through reflection).
  - A custom action named like a reserved verb is an **error**.
  - `SecurityConfigurationAnalyzer.BuiltInActions` (`:99-103`) and Moderation's two hand-kept copies
    (`ModerationRights.cs:49`, `ModerationInitCommand.cs:21`) are replaced by this.
  - Core, SoftDelete, History, Moderation and Contributions each declare their own verbs.
- **Q3 → A:** removing a row withdraws only **your own** contribution for that slot. The form reports
  whose version is now shown. Removing a whole version is `Delete` on the generated current type
  (moderators).
- **Q4 — verbs:**
  - **New:** ~~`Contribute`~~ (dropped, see Q11–Q13) and `RevertContribution` (on the contribution type: make it
    current by hiding every newer non-hidden contribution in its slot, atomically and audited, never
    re-attributing).
  - **Reused:** `Query`/`Read` (history), `Delete` (hide), `Restore`, `ViewDeleted`, `Purge` on the
    contribution type, and `Delete` on the current type (remove a version).
  - The generated contribution type is `ISoftDeletable` when SoftDelete is referenced; the analyzer
    warns otherwise.
- **Corrections vs new versions** (confirmed): the list holds one row per slot (the current version).
  Editing a row's text upserts **your** contribution for that slot, and the previous author's stays in
  the history. Adding a row with a new slot creates a version. Unchanged rows write nothing (the diff
  is by value against the hydrated `Before`).
  - Editing a row's *slot* (its language or script) is withdraw-old plus add-new. So on someone else's
    version, the old version stays and reappears; the form says so.
- **Q5 → A** (MintPlayer): `Song.OriginalLyrics` (see §9.5).

- **Q6 → A: attribution is the app developer's choice, per declaration.**
  - It is set with `[Contribution(Attribution = ContributionAttribution.Contributor | UpdatedAt | History)]`.
    The default is `None`: nothing extra is stored, loaded or shown.
  - The generator emits only what's asked for:
    - the read-only, shielded row attributes `ContributorName` / `UpdatedAt` / `ContributionCount`,
      marked with a `contributionAttribution` rendering hint
    - the count on the current document (only with `History`)
    - a batched name lookup in the same lazy request (only with `Contributor`); names are resolved at
      read time, never copied
  - **An ng-spark AsDetail row renderer** shows "by *Alice* · 3 days ago · History (4)", where History
    opens the slot's contributions query. MintPlayer enables it; other apps may not.
  - The generator emits a shape hash. When it changes (e.g. `History` is turned on later),
    `RebuildCurrentAsync` runs once for that type at startup.

- **Q7 → A: moderation runs through generated standard screens.**
  - A generated `{Target}{Property}Contributions` query over the contribution type is filtered by target and
    slot. It has the row actions `Delete`/`Restore`/`RevertContribution` and a `ViewDeleted` toggle
    via SoftDelete.
  - It is reached from the row's "History (n)" link, and optionally as a sub-query on the target
    page. Opening a contribution shows a line diff against the current text.
  - Flags go to Moderation's existing review queue, because the contribution type is `IModeratable`.
  - **No new npm package and no extra Angular routes for consumers.** The generated query uses the
    existing `sparkRoutes()` query routes. The two new client pieces (the attribution row renderer and
    the line-diff renderer) live **in `@mintplayer/ng-spark`**, selected by rendering hints. This is
    deliberately unlike `ng-spark-auth`. The Contributions library is server-side only.
  - Test: hidden contributions and their text are visible only with `ViewDeleted` and `Read`.

- **Q8 → A, attributed to the owner.** Each migrated `Song.Lyrics` text becomes a contribution by a
  configurable **migration author id**, set to the owner's `MintPlayerUsers/{guid}`, since the owner
  added all existing lyrics. It falls back to a system "Migration" user when unset.
  - **The slot is `und/Latn`:** all existing lyrics are Latin script, and the language is
    undetermined (BCP 47 `und`).
  - The owner fixes the language later by a normal contribution (withdraw-old plus add-new). Changing
    the original's language updates `Song.OriginalLyrics` in the same save (MintPlayer validator).
  - The migration asserts one text per song, so no `und/Latn` collision is possible.

- **Q9 → B: names always come from the target plus the property, with no override.** For example
  `SongLyricsContribution`, `SongLyricsCurrent`, and the query `SongLyricsContributions`.
  - They are unique by construction, even when two targets share an element type.
  - These names become RavenDB collection names and `security.json` rights, so they are stored data.
    Documented: renaming the target or the property later means migrating collections and grants.

- **Q10 → A: generic three-way conflict resolution in ng-spark (M1c).** The owner raised it after M1b.
  - **Legacy MintPlayer**
    (`legacy/MintPlayer.Data/Entities/Subject.cs:21-23`, `Repositories/SongRepository.cs:224-228`,
    `pages/song/edit/edit.component.ts:137-140`):
    - a `rowversion` compared by hand, with a race
    - a 409 carrying the full server DTO
    - an inline banner with "Current database value: X" per scalar field, and no merge
    - a bug: it never adopted the new stamp, so saving again gave a 409 every time
    - no coverage of lists
  - **Spark today** (`ng-spark/po-edit/src/spark-po-edit.component.ts:269-278`) only shows "Concurrency
    conflict" and keeps the form values.
  - **Design.** On a 409, ng-spark re-fetches the PO (a normal GET, so read rights and row security
    apply; the server keeps its bare 409 with no change vectors). It then compares **base** (as
    loaded), **mine** and **theirs**:
    - **Scalar attributes:** changed only by me → keep mine; changed only by them → take theirs; both
      changed to different values → a **true conflict**.
    - **AsDetail lists are compared per row, keyed by the required `[ValueKey]`:**
      - A row added, removed or changed on one side only is taken from that side.
      - A row added on both sides with the same key, or removed on one side and edited on the other,
        is a true conflict (keep or remove).
      - A row changed on both sides recurses with the same per-attribute rule.
      - Row order follows *theirs*, with rows only I added appended.
    - **Contribution rows** only conflict between two tabs of the same user.
  - **The dialog lists only true conflicts:** Mine / Theirs per attribute (grouped by row), rendered
    with the normal attribute renderers, plus "keep all mine" and "take all theirs". It shows who and
    when if the target is `IAuditable`, and lists what *they* changed.
  - **After resolving:** the form is rebased onto the fresh etag (fixing legacy's re-409 bug).
    **Nothing is saved automatically;** the user reviews and saves, and server validation and business
    rules run again (the guard against cross-field combinations).
  - Reuses the normalization in History's `revision-diff.ts`. Lives in `@mintplayer/ng-spark` (no new
    package).

### Attribute-level rights, replacing F4 (grilled 2026-09-30, Q11–Q14)
Background:
- Vidyano semantics were read from its decompiled server source (`C:\Repos\Vidyano.Service\...\SecurityScope.cs`,
  `Repository\VidyanoModelContext.cs`) and ~175 real `security.json` files.
- A read-path audit of Spark was done, and the Spark security model was mapped (see the investigation
  notes in the session).
- **F4's `Contribute/T` verb is dropped:** editing a `[Contribution]` attribute *is* contributing.

- **Q11 → the full basket in this PR:** Query, Read and Edit (and New) per attribute, in **Spark
  core**, because enforcement must sit in every read path and all of them are core. Contributions,
  Moderation, History and SoftDelete *respect* it through the shared redaction and shielding.
- **Q12 → Vidyano's three-segment syntax:** `Edit/Song/Lyrics`, `QueryRead/Employee/Salary`.
  - It is valid only for Query, Read, Edit, New and the combined verbs made of them, which expand
    as prefixes.
  - AsDetail row attributes target the row type (`Edit/Lyrics/Text`), with no clash with dotted
    `Parent.Child` paths.
  - SPARK014 turns from "refused" into the validator: the attribute must exist in that type's model,
    checked against the generated `AttributeNames`. The runtime validator also refuses an unknown
    type or attribute at startup.
  - A custom-action right with a third segment is refused. No backward compatibility.
- **Q13 → the type right is REQUIRED, and attribute rights compose over it** (owner's rule: start
  from the PO right, compose attribute rights over it; the result is the effective right).
  - **Per (type, attribute, verb, caller):** if any attribute-level right mentions the attribute,
    the combined type+attribute chain decides with Spark's tiers: important-deny > important-allow >
    deny > allow. Otherwise the attribute inherits the type decision.
  - **No unlocking** (unlike Vidyano): an attribute grant without the type grant does nothing. A lyrics
    contributor therefore holds `Edit/Song` plus denies on the other Song attributes, and
    `Edit/Lyrics/...` on the row type as needed.
  - Delete and custom actions stay type-level.
  - **Mitigation for the stale-deny trap:** an analyzer warning and a security-posture report line
    when a group restricts some attributes of a type for a verb but not a newly added one ("group X
    restricts 7 of 8 attributes of Song for Edit; `Genre` is editable — intended?").
  - The effective table is computed once per request per type and cached, never per row.
- **Q14 → static attribute rights REMOVE the attribute; the per-row hook blanks indistinguishably.**
  - **No Read right:** the attribute is absent from the PO and from that caller's entity-type
    definition (`EntityTypes/Get|List` prune per caller, which the forms already honour).
  - **No Query right:** the column is absent from queries, and search fields, sort, filter and
    distincts ignore the attribute as if it didn't exist.
  - **No Edit right** (or **no New right** on a create): the attribute is read-only in the prune,
    and the server shields it. The mapper only writes present, allowed attributes.
  - **The per-row `GetProtectedAttributesAsync` hook** keeps the attribute (the column can't vary per
    row), but its value becomes a **plain empty value indistinguishable from "no value"**: no
    `IsVisible` flip, no redaction marker.
  - Both forms get every leak fix below.
- **Existing leaks found by the audit, fixed here (reproduce each with a failing test first):**
  1. **Search pushdown** (`QueryExecutor.cs:966, 2017-2100`) searches every readable string property
     (hidden ones too) with `*word*` wildcards, which is a substring oracle. Limit it to readable,
     queryable attributes, including their `Search`/`Sort` companions.
  2. **The Update response echoes shielded stored values** (`DefaultPersistentObjectActions.cs:284` →
     `Update.cs:77`), a direct leak read independently by two agents. Re-present or redact the save
     response.
  3. **Breadcrumbs and `po.Name`** render raw fields (`BreadcrumbResolver.cs:204-245`,
     `EntityMapper.cs:224-285`), for the row itself and for reference targets. Blank denied or
     protected tokens.
  4. **Sort, filter, distincts and counts** are gated only by `ShowedOn` and the app's `canSort`/
     `canFilter`/`canListDistincts` flags. Also gate them by the effective attribute rights, and
     check the declared name before companion redirection (as #295 does).
  5. **Index-derived and projection columns** are ordinary attributes (synchronize makes the PO's attributes the union of collection and index properties, hidden via `ShowedOn`). **Q15 → B:** no inheritance and no `[DerivedFrom]`; the developer denies each derived attribute (e.g. `SalaryBand`) explicitly. Name-based enforcement covers them once denied.
  6. **The shield only covers scalars:** it overwrites `attribute.Value`, which is useless for
     AsDetail/`Objects`, and it skips dotted names. It must cover every attribute kind, proven by a
     reflection-driven test that exists in tests only; runtime uses model metadata.
  7. **Create has no shield:** posted values for non-New attributes are dropped, so the CLR default
     or initializer stays. The save is not refused, because refusing would reveal which attributes
     exist.
- **Technical decisions following from the above** (Claude's; the owner may override):
  - **Refresh:** values set by server hooks (`OnRefreshAsync`) are trusted. Only *client-posted*
    values for non-editable attributes are dropped.
  - **Custom-action results:** POs returned in a result or retry are presented through the same
    removal and redaction.
  - **System context** (sync, replication) bypasses attribute rights, as it bypasses the shield today.
  - **History:** a revert restores only attributes the caller may edit, and the response says it was
    partial. Revisions are presented with removal for Read.
  - **The M1c conflict dialog** is correct once the definition is pruned (`conflict-merge.ts:145`
    already makes read-only attributes theirs-wins). A test pins that a removed attribute never
    appears.

All grilling questions are resolved (2026-09-30).

## 5b. Earlier open question (resolved)

- **Q1 — Who may contribute?** (SUPERSEDED by Q11–Q13.) Originally: contributing to `Song.Lyrics`
  should not require `Edit/Song`; a `Contribute/Lyrics` right, granted by Moderation's reputation
  groups, would gate it. Final: the type right is required, so contributors hold `Edit/Song` plus
  attribute-level denies. Moderation's reputation groups can grant exactly that combination.

## 5c. Implementation status, breaking changes and open items (kept current)

**Commits on `feat/462-dark-mode`** (the owner keeps everything on this branch in one PR, including
the dark-mode work, whose ng-bootstrap half is tracked in MintPlayer/mintplayer-ng-bootstrap#420):
- `7014143a` **M1:** the `ERefreshTrigger` enum. `6663391e` adds a loud startup failure for a stale
  boolean `triggersRefresh`.
- `6660f8b9` **M1b / F7:** saves write with the checked change vector, and RavenDB
  `ConcurrencyException` → 409 (red → green: `ConcurrentWriteRaceTests`).
- `98ab4641`, `aefc353a` **M1c:** three-way conflict resolution, plus its follow-up (names via History,
  reference labels, row cells).
- `c81060d7` **M2a / F1, F2, F3, F5:** the materialize hook, satellite attributes skipped by revert
  and sync, SPARK017, interceptor `Order`.
- `a8ac860e` **M2b / F6, Q2:** eviction of interceptor writes on refusal (`SessionWriteSnapshot`), the
  reserved-verbs registry (SPARK023, startup check).
- `1cabdff2` **M2c-1:** the attribute-rights foundation.
- **M2c-2a:** read-side enforcement — removal (PO presentations, per-caller definitions, query
  columns), query operations (search, sort, filter, distincts, counts), breadcrumb/`po.Name` token
  blanking.
- **M2c-2b:** write-side enforcement — `IAttributeWriteShield` in `IDatabaseAccess` drops every posted
  attribute the caller may not write (static Edit/New per type and per AsDetail row type, plus the
  per-row hook on the stored row, dotted names included) for every attribute kind; create drops
  non-New values; `po/create`/`po/update` answer with the row re-read and presented like `po/load`;
  a History revert is partial and says so; per-row blanking is indistinguishable; retry prompts are
  presented; endpoints name rights by the definition's `Name` (nested classes).
- **M2d:** the natural-id probe and save validation see only values the caller may write. A
  natural-id create is shielded (static `New` rights, no per-row hook — there is no row) before the
  collision probe, so a `New`-denied key can neither choose the derived id (rewriting the row that
  holds it) nor turn the collision answer into an existence oracle; the id then derives from the CLR
  default, exactly as for a create that posts no key (`WvItems/` → RavenDB completes it). The shield
  after the gates stays before the interceptors (idempotent; on a collision it adds the `Edit` rules
  on the stored row). An update never moves a natural id: the loaded document is written under its
  own id (RavenDB ids are immutable), so a changed key only changes the field. `po/create` and
  `po/update` validation now runs inside the save via `ISaveValidation`, right after the shield, and
  skips the attributes the caller may not write (static refusal, per-row `Edit`, per-row `Read` not
  changed): they keep the stored value (CLR default on a create). The refresh hook sees the stored
  values for them; validating those values was rejected (a rule failing on a hidden value is an
  oracle, and an error the caller can't fix). Tests: `AttributeWriteProbeAndValidationTests`
  (red → green).
- **M3:** the library skeleton under `libs/contributions/` — `MintPlayer.Spark.Contributions.Abstractions`
  (the attributes, `ContributionAttribution`, `IContribution`, `ICurrentContribution`,
  `IContributions`, `IContributionValidator<T>`, `ContributionRights.RevertContribution` as a reserved
  verb), the runtime `MintPlayer.Spark.Contributions` (`AddContributions()` registers a placeholder
  that throws until M5) and `MintPlayer.Spark.Contributions.SourceGenerators` (an empty
  `ForAttributeWithMetadataName` pipeline, `IsPackable=false`). The public types sit in namespace
  `MintPlayer.Spark.Contributions` in the Abstractions assembly, which keeps SPARK017's metadata name
  valid. The runtime nupkg embeds only `analyzers/dotnet/cs/MintPlayer.Spark.Contributions.SourceGenerators.dll`
  (no `MintPlayer.SourceGenerators.Tools` dependency, so nothing else to embed), plus
  `buildTransitive/MintPlayer.Spark.Contributions.targets` that adds the analyzer reference in-repo
  only. AllFeatures is unchanged, because it does not list every optional feature. All three start at
  `11.0.0-preview.92`.
- **M4:** the generator and analyzer. Per `[Contribution]` property: `{T}{P}Contribution`
  (`IContribution`, `IHasNaturalId`, `ISoftDeletable` when SoftDelete is referenced) and `{T}{P}Current`
  (`ICurrentContribution`, `IHasNaturalId`; it also carries `TargetId`, which its explicit `GetId()`
  needs), both `partial`, with static `GetId`s producing exactly
  `Songs/1234/LyricsContributions/ko/Kore/User/MintPlayerUsers/abc` and `Songs/1234/Lyrics/ko/Kore`;
  `{T}{P}ContributionMetadata : ContributionDescriptor<…>` (new in Abstractions, with
  `ContributionRegistry` and `ContributionSlotFormat`); on the element, the get-only
  `[ValueKey] Key` (`ko/Kore`) and the read-only attribution properties; one module initializer per
  assembly registering the keys with `SparkValueObjects` and the descriptors with
  `ContributionRegistry` (what `AddContributions()` will read). Rules SPARK025–SPARK029 and
  SPARK031–SPARK035 (`docs/diagnostics.md`), with fixes for SPARK029 and SPARK031. Generator and
  analyzer share one inspector: a blocked declaration generates nothing.
  - **Deviations:** no lazy-load helper is generated (domain projects need not reference the RavenDB
    client; M5 loads generically from the descriptor); the rendering hint is metadata
    (`AttributionRenderingHint`, `AttributionAttributeNames`) that M5 applies to the model, because a C#
    property cannot set a renderer; an element from another assembly is a warning (SPARK032) that
    generates nothing; no slot-type code fix; `Guid` slots format as `N` (the `D` form is 36
    characters, over the 32 T2 allows).
- **M5a:** the runtime core. `ContributionsInterceptor` (+100) with one typed handler per descriptor:
  hydration on every materialize reason (one lazy prefix load, `pageSize` 1024); the before-save diff
  by slot and by value (Save/New only, and only when the attribute was posted and writable) writing
  only the caller's own contribution (`ISparkCurrentUser.Id`) and the current document with a pinned
  change vector; withdraw = soft delete (reason `withdrawn`) or delete; recompute (latest visible
  `UpdatedAt`, none → delete) on withdraw, on before-delete of a contribution and on its before-save
  (restore); owner delete cascade; `RebuildCurrentAsync`; the startup shape check; `AddContributions
  (params Assembly[])`. Moderation is reached through a new core contract, `ISatelliteWriteGuard`.
  Tests: `ContributionsRuntimeTests` (R2–R5, races, cascade, rebuild, shape) and
  `ModerationSatelliteWriteGuardTests`.
  - **Deviations:** no in-pipeline retry on a conflict — the commit is the base `OnSaveAsync`'s single
    `SaveChangesAsync`, after every hook, so a racing contributor gets the F7 409 (atomic: nothing of
    the refused save is written) and the M1c client flow retries; only the rebuilds, which own their
    sessions, retry (≤3). The contributor name is not resolved yet (M5b). Hydration is one request
    per entity: a batched multi-entity load hydrates entity by entity.

- **M5b (server half):** the generated types are model types without app hand-work — core's
  `SparkModelSatellites` registry (satellites of an owner type, a query minted once, renderer seeds for
  attributes without a renderer) read by `ModelSynchronizer` and `ModelShapeDiscovery`; the
  contributions query (`Custom.SparkContributionsOfTarget`, parent = the target, names filled); the
  attribution renderer `contributionAttribution` with the History-link `rendererOptions`; contributor
  names through core's optional `ISparkUserNameResolver` (History registers its resolver there);
  `POST /spark/po/revert-contribution` (`RevertContribution`); Delete on the current type removes a
  whole version; the Q3 notices; the moderator audit through the optional `ISatelliteAuditSink`
  (Moderation implements it). The client contract is in the library README. Tests:
  `ContributionsSurfaceTests`, `ContributionsModelSyncTests`.
  - **Decisions:** a library verb gets an add-on route, not a custom action (custom actions are
    app-declared in `customActions.json`); reverting to a hidden version is refused — restore it
    first (two rights, two audited steps); a revert without SoftDelete is refused (it would destroy
    other users' versions); notices and audits are sent after the commit; names are resolved per
    hydrated entity (one batched resolver call), not inside the lazy request.
  - **Deviations:** a refused revert is Spark's indistinguishable 404 (401 anonymous), not a 403 —
    the add-on refusal convention (#453). ~~The history grid shows `ContributorId` too unless the app
    denies `Query/{Type}Contribution/ContributorId`.~~ (M5b client: hidden by default, below.)

- **M5b (client half):** `@mintplayer/ng-spark/contributions` — `provideSparkContributions()` and
  `sparkContributionRenderers` (the `contributionAttribution` row renderer with the History link, and
  the generic `lineDiff` renderer); the query page reads `parentId`/`parentType` and column filters
  from the URL for any query (grid `presetFilters`, removable chips); "Revert to this version" in the
  contributions query's row menu and on a contribution's page; contribution-aware conflict merge.
  Server follow-ups: `ContributorId` hidden by default, `lineDiff` seeded, a revert success notice,
  `canRevertContribution`, the own-row marker, and the CLR fallback for rights on the generated types
  in the runtime validator.
  - **Decisions:** the "same user" test is the server's boolean `metadata.contribution.own` per row,
    not a comparison of ids on the client (the row has no `ContributorId`, the client knows no user
    id, and raw ids stay off the target page); the line diff compares with the target's row of the
    slot rather than the current type's PO (so a history reader needs no right on the current type);
    a row renderer is a new `rowComponent` registration kind; add-on row actions are a new
    `SPARK_QUERY_ROW_ACTIONS` extension; revert is offered only on a generated contributions query
    (source `Custom.SparkContributionsOfTarget`) and on types that have one.

**Breaking changes (M5b client), for the release notes:**
- `PersistentObject.Metadata` (new, server-written; ng-spark keeps it under the reserved row key
  `__sparkMetadata`, ignored by comparisons and never posted).
- `GET /spark/permissions/{type}` also answers `canRevertContribution`.
- `SparkModelSatellites.SeedNewAttribute` (new); synchronize writes `ContributorId` as
  `showedOn: PersistentObject`, `isVisible: false` and `Text`-like values with `renderer: "lineDiff"`
  on newly synchronized contribution types (model hash changes; re-run synchronize).
- ng-spark: `AsDetailColumnsPipe` takes the renderer registry as an optional third argument;
  `SparkQueryToolbarAction.kind` gains `'addon'`; `MergeResult.contributionNotices` (new); the query
  page treats unknown query-string parameters that name an attribute as column filters.
- The runtime security validator accepts attribute rights on a satellite type that is not in the
  model yet (CLR fallback), and SPARK014's CLR fallback ignores case.
- (M6 follow-up) `IClientAccessor.Notify(TranslatedString, …)` (new overload: a `Notify(default!, …)`
  substitute call is now ambiguous — write `default(string)!`); `NotifyOperation.TranslatedMessage`
  (new, optional; ng-spark prefers it over `message`); synchronize writes a contribution element's
  new `Key` attribute hidden (`showedOn: PersistentObject`, `isVisible: false`).

**Breaking changes for the release notes** (no backward compatibility, preview; minor version bumps
only):
- `TriggersRefresh` is `ERefreshTrigger { None, Auto, ValueChanged, Blur }`. JSON `true` → `"Auto"`,
  and a stale `true` stops startup.
- ng-spark `NestedTriggerRequest` now carries `dispatch: RefreshDispatch` instead of
  `immediate: boolean`.
- **Racing writes return 409 instead of last-write-wins:** saves without an etag, replication syncs,
  and restores/soft deletes. Create, Delete, DeleteMany and SoftDelete restore now return 409.
- **Interceptor order is `Order`-based** (SoftDelete -300, History -200, Moderation -100, default 0,
  Contributions +100). App interceptors registered before the built-ins now run after them.
- **SPARK023:** a custom action named like a reserved verb is a build error, and a startup error.
- **SPARK011** now flags SoftDelete or History rights in an app that doesn't reference those packages.
- **`security.json`:** the three-segment attribute rights `{verb}/{Type}/{Attr}` (SPARK014 validates
  them instead of refusing).
- **Per-row redaction** blanks indistinguishably (no `IsVisible` flip).
- **Search is narrowed to the caller's query surface** (M2c-2a): only attributes shown on the query
  (`ShowedOn.Query`) and not `Query`-denied are searched, pushdown and in-memory alike. Hidden
  string properties, and properties that belong to no model attribute, no longer match.
- **Static attribute rights remove attributes** from persistent objects, per-caller definitions and
  query columns/rows; breadcrumb tokens for refused or per-row protected attributes render empty.
- **Moderation.Abstractions** now references Spark.Abstractions.
- **`ISatelliteWriteGuard`** (new, Spark.Abstractions): a veto over documents written on the caller's
  behalf that are not PO saves. `AddModeration` registers one (suspension, lock on an `IModeratable`
  document).
- **`AddContributions(params Assembly[])`** replaces the placeholder; `IContributions` works.
- **Synchronize writes model files for satellite types** (`SparkModelSatellites`): an app with a
  `[Contribution]` gets `{Target}{Property}Contribution.json` and `{Target}{Property}Current.json`, and
  its model hash changes accordingly (re-run synchronize).
- **`IContribution.ContributorName`** (new member; generated, `[JsonIgnore]`).
- **New optional core contracts:** `ISparkUserNameResolver`, `ISatelliteAuditSink`.
- **.NET client:** a `notify` operation with a null `durationMs` no longer throws.
- **Contribution saves of a contributor racing another on the same slot answer 409** (no
  last-write-wins, no server retry).
- **The save shield drops instead of restoring** (M2c-2b): a refused attribute is removed from the
  posted object before `OnBeforeSaveAsync`, the interceptors and the mapper — hooks that read
  `obj["X"]` for a refused `X` no longer find the stored value there. It now also covers static
  `Edit`/`New` rights, AsDetail rows and embedded objects, references and reference arrays, and dotted
  per-row names; a value the hook protects only for `Read` is dropped unless `isValueChanged`.
- **A create drops values for `New`-denied attributes** (no refusal; the CLR default stays).
- **`po/create` and `po/update` return the row as `po/load` presents it**, not the posted object
  (fresh etag, breadcrumb, per-row blanking, attribute removal, disabled actions).
- **A partial History revert** answers 200 with a warning notification in `operations`.
- **`SaveContext.UnwritableAttributes`** (new): what static rights kept out of the save.
- **Rights for a nested entity class** are named by its definition `Name` in every endpoint
  (`Create`, `New`, `Refresh`, `DeleteRow`, custom actions), as `IDatabaseAccess` already did — not by
  the CLR name's last dotted segment (`Outer+Inner`).
- **Save validation moved into the save** (M2d): `po/create`/`po/update` validate after every gate
  (row Edit gate, collection guard, etag check, disabled actions, write shield), so an invalid
  payload against a row the caller may not edit is a 404 (stale etag 409, disabled action 403) rather
  than a 400; and an attribute the caller may not write is no longer validated.
- **A `New`-denied natural key no longer chooses the id** (M2d): the create gets the id the CLR
  default derives, instead of colliding with (and, with `Edit`, rewriting) the row the posted key names.
- **Refresh** asks the per-row hook for the redacted names instead of reading an `IsVisible` delta,
  and a refreshed protected attribute is blanked the same indistinguishable way.
- **Dark mode** (the other half of this PR): `sidebarTheme` is removed. ng-bootstrap's theme is stored
  in a cookie instead of localStorage, so stored choices reset.

- **M6:** the demo consumer — QnA's `Question.Translations` (two slots, `Language`/`Script`; full
  attribution), wired with `AddContributions()`, `provideSparkContributions()` and
  `sparkContributionRenderers`; the model synchronized (`QuestionTranslation`,
  `QuestionTranslationsContribution` with its query, `QuestionTranslationsCurrent`) and labelled in
  en/fr/nl; security.json per the README's rights table; the contribution type is `IModeratable`
  (author = translator, posted = `UpdatedAt`). QnA's `Edit` row rule was author-only, which makes a
  target impossible to contribute to: it is now open to every signed-in user for `Edit`, and
  `GetProtectedAttributesAsync("Edit")` keeps everything but the translations to the author or a
  moderator. Fixes the wiring found:
  - History stamped `ModifiedBy`/`ModifiedAt` on every edit, so a contribution-only save rewrote an
    `IAuditable` target — a new revision and etag, the translator named as modifier (R3). It now
    skips a `Save` whose tracked entity the session sees unchanged.
  - `--spark-verify-model` refused the generated `Custom.SparkContributionsOfTarget` query, since no
    `{Type}Actions` class exists; it now also accepts the `IPersistentObjectActions<T>` a module
    registered in DI (what the executor resolves).
  - A well-known group's `Read` on the contribution type failed startup (no row rule), and a
    contribution of a row the caller may not see (a QnA draft) loaded by id. `ContributionActions<T>`
    now judges every row by `Read` on the target type and the target's row-gated load (once per
    target per request).
  - Rows need `New`/`Delete` on the row type (the README said "optionally `Edit/Lyrics/...`"): without
    `New` the form shows no **Add**.
  - The ng-spark edit form drew attributes from the type only, so per-row read-only attributes stayed
    editable (and were dropped by the save); it now applies the loaded object's `isReadOnly`.
  Tests: `ContributionsAuditedTargetTests` (red → green for the History fix), the QnA E2E classes
  `QnAContributionsTests` and `QnAContributionsBrowserTests` (built; run in M7).
- **M6 follow-up (done):**
  - **Notices follow the app-chosen language.** The server never learns ng-spark's language
    (`SparkLanguageService` keeps it in `localStorage`; no header carries it), and its only culture
    source, `IRequestCultureResolver`, reads `Accept-Language` — the browser's. ng-spark already
    resolves server text client-side (labels, validation messages are `TranslatedString`), so notices
    do the same: `NotifyOperation.TranslatedMessage` (new, optional) carries every language,
    `IClientAccessor.Notify(TranslatedString, …)` (new overload) fills it and resolves `Message` in the
    request culture as the fallback for other clients (the .NET `SparkClient` sends its own
    `Accept-Language`), and ng-spark's `notify` handler shows the translation of `currentLanguage`.
    Contributions' withdraw/"stays"/revert notices and History's partial-revert notice (now en/fr/nl)
    use it; an app's `translations.json` entry overlays the built-in texts per language. Tests:
    `ContributionsSurfaceTests.A_notice_carries_every_language_…`, the revert notice and
    `HistoryTests.A_revert_restores_only_…` (red → green), `provide.spec.ts` "notify language" (red →
    green).
  - **The generated row `Key` is hidden by synchronize:** `SparkModelSatellites.SeedNewAttribute` on
    the element (`ShowedOn: PersistentObject`, `IsVisible: false`), for a newly created attribute only,
    like `ContributorId`. QnA's authored (hidden) `Key` is unchanged by a re-synchronize; the model
    hash did not move. Test: `ContributionsModelSyncTests.The_generated_row_key_is_hidden_…`.

**Known limitations (documented, not fixed):**
- **Per-row oracle (owner Q16 → C, 2026-10-01).** An attribute that the per-row
  `GetProtectedAttributesAsync` hook protects, and that is a shown, queryable column, stays
  searchable, sortable and filterable: the value is blanked, but which rows match still leaks. The
  app sets `canSort`/`canFilter`/`canListDistincts: false` (or doesn't show the column on the query),
  or uses a static attribute right when the rule doesn't depend on the row.
  Documented in `docs/guide-authorization.md`. Rejected alternatives: a
  `GetProtectableAttributes()` declaration that disables those operations, and in-memory filtering
  after redaction.
- **Search scope** (M2c-2a): search covers only attributes shown on the query and not Query-denied.
  Hidden string properties and properties without a model attribute are no longer searched. There is
  no "searchable but not shown" opt-in (none was requested).
- **Hard deletes** are not concurrency-checked: RavenDB 7.2.6 ignores the check on `Delete(entity)`.
- A no-op save sends no write, so it gets no conflict check.
- F6 can't take back `session.Advanced.Defer` commands, or `Delete(id)` on a document that was never
  loaded.
- Satellite detection keys on the Newtonsoft `[JsonIgnore]`, not System.Text.Json's.
- The materialize hook runs once per entity *instance*, not per document id.
- **Contributions (M5a):**
  - A create of a target with contribution rows stores the target before the commit to learn its id;
    a server-assigned id convention (`Songs/` or `Songs|`) is refused with an explicit error.
  - A withdrawal's or a recompute's `Delete(id, cv)` of a current document is a deferred command,
    which F6 cannot take back after a later refusal in the same request (only a later
    `SaveChangesAsync` in that request would commit it).
  - Hydration loads at most 1024 current documents in the lazy request; more page with further
    requests.
  - Validator errors are reported as one validation error (messages joined, the first attribute).
  - A contribution a moderator hid cannot be re-saved by its author (400); their own withdrawal can.
  - The single-valued (no slots) path is exercised by the generator tests only, not by an HTTP test.
  - ~~(M6) The save notices are in the request's culture, not the SPA's chosen language~~ and
    ~~synchronize leaves the element's generated `Key` attribute visible~~ — fixed (M6 follow-up, above).

**Open items:**
- ~~M1c "Changed by" fallback~~ **→ decided (owner, 2026-09-30): configurable.**
  `SparkConfig.conflictDialog.showChangedBy`, default `false`.
  - **`false`:** no History lookup and no user shown.
  - **`true`:** the History-resolved name, only for `History/T` holders, and only when it matches this
    version; otherwise nothing.
  - **The raw user id is never shown.** The `ModifiedAt` time is shown in both modes.
- **Unrun tests** (all run in the M7 sweep): the existing `ConcurrentWriteRaceTests` after F6 changed
  eviction, the M1c E2E selectors (`input#Model`, `.spark-conflict-dialog`), and every whole suite.
  (M2c-2b, 2026-10-01: `ConcurrentWriteRaceTests`, `RefusedWriteEvictionTests` and the
  PersistentObject endpoint, SoftDelete, Moderation, History, Refresh and redaction classes ran green.)
- **The version gate:** check whether CI expects every `libs/` csproj bumped in lockstep. Past PRs
  bumped 23–29 of 30, while this branch bumps only the touched ones.

## 5d. Test-run speed and CI cost (owner decisions, 2026-10-01; work tracked as M8 in the plan)

**Summary: every measure taken.** The local sweep (`npm run test:affected -- --skip-nx-cache`, all
projects, E2E included) went from **21m49s to 7m39s (459 s), 2.85×**, on the owner's 4-core/8-thread
laptop. Details and evidence are in the numbered items below.

Adopted:
1. **One local script, `npm run test:affected`** (`tools/test-local.mjs`): runs only the affected
   projects, unit and E2E together. Item 2.
2. **Coverage off locally:** a `-c local` configuration per test target drops coverlet and vitest
   `--coverage`. CI is unchanged. Item 2.
3. **E2E apps built once through nx**, then `SPARK_E2E_SKIP_APP_BUILD=1` skips the per-app
   `dotnet build`. Item 4.
4. **Zero-wait database deletes** in every RavenTestDriver base (`RavenDatabaseDeletion`). Item 4.
5. **Test hosts deploy only the indexes a test needs.** Spark core gained an opt-in
   `SparkModuleRegistry.IndexDeploymentFilter`; production startup is unchanged. The test-assembly
   index that every boot still deployed was nested into its test class. Spark.Tests 515 s → about 180 s.
   Items 10 and 13.
6. **CodeCoverage.Tests deploys its index only in classes that query it** (opt-in `DeployIndexes`,
   30 of 73 classes) and runs on half the cores. 172 s → 104 s. Item 10.
7. **A database per class instead of per test** for the classes that write nothing, the OIDC classes
   (unique client ids and emails per case) and the read-only query classes, which seed once per
   class. Item 5 (batch 1) and item 11.
8. **A host per class instead of per test** where the configuration is shared (`SharedSparkHost<T>`,
   `OidcSharedHost`). Items 5 and 11.
9. **The per-test database is created lazily**, on the first use of `Store`. Item 11.
10. **One OIDC signing key per test process** instead of a new RSA-2048 key per host. 10–12 below
    are cheaper per-boot work.
11. **One PBKDF2 iteration in test hosts** instead of 100,000 (`PasswordHasherOptions`).
12. **One Data Protection key ring per process, and no topology cache files** per test database.
    Item 13.
13. **Dynamic PGO off in test processes** (`TieredPGO=false` for test projects) **and in the embedded
    RavenDB server** (`DOTNET_TieredPGO=0`), about 15% less CPU. Item 13.
14. **`--parallel=4` for the local sweep** (CI keeps 3), so the CodeCoverage.Tests chain, the critical
    path, gets a slot early. 557 s → 459 s. Item 14.
15. **CodeCoverage.Tests' `WaitForIndexing` polls every 10 ms** instead of 100 ms, with the same
    semantics. Item 14.
16. **The leftover per-request debug `Console.WriteLine` middleware was removed** from Spark core.
    Item 10.

Measured and rejected:
- disposal off the critical path (lever B)
- more xUnit threads
- `maxParallelThreads` 0.25x
- nx `--parallel=2` and 5 (5 risked low memory)
- all cores for CodeCoverage.Tests
- vitest `--no-isolate`
- workstation GC and three RavenDB server options
- the value-object check short-circuit, a single model hash and a lookup-scan cache (each saved
  under 3 CPU-s)
- `DOTNET_TieredCompilation=0`
- database pooling
- extra embedded servers per process
- GPU acceleration
- Defender exclusions (blocked by group policy)
- migrating CodeCoverage.Tests to databases per class (fixed ids; ceiling about 90 CPU-s)

**Rule: CI runs cost money, never push just to get a test run** (owner, 2026-10-01). It is recorded in
the global and repo `CLAUDE.md`. A sub-agent's unrequested push and draft PR #465 prompted it. The full
sweep therefore has to be practical locally, and CI's behaviour stays exactly as it is.

**Superseding requirement (owner, 2026-10-01, later the same day):** "CI's behaviour stays exactly
as it is" no longer holds. Wall time must not keep growing as tests are added, **both on CI and
locally**. Target: about 4× faster PR validation (CI ~17 min today) and the same scaling property
locally. The owner: "I can't wait 15 minutes before I know if a pull-request is valid or not", and
proper parallelism is essential. The owner is willing to build a tool for it. A five-agent
investigation (profiling, RavenDB test infra, CI sharding, test impact analysis, host boot cost) is
running; its ranked findings will be recorded here as item 10.

**Owner decision (2026-10-01): test databases run in memory by default, configurable.** The
RavenTestDriver bases in `MintPlayer.Spark.Testing` keep test databases in memory instead of on
disk unless configured otherwise; developers with less RAM can switch persistence to disk.
~~Evidence for the gain is pending … no driver sets `RunInMemory`~~ **Superseded (2026-10-01):
in-memory is ALREADY the behaviour.** The RavenDB test driver itself appends `--RunInMemory=true` to
the embedded server's arguments (`RavenTestDriver.cs:342-344,366-368` in RavenDB.TestDriver 7.2.6;
the string is confirmed in the installed DLL). The grep of `libs/testing` was a clean grep, not
evidence. So the decision brings **no speed gain**; the work is the **opt-out** only:
- Per-database `RunInMemory` in the database record, set in `PreConfigureDatabase`, as RavenDB's own
  suite does (`RavenTestBase.cs:236-252`). The server-wide flag can't be turned off, because the
  driver appends it after the caller's arguments.
- The configuration surface: a shared helper used by `SparkTestDriver`, `SparkSharedDatabase` and
  `CoverageRavenTest`, with `protected virtual bool RunInMemory` whose default reads the environment
  variable `SPARK_TEST_RAVEN_PERSIST`; a subclass override wins. Not `xunit.runner.json`, which has no
  custom keys.
- Optionally, `DataDirectory` = `%TEMP%\spark-ravendb\<pid>`. In-memory storage still uses
  memory-mapped temp files (deleted on close, never fsync'd), by default under the Nx-cached
  `bin\RavenDB`.
- Estimated memory: 5–20 MB per empty database plus 1–5 MB per index; about 1 GB peak per test
  process today.

**Measured: xUnit uses half the cores.** `tests/MintPlayer.Spark.Tests/xunit.runner.json:3` sets
`"maxParallelThreads": "0.5x"`: 2 threads on CI's 4-vCPU runner (the CI agent saw 2) and 4 on the
owner's 8-thread laptop. Raising it (2x/4x, `parallelAlgorithm: aggressive`) is the first experiment
once the profiler shows whether the CPU is idle during a run. The owner proposed 16 lanes, cores × 4,
pipelined like CPU instructions. The recommendation is to build that as a database pool with a
background producer, teardown in the background and longest-first scheduling inside the existing
xUnit and driver stack, not as a new test runner.

**Measured evidence (2026-10-01):**

| Suite | Local (serial sweep) | CI (run 36884431200) | Ratio |
|---|---|---|---|
| MintPlayer.Spark.Tests | 21m21s (4 threads) | 9m59s (2 threads, with coverage) | 2.1× |
| CodeCoverage.Tests | 3m34s | 2m32s | 1.4× |
| SourceGenerators.Tests | 64s | 51s | 1.25× |
| .NET suites end to end | 26.2 min, one after another | test step ~12 min (`nx parallel: 3`) | 2.2× |
| Whole CI job | — | ~17 min, E2E 138 tests included | — |

- **Per-test thread time** was about 5,070 s locally against about 1,200 s on CI, so each test took
  roughly 4× longer. All 4 local threads were busy for 1,257 of 1,275 s.
- **57% of local slot time is setup and teardown** (2,897 of 5,069 s): per-test databases and per-test
  host boots. Examples:
  - `NestedRefreshEndpointTests`: 136 s setup for 1.7 s of test body
  - the OIDC family: about 700 s of body time
- **The licence:** the machine-level `RAVENDB_LICENSE` took precedence (`SparkTestDriver.cs:327-367`)
  and behaved as the Community licence. Locally `ThrottleAccuracySpikeTests.S_M3` failed with
  "minimum revisions 1000 exceeds the licensed one 2"; in CI, with the Developer licence, it passes.
  Memory records Community as 3 cores, no ETL, and expired 2026-09-25.
- **The localhost:8080 RavenDB:** measured at 7.2.6 (build 72033, the same as the client), Developer
  licence, `MaxCores` 9. It holds about 70 real development databases.

**Owner decisions, each lever (2026-10-01):**
1. **Licence:** done by the owner. `setx RAVENDB_LICENSE "C:\Repos\MintPlayer.Spark\.secrets\raven-license.log"`;
   the driver accepts a file path.
2. **One `package.json` script** for the affected tests, unit and E2E together. Accepted, because nx
   parallelism overlaps the projects as CI does.
3. **The Nx remote cache is left as it is.** Disabling it locally, or using a read-only token, was
   rejected by the owner. The local environment carries the `NX_SELF_HOSTED_REMOTE_CACHE_*` variables.
4. **Zero-wait deletion in every `RavenTestDriver` base class,** plus the opt-in
   `SPARK_E2E_SKIP_APP_BUILD`.
   - `SparkSharedDatabase` and CodeCoverage's `CoverageRavenTest` still used the 15 s confirmation
     wait, across 458 inline stores; that wait is the disposal trap behind earlier timeouts.
   - The E2E host rebuilt the app redundantly (`SparkAppTestHost.cs:~609`).
5. **A database per test class through xUnit class fixtures** (`IClassFixture<SparkSharedDatabase>`),
   the same on CI and locally. Only 1 of 181 driver classes used it so far; the first one went from 13 s
   to 647 ms. Rejected alternatives:
   - **A local-only database mode:** CI would never exercise sharing, and you'd get "red only on my
     machine".
   - **One shared database plus cleanup:** a document delete leaves indexes, subscriptions,
     compare-exchange values and revisions behind, and background writers race the reset.
   - **Never deleting databases:** measured 17% slower.
   - **Restoring from a template:** indexing is the cost, not creation.
   **Batch 1 measured (2026-10-01, this machine, Developer licence, `dotnet test --no-build`, no
   coverage):** 41 classes migrated (list and the not-migrated reasons in plan M8).
   - **Per class, each in its own `dotnet test --filter` process:** the host-sharing classes dropped
     sharply, e.g. AttributeRightsEnforcement 47.2 → 15.2 s, AsDetailRowIdentityRoundTrip 38.0 → 14.0 s,
     RetryFromEveryHook 37.0 → 14.6 s, ExecuteQueryRequestValidation 35.6 → 14.6 s,
     NestedRefreshEndpoint 34.5 → 14.8 s, RefreshEndpoint 34.5 → 14.5 s, ColumnFilterDisclosure
     34.3 → 15.2 s, DistinctValuesEndpoint 33.0 → 15.1 s, GetProgramUnitsEndpoint 31.9 → 16.1 s,
     ComposedQuery 29.4 → 15.1 s, OidcPageTheme 27.3 → 14.2 s. Database-only classes moved 1–3 s
     (e.g. SparkExternalLoginLinker 14.9 → 8.7 s). About 8 s of every figure is process start plus
     the embedded server, and the first host boot in a process pays the JIT, so these numbers
     overstate what the same change saves inside a full run.
   - **Whole project, run alone, same command before and after: 513 s → 511 s, 3510/3510 green both
     times.** No measurable wall-time gain. The before run is the same branch with the batch-1 test
     changes stashed (`git stash -- tests/MintPlayer.Spark.Tests`), so nothing else differed. Batch 1
     touches ~310 of 3,510 cases, and the run's wall is set by the classes still on per-case
     databases and hosts; the 18m20s / 21m21s figures above were measured under contention with the
     other suites (parallel `test:affected`) and with coverage (serial), not alone, which is why
     both runs here are far below them.
   - **Order independence:** all 41 classes passed reversed, seed 1 and seed 2 shuffles and the
     default order (313 cases per run). The orderer demonstrably reorders: the TRX start times of
     ComposedQueryTests differ in all three modes. The shuffle caught one real defect: the shared
     SortColumnDisclosure host resolved `IQueryExecutor` from the root provider, so one Raven session
     spanned the class and the `RqlRecorder` that later cases attach to the store never saw their
     queries (3 of 4 failed in every order, the first-run case passed). Fixed by resolving the
     executor from a scope per case, the way a request does; the same pattern is used in the other
     shared executor classes.
6. **Keep the `RavenTestDriver` implementations; no external `localhost:8080` server for tests.** The
   gain would be small (one embedded-server start per process) against the risk to the dev databases,
   the switch from memory to disk, and leftover cleanup.
7. **CI already skips unaffected test projects; no workflow change** (owner question, 2026-10-01;
   answered by measurement).
   - **How:** CI runs `nx run-many -t test`, deliberately not `affected` (`pull-request.yml:245-257`).
     Unaffected projects are restored from the remote Nx cache, with their coverage reports, instead
     of being re-run. `affected` would drop their coverage reports. PR #377 uploaded 1 report of 8
     that way and read as 35.1% against an 81.8% base.
   - **Measured** with `nx show projects --affected --files=<f> --withTarget=test`:
     - a change in `apps/CodeCoverage/…/Program.cs` affects only `CodeCoverage.Tests`
     - a change in `libs/spark/…/DatabaseAccess.cs` affects all five .NET test projects
     - a change in `package.json` affects everything (shared input)
   - **Caveats:**
     - Shared-input changes (`package.json`, `nx.json`, root props) invalidate everything once.
     - The cache hits only when master ran with the same inputs.
     - `run-many --target=build` of the five apps (`pull-request.yml:132`, for the model-verify loop)
       runs on every PR regardless of what changed. Left as is unless the owner asks.
8. **Measured result of (2) + (4)** (2026-10-01, `--skip-nx-cache`, Developer licence):
   `npm run test:affected` took **21m49s including E2E**, all 13 projects green, against the old
   serial sweep's 26.2 min *without* E2E. MintPlayer.Spark.Tests went from 21m21s to **18m20s** and
   remains the critical path. So the licence was not the suspected 4× factor: per-test setup and
   teardown is, which is what (5) targets. Details are in the repo `CLAUDE.md`, "The local sweep".
9. **Rejected: GPU (CUDA) acceleration and Defender exclusions** (owner questions, 2026-10-01).
   - **GPU:** nothing in these suites is GPU work. RavenDB has no GPU support (indexing, Voron
     storage and transactions all run on the CPU), and the time goes to database setup and teardown,
     host boot and I/O, which are latency-bound, not data-parallel.
   - **Defender exclusions:** group policy blocks local exclusions on the owner's machine, and
     Defender real-time protection was already off there (owner's check, 2026-10-01). So on-access
     scanning by Defender is not a factor in the local times.
   - **Hardware (for reading the numbers):** i7-11370H (4 cores, 8 threads) with a Samsung 980 Pro
     NVMe. The CPU is the constraint: MintPlayer.Spark.Tests takes 511 s run alone against 18m20s
     inside the parallel `test:affected` run (item 8), and CodeCoverage.Tests 3m34s against 10m41s.
10. **Where the time goes, and the first two levers** (2026-10-01, this machine, Developer licence,
    `dotnet test --no-build`, no coverage, each project run alone; script `bench.sh`).
    - **Profile (Spark.Tests, 4 xUnit threads, ~484 s wall = 1,936 thread-s)**, from stopwatches in
      `libs/testing` and around `IndexCreation.CreateIndexes`, since reverted:

      | Phase | Count | Thread-s | Mean | Share |
      |---|---|---|---|---|
      | Host boot (`SparkEndpointFactory` `_host.Start()`) | 719 | 826 | 1,149 ms | 43% |
      | … of which deploying the test assembly's ~37 indexes | 643 | 619 | 963 ms | 32% |
      | Per-test database dispose | 1,290 | 405 | 314 ms | 21% |
      | … of which our zero-wait delete | 1,341 | 33 | 25 ms | 2% |
      | Per-test database create | 1,290 | 114 | 88 ms | 6% |
      | Seeding, index waits, host build, shared DBs | | ~110 | | ~5% |
      | Test bodies and non-Raven tests | | ~480 | | ~25% |

      The machine is CPU-saturated during a run (98–99% busy; ~43% busy at rest from Defender for
      Endpoint and other background load), so wall time tracks total thread time. Raising xUnit to 8
      threads made it slower (projected ~720 s), because each operation roughly doubled in cost.
    - **Lever A, done: test hosts deploy only the indexes a test can need.**
      - *Mechanism.* `SparkModuleRegistry.IndexDeploymentFilter`, an opt-in `Func<Type, bool>`: `null`
        deploys exactly as before, so applications are unaffected. It narrows deployment only:
        `PopulateIndexTypes`/`PopulateProjectionTypes` and the model hash still see every index, so a
        filtered host builds the same model. With a filter, Spark instantiates the types RavenDB's own
        `IndexCreation.GetAllInstancesOfType` would (non-abstract class implementing
        `IAbstractIndexCreationTask`, `Activator.CreateInstance`; RavenDB.Client 7.2.6) and calls
        `IndexCreation.CreateIndexes(tasks, store)`. Deliberately not `RavenIndexHierarchy.IsIndex`,
        which covers only `AbstractIndexCreationTask`.
      - *Test default.* `SparkEndpointFactory` deploys every top-level index, every index from an
        assembly other than `TContext`'s and the entry assembly (modules, framework), and the nested
        indexes the fixture armed through `configureIndexCatalog` (diffed from the catalog around the
        callback). Opt-out: `deployAllIndexes: true`, or replace the filter from `configureSpark`.
      - *Why "nested" is the rule.* A downstream consumer's `TContext` usually lives in its
        application assembly, whose real indexes are top-level classes; they must not silently stop
        deploying. Fixture indexes are declared inside the test class that uses them (all but one of
        Spark.Tests' 41). A skipped index fails loudly (`IndexDoesNotExistException`).
      - *Rejected alternatives.* Deploying nothing from the context assembly unless armed (would drop a
        consumer's real indexes); applying the filter only when the context assembly "is a test
        assembly" (no reliable signal: the entry assembly is `testhost` either way); caching index
        definitions per process (saves the client-side `CreateIndexDefinition`, not the server-side
        index creation and teardown that dominate); one shared database per process (a separate,
        larger change; item 5 covers per-class sharing).
      - *Measured.* Per boot, `MintPlayer.Spark.Tests (1 of 37, deployment filter set)` instead of 37.
        **Spark.Tests 515 s → 199 / 183 / 167 / 193 s** (four runs; the last on the final committed tree), 3510/3510 green before and all green
        after (3515–3518 with the new tests). No test needed arming: every fixture that queries an index
        already armed it or deployed it itself. The four reflection tests on `CreateSparkIndexes`' signature were updated;
        new tests pin the filter (`SparkEndpointFactoryIndexDeploymentTests`, and a filtered
        `CreateSparkIndexes` test asserting the catalog still receives every index).
      - Also removed: `SparkMiddleware`, a pass-through that wrote "Before/After the next middleware"
        to the console on every request, in production too (and serialised parallel test workers on
        the console lock). Nothing asserted on it.
    - **Lever B, measured and rejected: skipping the driver's waiting delete.**
      - *Finding.* Timestamps on each dispose phase over a full run (after lever A): mean 114 ms, of
        which our zero-wait delete 11 ms, the store's own teardown ~0 ms, and **the driver's second
        delete (`RavenTestDriver`'s `AfterDispose` handler) 103 ms** (p90 164 ms). Our zero-wait delete
        is answered every time with `RavenException: System.TimeoutException: Waited for 00:00:00 but
        didn't get an index notification for N. Last commit index is: N-1` — the delete is in the Raft
        log, only its application is not awaited — and was swallowed as a failure. The driver's
        delete then finds the database still being torn down and waits for it.
      - *Tried.* Treat that answer as accepted and remove the store from the driver's private
        `_documentStores` registry, so the driver's handler returns early. Dispose fell to 16 ms mean,
        and a polling test confirmed the database is still deleted by the server on its own.
      - *Measured, wall:* Spark.Tests 196 / 186 s with it against 199 / 183 / 167 s without;
        CodeCoverage.Tests (8 threads, an index in every database) 209 / 200 s against 194 / 152 s.
        No gain, possibly a loss. On a saturated CPU the server performs the unload either way; the
        wait only throttled the test threads, and without it unloads pile up concurrently. Code
        reverted; recorded in `SparkTestDriver.DisposeAsync`'s rejected list. Backgrounding the
        dispose (already rejected in item 4's history) fails for the same reason plus the races.
    - **CodeCoverage.Tests: not the same waste.** `CoverageRavenTest.SetupDatabase` deploys the app
      assembly's indexes, which is one index, `Commits_ByRepository`, a real production index that the
      code under test queries (ingestion, base resolution, browse, badges). Left as is. Measured run
      to run on this machine: 194 s and 152 s for the same build, 1045/1045 green, so single-run
      differences under ~40 s are noise here.
    - **What is left** (profiler ranking, after lever A): the remaining per-boot cost (~0.25 s × ~700
      boots) and per-test databases, which per-class sharing (item 5, batches 2–3) removes.
11. **Item 5, batch 2: what a per-test database costs, and where sharing still pays** (2026-10-02,
    this machine, Developer licence, `dotnet test --no-build`, no coverage, each project run alone).
    Starting point: the full sweep at 10m07s after lever A, CodeCoverage's opt-in indexes and
    half-core xUnit, and one OIDC signing key per process.
    - **Measured first: one create/delete cycle on the embedded test server.** A throwaway class
      (not committed) ran 400 cycles at 4-way parallelism per variant and read the Raven server
      process's `TotalProcessorTime` before and after:

      | Cycle | Wall (400 cycles) | Server CPU / cycle | Test-process CPU / cycle |
      |---|---|---|---|
      | Empty database | 10.9 s | 96 ms | 3 ms |
      | + 5 documents and a load | 12.6 s | 115 ms | 6 ms |
      | + a static index and a query | 25.6 s | 204 ms | 28 ms |

      So an empty database is not free: about 0.1 CPU-s each, and twice that with an index. Spark.Tests
      had ~1,290 per-test databases (~130–260 CPU-s, against the ~347 CPU-s the server used in a
      run), plus a host booted per case in the OIDC classes. Worth doing; the saving is less CPU, which
      on this saturated machine is the only thing that shortens a sweep.
    - **Lazy per-case database (`SparkTestDriver.Store`).** The database is now created on the first
      read of `Store`, not in `InitializeAsync`; `DisposeAsync` disposes the backing field. Counted
      with a temporary counter over a full run: **76 of 1,102 cases (7%) never read `Store`**
      (startup refusals, template rendering, culture validation). Small, but free and with no
      isolation risk. Locked, so two tasks reading it at once cannot create two databases.
    - **OIDC: one provider host and database per class.** `OidcTestHost` now takes an
      `OidcSharedHost` class fixture. Each case seeds through `ClientId(name)` and `UserEmail(local)`
      (the case's `Scope` appended), and the whole-collection `Query<OidcToken>()` /
      `Query<OidcAuthorizationRequest>()` assertions became `CaseTokensAsync` /
      `CaseAuthorizationRequestsAsync`. Those are filtered in memory over a collection query on
      purpose: a `Where` would ride an auto-index, and several callers assert absence, which a stale
      index satisfies for the wrong reason. 10 classes migrated (Authorize, Consent,
      ConsentWithdrawal, Introspection, Login, ResourceServer, TokenEndpointGuard, TokenForgery,
      TokenSecurity, TwoFactor; 176 cases). OidcAdminRoute boots one host per class instead of two per
      case (its cases already used distinct client ids).
      - *Kept per case, with the reason,* through `OidcTestHost`'s parameterless constructor (a host
        and database per case): **OidcScopeIntegrity**, which disables shared `OidcScope` documents,
        `openid` among them; **OidcAdminRegistration**, where the synchronizer writes model files into
        the host's content root, so `File.Exists` could pass on a sibling's file. **OidcCorsScope**
        stays on `SparkTestDriver`: the host snapshots every enabled application's origins at startup,
        which is database-wide by construction.
      - *The shuffle and a full run found one real race.* OidcTokenSecurity's two chain-revocation
        cases (code replay, refresh-token reuse) failed once in a full run: an access token stayed
        `valid`. `Token.RevokeAuthorizationChainAsync` finds siblings through an index and is
        documented as best-effort. On a fresh database the index had nothing to catch up on; on the
        shared one it carries earlier cases' tokens. The cases now wait for indexing before the
        replay, pinning what they test: a chain the index can see is torn down. The production code
        is unchanged.
      - *Per class, summed TRX spans:* the OIDC classes went from ~131 s of class time to ~25 s, plus
        one fixture boot each (~0.1 s dispose measured per fixture). The 193 OIDC cases run alone in
        21 s of wall time, process start included.
    - **Read-only classes seed once per class.** SearchPushdown and SortCompanionRedirect
      (`SparkSharedDatabase` with the cars and index seeded in `InitializeAsync`), DateTimeOffsetRoundTrip
      and AsyncCustomQuery (`SharedSparkHost`, which also boots the query host once; the executor
      comes from a scope per case, as SortColumnDisclosure found it must, so each case's
      `RqlRecorder` sees its own queries). The one SearchPushdown case that adds a document moved to
      `SearchPushdownWriteTests` on a database per case: its van would have shown up in the other
      cases' `volkswagen` searches.
    - **Order independence:** every migrated class passed default order, `SPARK_TEST_ORDER=reverse`
      and seeds 1 and 2 (221 cases per run for OIDC + AdminRoute + SearchPushdown, 52 for the seed
      classes).
    - **Measured, Spark.Tests alone:** before 191 / 181 / 178 s (the last runs of the previous
      commits); after **161 / 139 s** (committed tree), with 156 s from an intermediate build and
      one noisy 192 s run, all 3515 green. That is about −15%, within this machine's ±10–15% noise
      per run but consistent across runs.
    - **Not migrated, with the reason:**
      - The remaining per-case classes cost little each: the DB-only ones run at 0.1–0.2 s per case
        including their database (e.g. LookupReferenceService 35 cases in 6.6 s, BreadcrumbResolver
        22 in 2.5 s), and nearly all reuse fixed ids (`LookupReferences/CarBrand`, `people/{i}`,
        `users/alice`) or boot a differently configured host per case (AccountFlow, SoftDelete with
        its revisions configuration, Moderation*, Contributions*, SubQueryActions). Scoping them saves
        ~0.1 CPU-s per case for a hand rewrite per class; not worth it at this ceiling.
      - **CodeCoverage.Tests: rejected on the measured ceiling.** Counted with a temporary counter
        over a full run (113 s, 1045 green): **563 databases, 303 of them with the index.** At the
        cycle costs above that is at most ~26 + ~62 ≈ 90 server CPU-s even if every one went away.
        But the classes share fixed GitHub ids, owners and full names (`RepoId`, `OldOwnerId`,
        `"acme/widgets"`) that flow into webhook payloads and into index lookups by full name, so
        each of ~70 classes needs a careful rewrite. A realistic partial migration buys perhaps half
        the ceiling.
    - **Full sweep after batch 2** (`npm run test:affected -- --skip-nx-cache`, everything affected,
      E2E included, `dotnet build-server shutdown` first): **563 s and 575 s** (nx run duration 9m01s
      and 8m37s), against 10m07s before; all 60 tasks but one green each time. Each run had one
      Spark.Tests failure under full load, in classes this batch did not touch:
      `ModerationVoteTests.M5_the_badge_shows_a_new_vote_as_pending_at_once_without_any_recompute`
      (pending 0 instead of 10; passed 3 of 3 runs alone; the reputation read already waits up to 30 s
      for non-stale indexes, so the cause is not yet understood) and the known
      `ThrottleAccuracySpikeTests.S_M3` (a throttled message deferred twice). Neither was loosened.
    - **Where that leaves the ~7m15s target:** the sweep is CPU-bound, and what remains is mostly not
      per-test databases. By the per-task figures (E2E 152 s, the Angular/vitest suites ~100 s,
      builds 72 s, CodeCoverage 104 s, Spark.Tests now ~150 s), the next levers are the E2E suite and the
      vitest suites, not further database sharing.
12. **The CPU budget of a sweep, and concurrency settings** (2026-10-02, this machine).
    - **Method:** system-wide busy CPU-seconds from the raw `_Total` processor counter, read before and
      after each suite run alone (no sampling, so no observer cost). The machine's background load is
      subtracted: **~1.9 of 8 logical cores busy at rest** (60 s idle reference). VS Code, Defender for
      Endpoint and WSL are the largest parts (per-process raw counters at rest: VS Code ~0.9 cores,
      MsSense 0.32, MsMpEng + DLP 0.22, WSL 0.19).
    - **Net CPU per suite run alone:**

      | Suite | Wall | Net CPU-s |
      |---|---|---|
      | MintPlayer.Spark.Tests | 140 s | ~715 |
      | Angular/vitest, 8 suites sequential | 209 s | ~470 |
      | CodeCoverage.Tests | 109 s | ~427 |
      | E2E | 92 s | ~309 |
      | SourceGenerators.Tests | 35 s | ~154 |

      Builds (72 s wall) come on top. About 2,500 CPU-s on the ~6 free cores gives a **floor of ~400 s
      even if packed perfectly**. So the 3× target (~436 s) needs both less work and good packing.
    - **Angular suites:** each demo app has one spec file, but `@nx/angular:unit-test` compiles the
      whole app (`@spark-demo/demo-app`: ~29 net CPU-s for one spec). ng-spark's 1,009 tests cost ~130
      CPU-s, and `--no-isolate` changes nothing (178 vs 167 busy CPU-s, 16.2 s either way). **Rejected.**
    - **Concurrency, measured as full sweeps:** `maxParallelThreads` 0.25x for both heavy suites gave
      533 s, all green, against 563/575 s at 0.5x. It is **not adopted**: it gives CI's 4-vCPU runner one
      thread and makes single-project local runs much slower, and `xunit.runner.json` can't tell the
      two apart. nx `--parallel=2` at 0.5x gave 540 s with 41% recoverable time (poor packing).
      **Rejected.**
    - **Load-sensitive tests** that fail only in a fully loaded sweep and pass alone: `S_M3` (throttle
      deferred twice), `ModerationVoteTests.M5` (pending 0, not 10), and
      `ComplexFieldIndexingTests.Verbatim_complex_map_faults_per_document_on_Corax` (index errors not
      yet recorded when read). None was loosened; they need owner decisions.
14. **Packing: the local default becomes `--parallel=4`** (2026-10-02, this machine, after item 13's
    changes, full `--skip-nx-cache` sweeps, all green):

    | nx `--parallel` | Wall | nx recoverable time | Critical path |
    |---|---|---|---|
    | 3 (nx.json, CI) | 557 s | 23% | 6m57s |
    | 4 | **459 s** | 12% | 6m25s |
    | 5 | 445 s | 5% | 6m45s |
    | 5, CodeCoverage.Tests at `MaxParallelThreads=1x` locally | 452 s | 3% | 7m02s |

    - **Why packing mattered:** the CodeCoverage.Tests chain (its builds, then the suite) is the
      critical path. At 3 slots it waited about 2 min for one.
    - **4, not 5:** the next sweep at 5 was stopped by Claude Code for low memory (7–12 GB free of
      40 GB; every heavy suite runs its own in-memory RavenDB). The gain from 4 to 5 is within noise.
      `tools/test-local.mjs` passes `--parallel=4` unless the caller gives one. CI keeps 3.
    - **Rejected:** giving CodeCoverage.Tests all cores locally (452 vs 445 s, noise).
    - **Kept:** CodeCoverage.Tests' `WaitForIndexing` polls every 10 ms instead of the driver's 100 ms,
      with the same semantics (`CoverageRavenTest`, hiding the non-virtual driver method). It's green,
      but the gain is not separately measured: alone, 104 s is within noise of 86–95 s. Its sweep
      measurement was stopped for low memory.
    - **Result:** **21m49s → 7m39s (459 s) at the committed default, 2.85×; 7m25s (445 s) at 5,
      2.94×.** A sweep with the final commit has not completed; the owner runs it.
13. **Less CPU work per host boot and per test process** (2026-10-02, this machine, Developer licence,
    `dotnet test --no-build`, no coverage, each project run alone).
    - **Method.** Stopwatches plus per-thread CPU (`GetThreadTimes`) around every phase of
      `SparkEndpointFactory` and `UseSpark`, and the RavenDB server process's `TotalProcessorTime`
      read before and after each phase in a sequential throwaway benchmark (one boot at a time, so
      server CPU is attributable). Per-process CPU for whole runs from the raw WMI counters
      (`Win32_PerfRawData_PerfProc_Process`), which also cover Defender and System, unreadable to
      `Get-Process` without admin. All instrumentation reverted.
    - **Boot profile, ranked by CPU** (sequential, `TestSparkContext`, means per boot; ~535 boots per
      Spark.Tests run, counted):

      | Phase | Test-process CPU | Server CPU | Note |
      |---|---|---|---|
      | Index deployment (`CreateSparkIndexes`) | 2 ms | **58 ms** | one index: the only top-level index of the test assembly |
      | `_host.Start()` outside `UseSpark` (DI, Data Protection key ring, TestServer) | ~22 ms | ~0 | thread CPU unchanged by the key-ring change below |
      | Content root files (model JSON, security.json) | 3–7 ms | – | |
      | `UseSpark` verifiers (hash, security, reserved actions, posture) | ~6 ms | ~3 ms | |
      | `VerifySparkValueObjectKeys` | <1 ms | ~47 ms, **but only 64 of 535 boots** have keyed collections | ~3 server CPU-s per run |
      | Model hash written + verified again | 2.8 + 1.5 ms | – | ~2 CPU-s per run |
      | `LookupReferenceDiscoveryService` scan | 77 ms wall | – | **10 times per run** (resolved lazily), <1 s |

      A boot costs ~60 ms of test-thread CPU and, before this item, ~100 ms of server CPU, so ~535
      boots were ~85 CPU-s of a ~715 CPU-s project. Per-test databases (1,066 disposed per run,
      ~95 ms server + ~20 ms client each, measured as 300 sequential cycles) remain the larger cost.
    - **Done: the test assembly has no top-level index any more.** `RowRuleLedgers_Overview` was the
      only one, and lever A deploys every top-level index into every host's database: 58 ms of
      server CPU per boot, for the one case that deploys it itself anyway. Now nested in
      `SparkRowRuleTests`. The deployment test that used it as its top-level example asserts that
      rule on the host's filter, against an abstract top-level probe, because asserting it in the
      database needs a deployable top-level index, which would bring the cost back; the armed,
      unarmed and `deployAllIndexes` cases still assert in the database.
    - **Done: no dynamic PGO in test processes or the embedded server.** Found by the per-process
      counters: dynamic PGO instruments tier-0 code and rejits it from the profile, which a test run
      (thousands of distinct methods, a handful of calls each) pays for without using.
      `Directory.Build.targets` sets `TieredPGO=false` for `IsTestProject` (into the runtimeconfig the
      test host runs under; applications unaffected), and `RavenServerLocator.DisableServerDynamicPgo`
      sets `DOTNET_TieredPGO=0`, unless already set, before the server starts (it is a child process;
      the variable overrides RavenDB's runtimeconfig, which turns PGO on). Spark.Tests alone, 2 runs
      each:

      | | Test process CPU | RavenDB server CPU | Wall |
      |---|---|---|---|
      | Before (with the index change) | 172 / 181 s | 255 / 278 s | 131 / 148 s |
      | Property only | 142 / 136 s | 279 / 287 s | 148 / 145 s |
      | Property + variable (committed) | 136 / 142 s | 211 / 246 s | 112 / 129 s |

      `DOTNET_TieredCompilation=0` gave the same CPU but one failure in its single run; not used.
    - **Done, small: one Data Protection key ring per test process** (Data Protection creates it
      eagerly at host start, so each boot generated a key and wrote its XML). The discriminator was
      already `testhost` for every host, so only the key is shared. One paired run: host start 48 →
      29 ms wall per boot, thread CPU unchanged (27.8 vs 27.6 ms). **And no topology cache file per
      database** (`DisableTopologyCache` in every RavenTestDriver base): the client wrote one per test
      database into `bin/` (797 in Spark.Tests' bin) for a cache only read when the server is
      unreachable at startup.
    - **CodeCoverage.Tests** (same commits, alternating old/new builds, 2 rounds each): test wall
      123 / 123 s → 95 / 86 s, all 1045 green; RavenDB server 212 / 216 → 172 CPU-s (one readable
      sample). Per case, measured sequentially: an empty database 54 ms of server CPU, plus
      `Commits_ByRepository` +115 ms, plus a seed and `WaitForIndexing` +37 ms (and +137 ms of wall:
      RavenTestDriver polls every 100 ms), plus a dynamic query +19 ms. The heavy classes cost more
      because of their own work: CommitAssemblerTests ~0.78 server CPU-s per case alone (uploads with
      attachments, parse, finalize, assemble), ReadRowFilterTests ~0.37, BadgeControllerTests ~0.42.
      No cheap fix: the index is the real production index the code under test queries, and a
      faster `WaitForIndexing` poll saves wall only (149 calls, ~5 s of the suite's wall at 4
      threads), which does not shorten a CPU-bound sweep.
    - **Measured and rejected:**
      - *Short-circuiting `VerifySparkValueObjectKeys` on an empty database* (a core change that would
        need its own proof): 64 boots × ~47 ms ≈ 3 server CPU-s per run.
      - *Computing the model hash once* (factory writes it, startup recomputes it): ~2 CPU-s per run.
      - *Caching `LookupReferenceDiscoveryService`'s scan*: 10 scans per run, under a second.
      - *Workstation GC for the RavenDB server* (`DOTNET_gcServer=0`; its runtimeconfig uses server
        GC): server CPU 289 / 293 s against 283 / 267 s; no gain.
      - *RavenDB server options* `Storage.IO.Metrics.Enabled=false`, `Storage.EnablePrefetching=false`,
        `Monitoring.OpenTelemetry.Enabled=false`: 300 create/delete cycles cost 34–35 server CPU-s
        with or without them.
    - **Defender for Endpoint is a real share, and not ours to switch off.** During a Spark.Tests run
      MsSense, MsMpEng and System together use ~190 CPU-s more than at rest (real-time scanning is
      off, item 9, but the EDR sensor still records file and process activity). Host boots raise it
      (file writes in the content root), database cycles barely do. The two file changes above are
      the cheap part of that; the rest is outside this repository's control.
    - **Full sweep after item 13: not yet measured.** The first attempt (2026-10-02) was stopped by
      the agent harness during the build phase because the machine ran low on memory (~4 GB free of
      40 GB, with other workloads open), not by a failure. Pending: a sweep on a quiet machine,
      compared with 533–575 s.

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

## 9. Look-ahead: lyrics timings across several recordings (MintPlayer, NOT yet scheduled)

Investigated on 2026-09-30 at the owner's request, so that the design already covers these cases.
None of this is planned work yet. It lands in MintPlayer after the Contributions library.

### 9.1 The problem, and what MintPlayer has today (`C:\Repos\MintPlayer`)
- **The problem:** one song has several media, e.g. the official music video with a ~30 s
  prelude/interlude, and the continuous official audio. Line timings therefore differ per medium, and
  timing every line by hand is tedious.
- **Media** (`MintPlayer.Domain/Entities/Medium.cs:11-21`) are embedded `{ Value (URL), TypeId }`.
  They have **no stable id, no duration, no official flag**, and the platform is derived from the URL.
  Media hang off `Subject` (songs, artists, people).
- **Timings** (`LyricsTiming.cs:57-62`) are `{ MediumUrl, List<double?> StartTimes }`, in seconds and
  parallel to the lines. They are **keyed by the exact URL string**, so a changed URL orphans the
  timing. There is no offset, no sharing between media, and every medium is timed from scratch.
- **Today's manual sync** (`lyrics/song-lyrics.ts`): a per-line "Set" button stamps
  `progress().currentTime` while the song plays.
  - It has no keyboard shortcuts, nudging, playback rate or latency compensation.
  - Seeking goes through a private adapter hack (`player-card.ts:109-120`).
  - Progress is polled every 50 ms.
- **Data volume:** 141 songs, 254 media, 127 with lyrics, only ~20 with timings
  (`PRD-Spark-Completion.md:52`).
- **Infrastructure:** a single Hetzner VPS with Docker Compose (RavenDB, Postfix, app). There is **no
  worker container and no Python**, and no background jobs yet; Spark Messaging is planned (F9).

### 9.2 Timing model (recommended)
- **One canonical timeline** per song: line start times in **milliseconds**, against a **reference
  medium** (usually the official audio).
  - It is timed on the **original** version and shared by every language/script version by line index.
    C4 makes this work, and it is why an "original" version is needed (Q5, still open).
- **A time map per medium:** piecewise segments `[{ fromMs, toMs, shiftMs }]`. One segment is a plain
  offset; a prelude or interlude is an extra segment. Lines inside an unmatched stretch are flagged.
- **Sparse per-line overrides** per medium, for stragglers.
- **Media need a stable id** (a `Guid`) instead of URL keys, and should store their duration when
  first timed.
- **The Contributions library fits this** (a direction, not decided):
  - the canonical timeline as a zero-slot `[Contribution]` on the song (one current per song)
  - each medium's map as a `[Contribution] List<MediumSync>` with the slot `[ContributionSlot] Guid MediumId`
    (a `Guid` slot is allowed by T2)
  
  Timings then get the same per-user ownership, latest-wins, revert and moderation as the lyrics.

### 9.3 Getting timings — from cheapest to most automated
1. **Better manual tools** (free, and the biggest win), modelled on lrc-tap
   (https://github.com/retrokidworks/lrc-tap):
   - **Tap mode:** Space stamps the next line, Backspace un-stamps and seeks back ~3 s.
   - **Playback rate** 0.25–2× through the player (`setPlaybackRate`; react to `onPlaybackRateChange`).
     Timestamps stay in media time.
   - **Nudge** ±50/100 ms (Alt+←/→), **shift everything from line N by Δ**, and a global offset.
   - **Tap-latency compensation** (~100–200 ms, calibrated by a tap-along test, unverified).
   - **Preview and loop** (click a line to seek 1 s before it), undo/redo, and LRC import/export
     (including Enhanced/A2 word-level LRC).
   - **Player adapter:** add `seek`/`setRate` to `@mintplayer/ng-video-player` as public API, replacing
     the private hack, with rAF interpolation for highlighting. YouTube, Vimeo and SoundCloud are
     precise enough; Spotify is not a usable sync target.
   - **A waveform is impossible for iframe media** (no audio access); it works only for self-hosted
     files.
2. **Anchor alignment for additional media** (free, needs no audio). On a new medium, tap 2–3
   anchor lines (first line, the first line after an interlude, the last chorus). The segment time map
   is derived from them, and the stragglers get fixed. That takes about 30 s instead of re-tapping
   every line, and it is safe for YouTube, since nothing is downloaded.
3. **Seed from LRCLIB** (https://lrclib.net/docs — free, no key, MIT code, DB dumps):
   - `GET /api/get?track_name=&artist_name=&album_name=&duration=` returns LRC `syncedLyrics`, only
     within **±2 s of the duration**, which guards against a different master. It is timed to the
     album track, so the result becomes the canonical timeline on the official-audio medium.
   - **Caveats:** the data has **no stated licence**, since the lyrics are copyrighted (settle that
     before public display), and K-pop coverage is unverified; measure it on MintPlayer's own song list.
   - **Not usable:** Musixmatch (paid), NetEase/QQ (unofficial endpoints), YouTube captions
     (`captions.download` requires owning the video), Spotify and Apple (no public lyrics API).
4. **Automatic forced alignment** (free software, CPU, an optional worker):
   - **Pipeline:** Demucs vocal separation (the maintained fork https://github.com/adefossez/demucs,
     since the facebookresearch repo was archived in 2025), then a CTC forced alignment of the
     **original-script** text to the vocals, done in one pass for the whole song. The output is the
     start/end and a confidence per line.
   - **Candidates:**
     - **WhisperX** (BSD-2; Korean uses `kresnik/wav2vec2-large-xlsr-korean`, and ja/zh/ru/uk models
       exist)
     - **torchaudio MMS_FA / ctc-forced-aligner** (1,100+ languages through uroman, but **the model
       weights are CC-BY-NC**, which blocks commercial use)
     - **lyric-align** (MIT, built for CJK singing, very young)
     - Not recommended: stable-ts (paused), NeMo NFA (heavy, tested on English), MFA, aeneas and
       Gentle (built for speech)
   - **Cost (estimated, unmeasured):** ~2–5 CPU-minutes and ~2–3 GB RAM per 4-minute song. The
     backlog is small (127 songs with lyrics).
   - **Known failure:** repeated choruses assigned to the wrong repeat. That's why a human review
     stays mandatory:
     - low-confidence, too short or long, out-of-order or unmatched lines shown in amber
     - play-from-here and nudge
     - manually confirmed lines never overwritten by a re-run
5. **Automatic transfer between media** (only where both audios are legitimately available):
   - Fingerprint/correlation anchors: audfprint, audalign or BBC audio-offset-finder (avoid Panako,
     which is AGPL).
   - Merge them into offset segments, treating unmatched stretches as inserted, and optionally refine
     with chroma DTW (synctoolbox (MIT), libfmp or librosa).
   - The output is exactly the §9.2 segment map.
   - `ffsubsync` and `alass` are not usable directly: they rely on speech-VAD, which fails on music,
     and need the medium's audio. Their piecewise-shift *model* is what §9.2 adopts.

### 9.4 Audio source and legal position
- **yt-dlp works technically,** but YouTube's Terms of Service and Developer Policies prohibit
  downloading or separating audio without permission, and a datacenter VPS routinely gets "confirm
  you're not a bot" (PO-token) walls, so expect constant breakage. Storing copyrighted audio adds
  copyright exposure.
- **The lower-risk route:** an admin uploads the **official audio file**, the worker aligns it, stores
  **only timings**, and deletes the audio (a ≤24 h TTL for retries). YouTube and other embedded
  media then get their timings through **anchor alignment** (§9.3.2), never through a download.

### 9.5 Architecture, if and when automation is built
- **.NET:** a Spark Messaging job `{ songId, mediumId, audioRef, originalLines, language }`, running
  after MintPlayer's planned F9 messaging.
- **Worker:** a new `mintplayer-aligner` service in `deploy/docker-compose.yml`. It runs Python 3.11
  with pinned CPU torch, demucs, WhisperX (or ctc-forced-aligner), audfprint/synctoolbox and ffmpeg,
  with the models baked into the image (~1.5–3 GB). It uses single-slot concurrency to cap RAM.
- **Result:** per-line `{ index, startMs, endMs, confidence }` plus, where applicable, a segment map.
  It is written as a **contribution by a system "aligner" user**, so it goes through the same review,
  revert and moderation as human timings.

**Audio source (owner, 2026-09-30): pluggable, with `yt-dlp` as an opt-in.**
- The worker takes its audio from an `IAudioSource`:
  - **`UploadedFile`** is the default.
  - **`YtDlp`** is **off by default** and enabled in configuration at the owner's discretion. The
    Align dialog then also offers the song's YouTube media.
- `YtDlp` fetches the audio stream only, into a temporary folder that is deleted after the job (or
  after a short retry window). Only timings are kept.
- A failed download fails the job **visibly** ("download failed, upload the audio instead") and never
  falls back silently.
- It is a MintPlayer-only worker component, never part of a Spark package.
- **Kept on record:**
  - YouTube's Terms of Service and Developer Policies prohibit downloading or separating audio without
    permission, so the realistic risk is YouTube blocking the server or account.
  - Datacenter IPs regularly hit bot checks, so the step will break from time to time.
  - Keeping copyrighted audio briefly adds exposure.
- **Benefit:** a YouTube video's own audio is aligned directly, so its prelude and interlude come out
  right without anchors.

**The owner's "Align" flow (2026-09-30):**
1. The user triggers an **`Align` custom action** on the song. Its right is `Align/Song`, declared
   in MintPlayer's `[SparkReservedActions]` constants.
2. The backend answers with a **retry action** (`IRetryAccessor.Action(...)`) showing a dropdown of the
   song's versions. Only versions that pass **both** filters are offered:
   - **The same language as the original.** It must be what's sung: `ko/Kore` and `ko/Latn` qualify,
     a translation such as `en/Latn` never does.
   - **Supported by the configured aligner.** The worker's capabilities (e.g. `ko/Kore`, `ko/Latn`,
     `ja/Jpan`) come from configuration, not from asking the worker. So an aligner without Hangul
     support can still align the romanized version.
   
   If nothing qualifies, the action says so. The dialog also picks the **audio** (an uploaded
   official-audio file, per §9.4).
3. The user picks a version.
4. A job is **enqueued on Spark Messaging** and processed when it is dequeued. Its result is written
   as a **system-"aligner" contribution** of the canonical timeline, which never overwrites lines a
   human has confirmed.

Because all versions share the line structure (C4), timings from any aligned version apply by line
index to every version. The chosen version affects accuracy only.

**Q5 (owner, 2026-09-30): A.** `Song.OriginalLyrics` holds the original's slot key. Only `Edit/Song`
may change it (a contributor holds `Edit/Song` but is denied `Edit/Song/OriginalLyrics`). The line count is validated against it, and switching the original
is refused unless the line counts match.

### 9.6 Spikes to run when this is scheduled
- **L1 — Aligner accuracy:** 5 songs (including 2 K-pop and 1 with a skit), aligned with WhisperX-ko,
  MMS and lyric-align after Demucs, and scored against hand-made timings (median and p90 error per
  line).
- **L2 — CPU runtime and RAM** on the production VPS size.
- **L3 — LRCLIB coverage** of MintPlayer's song list (hit rate at ±2 s).
- **L4 — Model licences:** `kresnik/wav2vec2-large-xlsr-korean`, `jonatasgrosman/*`, the MFA Korean
  model.
  - **Owner, 2026-09-30:** MintPlayer is **non-commercial**, so MMS (CC-BY-NC 4.0) is usable, with
    three conditions:
    - Credit the MMS model and its licence in MintPlayer's credits.
    - The aligner stays a MintPlayer-only container and is **never** a Spark package default, since
      other Spark apps may be commercial.
    - Re-check if MintPlayer ever takes ads, paid tiers or sponsorship.
  - The licence status of *outputs* (timings) was not established; it doesn't matter under
    non-commercial use.
- **L5 — Anchor alignment UX:** how many anchors a typical music video needs, and the error remaining
  after deriving the segments.
- **L6 — The legal position** on displaying lyrics from LRCLIB, and on user-contributed lyrics
  generally.

## 8. Out of scope (genuinely not being done)

- **Auto-hiding a contribution when a flag is upheld** (a possible Moderation addition later).
- **Full-text search over contributions** (a separate index over the current documents, when needed).
