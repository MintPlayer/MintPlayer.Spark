# Plan — Issue #281: load base documents as the entity type in `RowSecurity`

**PRD:** [issue_281_PRD.md](issue_281_PRD.md) ·
**Branch:** `fix/issue-281-rowsecurity-typed-base-load`

Test-driven by request: S0 reproduces, M1 lands the failing tests (red), M2 makes them pass, M3
re-verifies in the demo app. The PR is squashed, so the intermediate red commit never reaches
`master`.

| | Milestone | State |
|---|---|---|
| S0 | Spike: reproduce the throw in the test app | ✅ done — see below |
| S1 | Spike: does a projection query poison the identity map? | pending |
| M1 | Failing tests pinning the contract (red) | pending |
| M2 | The fix: typed batched reload, both call sites | pending |
| M3 | Verify in the demo app + full suite sweep | pending |
| M4 | Version bump, docs, follow-up issue | pending |

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

## S1 — Spike: identity-map interaction (no commit)

Two questions the fix depends on, neither measured in this repo (PRD F3):

1. Is a server-side `ProjectInto<VNote>` result tracked in `DocumentsById` under the document id? If
   it were, the later typed `LoadAsync<Note>` would get the projection back and fail the cast.
2. Does an earlier `LoadAsync<object>` in the same session poison a later `LoadAsync<Note>`? (IL says
   yes — the tracked instance is returned regardless of `T`.) This is why the fix must land at
   **both** call sites at once, and it is worth an assertion rather than a comment.

Write both as tests in M1 (AC 5) rather than as a throwaway probe — they are exactly the regressions
that would bite a future change to load ordering.

## M1 — Failing tests (red) — PRD R1, R2, R4, R5; AC 1–6

**File (new):** `tests/MintPlayer.Spark.Tests/Services/RowFilterProjectionReloadTests.cs`
— already scaffolded in S0 with the two `FilterAsync` cases. Extend to:

| Test | Pins |
|---|---|
| `A_projection_is_judged_as_the_entity_type_even_when_the_stored_clr_type_does_not_resolve` | R1, AC 1 — **red on `master`** |
| `A_projection_is_judged_as_the_entity_type_when_the_stored_clr_type_resolves` | R1, R3, AC 6 — green control |
| `A_projection_whose_base_document_was_deleted_is_dropped` | R4, AC 4 |
| `Redaction_over_a_projection_reads_the_entity_type` | R2, AC 3 — **red on `master`** |
| `A_projection_query_in_the_same_session_does_not_poison_the_typed_reload` | S1/AC 5 |
| `An_untyped_load_earlier_in_the_session_does_not_defeat_the_typed_reload` | S1/F3 |

Fixture shape follows the house convention (`RowFilterPushdownTests`): nested `Note`/`VNote`, a
`DefaultPersistentObjectActions<Note>` subclass, `RowSecurity` built from NSubstitute
(`new RowSecurity(actionsResolver)` — the `[Inject]` ctor defaults `logger`/`httpContextAccessor` to
`null`, so `IsSystemContext` is `false` and rules apply). Use **its own** static principal field, not
a shared one: `xunit.runner.json` sets `maxParallelThreads: 0.5x` and there are no `[Collection]`
attributes, so fixture classes run concurrently.

⚠️ Do **not** name the entity `Note`-with-a-`NoteActions` in a DI-hosted fixture: the real
`ActionsResolver` scans every loaded assembly for `{EntityName}Actions` and caches process-wide
(`ActionsResolver.cs:70-95`), so it would bind `RowLevelQueryAuthorizationTests.NoteActions`. The
NSubstitute route this file uses sidesteps that entirely.

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
- Demo-app check: Fleet on https://localhost:5003, RavenDB `SparkFleet` at `localhost:8080`
  (`PublicServerUrl` must be `localhost`), signed in as a **non-admin** `Fleet managers` user — an
  `Administrators` account returns `null` from `CarActions.GetRowFilterAsync` and short-circuits at
  `RowSecurity.cs:157`. Hit `GET /spark/queries/a20e8400-e29b-41d4-a716-446655440001/execute` and the
  Cars grid. To make Fleet reproduce the pre-fix throw, strip `@Raven-Clr-Type` from one seeded `Car`
  document via Raven Studio first. Do **not** run `ng serve`/`npm start` — the host spawns the dev
  server via `UseSpaImproved` + `UseAngularCliServer`.

Known flake: the full suite is load-sensitive and the E2E host hangs *after* tests pass (post-#277).
Re-run named tests in isolation before calling anything a regression.

## M4 — Release

- Lockstep version bump for the preview release (CI auto-publishes on push to `master`; never
  `dotnet nuget push` by hand).
- Release notes entry: fail-closed 500, not a disclosure; no consumer action beyond upgrading.
- Open a follow-up issue for the two `BreadcrumbResolver` untyped loads (PRD out-of-scope): a `JObject`
  there silently renders no breadcrumb instead of throwing, which is the quiet failure mode this PRD
  argues against everywhere else.
