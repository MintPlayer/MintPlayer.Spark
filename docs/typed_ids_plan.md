# Typed ids, `Id<T>`, on RavenDB and InMemory: plan

Issue [#536](https://github.com/MintPlayer/MintPlayer.Spark/issues/536), part 1. Pull request #713, branch
`feat/536-typed-ids`. The decisions and their evidence are in [`typed_ids_PRD.md`](typed_ids_PRD.md).

**Status (2026-11-26): built.** Every milestone below is done, CI is green, and the review's one finding is fixed. No
decision is open.

## 1. The split

#536 was built as one pull request, #697, with the relational providers. Its id model (M2) touched every library and
app, so the owner split it out (2026-11-26) to make the review smaller:

- **#713 (this):** M2 and the fixes it needs, on RavenDB and InMemory.
- **#697:** everything else in #536 (M1 and M3–M9: the SQL Server and PostgreSQL providers, views, migrations,
  messaging's port, the SQL demo). It will be rebuilt on master once #713 is merged, so it shows only what remains.

How the branch was built: the 22 M2 commits were replayed onto master as they are, without #697's PRD, plan and spikes.
The code was identical to #697 at `55a88f341`. The fixes #697 made after M2 were then taken file by file from
`0c53eaaf0`, `fb9a04791`, `4d134af61`, `be7e36919` and `5b3a502e9`, leaving out their relational parts. One conflict,
in `QueryExecutor`: `SparkExpressions.Captured` arrives with #697's M3, so the case-insensitive id set is an
`Expression.Constant` here. The commits keep the dates they were written on #697.

## 2. How the design was reached

### Rounds 4–5: the id model (2026-11-24)

The owner had the design debated in a browser with ChatGPT, Claude and Gemini (rounds 1–3), then Grok, Copilot and
Mistral (challenge, rounds 4–5). DeepSeek answered only round 1, because its account was suspended. All six ended
with "AGREE WITH CONSENSUS", and the owner accepted it ("probably good"): PRD D23.

Facts checked locally during the rounds: Raven.Client 7.2.7 has **no** `RegisterIdConvention`,
`IdentityTypeConvertors` or `DelegateTypeConverter` (two of the first designs relied on them). It does have
`FindIdentityProperty`, `RegisterQueryValueConverter`, `OnAfterConversionToEntity` and `GenerateNextIdForAsync`.

| Topic | After round 3 | Final (rounds 4–5) | Why |
|---|---|---|---|
| Payload | a kind tag + long/Guid/string | **the canonical string only**; kind is model metadata | simpler; the wire format is the domain format |
| RavenDB int ids | a framework HiLo over compare-exchange | **RavenDB's native HiLo**, `"people/1-a"` opaque | the owner's requirement said "RavenDB's HiLo"; one allocator less |
| RavenDB sync | store events | **a pass-through property** (`__DocumentId`) | follows from the string payload |
| `x.Id` queries | an execution-time rewrite | **spike order: body field → rewrite → helpers** | the rewrite is "a LINQ provider shim forever"; S-ID1 then chose the rewrite |

### Round 10: every id lower case (2026-11-24)

The owner proposed that Spark lowercases every id when it creates, parses or reads one, so `GetById("ABC")` finds
`abc` on every provider. ChatGPT, Claude, Gemini, Grok and Copilot agreed (PRD D7). For existing production ids all
five chose no re-key, and the owner followed: O15, gated by spike S-ID4, which passed (PRD §4). Claude added that
`LoadStartingWithAsync` must be shown to ignore case on mixed-case ids; S-ID4 showed it.

## 3. Milestones and their records

### M2a: the foundations (`e9803bec3`)

- `Id<T>`, `ISparkId`, `SparkIdKind` and `[IdKind]` live in `MintPlayer.Spark.Attributes`, the dependency-free package
  entity libraries reference. The struct carries its System.Text.Json converter as an attribute.
- `ISparkIds` (`NewAsync`, `Parse`, `TryParse`, `Canonicalize`, `KindOf`) is implemented by core's `SparkIds`. The app
  model's overrides go in `SparkIdOptions.Kinds`, and the provider's HiLo is `ISparkIntegerIdSource`.
- `SparkIdValues` (Abstractions) is the reflective bridge between a string and an `Id<T>` that core's mapper, row
  security and query executor use. It let the entities convert one library at a time.
- RavenDB: `UseTypedIds()` covers the `__DocumentId` identity, lower-case prefixes and an `ISparkId` query value
  converter. A Newtonsoft converter and a contract resolver drop the own `Id` from the body. `RavenIdQueryRewriter`
  handles `x.Id ==`, `OrderBy` and `Contains` → `In`. `RavenIntegerIdSource` is the HiLo.
- InMemory reads and writes `Id<T>` and lowers new ids.
- `DocumentIdGenerator` (LibraryGenerators) emits `__DocumentId`; a non-partial entity is SPARK064.
- Tests: `SparkIdConformanceTests` (both providers) and `DocumentIdGeneratorTests`.
- The deviations from D23 found while building it are in PRD D23.

### M2b: the typed session contract, D27 (`f7f5added`)

- `ExistsAsync`, `Delete` and `GetVersionsAsync` each have a typed form (`Id<T>`) and a `Type` form; the untyped string
  members are gone. Framework sites pass the runtime type, and `QueryResultProjector` reads versions per
  `ObjectTypeId` through the model loader.
- Most of the ~80 sites in the inventory turned out to be raw RavenDB sessions, which keep their own API.
- Found: with `Delete(string)` gone, an id bound to `Delete(object entity)` without a compile error
  (`DeleteDocumentUncheckedAsync` did). Both providers now refuse a string there.

### S-ID6: static indexes over typed ids (`62397fa9c`)

Index maps over typed ids go through `RavenIndexIdRewriter`, which both Spark index bases apply. A raw
`AbstractIndexCreationTask` on a converted entity moves to a Spark base. Evidence in PRD §4.

### M2c: every library and app on typed ids, one per commit

For each library: add the LibraryGenerators analyzer reference; make the entities `partial` with `Id<Self>` and the
right kind (`[IdKind(SparkIdKind.Natural)]` for built ids); make the id builders return `Id<T>` and pass `.Value` to raw
RavenDB sessions; run the library's test classes.

| Commit | Library | Record |
|---|---|---|
| `86b8aa3a5` | Settings | and SPARK065 (`IdAsEntityAnalyzer`) for an id passed as an entity |
| `f8373214b` | Logging | |
| `564c26493` | Boards | |
| `75df4229b` | Notifications | |
| `a9a39e83f` | Migrations | |
| `1a8d47424` | Organizations | a wire id is keyed by the membership's stored `OrganizationId`, not the loaded `Id` |
| `40e703e6a` | Apps | id lists inside documents (`EndpointIds`, `AppIds`, `InstallationIds`) are removed and compared ignoring case; the generated `SparkDocumentIds.g.cs` writes `#nullable enable` itself (CS8669) |
| `5c141741e` | Mail and Files | |
| `1da467c1e` | Mcp | the plan-id pattern accepts the legacy `McpPlans/` ignoring case |
| `e61c892f5` | Moderation | case ids accepted ignoring case; a query projecting `v.Id` reads the documents instead |
| `33c7bac5d` | Contributions | its shape marker; the generated `{X}Contribution`/`{X}Current` convert with QnA |
| `8259c71cd` | Authorization | agent grants and pending external logins; a user's grants are listed by a prefix RavenDB matches ignoring case |
| `b181abbf0` | IdentityProvider | the seven OIDC documents; two hashes keep the pre-#536 form (PRD D7); every ownership check compares ignoring case. 848 IdP/Mcp/Apps tests and SparkId.Tests green |
| `7b63ad9ac` | Dashboards, data-protection keys | both natural; production's `DataProtectionKeys/…` still decrypt (the SP-B compatibility tests) |
| `7fc10a247` | HR, Fleet, QnA, DemoApp | all Guid; `ContributionsEmitter` emits `Id<T>` and its own `__DocumentId`; legacy rows matched ignoring case (`ContributionDescriptor.AnyCase`, ng-spark's `lineDiffTextOf`) |
| `00de4dc00` | SeoTester | Site, AuditRun and AuditLogEntry Guid; the derived documents natural |
| `34c09ecad` | MintPlayer | a hierarchy is typed by its root (`Subject`); the `Spark_Search` generator maps a typed id as `d.Id.Value` |
| `ee3471889` | CodeCoverage | 33 documents, all natural; about 25 in-memory id comparisons now ignore case; a hash of an id hashes it lower case (`DependencySlots`); raw-session traps fixed (PRD §5). The 49 touched classes, 1063 tests, green |

Not converted in this pass (PRD §2): Replication (RavenDB-only, and `SyncActionRetrySweeper` projects `a.Id` from a raw
index query), Messaging (converts with its port in #697's M7), `SparkUser` and `SparkRole` (ASP.NET Identity's string
id), and references to other documents (they stay strings; the caller passes `.Value`).

### The route binder (`b0cdb5b06`)

`SparkIdModelBinderProvider` binds `Id<T>` controller parameters (PRD, "The route binder"). Test:
`SparkIdModelBinderTests`, 6 tests, red first (no binder).

### Fixes taken from #697 after M2 (`ddbd47e5b`, `8eb9ddb31`, `01c759149`, `37bf64860`)

CI on #697 had been red since M2, because the lower-case ids were verified only on the classes M2c touched. Real
defects it found, each now compares ids ignoring case:

- CodeCoverage's forge disconnect (`ForgeEventsRecipient`) and the commit-graph sidecar id regex (answered
  "unavailable").
- `QueryExecutor.RestrictToIds` in memory: custom-query row actions answered 404.
- `BreadcrumbResolver`'s id maps: a legacy-cased reference lost its label (MintPlayer's song page showed no artists).
- The object file store: keys are lower case, and the reconciler moves legacy keys once.
- **`__DocumentId` is internal, not private** (PRD D23): MintPlayer's songs and artists were stored under a generated
  id and loaded with an empty one. Pinned by `SparkIdConformanceTests.An_id_declared_on_a_base_type_round_trips`, red
  on RavenDB before.
- The E2E host's raw store applies `ApplySparkConventions`.
- Test expectations of legacy-cased ids became the loaded lower-case id (`8eb9ddb31`).
- A typed-id reference resolves its breadcrumb, and an edit that keeps a dangling typed reference is no longer refused
  (`SparkIdValues.ReferenceTexts`, `01c759149`).
- SPARK064's message names the internal `__DocumentId` (`37bf64860`).

**Lesson:** a change of id casing needs the whole suite on CI, not the touched classes.

### Release notes (`0c14fadff`)

`docs/release-notes-713.md`: the breaking changes for apps (`Id<T>` everywhere, partial entities, case-insensitive id
comparisons, `ApplySparkConventions` on hand-built stores, the typed session members).

### CI on #713 (`26359d4f9`)

The first run on #713 (45861760062) was red in two places that #697 had fixed in later commits:

- **SPARK053** (`DefaultIndexAnalyzer`) reported every Spark multi-map: it didn't know the typed-id `AddMap` that
  `SparkMultiMapIndexCreationTask` hides RavenDB's with. It accepts it now (from #697's `be7e36919`).
- `DashboardHistoryTests` built a second Raven client without the Spark conventions, so it couldn't set an `Id<T>`
  (from #697's `5b3a502e9`).

Green after: run 45864438531 (30 pass, 6 skip).

### Coverage (`ffd101e97`)

The coverage server reported `Id.cs` lines untested: the ordering operators, the untyped `CompareTo`, the
System.Text.Json read, and `[IdKind]`. New `IdValueTests` and a `[IdKind(Natural)]` case in `SparkIdConformanceTests`
(on RavenDB and InMemory, 20 tests). Patch coverage went from 94.1% to 96.9%, with `Id.cs` fully covered. Run
45868384973 green.

### Review finding: a pre-upgrade document's grid row lost its etag (`6ef4df498`)

Found by the review of #713 (2026-11-26). `QueryResultProjector` merged the per-type versions into an ordinal
dictionary, but RavenDB keys a version by the stored id. A document stored before typed ids keeps its casing there
(`People/…`: master named new documents after the collection's casing), while its typed row id is lower case. So every
such row, which is every document in today's production databases, had no etag, and a delete from the grid was refused
(#467 D14: a missing etag is a 400).

- The merge now ignores case, like RavenDB's ids. Lowering the keys in the provider instead would break entities with a
  `string` id, whose rows keep the stored casing.
- Red first: `QueryRowEtagCasingTests`, "Expected row.Etag not to be null or empty, but found <null>". Green after,
  with `Issue467StaleDeleteTests`.
- The owner decided against a data migration that lowers the document ids (PRD O15).
- **#697 must keep this:** its `QueryResultProjector` still has `StringComparer.Ordinal`, and its rebuild takes #713's.

## 4. Verification

Local, by class (no sweep):

- The solution builds, 0 errors.
- `MintPlayer.Spark.Tests`, the typed-id classes (`SparkIdConformanceTests`, `RavenTypedIdIndexTests`,
  `SparkIdModelBinderTests`, `ReferenceTargetCollectionTests`, `BreadcrumbResolverTests`, the file-store and natural-id
  classes, `CrossCollectionBindingTests`, `AddSparkDocumentStoreFactoryTests`): 102 passed; the 24 skips are the S3 and
  Azure Blob conformance classes, which need those services.
- `MintPlayer.Spark.SourceGenerators.Tests`: `DocumentIdGeneratorTests` and `ContributionsGeneratorTests` 17 passed,
  `DefaultIndexAnalyzerTests` 23 passed.
- `DashboardHistoryTests` 6, `IdValueTests` + `SparkIdConformanceTests` 20, `QueryRowEtagCasingTests` +
  `Issue467StaleDeleteTests` 8.

CI runs the rest: every suite, E2E included.

## 5. After #713 merges

- Rebuild #697 on master and force-push it (owner, 2026-11-26), so it shows only M1 and M3–M9.
- Its relational PRD refers to `typed_ids_PRD.md` for D7, D23 and D27 instead of repeating them, and keeps the SQL
  parts (collations, rules 8 and 11, L1).
- Its `release-notes-697.md` drops the items that shipped here.

## 6. Log

| Date | What |
|---|---|
| 2026-11-24 | Rounds 4–5 and 10 decide D23, D7 and O15; spikes S-ID1, S-ID4, S-ID5 pass; M2a, M2b, S-ID6 and the first M2c libraries built on #697 |
| 2026-11-25 | The rest of M2c and the route binder; #697's CI fix for lower-case ids and the base-type document id |
| 2026-11-26 | The owner splits M2 out of #697 into #713; release notes; CI fix; coverage tests; review finding fixed; no data migration (O15 reaffirmed); this PRD and plan |
