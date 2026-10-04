# Plan: remove `IsVisible` + Spark JSON schemas (#264)

PRD: [remove_isvisible_PRD.md](remove_isvisible_PRD.md), with the final design in **§0** and the decisions in §7 (`G-Q*`).
Issue: [#264](https://github.com/MintPlayer/MintPlayer.Spark/issues/264)
Status 2026-10-04 (v3, after the grill): **not started.**

Rules:
- **Commit per milestone; run tests only at the end (M10).** Check each intermediate milestone by building and reading
  the code.
- **One PR**, on a fresh branch from `master`. Minor version bump only (no platform major changed).
- From the prototypes, take only:
  - `AttributeRightsWellKnownGroupsTests` from `worktree-agent-a4b724a97914c0919` (`1f732374`)
  - the write-gate removal from `spike/264-runtime-visibility` (`dd2807d8`)

  Everything else in them is superseded (G-Q3a, G-Q6).
- Every decision taken during implementation goes into PRD §7 with its evidence.

## M0: red tests for the live bugs
- E2E Fleet: Stolen + police report → saved and stored; detail page on load shows the field only when Stolen.
  **Red on master.**
- E2E HR: the create form shows LastName and a Person can be created. **Red on master.**
- G6: a server validation error on an attribute that is not drawn is visible on the page. **Red on master.**

## M1: write gate (G-Q7)
- Guard test first: every attribute that is `isVisible: false` on master is `isReadOnly`, `Edit`/`New`-denied,
  `[IgnoreProperty]` or deleted after M5.
- Delete `!def.IsVisible` from `EntityMapper.IsWritableBySchema`. Rewrite `ModelFileShapeWriteGateTests.cs:57-100`.
- Cherry-pick `AttributeRightsWellKnownGroupsTests`.

## M2: `showedOn: None` + runtime `ShowedOn` (G-Q3, G-Q4)
- `EShowedOn.None = 0` (C# + `showed-on.ts`); `ModelSynchronizer.cs:889-890` derives `showedOn` only when it is absent.
- Server: the per-request `PersistentObjectAttribute.ShowedOn` set by actions survives into the response; on PO
  endpoints every received attribute already ships.
- ng-spark: po-detail, po-edit, po-create, po-form, grid helpers, AsDetail pipe and the picker draw from the **runtime**
  attribute `showedOn` (fallback: model). The loaded/new PO's `showedOn`/`isRequired`/`isReadOnly` apply on the first
  render (G5/G7). The refresh overlay carries `showedOn` instead of `isVisible`.
- Tests: `None` ships on the PO and draws nowhere; sync preserves `None`; a runtime `ShowedOn` set in `OnLoad` shows on
  the detail page and the edit form without a refresh.

## M3: delete `IsVisible` everywhere (G-Q1/3, G-Q8, G-Q16)
- Model: `EntityAttributeDefinition.IsVisible`, the `ModelFileShape` structural field (+ remarks :40-46, :160-170),
  `ModelSynchronizer.cs:916`.
- Seed API: `SparkNewAttributeSeed`, `SparkModelSatellites.SeedNewAttribute`/`NewAttributeSeedFor`,
  `ModelSynchronizer.cs:936-940`, `ContributionDescriptor.cs:273-282`; update `ContributionsModelSyncTests.cs:99-133`.
- Wire: `PersistentObjectAttribute.IsVisible` + converter (:132,158,208), `QueryColumn.IsVisible`,
  `QueryResultProjector.cs:54-75`, `SparkComposedQueries.cs:49-58` (error text), `DenyAllRowSecurity.cs:81`, fixture
  initialisers.
- Loader: `"isVisible": false` → startup error naming the attribute and the replacement; `true` stripped by sync.
- ng-spark: remove from the TS models and every reader (PRD §9.1); update the specs.

## M4: attribute-rights gaps G1–G4, G6
- `ForFormAsync(verb)` + `types/{id}?for=new|edit|read` (narrowing only); the create form asks `for=new`.
- Prune metadata by Query for the grid-column helper, or delete `visibleGridAttributes` if it is unused.
- `SparkSelectionResolver.cs:94` builds columns from the pruned definition.
- G6: validation errors on attributes that are not drawn become a form-level error.

## M5: migrate the apps
- **Fleet:** `PoliceReportNumber` → `showedOn: None`; `CarActions` sets the runtime `ShowedOn` (+ `IsRequired`) in
  `OnLoad`/`OnNew`/`OnRefresh`. `PromoVideoUrl` likewise. `Car.CreatedBy` → `[IgnoreProperty]`.
- **HR:** `LastName` → `showedOn: PersistentObject`; `GetPeople` sorts by `FullName`; breadcrumb `{FullName} @ {Company}`.
- **CodeCoverage:**
  - `[IgnoreProperty]`: Account.GitHubId and InstallationId (delete `AccountActions.cs:51-53`), Repository.GitHubId.
  - Delete Home.Title, ForgeAccounts.Title and SparkUser.Id.
  - Denies:
    - Account.OwnerKey and Repository.PreviousFullNames: `QueryRead`, both groups
    - ForgeAccounts.Provider: `Read`, `authenticated`
    - Home AccountCount/RepoCount: `QueryRead`, `anonymous` (drop the `HomeActions` flips)
  - Repository.OwnerKey and MyAccountRow.Provider: derive the provider from the id on the client, then deny.
  - `Repository.IsPrivate` becomes its own narrow 🔒 column (renderer on IsPrivate). `MyAccountRow.Type` is folded into
    the avatar cell (`MyAccountsService.cs:108-110`).
  - `GateSettingsActions.cs:41` → runtime `ShowedOn`.
  - Update `RemainingActionsTests.cs:126-144`, `GateSettingsTests.cs:86-146`, `AttributeTabGuardTests.cs:129`,
    `ModelColumnGuardTests.cs:39`.
  - ⚠️ CodeCoverage is production; the 🔒 column is a visible change.
- **QnA:**
  - `CreatedBy`/`ModifiedBy` → `[Reference(typeof(SparkUser))]`, `showedOn: PersistentObject`, `isReadOnly: true`.
  - `DeletedBy` → `showedOn: None`.
  - `security.json`: `Read/SparkUser` for `anonymous` + `authenticated`, and attribute denies on every SparkUser
    attribute except `UserName`. Verify with a test that `/po/sparkuser/{id}` exposes nothing else.
  - The application's model decides how Contributions' `ContributorId`/`Key` are shown.
- Run `--spark-synchronize-model` per app and commit the regenerated models (the model hash changes).

## M6: schema generation (G-Q11, G-Q12, G-Q13, G-Q18a)
- A generator (a small console tool or test-driven step) runs `JsonSchemaExporter` over the deserialized types:
  `EntityTypeFile`, `SecurityConfiguration`, and the `programUnits`/`translations`/`culture`/`actions` file types.
  Settings:
  - `additionalProperties: false`
  - `patternProperties: { "^_": {} }`
  - `TransformSchemaNode` for string-written `[Flags]` enums (`showedOn: "Query, PersistentObject"`)
- Output: the **unversioned** set in `schemas/` at the repo root, which is **gitignored** (G-Q19/Q20). It runs as part
  of the build, so a fresh clone gets it on the first `dotnet build`.
- **Deterministic output**: stable property order, `\n` line endings, no timestamps. A test generates twice and compares
  byte-for-byte; the revision comparison depends on it.
- Revision `n` (G-Q21): compiled into the package as `SparkSchemaRevision.Current`.
  - CI passes it as an MSBuild property.
  - Local builds derive it from `git describe --tags --match "schemas/v*"`, falling back to `0` when no tag is present.
- Guard tests:
  - generation is deterministic
  - every model/security/… file in `apps/` validates against the locally generated schema (using a JSON Schema
    validator in the test project)
  - the six schema types match what the loaders deserialize

## M7: sync writes `$schema` (G-Q17)
- `--spark-synchronize-model` adds `"$schema": "https://schemas.spark.mintplayer.com/v{SparkSchemaRevision.Current}/<file>.schema.json"`
  when it is missing, and rewrites only the `v{n}` segment of an existing `schemas.spark.mintplayer.com` URL. Any other
  `$schema` (this repo's relative paths) is left alone.
- **Line-level edit only**: formatting, key order and `_comment`s stay byte-for-byte. Test with a file holding comments
  and odd formatting.
- This repo's files get **unversioned** relative paths `../../../schemas/<file>.schema.json` (depth per app). The sync
  leaves relative paths alone (G-Q20).
- Confirm `$schema` does not affect the model hash or loading (System.Text.Json ignores it; `ModelFileShape` hashes
  structural fields only).

## M8: revision tagging, releases, `apps/SparkSchemas` + deployment (G-Q9, G-Q19, G-Q21)
- **In the master publish workflow, before the NuGet pack**, with `concurrency` serialising master pushes:
  1. generate the set
  2. find the latest `schemas/v*` tag (none means `n = 0`)
  3. download that release's assets and compare
  4. if they differ: tag the commit `schemas/v{n+1}`, create the GitHub release with the six files, deploy the site, and
     pack with `n+1`
  5. otherwise: pack with `n`

  The package can therefore never embed a revision whose release does not exist.
- `apps/SparkSchemas/Dockerfile`: the build stage downloads the assets of **every** `schemas/v*` release into
  `/v{n}/`. The serve stage is nginx (alpine), with `Access-Control-Allow-Origin: *`, `Content-Type:
  application/schema+json`, and immutable cache headers. The site can be rebuilt from releases alone.
- Deploy modelled on `code-coverage-deploy.yml` (build → GHCR → SSH), triggered by a new revision release and by
  `apps/SparkSchemas/**`. An image-check workflow on PRs (like `code-coverage-image-check.yml`).
- GitHub setting: a tag-protection rule on `schemas/*`. **Repository settings change: confirm with the owner.**
- VPS: add the service to the server-managed `docker-compose.yml` and the reverse proxy for `schemas.spark.mintplayer.com`
  (DNS already points there). **This is an operator step; check with the owner before touching the VPS.**

## M9: docs and release notes
- `guide-authorization.md`:
  - "Hide an attribute"
  - everyone = both `wellKnown` groups
  - an important **type** grant unlocks denied attributes
  - denied breadcrumb tokens render blank
  - "`showedOn` is layout; protect with `isReadOnly` or a deny"
- `guide-triggers-refresh.md`: the runtime-`ShowedOn` pattern (OnLoad/OnNew/OnRefresh).
- `guide-queries-and-sorting.md:66-84`: sort only on a shown + indexed column.
- A new `guide-json-schemas.md` (URLs, revisions, `$schema` handling, the `_` comment convention).
- `libs/contributions/MintPlayer.Spark.Contributions/README.md:247` (G-Q16a), `libs/spark/MintPlayer.Spark/README.md:392,509,597-605`,
  `AGENTS.md:214-224`, `IPersistentObjectActions.cs:53`, `DefaultPersistentObjectActions.cs:454`, the stale docs in PRD §9.1.
- `issue_348_PRD.md` F10: a schema exists now.
- Release notes (minor, breaking):
  - `IsVisible` and the seed API are removed
  - the `isVisible: false` startup error and the migration table
  - `showedOn: None`
  - the schemas and `$schema`

## M10: sweep and browser check
- `RAVENDB_LICENSE='C:\Repos\MintPlayer.Spark\.secrets\raven-license.log' npm run test:affected`, E2E included.
- playwright_node:
  - Fleet: stolen-car round trip
  - HR: create a Person; grid ordered by FullName
  - CodeCoverage: Home as anonymous, the 🔒 column
  - QnA: "asked by {UserName}" as a visitor, and `/po/sparkuser/{id}` shows nothing else
- Editor check: open a model file in VS Code, confirm validation, and confirm a stray `"isVisible"` is flagged.
