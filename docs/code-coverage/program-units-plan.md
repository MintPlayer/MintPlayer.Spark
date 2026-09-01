# Plan — Adopt Spark program units (preview.67)

Companion to [program-units-PRD.md](program-units-PRD.md). One branch, one PR to `master`, squash-merged.
Milestones are ordered so that **every runtime-failing change is forced to fail early**, before any feature
work is built on top of it. Tests are batched into a single sweep at M8 — intermediate milestones are
verified by reading the code and building.

Branch: `adopt-spark-program-units`.

**✅ All milestones implemented 2026-08-29.** Build clean, 144/144 tests, both verify gates exit 0, and
the composed page driven in a browser. Where the milestones below turned out to be wrong or incomplete,
the correction is recorded in **[PRD §9 As-built](program-units-PRD.md#9-as-built)** rather than by
rewriting the step — the plan is kept as it was written so the diff between intent and outcome stays
readable. Two items remain open (PRD §9.4): the GitHub Apps' `user:email` permission, and SP1.

Legend: 🟦 Coverage repo · 🟩 MintPlayer.Spark issue (no code, one issue)

---

## M0 — File the upstream issue 🟩

**✅ DONE.** Nine asks, one issue (PRD §7). No code, no waiting — every one has a recorded workaround, so
nothing below blocks on it. Done first so the findings are filed while #324 is fresh.

**Exit**: ✅ filed 2026-08-28 as [MintPlayer.Spark#327](https://github.com/MintPlayer/MintPlayer.Spark/issues/327).

---

## M1 — Bump and break loudly 🟦

Get every compile error and startup throw on the table before writing a line of feature code.

1. **Versions.** `Coverage/Coverage.csproj:20-31` (7 packages + source generators),
   `Coverage.Library/Coverage.Library.csproj:11`, `Coverage.Tests/Coverage.Tests.csproj:14` →
   `10.0.0-preview.67`. Keep `MintPlayer.Spark.Authorization` — it is now the *authentication/identity*
   package (`SparkUser`, `AddGitHub`, `SparkAuthenticationOptions`), and both namespaces Coverage imports
   are unchanged. Add `MintPlayer.Spark.Controllers`.
2. **`ClientApp/package.json`**: `@mintplayer/ng-spark` and `@mintplayer/ng-spark-auth` → `22.8.0`
   (lockstep; auth peer-depends on ng-spark at the same version). `@mintplayer/ng-bootstrap` stays `22.16.0`.
3. **`Program.cs:51`** — delete `spark.AddAuthorization()` (F2) and rewrite the stale comment block at
   `:44-50`, which describes the `Everyone` model that no longer exists.
4. **`Program.cs:54-81`** — D5: accept the `LocalCredentials = Disabled` default and let startup throw when
   `GitHub:{env}:ClientId` is absent. Replace the "Without credentials the app still boots" comment with
   the new contract, and note it in the README's local-setup section.
5. **`App_Data/security.json`** — F1/D8: add the `wellKnown` block, rename the group, and split each of the
   four `QueryRead/…` grants into two (anonymous + authenticated) with fresh unique ids. Right ids must be
   unique across the file (now rejected at load).

**Exit**: `dotnet build` clean; app boots with credentials present and throws a *named* error without them.

---

## M2 — Client-side breakage 🟦

Both items fail silently, so they are done deliberately rather than discovered.

1. **`app.routes.ts:12`** and **`src/spark-auth.setup.ts:61`** (F4) — `sparkAuthRoutes()` →
   `sparkAuthRoutes(withExternalLogin(githubProvider()))`. `spark-auth.setup.ts` is a generate-once template
   that is never overwritten, so it needs hand-editing. Then **guard every cross-feature `[routerLink]`**:
   `SPARK_AUTH_ROUTE_PATHS` is now partial, and a `routerLink` bound to `undefined` silently navigates to
   the current route.
2. **`pages/account/account.component.ts:14` + `.html:11`** (F5) — `SparkSubQueryComponent`
   (`@mintplayer/ng-spark/po-detail`) → `SparkQueryCardComponent` (`@mintplayer/ng-spark/grid`).
   `queryId` / `parentId` / `parentType` / `reloadToken` carry over unchanged.

**Exit**: the app builds and every auth page still resolves; sign-in works end to end.

---

## M3 — Model and security baselines 🟦

1. `--spark-synchronize-model`, commit the `triggersRefresh` drift (F7) and `modelHashes.json`.
2. `--spark-synchronize-security`, commit `securityPosture.txt` (D10).
3. Add `--spark-verify-security` to `.github/workflows/ci.yml:31` beside `--spark-verify-model`.
4. **Delete `Coverage/bin/**/App_Data/Model/MyAccountRow.json`** (D9).
5. **Grant the `user:email` account permission** ("Email addresses: Read-only") on **both** the development
   and production GitHub Apps (F6). Nothing in the build can verify this — tick it by hand, in the App
   settings UI, and record the date in the PRD's as-built section.

**Exit**: both verify flags exit 0; the posture report's anonymous surface has been read line by line.

---

## M4 — Controllers under Spark 🟦

D4/D7. Six controllers, authorization declared rather than hand-rolled.

1. `spark.AddControllers()` + `spark.UseControllers()` in the `AddSpark` callback; delete
   `endpoints.MapControllers()` at `Program.cs:271` (F8/SPARK010).
2. `spark.AddAntiforgeryProtection(a => { a.PathPrefixes = ["/spark","/connect","/api"]; a.WarnOnly = true; })`
   (D6).
3. Declare the new resources in `security.json` and attribute each controller. Target shape:

   | Controller | Today | Becomes |
   |---|---|---|
   | `BadgeController` | `[AllowAnonymous]` + query-string token | `[AllowAnonymous]` (unchanged — the badge token is the gate) |
   | `BrowseController` | *no attribute at all* | `[SparkAuthorize("Browse", "Coverage")]`, granted to **both** anonymous and authenticated |
   | `MeController` | `[Authorize]` | `[SparkAuthorize("Read", "Account")]` — an existing right, already granted |
   | `TokensController` | `[Authorize]` + in-code ownership | `[SparkAuthorize("Manage", "UploadToken")]`, authenticated only; ownership checks stay in the method bodies |
   | `RepoSettingsController` | `[Authorize]` + in-code ownership | `[SparkAuthorize("Manage", "RepoSettings")]`, authenticated only; ownership checks stay |
   | `UploadsController` | `[Authorize(AuthenticationSchemes = "covt,GitHubOidc")]` | `[SparkAuthorize("Upload", "Coverage")]` — keep the `AuthenticationSchemes` restriction alongside it |

   Never name a custom verb after a combined action (`Read`, `QueryRead`, `EditNew`, …) — `GroupRights.Expand`
   runs `SparkCombinedActions.Expand` on every right and would silently fan it out. `Browse`, `Manage` and
   `Upload` are all safe.
4. **The ownership checks stay in C#.** A declared right answers "may this caller use this endpoint at all";
   it cannot answer "does this caller own *this* repo". Do not delete those checks.
5. Re-run `--spark-synchronize-security`; the posture report should now list the `Browse/Coverage` anonymous
   grant. Review that line specifically.

**Exit**: every endpoint still reachable by the callers that could reach it before — anonymous badge,
anonymous browse, authenticated me/tokens/settings, `covt_` and OIDC uploads — and no others.

---

## M5 — The shell 🟦

1. Replace `shell.component.{ts,html,scss}` with `<spark-shell>`. Delete: `shellState`, `isSidebarVisible`,
   `toggleSidebar`, `onShellToggle`, `onMenuItemClick`, `setupResizeListener`, `updateSidebarVisibility`,
   `isAboveBreakpoint`, the `afterNextRender`/`PLATFORM_ID`/`DestroyRef` plumbing, the unused `effect`
   import, the `::ng-deep mp-shell::part(hamburger)` rule and the slotted-nav SCSS.
2. Keep as slots:
   - `*sparkShellTopbarEnd` — **replaces** the default `<spark-language-selector/>`, so re-render it
     explicitly, then the Resync button (M7) and the app's own auth block.
   - The GitHub auth block stays app-owned (`GitHubLoginService`, popup handshake, error map) rather than
     `<spark-auth-bar>`.
3. The login-error `bs-alert` and `onLoginAlertVisible` move to `*sparkShellMainHeader`.
4. Theme with `--spark-shell-topbar-bg` / `--spark-shell-sidebar-bg` / `--spark-shell-main-bg` to match
   today's look. `sidebarTheme` stays `'dark'`.
5. Keep `ShellComponent` as the layout route parent (`app.routes.ts:8-11`) — `sparkRoutes({ poDetail })`
   stays nested inside it.

**Exit**: the app looks unchanged, the drawer still collapses on navigate (now via `dismissOnNavigate`),
and no resize listener remains in app code.

---

## M6 — The composed Home page 🟦

The heart of the change. Build the query **before** the page — the grid is the part with the silent failures.

1. **`App_Data/Model/MyAccountRow.json`** — hand-authored, **no `clrType`** (this is what `preview.67`
   made first-class). Attributes carry `showedOn: "Query"`; `queries[0].source = "Custom.MyAccounts"`,
   `entityType = "MyAccountRow"`, and an explicit distinct `alias` (a duplicate alias refuses startup
   since .62). Keep the query in *this* file, naming its own type.
2. **`Coverage/Actions/MyAccountRowActions.cs`** — `partial`, `[Inject]` fields, one public method
   returning `IEnumerable<T>` of a row shape carrying `Id`, `Login`, `AvatarUrl`, `RepoCount`,
   `AggregateCoverage`, `IsAppInstalled`. A readable `Id` of **any** type narrows without a
   `RestrictToIds` hook, and an anonymous type is acceptable — the mapper reflects by attribute name.
   Move the aggregation currently in `MeController.GetAccounts` (`:29-72`) into a service both call, so
   the controller and the query cannot drift.
   ⚠️ **The id is now enforced**: `QueryResultProjector` throws by name on a null or duplicate row id at
   first render, rather than silently collapsing the grid. `Id => Login` satisfies it.
3. ~~`Coverage.Library/Entities/MyAccountRow.cs`~~ — **no longer needed.** `preview.67` removed the CLR
   class requirement entirely; do not add one.
4. **`security.json`** — `Query/MyAccountRow` for authenticated **only**, and **never `Read`** (D2).
   The framework now also defaults a composed row's link to `null` because `clrType` is absent, so the
   nested-anchor and dead-link hazards are closed on both sides.
5. **Cells — what `preview.67` gives us free, and what still needs a renderer.** The home page today
   (`home.component.html:61-79`) is the functional spec; **nothing there may be lost.**

   | Today | After | Needs |
   |---|---|---|
   | per-account link `['/a', login]` | `rowRoute` on the card — forwarded through `spark-query-card` since `.67` | no renderer |
   | `{{repoCount}} repos · {{coverage}}%` | plain columns + reuse `coverage-bar` | no renderer |
   | `<img>` **or** `bi-person`/`bi-people` fallback | ⚠️ **still a custom renderer** | `AvatarUrl` + `Type` |
   | green/grey translated installed badge | ⚠️ **still a custom renderer** | — |

   ⚠️ **The built-in `image` data type is not sufficient for the avatar, and I previously recorded that
   it was.** Verified in `spark-grid-cell.component.html:22-30`: it renders `<img>` only inside
   `@if (display(); as src)`, so an account with **no** avatar URL renders an **empty cell** — today it
   shows `bi-person` for a User and `bi-people` for an Org. It also consults no `rendererOptions` (sizing
   is a fixed `max-height: 2.5em`) and hardcodes `alt=""` as decorative. Keep a small `avatar` column
   renderer that falls back to the icon; use the built-in `image` type anywhere a URL is always present.
   The fallback needs `Type` (User vs Org) as well as `AvatarUrl`, so declare `Type` in
   `MyAccountRow.json` (step 1) as `"showedOn": "Query", "isVisible": false` — a value the renderer
   reads but the grid never draws. This is a fresh declaration here, not part of M9's migration of the
   existing `Repository` attributes.

   ⚠️ **The installed badge is not a boolean cell.** A `boolean` column renders a checkbox; today this is
   a green/grey `bs-badge` with translated `app.appInstalled` / `app.appNotInstalled`. That is a
   translated, colour-carrying affordance, so it stays a renderer.

   **SP2 survives** — both renderers draw inside `mp-datatable`'s shadow root, so inline styles are still
   the rule. **SP3 becomes a `rowRoute` check** rather than a nested-anchor check (see M8).
6. **`App_Data/Model/Home.json`** — virtual: **no `clrType`**, `"tabs": []`, `"groups": []` (so attributes
   land in one unheaded card and the only heading is the `<h2>`), read-only attributes for the greeting and
   counts, `"breadcrumb"` set, and `persistentObject.queries` naming the accounts query alias.
7. **`Coverage/Actions/HomeActions.cs`** — `partial`, no base class, exactly
   `public async Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)`. A wrong-shaped
   signature throws loudly; no class means 404. Scaffold via `IManager.GetPersistentObject`, fill
   `obj["…"].Value`, set `obj.Breadcrumb`. Grant `Read/Home` to anonymous **and** authenticated.
8. **`App_Data/programUnits.json`** — one group, a `persistentObject` unit for Home
   (`objectId`, `alias`), and a `url` unit for the GitHub App install link. Every `name` is a
   `TranslatedString` (en/fr/nl).
9. **Routing**: `/home`'s `loadComponent` goes away; `''` redirects to the Home program unit's route.
   Keep `vanity-redirects.ts`'s `/home` fallbacks working — update the three `returnUrl = '/home'` string
   literals (`github-login.service.ts:34`, `shell.component.ts:50`, `home.component.ts:84`).
10. **Reconnect banner and install hint** stay Angular, mounted through the existing `poDetail` override's
    `extraContentTemplate` (4.4 — it works on virtual types). Delete the old `home.component.*`.
11. **Translate** everything moving server-side: "Resync" and its tooltip, plus the Home attribute labels.

**Exit**: `/` renders the composed page with a breadcrumb title and a populated accounts grid.
**If the grid shows exactly one row, it is S1** (missing `Id`). **If it is empty with no error, it is S2**
(the query bound to the virtual type).

---

## M7 — Resync in the topbar 🟦

1. Declare `Resync` in `customActions.json`; grant `Resync/MyAccountRow` to authenticated. Actions attach by
   **right**, not declaration, so it can hang off `MyAccountRow` cleanly. A parentless, selectionless action
   is legal.
2. Server side: reuse `MeController.Resync`'s `InvalidateAsync` + re-query, and emit `refreshQuery` so the
   grid updates itself.
3. Client side, in `*sparkShellTopbarEnd`: `SparkService.getCustomActions('<alias>')` →
   `executeCustomAction('<alias>', 'Resync')`. Mount `SparkRetryActionModalComponent` once
   (already present in `app.html`) and keep `provideSparkClientOperations()` in bootstrap.
4. Show the button only when authenticated and only on the Home route.

**Exit**: SP5 green — the button executes and the grid refreshes without a manual reload.

---

## M9 — The renderer migration 🟦

New in the `preview.67` revision, and the largest client-side item. Do it **immediately after M1**, not
here — the `attribute` → `column` change is a compile error, so nothing else builds until it is done.
Numbered M9 only to keep the existing milestone references stable.

1. **Split or widen the six dual-role components.** `coverage-sparkline`, `short-sha`, `build-sessions`,
   `repo-name`, `date-time` and `coverage-delta` are each registered as **both** `detailComponent` and
   `columnComponent` (`app.config.ts:25-61`). A detail renderer still receives `attribute`
   (`EntityAttributeDefinition`); a column renderer now receives `column` (`SparkCellColumn`). Declare
   **both** inputs on a dual-role component, or split it in two.
   ⚠️ `withDeclaredInputs` filters undeclared inputs, so a component that keeps only `attribute` compiles
   and silently receives nothing on the grid path. Prefer splitting where the two roles already differ.
2. **`coverage-bar` is the easy case** — it already maps two distinct components, so each takes exactly
   one of the two inputs.
3. **Delete `row-attr.ts`.** Spark's `valueFor(item, key)` reads all three row shapes (`QueryResultItem`,
   `PersistentObject`, and the flat record an AsDetail sub-table passes) — which is exactly what our
   helper hand-rolled. Call sites become `valueFor(item, 'IsPrivate')?.value`. Keep `row-attr.spec.ts`'s
   cases as a regression pin against `valueFor` before deleting the helper.
4. **Mark three attributes `"showedOn": "Query", "isVisible": false`** so their values reach a renderer
   without drawing a column: `Repository.IsPrivate`, `Repository.FullName`, and whichever attribute
   `short-sha`'s `rendererOptions.titleAttribute` names. Under `preview.65` a row carried every attribute
   and this was free.
   ⚠️ **Comment the `titleAttribute` one at its declaration.** The sibling is named at the model-JSON call
   site, not in the component, so forgetting the mark yields a silently absent tooltip with nothing wrong
   in either the component or the model.
5. **Check the AsDetail path too.** Eight `AsDetail` attributes exist, one an array (`Build.Sessions`,
   drawn by `build-sessions-renderer`), so both the grid and the AsDetail row shapes are exercised.

**Exit**: `ng build` clean; every grid renders as before; the private-repo lock still appears on
`repo-name`; the `short-sha` tooltip still appears.

---

## M8 — Verification sweep 🟦

Batched, once, at the end.

1. `dotnet build`; `dotnet test` (`Coverage.Tests` uses no Spark testing API, so nothing there should move).
2. `--spark-verify-model` and `--spark-verify-security` both exit 0.
3. **SP1** — open `/query/repository-commits`; a `NotSupportedException` is the `.In()` composition (4.3).
4. **SP4** — `--spark-synchronize-model` twice, diff `App_Data/Model` and `modelHashes.json`; confirm
   `MyAccountRow.json` survives byte-identical and appears under `files` but not `entities`.
5. **SP2** — Playwright screenshot of the accounts grid: the avatar renderer's inline styles apply
   inside the shadow root, a Bootstrap class does not, **and an account with no `AvatarUrl` shows the
   `bi-person`/`bi-people` fallback rather than an empty cell.**
6. **SP3 (re-aimed)** — `rowRoute` navigation: clicking an account row reaches `/a/{login}`, and it works
   on the **auto-rendered sub-query card** on the composed Home page, not only on a bare
   `<spark-query-grid>`. This is the path `.67` had to forward `rowRoute` through, so it is the one worth
   proving.
7. **SP6 (new)** — the two renderer regressions M9 can break silently: the private-repo lock on
   `repo-name` (needs `Repository.IsPrivate` marked), and the `short-sha` tooltip (needs the
   `titleAttribute` target marked). Both fail with no error, so check them in a browser, not a spec.
8. **Functional parity check against `home.component.html:61-79` before deleting it** — avatar *or*
   icon fallback, account link, repo count, coverage %, and the translated green/grey installed badge.
   The old markup is the acceptance criterion; diff the rendered row against it.
9. Manual matrix: anonymous badge · anonymous browse of a public repo · sign-in · first-time provisioning ·
   `covt_` upload · OIDC upload · token create/list/revoke · gate settings.
10. Record the spike outcomes and the as-built deltas in the PRD.

**Exit**: PRD §8 exit criteria all met; PR opened.

---

## Ordering rationale

M1 and M2 come first because **four of the nine breaking changes fail at runtime and two fail silently** —
discovering `sparkAuthRoutes()` mounting nothing *after* building a new shell on top of it would be
expensive and confusing. M3 establishes the committed baselines before M4 widens the anonymous surface, so
the widening shows up as a reviewable diff in `securityPosture.txt` rather than as new noise. M6 builds the
query before the page because both of its failure modes are silent and are far easier to diagnose against a
bare grid than inside a composed page.
