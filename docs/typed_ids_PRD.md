# Typed ids, `Id<T>`, on RavenDB and InMemory: PRD

Issue [#536](https://github.com/MintPlayer/MintPlayer.Spark/issues/536), part 1. Pull request #713, branch
`feat/536-typed-ids`. The how and when are in [`typed_ids_plan.md`](typed_ids_plan.md).

#536 makes Spark run on SQL Server and PostgreSQL as well as RavenDB. Its id model had to come first, and it
touches every library and app, so the owner split it out (2026-11-26): this pull request types the ids on the two
stores Spark has today, RavenDB and InMemory. The relational providers follow in #697, whose
`docs/relational_providers_PRD.md` holds the SQL side of the same decisions.

**Numbering.** The decisions keep the numbers they have in #536's PRD (D7, D23, D27, O15, spikes S-ID1 to S-ID6),
because the code comments cite them that way ("#536 D23 rule 6").

## 1. Goal

- One id type on every provider: an entity's `Id` and its references are `Id<T>`, not `string`.
- An id means the same thing whatever store backs the app: `GetById("ABC")` and `GetById("abc")` find the same row,
  and the framework renders one canonical form.
- The stored RavenDB documents don't change, and production needs no data migration.

## 2. Scope

**In this pull request:**
- `Id<T>`, `ISparkId`, `SparkIdKind`, `[IdKind]` (`MintPlayer.Spark.Attributes`) and the ids service `ISparkIds`
  (core's `SparkIds`).
- RavenDB: the generated `__DocumentId` identity, lower-case id conventions, the query and index id rewriting, the
  HiLo source, the Newtonsoft converter.
- InMemory: reads and writes `Id<T>`, lowers new ids.
- The typed session contract (D27) and the `Id<T>` route binder.
- Every library and app converted: Settings, Logging, Boards, Notifications, Migrations, Organizations, Apps, Mail,
  Files, Mcp, Moderation, Contributions, Authorization's agent grants and pending external logins, the identity
  provider, Dashboards, HR, Fleet, QnA, DemoApp, SeoTester, MintPlayer, CodeCoverage.
- Analyzers SPARK064 (an entity must be `partial`) and SPARK065 (an `Id<T>` passed as an entity).

**Not in this pull request:**
- Anything relational: D7's collations and key columns, D23 rules 8 and 11 (EF Core), L1's collection-collision
  check (it lives in #697's relational model). See #697.
- D23 rule 12's id templates (`[IdTemplate]`, generated builders and parsers): not built.
- D7's debug guard that refuses storing a new id that is not lower case: not built.
- `SparkUser` and `SparkRole` keep `string` ids (ASP.NET Identity's), and the libraries' own references to other
  documents stay strings in this pass (plan §3, M2c). Replication keeps string ids (RavenDB-only by design).
  Messaging converts with its port in #697 (M7).

## 3. Decisions

### D23: the id model, `Id<T>`

ChatGPT, Claude, Gemini, Grok, Copilot and Mistral reached it over five moderated rounds (2026-11-24); the owner
accepted it ("probably good"). Plan §2 has the rounds. Rules 8, 11 and 12 are #697's or not built (§2); their numbers
are kept so the code comments stay valid. A feature author writes only:

```csharp
public Id<Person> Id { get; set; }
public Id<Company>? CompanyId { get; set; }
public List<Id<Profession>> ProfessionIds { get; set; } = [];
```

1. **One generic readonly struct `Id<T>`** on every provider. No per-entity id structs, no per-provider aliases.
2. **It wraps only the canonical string `"{collection}/{key}"`.** Equality, hash, ordering and `ToString` are ordinal
   string operations. The key is everything after the first `/`, so a natural key may contain `/`.
   - **Without an attribute the kind is Guid** (owner, 2026-11-24), as every generated id already was. A Guid can't
     be counted or guessed, which the existence-hiding rules rely on, and needs no server coordination. New Guids
     stay random (v4), not v7: a v7 id carries its creation time into every URL.
   - Integer (HiLo) and Natural are opt-ins, through `[IdKind]` or the app model (`SparkIdOptions.Kinds`), which
     overrides the attribute, also for entities in feature packages.
3. **An id is canonicalised once, through the app model:** `ids.NewAsync<T>()`, `ids.Parse<T>(text)`, the JSON
   converters and the route binder. `default` is the empty id, and a store refuses it.
4. **The collection prefix is fixed by the type.** The canonical string is the format on the wire, in row security, in
   audit and in live updates. A URL may carry the key alone.
5. **The id is known before `StoreAsync`.** `NewAsync` draws a Guid, or from RavenDB's HiLo for the Integer kind, so
   references staged in the same session already hold the final id. A Natural-kind entity builds its id from its
   business key; `NewAsync` refuses it.
6. **RavenDB:**
   - A generated hidden `string? __DocumentId` is the identity property (`FindIdentityProperty`); RavenDB refuses a
     non-string identity. Its setter goes through `Id<T>.FromTrusted`, which lowers what RavenDB hands back, so a
     legacy mixed-case id loads lower case (D7).
   - The public `Id<T>` is `[JsonIgnore]`, so a document body never holds an `Id` field and a re-save of a
     string-Id document is byte-identical (S-ID1 P2). References are stored as plain strings.
   - Integer ids are RavenDB's native HiLo, lowered with their node tag (`people/1-a`), from Spark's own
     `RavenIntegerIdSource`: `store.HiLoIdGenerator` throws once `ApplySparkConventions` sets the generator (S-ID1 C13).
     Sorting by id is alphabetical on RavenDB (`people/10-a` before `people/2-a`).
7. **Id lookups the framework makes itself** (detail load, reference resolution, row security, lookups) go through the
   provider: `Load` on RavenDB. **LINQ an app author writes against `x.Id`** is rewritten at execution time, behind
   Spark's session (`RavenIdQueryRewriter`): the own `x.Id` becomes the identity, so the client emits `id()`, for `==`, `!=`,
   `Contains`/`In` and `OrderBy`. `id()` matches the real document id ignoring case, so it finds every document
   written before (none has a body `Id`) and a lower-case value finds a legacy id. `coll.Contains(x.Member)` becomes
   `x.Member.In(coll)`. A body-field `Id` (option a) was measured and rejected: RQL treats it as an ordinary field, so
   every production document is invisible to `where Id = …` (S-ID1 C6).
   - **Static indexes** (S-ID6): both Spark index bases apply `RavenIndexIdRewriter` to their maps, so `x.Id` prints
     as `Id(p)` and a reference's `.Value` as `.ToString()`. An index over a converted entity derives from a Spark
     base.
8. (EF Core, #697.) The one part that applies here: `Id<T>` implements `IComparable<Id<T>>`, ordinal, because InMemory
   sorts by the CLR value and throws without it. Sorting by id is unspecified for Guid ids.
9. **One framework-owned JSON setup:** Newtonsoft for RavenDB documents, System.Text.Json for MVC and SignalR. A JSON
   string reads lower case; the empty id writes `null`.
10. **Not supported:** composite keys, id obfuscation.

**Deviations, found while building (M2a):**
- `FromTrusted` is public, hidden by `[EditorBrowsable(Never)]`, not internal: generated code in an app's assembly
  can't call an internal member. It lowers, so it can't produce a non-canonical id.
- A Guid key is canonical in its `D` form (dashes), not `N`: every id RavenDB generated so far has that form, so
  parsing never moves a stored id.
- A reference compared with an `Id<T>` value needs no rewrite: one `RegisterQueryValueConverter<ISparkId>` covers
  every `T`, because RavenDB matches converters by the value's runtime type.
- **A hierarchy is typed by its root** (M2c, MintPlayer): the abstract `Subject` holds `Id<Subject>`, and the value
  keeps the derived collection's prefix (`songs/313`).
- **`__DocumentId` is internal, not private** (found by CI, 2026-11-25). RavenDB looks for the identity on the stored
  type, and reflection hides a base type's private member from a derived one, so MintPlayer's songs and artists
  (`Song : Subject`) were stored under a generated id and loaded with an empty one.

### D7: every id is lower case

The owner proposed it (2026-11-24): `GetById("abc")` and `GetById("ABC")` must behave the same whatever database backs
the app. ChatGPT, Claude, Gemini, Grok and Copilot agreed in round 10.

- **Spark lowers (`ToLowerInvariant`) every id where it is canonicalised**: `NewAsync`, `Parse`, the JSON converters,
  the route binder, and when a provider reads an id back (the `__DocumentId` setter). The whole string is lowered,
  prefix included: `Accounts/X` becomes `accounts/x`.
- `Parse` normalises the key: a Guid in any form becomes its `D` form, and digits lose their leading zeros (`"042"`
  becomes `"42"`). A Natural key is kept as written, lowered, and may contain `/`.
- **Casing that carries meaning never lives in an id.** A case-sensitive external key is its own property.
- **RavenDB conventions** (`ApplySparkConventions`, set before `Initialize()`): generated ids are lowered,
  `UseNaturalIds` lowers `GetId()`, `TransformTypeCollectionNameToDocumentIdPrefix` lowers the prefix (RoleStore and
  HiLo read it), and the HiLo id is lowered with its node tag. A store built by hand (a test driver, a tool) must call
  `ApplySparkConventions`, or RavenDB can't set an `Id<T>` ("property type is not a string").
- Two keys that differ only in case are one RavenDB document already, so lowering makes no new collision.
- **Consequence: compare ids ignoring case wherever a loaded id meets a stored string.** A reference, an id list
  inside a document, a hash input or a log key written before the upgrade keeps its casing, while the loaded id is
  lower case. Plan §3 lists every site that changed (about 25 in CodeCoverage alone, the OIDC ownership checks, the
  breadcrumb maps, the grid etags).
- **A hash of an id hashes it lower case**, so callers' casing can't split one value into two (`DependencySlots`).
  Two OIDC ids hash the pre-#536 form instead (`OidcGrantReference`, `OidcRefreshFamilies`), because those hashes are
  stored document ids in production: hashing the lowered form would have moved every grant and refresh family.
- **Object-store file keys** (S3 and the other `ObjectFileStore` backends) are the lower-cased entity id. The files
  reconciler moves a legacy-cased key once (`FilesReconciler.LowerCaseLegacyKeysAsync`), at startup and on every
  reconcile.

### O15: no re-key, and no migration that lowers document ids

- **Decided (round 10, 2026-11-24):** existing RavenDB data with mixed-case ids is not re-keyed. All five LLMs chose
  it, and spike S-ID4 proved it on a copy of the development `Coverage` database (§4): loads, prefix scans and
  reference equality ignore case on Lucene and Corax, and a re-save keeps the stored casing.
- **Reaffirmed by the owner (2026-11-26)**, after the review found grid rows of legacy documents without an etag:
  "No migration needed". RavenDB ids are case-insensitive, Spark lowers on load, and a typed reference is written back
  lower case on its document's next save. The fix belongs in the code that compares a stored id with a loaded one,
  because the stored casing also returns from backups, external links and the sidecar. PostgreSQL compares text
  case-sensitively, but no data lives there, and #697's providers write lower case from the first row. A tool that
  copies a RavenDB database into SQL would lower the ids then.

### D27: a typed session contract

`ExistsAsync`, `Delete` and `GetVersionsAsync` take an `Id<T>`, or a `Type` and the id (about 89 call sites). The
untyped string members are gone. With D23 the type is inferred: `Delete(person.CompanyId)` can only delete a
`Company`. `Delete(object entity)` refuses a string on both providers (found while building M2b: with `Delete(string)`
gone, an id bound to the entity overload without a compile error).

### The route binder (D7)

`SparkIdModelBinderProvider` (core, first in `MvcOptions`) binds an `Id<T>` or `Id<T>?` controller parameter through
`ISparkIds.Canonicalize`: any casing and the key alone bind to the canonical id. Text that is not an id of the entity,
such as another collection's id, is a model error (400). **Minimal APIs keep taking the text and call
`ids.Parse<T>`**, because a `BindAsync` on `Id<T>` would put ASP.NET types into the dependency-free attributes
package.

### Analyzers

- **SPARK064** (`DocumentIdGenerator`): an entity with `Id<Self>` must be `partial`, so the generator can emit its
  internal `__DocumentId`.
- **SPARK065** (`IdAsEntityAnalyzer`): an `Id<T>` passed to a session's `entity` parameter (RavenDB's
  `Delete<T>(T entity)`, `StoreAsync(object entity)`, `Evict`) is a compile error. The call compiled and threw "not
  associated with the session" at run time.

## 4. Spike results

The spike code is in #697's `docs/relational_spike/` (`sid1`, `sid4`).

### S-ID1 + S-ID5 (RavenDB half): `Id<T>` on RavenDB 7.2 (2026-11-24)

A console app with a minimal `Id<T>`, its Newtonsoft converter and a hand-written `__DocumentId`, against RavenDB
7.2.6 (client 7.2.7, .NET 11). **51 checks, 46 pass;** the 5 failures are expected negatives kept as evidence (four
raw `Contains` queries and the option-(a) body-field query).

- C1, C2/R7: store, load and an upper-case load give one instance; a non-public `__DocumentId` works as the identity.
- C4: raw `list.Contains(x.Id)` throws `NotSupportedException`, even over `List<string>` (the control); `In` works.
  Rewritten to `In`: ✓.
- **C6: a body-field `Id` query finds 0 documents without the field, which is every production document.** Rejects
  option (a).
- C9: a legacy `People/Legacy…` document loads by its lower-case id as one instance, `LoadStartingWith` finds it, a
  save keeps the stored casing, no duplicate.
- C13: HiLo `people/1-A` lowers to `people/1-a`; `store.HiLoIdGenerator` throws once `AsyncDocumentIdGenerator` is set.
- R1–R6: the rewrite gives `id() =`, `id() !=`, `id() in (…)`, `order by id()`, and a lower-case value matches a
  mixed-case stored id.
- P1–P4: with `[JsonIgnore]` on the public `Id<T>`, a load, change, change back and save is byte-identical.
- **G1: for a saved legacy document, RavenDB reports the stored casing** (`OnBeforeStore`'s `DocumentId`). This is
  why the debug guard was to check new documents only, and why a version or id RavenDB hands back can differ in case
  from the loaded id (§5).

### S-ID4: mixed-case legacy ids on a copy of a Coverage database (2026-11-24)

The local development `Coverage` database (4677 documents, 29 collections, every id mixed case, references in bodies
too) exported and imported into a throwaway database. **15/15.**

- Every document loads by its lower-cased id (4677/4677); three casings give one instance; `LoadStartingWithAsync`
  with a lower-case prefix finds every document of all 26 prefixes.
- A reference query with the original, lower and upper value agrees on the existing static index, an auto-index, a
  Lucene and a Corax index.
- Loading and saving 248 documents of 7 collections twice: no duplicate, **0 ids changed casing**; the only body
  change is the typed reference lowered ×32, and queries are blind to it.

### S-ID6: RavenDB static indexes over typed ids (2026-11-24, during M2)

Without a rewrite, RavenDB's index translator writes `p.Id.Value` as a body field that isn't there, and
`LoadDocument(p.CompanyId.Value.Value)` as `LoadDocument(p.CompanyId.Value, …)`, which fails on every document ("1 map
attempts, 1 has failed"). With `RavenIndexIdRewriter` the maps print `Id(p)` and `LoadDocument(p.CompanyId.ToString(),
…)`: 0 index errors, and `Where(r => r.CompanyId == acme.Id)` on the index returns the row. `SparkIndexCreationTask`
rewrites `Map`, and `SparkMultiMapIndexCreationTask` hides `AddMap` with a rewriting one. Test:
`RavenTypedIdIndexTests`.

## 5. Risks and traps

- **A raw `IAsyncDocumentSession` gets no id rewriting.** A typed `Contains` there binds to the span overload and
  RavenDB rejects it (use RQL `id() in ($ids)`), and `.Select(r => r.Id)` then `.OfType<string>()` silently returns
  nothing. Project into a row with a `string? Id`.
- **An `Id<T>` binds to `object` parameters without a compile error:** `Delete<T>(T entity)`, `Attachments.GetAsync(
  object entity, …)`, `IncrementalTimeSeriesFor`, `QueryParameters`. SPARK065 catches the session's entity
  parameters where the analyzer is loaded; pass `.Value` to the rest.
- **What RavenDB hands back keeps the stored casing:** `@metadata.@id`, `GetDocumentId`, a version keyed by id. Code
  that matches those against a loaded id compares ignoring case. The review of #713 found one that didn't: the grid's
  etags (plan §3).
- **A change of id casing needs the whole suite on CI**, not the touched classes: M2 alone was red on CI in places no
  touched class covered.
- Keys, hashes and dedup keys built from a loaded id shift once at deploy (for example a repository's scheduled
  minute in CodeCoverage). Accepted as a one-off.

## 6. Open questions

None.
