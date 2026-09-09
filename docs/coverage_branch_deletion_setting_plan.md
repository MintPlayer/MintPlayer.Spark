# Branch-deletion setting — implementation plan

**PRD:** [coverage_branch_deletion_setting_PRD.md](coverage_branch_deletion_setting_PRD.md)
**Status:** in progress — all decisions taken
**Branch:** `feat/branch-deletion-setting` · one pull request
**Breaking changes are allowed.** The owner has confirmed no backward compatibility is required and
that production data may be modified directly. That removes the transitional dual-read the token
work would otherwise need — it does **not** remove the migration (the data still has to move) or the
validation gate (that is a security hole, not a compatibility concern).

Red/green where there is behaviour to pin. The habit that has paid all day: **check that a RED fails
for the right reason**, and treat "no test would go red if this did nothing" as unfinished.

## Decisions taken

**D-A — a `<bs-select>` driven by a LookupReference, over an ENUM.** The owner chose the select; the
investigation showed the property cannot be `bool?`, for four independent reasons:

1. `spark-po-form.component.html:53` tested `dataType === 'boolean'` **before** `:58` tested
   `lookupReferenceType`, and a lookup does not change `dataType` — so a lookup-backed bool was
   swallowed by the checkbox and the select was unreachable. **Fixed in this PR** (see M0).
2. `spark-po-edit.component.ts:98` coerced `?? false` keyed on `dataType` alone, destroying `null`
   before render. **Also fixed** — but it only matters for a lookup-backed *boolean*.
3. Lookup keys are strings on the wire (`LookupReferenceService.cs:282`), a boolean form value is a
   real JS boolean, and there is no `compareWith` — so `true !== "True"` and a persisted value would
   render as the placeholder. **Not fixed; an enum sidesteps it.**
4. A null `bool?` lookup key collapses to `""`, and `""` into a `bool?` throws inside
   `Convert.ChangeType` and is **swallowed by a bare `catch`** (`EntityMapper.cs:1139`) — a silently
   dropped write. The natural way to author "Inherit" is the one that discards it.

So: `Repository.DeleteBranchOnPrClose` is `EDeleteBranchPolicy` (`Inherit` / `Enabled` / `Disabled`)
with a `TransientLookupReference`, following `Car.Status` (`ECarStatus?` + `CarStatus`), which is that
exact shape working in production today. `Account.DeleteBranchOnPrClose` stays a plain `bool` — two
states, a checkbox is honest.

Each of the three states now has a **name and a translated label the user reads**, and the migration
becomes expressible: `Inherit` is a value a patch script can name; `null` is an absence.

**D-B — `RepositoryGitHubId` becomes a multi-repository reference.** The owner: *"the user will never
enter id-numbers… a token should be allowed to work over several repositories."*

```csharp
[Reference(typeof(Repository), "ApiToken_SelectableRepositories")]
public List<string> GithubRepositories { get; set; } = [];
```

Three corrections to the request, all verified:

- **Not `List<long>`.** Reference elements are **document ids as strings** at every layer — the
  synchronizer, `EntityMapper`, `BreadcrumbResolver`, `ReferenceResolver`'s `.Include()`, and the
  Angular picker, which can only emit `po.id`. So `"Repositories/{gitHubId}"`.
- **Not `Account_UploadTokens`** (that returns tokens) **and not `Account_Repositories`** either: it
  calls `EnsureParent("Account")`, and the picker sends the *form's own* parent — `ApiToken`, or
  nothing on New — which throws and surfaces as a **500 from the picker**. `ProjectColumnActions`
  documents being bitten by exactly this. A new parent-free query is required.
- **The prior art is `HR.Person.Professions`** — `[Reference(typeof(Profession))] List<string>`,
  rendering as `bs-tree-select` + chips. Array + query is supported end to end; HR simply lets the
  query be derived.

## Status

| | |
|---|---|
| **M0** ng-spark: a lookup must win over the raw data type | **Done** — 4 sites |
| **S1** does any production Repository hold `DeleteBranchOnPrClose: true`? | not started |
| ~~**S2** does a `bool?` survive the round trip?~~ | **dropped** — D-A chose an enum, whose path (`Car.Status`) is already exercised in production |
| **M1** row filters — the write path's access control | not started |
| **M2** rights + the read-only sweep | not started |
| **M3** the entities, the resolver, and the migration | not started |
| **M4** the recipient reads the effective value | not started |
| **M5** the edit surface (shape decided by **D-A**) | not started |
| **M6** end-to-end: tick the box, merge a PR, branch disappears | not started |
| **M7** ApiToken grid — `Description` first | not started |
| **M8** ApiToken grid — hide the two ids (**D-B**), reference the user | not started |
| **M9** versions, docs, decision register | not started |

---

## S1 — measure before deleting

**Question.** The migration in M3 deletes `DeleteBranchOnPrClose` from every Repository so it becomes
`null`/inherit. If any row holds **`true`**, a bare delete silently **disables a working feature**.

**Method.** Against production RavenDB, read-only:
`from Repositories where DeleteBranchOnPrClose = true` — and the same for the whole collection, to
know the denominator.

**Why it is a spike.** It decides whether the migration is `delete this.X` or
`if (this.X !== true) { delete this.X }`. The flag has only been on `Repository` for about a day, so
`true` is unlikely — but #382 shipped the deletion path and #391 proved it works, and "unlikely" is
not "measured". Record the counts here and in the PR.

## S2 — prove a nullable boolean survives the round trip

**Question.** No entity in this workspace has a `bool?`. Synchronize → JSON → wire → `EntityMapper`
→ client has never been exercised for one.

**Method.** A `SparkTestDriver` test: an entity with a `bool?`, saved through the real PO path with
the value **null**, reloaded, and asserted still null. Then the same for `true` and `false`.

**Why it is a spike, not just M3's test.** If null does not survive, option (A) is dead on arrival
and D-A must be re-decided before any of M3–M6 is written. ⚠️ Known hazard to include: an empty
string on a `bool?` throws inside `Convert.ChangeType` and is **swallowed by a bare `catch`**
(`EntityMapper.cs:1133-1142`) — a silently dropped write. Assert that a null round-trips as null and
does not become false.

---

## M1 — row filters first, before any right is granted

Deliberately first: granting `Edit` before this is a live hole.

- `RepositoryActions.GetRowFilterAsync` — `Query` → `ListingFilter(owners)`, `Read` →
  `Filter(owners)`, **everything else → `r => r.OwnerLogin.In(owners)`**. Delete the class doc at
  `:14-20` and the inline comment at `:58-61`; both assert writes are impossible, which M2 makes
  false.
- `AccountActions` — add `GetRowFilterAsync`: `null` for `Query`/`Read` (accounts stay public — that
  rationale is still right), `a => a.Login.In(owners)` for everything else. Rewrite
  `RowSecurityRationale`, which says "deliberately unfiltered" and would become false for writes.

`.In()`, never `Contains`. A pushed-down `Contains` is what took both ApiToken query surfaces down
this morning, and it fails **only** on the query path — the save path compiles the same expression
in memory, where `Contains` is fine, which is why tests stayed green.

**RED first.** A test per type: a caller who manages nothing must be refused an edit of a **public**
repository, and of an account they do not own. Both must fail before the filter change — if they
pass already, the fixture is not reaching the WITH CHECK path
(`DatabaseAccess.cs:245` / `EnsureRowSaveAllowedAsync`) and proves nothing.

## M2 — rights and the read-only sweep

- `security.json`: `+ Edit/Repository`, `+ Edit/Account`, authenticated role only. No `New`, no
  `Delete`. Re-run `--spark-synchronize-security` and review the `securityPosture.txt` diff.
- `Repository.json`: `"isReadOnly": true` on **17 of 18** attributes — all but
  `DeleteBranchOnPrClose`. `Account.json` needs nothing; its five are already read-only.

Enforced server-side per attribute by `EntityMapper.IsWritableBySchema:573-583`, and hand-set values
survive re-synchronize because `ModelSynchronizer` assigns `IsReadOnly` **only on creation** (`:841`).

**Test:** a `PUT` that changes `Name` *and* `DeleteBranchOnPrClose` returns 200 with **only** the
checkbox applied — the read-only field is silently restored, which is the documented posture.

## M3 — entities, resolver, migration

*(shape depends on **D-A**; written here for option (A))*

- `Account.DeleteBranchOnPrClose` — `bool`.
- `Repository.DeleteBranchOnPrClose` — `bool` → `bool?`. Rewrite its remarks: the current text says
  the flag "cannot be a global default" on a multi-tenant server, which an account-scoped default
  directly contradicts.
- `Repository.ResolveDeleteBranchOnPrClose(repo, account)` — one static, so the settings UI and the
  recipient cannot drift.
- `Repository.json`: `"isRequired": true → false`. ⚠️ Synchronize will **not** do this and
  deliberately refuses to clear a hand-set `isRequired` — hand-edit it and re-stamp `modelHashes.json`.
- Migration `M_2026________DeleteBranchFlagBecomesInheritable`, following
  `M_202608180900_DropParentSha` verbatim including `WaitForCompletionAsync`. Shape decided by **S1**.

**Test:** a repository with the member absent inherits; with `true` overrides an account `false`;
with `false` overrides an account `true`. The middle one is the regression S1 protects.

## M4 — the recipient reads the effective value

`GitHubEventsRecipient.DeleteHeadBranchIfEnabled` — conditional point load, no query, and **not**
`GetOrCreateAccount` (it stores on miss; a read path must not mint documents).

Extends the nine facts from #391 rather than replacing them: add *inherits from account*, *repository
overrides account*, and *neither set → no delete*.

## M5 — the edit surface

**Option (A):** move the boolean branch after the renderer check in `spark-po-form.component.html`
(today `dataType === 'boolean'` at line 53 pre-empts the escape hatch at 339, so a registered
`editComponent` is dead code); stop `po-edit`/`po-create` coercing `null → false` for non-required
attributes; add the `inherited-boolean` renderer. **The first two are latent library bugs and get
their own tests regardless of this feature.**

**Option (B):** the enum, its lookup, and no framework change.

**Option (C):** a control on the existing repo-settings page; the PO attribute stays read-only.

## M6 — the end-to-end that actually matters

Everything else is machinery. This is the acceptance test for the whole PR, and the thing #391
failed to deliver:

1. Tick the box on an Account (or a Repository) **in the running app**.
2. Confirm it persisted.
3. Merge a pull request in that repository.
4. **The head branch is gone.**

If this cannot be demonstrated, the feature is not done — regardless of how many unit tests pass.
That is the whole lesson of #389.

## M7 — ApiToken: `Description` first

Hand-edit `order` in `ApiToken.json`. Presentational, not in the structural hash, no synchronize
needed, and synchronize preserves any value `> 0` (`:768`). ⚠️ Never `0` — it reads as unset.

## M8 — ApiToken: hide the ids, reference the user

*(blocked on **D-B** for the second id)*

- `[IgnoreProperty]` — **Spark's**, from `MintPlayer.Spark.Abstractions`, not RavenDB's. Do not add
  `using Raven.Client.Documents;` to that file; if both were in scope it would be CS0104 rather than
  a silent switch to the one that stops persisting.
- Then `--spark-synchronize-model`, which removes the blocks **and** re-stamps `modelHashes.json` in
  the same run. Hand-deleting the JSON does not stick.
- `CreatedByUserId` → `[Reference(typeof(SparkUser))]`, **stamped server-side** in
  `OnBeforeSaveAsync` and marked `isReadOnly: true` — nothing stamps it today, so it is blank for new
  tokens and forgeable.
- Hand-author `App_Data/Model/SparkUser.json` with only `Id` and `UserName`, in the house
  "HAND-AUTHORED" style of `MyAccountRow.json`.
  ⚠️ **Never register `SparkUser` on `CoverageSparkContext`** — synchronize would generate
  `PasswordHash`, `SecurityStamp`, `AuthenticatorKey`, `TwoFactorRecoveryCodes` and `Claims` as
  writable model attributes, in production, and `[IgnoreProperty]` cannot prevent it on a library
  type. `UserName`, capital N. No `Email`/`PhoneNumber` in the breadcrumb — `SparkUser` has no row
  rule, so nothing would withhold them.

## M9 — versions, docs, register

- `libs/**` → `10.0.0-preview.78`; `ng-spark` → `22.16.0` **only if option (A)** touches it. The CI
  guard added in #391 now fails the PR if a `libs/**` change ships without a bump — this is its first
  real exercise.
- `docs/decisions_messaging_and_project_automation.md`: a new C-number recording that the
  account-level default reverses the project-automation plan's D1 ("an Account-level default can be
  added later … adding it now is speculative"). This *is* that later, so it is anticipated rather
  than re-litigated — but it should be written down.
- `docs/code-coverage/product-overview.md`: the setting, where it lives, and how inheritance resolves.

---

## Deliberately not doing

- **Changing `SparkRightImplications`** — PRD D1. `Read ⇒ Query` stays.
- **Denying `Query/Account`.** Expressible, but it blanks the detail page, and the enumeration it
  targets is already granted to the anonymous role. If that enumeration is genuinely unwanted, the
  cheap and correct move is a row filter on `AccountActions` for the `Query` action — **not** a
  rights change. Offer it; do not assume it.
- **A `getEntityType(id)` client fallback.** Worth doing on its own merits — the server comment at
  `List.cs:24-27` promises a behaviour no component implements, and a catalogue miss is a blank page
  with no error — but it belongs to the Query-less-type problem this PR declines to solve.
- **An `IsAllowedAsync` override on `ApiTokenActions`.** The existing row filter already answers
  "read own, never query all", for every action. A second implementation would duplicate it and cost
  more (per-row, not memoized).
