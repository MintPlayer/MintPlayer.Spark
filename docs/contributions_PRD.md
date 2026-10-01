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

**Rule: CI runs cost money, never push just to get a test run** (owner, 2026-10-01). It is recorded in
the global and repo `CLAUDE.md`. A sub-agent's unrequested push and draft PR #465 prompted it. The full
sweep therefore has to be practical locally, and CI's behaviour stays exactly as it is.

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
