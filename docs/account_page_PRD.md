# PRD + status: account page in the top bar, opt-in email change, ApiToken references its Account

PR: [#466](https://github.com/MintPlayer/MintPlayer.Spark/pull/466) · branch `feat/account-page-in-ng-spark-auth`.
Status as of 2026-10-02: implemented and pushed except `695d682b` (local). CI: Spark/CodeCoverage/client green, one E2E flake (below).

## Scope — what was asked, what was built

| # | Ask | Built | Evidence |
|---|---|---|---|
| 1 | Ship the MyAccount page in ng-spark-auth, links only for features that are on | It already existed (`withAccount()`, `SparkAccountOverviewComponent`). Links are filtered by the mounted route **and** `GET /spark/auth/capabilities` | Browser, CodeCoverage `/account` = Profile, Passkeys, Personal data |
| 2 | No Passkeys button in the top bar | Removed. Passkeys show on the account page only when the server reports `passkeys` and the browser supports WebAuthn | `spark-auth-bar.component.*` |
| 3 | Account as `<a [routerLink] class="btn">` left of Logout in a `<bs-button-group>`, no user name in the bar | Done. The path is read from the router config (`findSparkAuthRoutePaths`), so the bar can sit outside the auth route subtree | Browser |
| 4 | Email change is opt-in | `SparkAuthenticationOptions.EmailChange` (`SparkEmailChange`, default `Disabled`). The request is refused, no mail is sent, an old change link no longer confirms. The profile form is a `<bs-input-group>` shown only when `emailChange` is reported | `AccountFlowTests`, `AuthCapabilitiesTests` |
| 5 | GitHub user identified by the token, not the editable user name | `GitHubAccessService` uses `GET /user`; the backfill matches by numeric id; degraded paths use only a login GitHub returned within the last hour | `GitHubAccessServiceRefreshTests` |
| 6 | ApiToken form: Scope dropdown, owner from the card's parent, repositories per account | Done (see below) | Browser: create gives 201 with `Account` set; edit picker lists the token's account repositories |
| 7 | ApiToken stores the **Account document id**; owner fields move to a `V` class + index; migration fixes data | `ApiToken.Account`; `VApiToken` + `ApiTokens_Overview` in `apps/CodeCoverage/CodeCoverage/Indexes` (indexes live in the app, not the library); migration `M_202610021200` | `ApiTokensOverviewIndexTests`, `ApiTokenReferencesItsAccountMigrationTests` |
| 8 | Connected-logins link only when linking is on | `capabilities.externalLogins` = `/spark/auth/external-logins` mapped | `AuthCapabilitiesTests`; browser |
| 9 | `/me` through UserManager | `GetCurrentUser<TUser>` (`695d682b`, **not pushed yet**) | `AccountFlowTests.Me_*` |

## Decisions (with the reason that decided them)

- **D1. `PersistentObject.Name` is the entity type name; `Breadcrumb` is the display text.** `EntityMapper` used to overwrite `Name` with the breadcrumb (`/po/load` returned `"name": "Test token"`), so no hook could identify a parent by type. The in-memory query search no longer matches `Name`. Apps compare `obj.Parent?.Name == "Account"` and need no plumbing (owner preference: no boilerplate in apps; Spark carries it).
- **D2. A create started from a sub-query card carries its parent.** `/po/create` takes `parentId`/`parentType`/`queryId`, which `SubQueryNewParent` resolves exactly like `/po/new`, and sets `obj.Parent` for the save hooks (Vidyano does the same). A client-posted `Parent` is never trusted: create replaces it, update clears it.
- **D3. Reference option queries vs card queries (Vidyano split).** A card query enforces its parent (`Account_Repositories`: `EnsureParent("Account")`). A picker's lookup query adapts to its parent (`ApiToken_SelectableRepositories`: Account parent on New, the token itself on Edit). Reason: the edit page passes the edited object as the picker's parent.
- **D4. Save validation skips model-READ-ONLY attributes, not hidden ones.** Read-only posted values are dropped by `EntityMapper.IsWritableBySchema`, so a required server-stamped field (`ApiToken.CreatedAtUtc`) failed every create. Hidden must still be validated: a refresh hook can make a hidden attribute visible and required (`RefreshEndpointTests.Save_enforces_a_hook_imposed_rule_…`, which CI caught when hidden was skipped too).
- **D5. ApiToken `Account` read-only and taken from `obj.Parent` on create**, re-authorized on every save. Repositories must belong to that account (narrower than "any account I manage").
- **D6. Uploads authorize Account scope as `claim(covt:account) == repository.Account`.** The AccountId/Provider claims and the login fallback are removed. No backward compatibility (owner preference): the migration fixes data, and unresolvable tokens fail closed and are named in a warning.
- **D7. `M_202609092100` guard.** Its `where t.RepositoryGitHubId != null` matched tokens WITHOUT the field (RavenDB absent-field semantics) and made them Repository-scoped for `"Repositories/undefined"`. Production already applied it, so the fix matters only on fresh databases (found via the dogfood upload tests). Red/green proven by `ApiTokenRepositoryIdListMigrationTests`.
- **D8. Dogfood upload tests seed from a JSON fixture** (`CodeCoverage.Tests/Fixtures/Dogfood/dogfood.json`, `JsonFixtureImporter` linked from MintPlayer.Spark.Testing, fixed test token whose hash is in the fixture) and print the server's output on failure.

## Open items

- **Push `695d682b`** (`/me` via UserManager). It re-runs CI on PR #466.
- **The owner's package bump** (26 csproj, MintPlayer source generators) is uncommitted in the working tree. Ask before including it in the PR.
- **E2E flake:** `QnAContributionsBrowserTests.A_translator_adds_edits_and_withdraws_a_version_and_a_moderator_reverts_to_it` timed out (15 s save navigation) on CI run 37031951328. It passed on the previous CI run and locally (1/1). The next push re-runs it.
- **GitGuardian:** incidents 37813615–37813617 are `modelHashes.json` false positives (see memory `reference_gitguardian_modelhashes_false_positive`). The owner dismisses them in the dashboard.
- **Dev data:** test token "verify-create-from-account" (`ApiTokens/0de706e6-…`) on the local dev server. Its plaintext was captured in a screenshot (deleted), so revoke it.
- **Capabilities without route strings:** MintPlayer.AspNetCore.Tools [#38](https://github.com/MintPlayer/MintPlayer.AspNetCore.Tools/issues/38) (PRD/PLAN in that repo's `docs/`, branch `feat/endpoint-type-metadata`). After it is published, bump `MintPlayer.AspNetCore.Endpoints` here and switch `GetAuthCapabilities` to `IsEndpointMapped(typeof(PasskeySignIn<>))` / `IsEndpointMapped(typeof(ListExternalLogins<>))`.
- **After merge:** check the production startup log for the `M_202610021200` warning that names tokens it could not resolve to an account.

## Versions on this branch (unpublished, do not bump again)
`@mintplayer/ng-spark-auth` 22.16.0 · `@mintplayer/ng-spark` 22.26.0 · `MintPlayer.Spark` and `MintPlayer.Spark.Authorization` 11.0.0-preview.93.
