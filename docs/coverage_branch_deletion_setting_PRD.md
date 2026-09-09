# Making branch deletion reachable — an account-wide default and an editable flag

**Status:** Implemented — see [coverage_branch_deletion_setting_plan.md](coverage_branch_deletion_setting_plan.md)
for what landed, and for the two things deliberately left undone
**Branch:** `feat/branch-deletion-setting` · one pull request
**Follows:** #382 (implemented the deletion), #391 (tested it), #389 (recorded that nobody can switch it on)

## The problem, stated plainly

`DeleteHeadBranchIfEnabled` works and is now covered by nine tests. **It has never run**, because
`Repository.DeleteBranchOnPrClose` has no writer: no `Edit` right, no controller, no client code.
#391 shipped proof of a feature nobody can use.

Two things are wrong, not one:

1. **It is unsettable.** Nothing anywhere assigns the flag.
2. **It is per-repository only.** Even once settable, an owner with 180 repositories would tick 180
   boxes. There is no account-wide default.

## What is being built

| | |
|---|---|
| `Account.DeleteBranchOnPrClose` | `bool` — the GitHub account/organization-wide default |
| `Repository.DeleteBranchOnPrClose` | `EDeleteBranchPolicy` — `Inherit` defers to the account |
| Rights | `Edit` on Account and Repository, no `New`, no `Delete` |
| Row filters | the access control for the new write path — **the part that must not be skipped** |
| Read-only sweep | only `DeleteBranchOnPrClose` is editable; every other attribute is locked |
| ApiToken grid | `Description` first; `AccountGitHubId` off the model; `RepositoryGitHubId` replaced by a repository **list**; `CreatedByUserId` a stamped reference |

## Decisions already taken

### D1 — Keep `QueryRead`, add `Edit`, let the row filter carry the access control

The original ask was "Edit but **not** Query on Account". Investigated, and it is the wrong shape:

- **The harm it aims at is already live and deliberate.** `security.json:21` grants `QueryRead/Account`
  to the **anonymous** role — coverage.mintplayer.com is a public dashboard, and `AccountActions`
  documents the choice: *"an Account is a GitHub login and avatar — data GitHub already publishes"*.
  Removing `Query` for signed-in users while leaving it for the public internet achieves nothing.
- **Removing it breaks the page.** `Read ⇒ Query` (`SparkRightImplications.cs:35-38`) can be undone
  by an explicit denial — denials beat allows — but both `GET /spark/types` and `/spark/types/{id}`
  gate on `Query`, and **the client never calls the single-type endpoint** (`spark.service.ts:23-25`
  is dead code). The detail page would render **blank**, the edit page spin forever, and the menu
  would still show the entry, because program units gate on `Read`.
- **An actions class cannot express it.** Both `IsAllowedAsync` and `GetRowFilterAsync` take an
  *entity*; they answer "may this caller touch this row". The type-level check has already passed
  before either runs (`RowSecurity.cs:14-16`). "The `Query` verb must not exist" is not a question
  the actions surface can be asked.

So: **`QueryRead` + `Edit`, no `New`/`Delete`, and the row filter does the work.** "No Create" is
satisfied exactly by not granting it. The framework is left alone, as intended.

### D2 — ApiToken needs no change for "read own, not query all"

Already true. `ApiTokenActions.GetRowFilterAsync` returns `token => token.AccountLogin.In(owners)`
for **every** action — no per-action branch — and `QueryExecutor` pushes it into the RQL, with
`FilterAsync` still gating when pushdown falls back. "Query all tokens" is unreachable for every
principal. Removing `Query/ApiToken` would add no security and would break the
`account-upload-tokens` sub-query.

The distinction worth keeping: **row questions belong to the row filter; verb questions belong to
`security.json`.** ApiToken's is a row question and is answered.

## The blocker the request did not anticipate

`Repository.DeleteBranchOnPrClose` as `bool?` **cannot be edited through the generic PO form
today**, and the failure is silent.

`po-edit/src/spark-po-edit.component.ts:98-99`:

```ts
} else if (attr.dataType === 'boolean') { data[attr.name] = itemAttr?.value ?? false; }
```

**Opening the edit form on an inheriting repository and pressing Save pins it to explicit `false`,
permanently and invisibly** — the account default then never applies to it again. Three further
blockers sit behind that one:

- `bs-checkbox`'s value accessor is boolean-only (`checkbox-value-accessor.ts:31,41,52`), so
  `ngModel` can never produce `null`.
- Indeterminate is display-only: a user click always clears it, per the HTML spec, and `mp-checkbox`
  says so in its own docs.
- A custom `editComponent` cannot rescue it: in `spark-po-form.component.html` the
  `dataType === 'boolean'` branch (line 53) **pre-empts** the renderer escape hatch (line 339), so
  the registration would be dead code. Grid and detail check the renderer first; the form does not.

Display is fine — the grid and detail page already render a null boolean as indeterminate, with a
comment explaining why. It is only the **edit** path that is two-state.

### The options

**(A) Fix the framework, then add a renderer — RECOMMENDED.**
Move the boolean branch after the renderer check; stop `po-edit`/`po-create` coercing `null → false`
for non-required attributes; add a small `inherited-boolean` edit renderer (Inherit / Yes / No)
writing `null | true | false`. **Two of those three are latent library bugs regardless of this
feature** — a renderer registration that silently does nothing, and a coercion that destroys a
nullable value. Cost: a `libs/spark` + `ng-spark` change, so a version bump and a Spark release.

**(B) Model it as a string enum instead — the no-framework-change option.**
`Inherit` / `Enabled` / `Disabled` as an enum rather than `bool?`. Spark maps it to `"string"`, the
form already renders a select, and every blocker above disappears. Loses the tri-state checkbox, and
makes "inherit" a stored named value rather than an absence. Deviates from the stated `bool?` spec.

**(C) Keep it off the generic form.** `RepoSettingsController` already owns per-repository settings
(`GetGate`/`PutGate` under `Manage/RepoSettings`). A three-way control there costs no framework
change and no new Spark rights — but it is a second settings surface, and it does not give the
Account PO its checkbox.

### Resolved — a lookup-driven select over an ENUM, plus the framework fix

The owner chose a `<bs-select>` driven by a `LookupReference`, which is the right mechanism. The
property could not be `bool?`, for a fourth reason none of the options above had found: **lookup keys
are strings on the wire** (`LookupReferenceService.cs:282` stringifies the key), a boolean form value
is a real JS boolean, and there is no `compareWith` — so a persisted `true` would render as the
placeholder even with the other three fixed.

So the shape is **(A) and (B) together**: the two library bugs are fixed because they are bugs — a
declared `LookupReference` on a boolean silently did nothing, in four places — and the property is an
enum because that is the honest model of three states. `Repository.DeleteBranchOnPrClose` is
`EDeleteBranchPolicy` (`Inherit` / `Enabled` / `Disabled`); `Account.DeleteBranchOnPrClose` stays a
plain `bool`, since the account is the bottom of the chain and has nothing to defer to.

What the enum buys beyond dodging the blockers: each state carries a **name and a translated label a
user reads**, and a migration can name `Inherit` where it could not name an absence. `Car.Status`
(`ECarStatus?` + `CarStatus`) is the same shape already working in production.

## Requirements

- **FR1** — an owner can set the flag on an Account, and on a Repository, from the app.
- **FR2** — a Repository with no explicit value inherits its Account's.
- **FR3** — **only** `DeleteBranchOnPrClose` is editable on either type; every other attribute is
  refused server-side, not merely hidden.
- **FR4** — a caller can only edit accounts and repositories they manage. No signed-in user may edit
  a public repository they do not own.
- **FR5** — existing repositories must **inherit**, not be pinned to the `false` they carry today.
- **FR6** — opening and saving the edit form must not silently change an inheriting repository.
- **FR7** — ApiToken grid: the two GitHub ids leave the model but **stay in the documents**;
  `Description` renders first; `CreatedByUserId` shows a username.

## Design

### Rights and row filters

`security.json`, authenticated role only: `+ Edit/Repository`, `+ Edit/Account`. Nothing else.

⚠️ **Granting `Edit` without fixing the filters is a security regression**, and both types are wrong
in different ways:

- `RepositoryActions.GetRowFilterAsync` returns `!IsPrivate || OwnerLogin.In(owners)` for every
  non-`Query` action — the **anonymous** tier. Grant `Edit` and any signed-in user can edit **every
  public repository**. Its comment ("writes are denied at the type level … the WITH CHECK path is
  unreachable") becomes false at that moment and must go.
- `AccountActions` has **no** `GetRowFilterAsync` at all, and `RowSecurity.IsAllowedAsync` treats a
  missing rule as *allowed*. Grant `Edit` and any signed-in user can edit **any account**.

Both gain a write branch, following `GitHubProjectActions`' precedent:

```
Repository:  "Query" → ListingFilter(owners) · "Read" → Filter(owners) · else → r => r.OwnerLogin.In(owners)
Account:     "Query"/"Read" → null (public, unchanged) · else → a => a.Login.In(owners)
```

`.In()`, never `Contains` — a pushed-down `Contains` is what took both ApiToken query surfaces down
this morning.

### Read-only sweep

`isReadOnly` is enforced server-side by `EntityMapper.IsWritableBySchema:573-583`, **per attribute**,
before the property lookup — a client posting a changed `Name` alongside the checkbox gets a 200
with only the checkbox applied. And `ModelSynchronizer` assigns `IsReadOnly` **only when creating**
an attribute (`:841`), so a hand-set value survives re-synchronize.

`Account.json`'s five attributes are **already all read-only**. `Repository.json` needs **17 of 18**
flipped — everything except `DeleteBranchOnPrClose`.

### Migration — the sharp edge

Repository documents are stored whole, so the non-nullable `bool` serializes as
`"DeleteBranchOnPrClose": false` on **every repository written since #382**. Change the type to
`bool?` with no migration and 100% of production is pinned to "explicitly off", and the account
default is dead on arrival (**FR5**).

`from Repositories update { delete this.DeleteBranchOnPrClose; }`, following
`M_202608180900_DropParentSha` verbatim, including `WaitForCompletionAsync` so a throw aborts startup
rather than marking the migration half-applied.

⚠️ **Measure first.** Run `from Repositories where DeleteBranchOnPrClose = true` against production
before writing it. If any row holds `true`, a bare delete silently **disables a working feature**,
and the migration must preserve those instead. The flag has only been on `Repository` for about a
day, so `true` is unlikely — but unlikely is not measured.

### The consumer

One conditional point load, no query, no `GetOrCreateAccount` (which stores on miss — a read path
must not mint documents):

```csharp
var account = repository.DeleteBranchOnPrClose is null
    ? await session.LoadAsync<Account>(repository.Account ?? Account.DocumentId(evt.Repository.Owner.Id), ct)
    : null;
if (!Repository.ResolveDeleteBranchOnPrClose(repository, account)) return;
```

The `??` chain lives in one static helper, because the settings UI will want the effective value too.

### ApiToken grid (FR7)

**(a) `[IgnoreProperty]` on `AccountGitHubId` and `RepositoryGitHubId`.** Spark's attribute
(`libs/attributes/…`), **not** RavenDB's — it removes the property from the model and leaves it in
the document, proven by `ApiToken.Hash` doing exactly this today. Hand-deleting the JSON does **not**
stick (synchronize regenerates it with a new id); ignoring the property is the one case where
synchronize deletes.

**Resolved, and larger than the question.** The owner's answer was that a person should never type
an id at all, and that a token should serve several repositories. So `RepositoryGitHubId` does not
become hidden — it is **replaced** by `GithubRepositories`, a `List<string>` of repository **document
ids** with a picker, and `Scope` is derived from whether that list is empty.

Three corrections to the request, each verified rather than assumed:

- **Not `List<long>`.** Reference elements are document ids as strings at every layer — the
  synchronizer, `EntityMapper`, `BreadcrumbResolver`, `ReferenceResolver`'s `.Include()`, and the
  Angular picker, which can only emit `po.id`.
- **Neither named query works.** `Account_UploadTokens` returns *tokens*; `Account_Repositories`
  calls `EnsureParent("Account")` while the picker sends the *form's own* parent — `ApiToken`, or
  nothing on New — which throws and surfaces as a **500 from the picker**. A new parent-free
  `ApiToken_SelectableRepositories` was required.
- **The prior art is `HR.Person.Professions`**, the only array reference in the workspace.

⚠️ **And it opened a hole that had to close in the same change.** A reference *array* is written
straight through — `EntityMapper` bypasses the collection guard that binds a scalar reference's id to
its type — and `EnsureRowSaveAllowedAsync` re-applies only the `AccountLogin` filter, which says
nothing about repository ownership. `ValidateRepositoryScopeAsync` is the entire defence, and it runs
on **edit as well as create**: the original hook returned early on an edit, which would have left an
existing token's scope unguarded — the easier attack, since the token already exists.

**(b) `Description` first.** `order` is presentational — not in the structural hash — and
synchronize preserves any value `> 0` (`:768`). ⚠️ Never use `0`; it reads as unset and gets
renumbered.

**(c) `CreatedByUserId` → `[Reference(typeof(SparkUser))]`.**

⚠️ **Do not register `SparkUser` on `CoverageSparkContext`.** Synchronize discovers by reflection and
would generate a model containing `PasswordHash`, `SecurityStamp`, `AuthenticatorKey`,
`TwoFactorRecoveryCodes` and `Claims` as writable, projectable attributes — in production. And
`[IgnoreProperty]` cannot save it, because `SparkUser` is a library type.

Instead, **hand-author** `App_Data/Model/SparkUser.json` declaring only `Id` and `UserName`, with the
house "HAND-AUTHORED" comment that `MyAccountRow.json` and `Home.json` already carry. It is
`UserName` — capital N; a wrong placeholder renders blank rather than erroring. Do not put `Email`
or `PhoneNumber` in the breadcrumb: `SparkUser` has no row rule, so nothing would withhold them.

⚠️ **Nothing stamps `CreatedByUserId` today** — it defaults to empty, is writable, and tokens made
through the current UI have it blank. As a reference it would be empty for new rows and forgeable.
It needs a server-side stamp in `OnBeforeSaveAsync` plus `isReadOnly: true`.

## Out of scope

- **Changing `SparkRightImplications`.** D1.
- **A per-caller read-only mechanism.** `isReadOnly` is static and global. The dynamic lever
  (`GetProtectedAttributesAsync`) shields reads *and* writes together, so it cannot express "visible
  but not editable for this caller" — a genuine framework gap, not needed here.
- **`GET /spark/types/{id}` as a client fallback.** Worth doing (the server comment promises a
  behaviour the client does not implement, and a catalogue miss is a blank page with no error), but
  it belongs to the Query-less-type problem this PRD declines to solve.

## Risks

- **R1 — the write path is a new authorization surface** on the one operation this app performs
  destructively against someone else's repository. FR4's row filters are the whole mitigation.
- **R2 — a `bool?` persistent-object property is unprecedented here.** No entity in the workspace has
  one, so synchronize → wire → mapper → client has never been exercised for it. Needs a round-trip
  test, not a reading of the mapping.
- **R3 — the edit form destroys `null`** (FR6). Mitigated only by option (A) or (B); under (C) the
  form is never used for it.
- **R4 — this reverses plan decision D1 of the project-automation work**, which chose `Repository`
  and explicitly rejected `Account` as "speculative". This is that "later", so it is anticipated
  rather than re-litigated — but `Repository.cs:62`'s remark that the flag "cannot be a global
  default" on a multi-tenant server must be rewritten, since an account-scoped default is precisely
  what it currently forbids.
