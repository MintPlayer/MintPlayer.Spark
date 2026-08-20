# Plan — Issue #281: load base documents as the entity type in `RowSecurity`

**PRD:** [issue_281_PRD.md](issue_281_PRD.md) ·
**Branch:** `fix/issue-281-rowsecurity-typed-base-load` ·
**PR:** [#282](https://github.com/MintPlayer/MintPlayer.Spark/pull/282) ·
**Follow-up:** [#283](https://github.com/MintPlayer/MintPlayer.Spark/issues/283)

Test-driven by request: S0 reproduces, M1 lands the failing tests (red), M2 makes them pass, M3
re-verifies in the demo app. The PR is squashed, so the intermediate red commit never reaches
`master`.

| | Milestone | State |
|---|---|---|
| S0 | Spike: reproduce the throw in the test app | ✅ reproduced on `master`, production trace frame-for-frame |
| S1 | Spike: identity-map interaction | ✅ answered both ways — landed as tests, not a throwaway probe (see S1) |
| M1 | Failing tests pinning the contract (red) | ✅ `20153a5` — 5 red / 1 green control; one test changed shape (deviation 2) |
| M2 | The fix: typed batched reload, both call sites | ✅ `1ea2f47` — 6/6 green |
| M3 | Verify in the demo app + full suite sweep | ✅ unit 1563/1563 · Client 38/38 · SourceGenerators 197/197 · E2E 78/78, incl. the new metadata-trigger test proved red (real HTTP 500 from Fleet) before green |
| M4 | Version bump, release notes, follow-up issue | ✅ `preview.57` across all 21 packages; notes written; follow-up filed as #283 |

**Deviations from this plan, as executed.** Two, both deliberate:

1. **`QueryDeclaredIndexBindingTests` was not extended** (M1's second file). Its purpose was an
   end-to-end assertion through the real `QueryExecutor` with a real index and projection binding —
   which the E2E test against the running Fleet app covers more convincingly, since it also exercises
   `DatabaseAccess` (AC 2), the HTTP surface, and a genuinely index-projected `VCar`. Adding a second
   row rule to a shared DI-hosted fixture would also have flipped `HasRowRule(Commit)` for the three
   existing tests there for no extra coverage. If the E2E suite is ever trimmed, this is the unit-level
   test to add back.
2. **One M1 test changed shape** — see S1 below; the originally-planned assertion was not one the fix
   can honestly make.

**Design note — why the reload uses reflection.** `FilterAsync` receives `Type entityType`, not a
compile-time `T`: its callers resolve the type from a model-JSON `ClrType` string at runtime, so a
generic `FilterAsync<TEntity>` would only push `MakeGenericMethod` up into `QueryExecutor` and
`DatabaseAccess` — more reflection, not less. The one route that would remove it —
`LoadAsync<BlittableJsonReaderObject>` (a fixed type argument) followed by
`Conventions.Serialization.DeserializeEntityFromBlittable(Type, …)` — is blocked: RavenDB 7.2.5
exposes the `Serialization` property publicly but its `ISerializationConventions` type is **not
public** (measured), so the method is unreachable. What remains is one `MakeGenericMethod` cached in
`ReflectionCache` per entity type, mirroring `DatabaseAccess.LoadEntityAsync` (`:347-365`) — the same
call with the single-id overload. Note the rule itself is already invoked through
`Delegate.DynamicInvoke` (`RowSecurity.cs:410`); this path is reflective end to end by construction.

---

## S0 — Spike: reproduce the throw (done, no commit)

**Result: reproduced on `master`, frame-for-frame with the production trace.**

The naive route does not work, and finding that out was the point of the spike (PRD F1): every
existing fixture writes its documents with `session.StoreAsync` in the same process, so
`@Raven-Clr-Type` always resolves and `LoadAsync<object>` returns a properly typed entity.
`RowFilterPushdownTests.A_projection_falls_back_to_the_batched_reload_with_the_compiled_filter`
already drives the exact failing call and passes.

What reproduces it is a base document whose CLR-type metadata does not resolve:

```csharp
await session.StoreAsync(alice);
session.Advanced.GetMetadataFor(alice)[Constants.Documents.Metadata.RavenClrType]
    = "Ghost.Note, Ghost";
await session.SaveChangesAsync();
```

then the ordinary projecting `FilterAsync` call. Observed:

```
System.ArgumentException : Object of type 'Newtonsoft.Json.Linq.JObject' cannot be converted to
type 'MintPlayer.Spark.Tests.Services.RowFilterProjectionReloadTests+Note'.
   at System.Delegate.DynamicInvokeImpl(Object[] args)
   at ...RowSecurity...<ResolveEffectiveRuleAsync>b__0  (RowSecurity.cs:410)
   at ...RowSecurity.FilterAsync                        (RowSecurity.cs:214)
```

The control test in the same file — identical but with normal metadata — passes, isolating the
trigger to metadata resolution rather than to the feature combination.

## S1 — Spike: identity-map interaction — **answered, landed as tests**

Two questions the fix depends on, neither measured in this repo before now (PRD F3):

1. **Is a server-side projection tracked in `DocumentsById` under the document id?** If it were, the
   later typed load would get the projection back and fail the cast. **Answer: no.** Pinned by
   `A_projection_query_in_the_same_session_does_not_poison_the_typed_reload`, which runs a real
   projecting query over the same ids first and then filters successfully.
2. **Does an earlier `LoadAsync<object>` poison a later typed load of the same id?** **Answer: yes** —
   the map returns the tracked instance regardless of `T`. That is *why* both call sites must land
   together, and it is also why the originally-planned test
   `An_untyped_load_earlier_in_the_session_does_not_defeat_the_typed_reload` **was not written as
   specified**: it asserts something the fix cannot deliver and should not claim. An untyped load that
   runs first genuinely does win.

   What is true, useful, and now pinned is the *ordering* — `FilterAsync` runs before `RedactAsync`
   and before `BreadcrumbResolver` on every path, so the typed reload gets there first and primes the
   map for them. That is
   `The_typed_reload_primes_the_session_so_a_later_untyped_load_yields_the_entity_type`: after
   `FilterAsync`, a subsequent `LoadAsync<object>` of the same id returns the entity, served from the
   map without a second request.

   **Consequence worth carrying forward:** the fix's benefit to the downstream untyped loads depends
   on call order that nothing enforces. Anything that moves `RedactAsync` or breadcrumb resolution
   ahead of `FilterAsync` silently un-does it. This is part of why #283 exists.

## M1 — Failing tests (red) — PRD R1, R2, R4, R5; AC 1–6

**File (new):** `tests/MintPlayer.Spark.Tests/Services/RowFilterProjectionReloadTests.cs`
— already scaffolded in S0 with the two `FilterAsync` cases. Extend to:

| Test | Pins |
|---|---|
| `A_projection_is_judged_as_the_entity_type_even_when_the_stored_clr_type_does_not_resolve` | R1, AC 1 — **red on `master`** |
| `A_projection_is_judged_as_the_entity_type_when_the_stored_clr_type_resolves` | R1, R3, AC 6 — green control |
| `A_projection_whose_base_document_was_deleted_is_dropped` | R4, AC 4 |
| `Redaction_over_a_projection_reads_the_entity_type_when_the_stored_clr_type_does_not_resolve` | R2, AC 3 — **red on `master`** |
| `A_projection_query_in_the_same_session_does_not_poison_the_typed_reload` | S1.1 / AC 5 |
| `The_typed_reload_primes_the_session_so_a_later_untyped_load_yields_the_entity_type` | S1.2 / F3 — replaces the planned `An_untyped_load_earlier_…`, see S1 |

Fixture shape follows the house convention (`RowFilterPushdownTests`): nested `Ledger`/`VLedger`, one
`DefaultPersistentObjectActions<Ledger>` subclass overriding **both** row hooks (so the same fixture
covers `FilterAsync` and `RedactAsync`), `RowSecurity` built from NSubstitute
(`new RowSecurity(actionsResolver)` — the `[Inject]` ctor defaults `logger`/`httpContextAccessor` to
`null`, so `IsSystemContext` is `false` and rules apply). Use **its own** static principal field, not
a shared one: `xunit.runner.json` sets `maxParallelThreads: 0.5x` and there are no `[Collection]`
attributes, so fixture classes run concurrently.

⚠️ Do **not** name the entity `Note`-with-a-`NoteActions` in a DI-hosted fixture: the real
`ActionsResolver` scans every loaded assembly for `{EntityName}Actions` and caches process-wide
(`ActionsResolver.cs:70-95`), so it would bind `RowLevelQueryAuthorizationTests.NoteActions`. The
NSubstitute route this file uses sidesteps that entirely — and the fixture is named `Ledger` rather
than `Note` so the hazard cannot be reintroduced by a later move to a DI-hosted shape.

**File (extend):** `tests/MintPlayer.Spark.Tests/Services/QueryDeclaredIndexBindingTests.cs`
— the only fixture with a real DI host, a deployed index, a registered projection and an
`EntityTypeFile` carrying `QueryType`/`IndexName`. Add a `CommitActions :
DefaultPersistentObjectActions<Commit>` overriding `GetRowFilterAsync` off its own static, seed one
commit with unresolvable metadata, and assert the end-to-end `ExecuteQueryAsync` returns only the
caller's rows (AC 1 through the real `QueryExecutor`). `Commit.Author` already discriminates, so the
entity needs no change; models are inline `EntityTypeFile`s, so **no model JSON, no
`--spark-synchronize-model`, no `SparkContext` change**.

Keep `CurrentAuthor = null` ("no restriction") by default so the three existing tests in that file
stay green once `HasRowRule(Commit)` flips to `true`.

## M2 — The fix — PRD R1–R6

**File:** `libs/spark/MintPlayer.Spark/Services/RowSecurity.cs`

One private helper, called from both `FilterAsync` (`:190-192`) and `RedactAsync` (`:312-314`):

```csharp
private static async Task<Dictionary<string, object>> LoadBaseDocumentsAsync(
    IAsyncDocumentSession session, Type entityType, IReadOnlyCollection<string> ids)
```

closing `IAsyncDocumentSession.LoadAsync<T>(IEnumerable<string>, CancellationToken)` over
`entityType` via `ReflectionCache.GetOrAdd` + `MakeGenericMethod`, awaiting the returned `Task` and
unwrapping with the existing `GetCompletedTaskResult()`. Mirrors `DatabaseAccess.LoadEntityAsync`
(`:347-365`), which is the same call with the single-id overload.

Details that are decisions, not incidentals:

- **Overload selection.** Neither parameter mentions `T`, so plain
  `GetMethod(nameof(LoadAsync), [typeof(IEnumerable<string>), typeof(CancellationToken)])`
  disambiguates cleanly from the two `Action<IIncludeBuilder<T>>` siblings — no `GetMethods()` scan.
- **`CancellationToken.None` passed explicitly.** `MethodInfo.Invoke` does not apply C# default
  parameter values.
- **Result conversion.** The reflected result is `Dictionary<string, TEntity>`; copy it into the
  `Dictionary<string, object>` the locals already declare by enumerating the non-generic `IDictionary`
  (`foreach (DictionaryEntry e in …)`). One O(page) copy of references, no extra reflection, and both
  `TryGetValue` loops stay as they are.
- **Comparer.** Keep `StringComparer.OrdinalIgnoreCase` (R5) — it matches what RavenDB itself builds.
- **The `ids.Count == 0` guard moves into the helper**, deleting both call-site ternaries.

The interface comment carries the *why*: the type argument is load-bearing because the rule and the
redaction hook are declared over the entity and invoked reflectively, so a value RavenDB materialized
as something else fails the argument check before any row is judged — and loading as the declared
type also primes the identity map for every later load of the same ids.

Untouched: the no-`Id` fail-closed branch (`:165-176`), both missing-document branches
(`:206-209`, `:326-333`), `ComposeRowFilterAsync`'s projection no-op.

## M3 — Verify in the test app — AC 1, 2, 7

- Unit sweep: `RowFilterProjectionReloadTests`, `RowFilterPushdownTests`,
  `RowLevelQueryAuthorizationTests`, `RowLevelWithCheckTests`, `RowLevelRedactionTests`,
  `QueryDeclaredIndexBindingTests`, `SearchPushdownTests`, `SortCompanionRedirectTests`,
  `BreadcrumbCompanionRuntimeTests`, `ComplexFieldIndexingTests` — then the full
  `MintPlayer.Spark.Tests` suite in one batch.
- E2E: `RowLevelAuthzTests` against the real Fleet host (`Fleet`/`Car` already carries rule +
  projection — PRD F1.2). Green before *and* after; it is the no-regression guard, not the repro.
### Demo-app check — **done, in a browser, before and after**

Driven through the real Fleet SPA with Playwright. Fleet's `Car` needed no changes: `CarActions`
already declares the row filter and `[GenerateIndex]` already binds the generic query to
`Cars_Overview`/`VCar`.

Recipe that actually works (the "strip via Raven Studio" line this plan originally carried was
replaced — see the trap below):

1. Point Fleet at a clean database so paging is deterministic:
   `Spark__RavenDb__Database=SparkFleetDemo281`, `ASPNETCORE_ENVIRONMENT=Development` (auto-creates),
   `dotnet run --project Demo/Fleet/Fleet --no-launch-profile`. Do **not** run `ng serve`/`npm start` —
   the host spawns the dev server via `UseSpaImproved` + `UseAngularCliServer`.
2. Register a user via `POST /spark/auth/register`, then patch the `SparkUsers` document.
   ⚠️ **Two separate lists:** `CarActions` calls `IsInRole("Administrators")`, which reads Identity
   **`Roles`**; security.json authorization reads the **`group` claim** (`ClaimsGroupMembershipProvider`).
   A non-admin needs `Claims: [{group: "Fleet managers"}]`; an admin needs **both**
   `Roles: ["Administrators"]` and `Claims: [{group: "Administrators"}]`.
3. Seed cars by **raw `PUT /databases/{db}/docs?id=…` carrying only `@collection: Cars`** and no
   `Raven-Clr-Type`, with `CreatedBy` set to the user's document id. This is the faithful shape — a
   document written by an import/ETL — and it needs no UI interaction.
4. Browse `https://localhost:5003/query/cars`.

Server versions, for reproducibility: the unit and E2E measurements ran against the **embedded 7.2.5**
test-driver server (matching the pinned `RavenDB.Client`), the browser run against a **local 7.1.1**
server. The defect and the fix behave identically on both — consistent with the cause being
client-side deserialization rather than anything server-version-specific.

Observed, same database and data on both sides:

| | Unfixed | Fixed |
|---|---|---|
| `GET /spark/queries/a20e8400-…/execute` | **500**, red banner, empty grid | **200**, rows render |
| Fleet log | `ArgumentException: … 'JObject' cannot be converted to type 'Fleet.Entities.Car'` at `RowSecurity.cs:410` → `:214` → `QueryExecutor.cs:207` | no `ArgumentException` at all |

**The gate is still a gate** (the check that matters — "stopped throwing" would also be satisfied by
disabling it). With a fourth metadata-less car owned by another user, and `Cars/Overview` returning
all four with that one sorting *first*:

| Caller | Filter branch | Result |
|---|---|---|
| anonymous | `car => false` | **401** — type-level authz denies before the row filter is reached |
| `Fleet managers`, non-admin | `car => car.CreatedBy == userId` | **200**, `TotalRecords: 3` — own cars only |
| `Administrators` | `null` | **200**, `TotalRecords: 4` — the foreign car included |

The admin branch matters as a *separate* path: `filter == null` makes `FilterAsync` return at
`RowSecurity.cs:157`, before the reload — so admins never hit the bug, and the fix must not disturb
them. It doesn't. The service/machine branch (authenticated, no `NameIdentifier` → also `null`) is the
same early return; its credential plumbing is covered by `ModuleCertificateCredentialTests` /
`JwtBearerCredentialTests` rather than by hand.

Known flake: the full suite is load-sensitive and the E2E host hangs *after* tests pass (post-#277).
Re-run named tests in isolation before calling anything a regression.

## M4 — Release — done

- ✅ Lockstep bump to `10.0.0-preview.57` across all 21 packages (CI auto-publishes on push to
  `master`; never `dotnet nuget push` by hand). `@mintplayer/ng-spark` unchanged at `22.1.0` — no
  client change.
- ✅ `docs/release-notes-preview-57.md`: fail-closed 500, not a disclosure; no consumer action beyond
  upgrading.
- ✅ Follow-up filed as [#283](https://github.com/MintPlayer/MintPlayer.Spark/issues/283).

  **It is worse than this plan predicted, and the wording here is corrected rather than left
  standing.** The expectation was "a `JObject` silently renders no breadcrumb instead of throwing".
  What the follow-up investigation actually found is a **fail-open**: `BreadcrumbResolver`'s BFS level
  gates each referenced document on `IsAllowedAsync(doc.GetType(), "Read", doc)`, and a `JObject`
  runtime type resolves to `DefaultPersistentObjectActions<JObject>` whose hooks are the permissive
  defaults — **measured returning `true`**, so the referenced entity's own row rule is never consulted
  and the redacted-placeholder branch is unreachable. The blank breadcrumb is a *consequence*, not the
  whole story, and it is probably the only reason this is not a disclosure today. #283 opens with a
  spike to confirm or rule that out.
