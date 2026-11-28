# Object pages: DOM ids on sections, and fragment links that reach them (#721) — PRD and plan

Issue #721 (enhancement). One pull request. This file holds the findings, the options, the owner's questions, the spikes
and the milestones; the decisions and the log follow as they are made. Paths are under `libs/node_packages/ng-spark/`
unless they start with `apps/`, `libs/spark/`, `libs/model/`, `libs/authorization/`, `tests/`, `docs/` or `node_modules/`.

Status: **decided, 2026-11-28; ready for M1.** §2 holds the investigation: three read-only lookups, with every
load-bearing claim re-read by hand. It corrects four points of the issue (F2, F6, F7, F5). The owner answered Q1–Q7 and
asked for Angular's fragment support (§5.1, Dec1–Dec8). **Spikes S1–S5 are done (§6.1):**
- A2 is feasible.
- An absolute `redirectTo` drops the fragment.
- A target below a growing sub-query card moves by up to 800 px after the first scroll, and the browser's scroll
  anchoring does not hold it.
- The sticky action bar covers a section scrolled to the top.

Parts E and F and Q1 are updated to match.

## 1. Why

A notification, a bot comment or a doc often has to send someone to **one section** of an object page. Today such a link
can only open the page. The case that raised it is #714's ruleset bell (Dec5 in
`docs/merge_queue_required_check_renames_PRD.md`). It links to an account page (`/{forge}/a/{owner}`), and the
repository list the manager needs comes last on that page:
- eight attribute-group cards: Identity, Installation, Connection, Defaults, MergeQueue, DependencyUpdates, PrSizeLabels,
  Versioning;
- then three sub-query cards: `account-repositories`, `account-projects`, `account-upload-tokens`.

`/github/a/MintPlayer#account-repositories` should land on the list.

## 2. Findings (2026-11-28)

### F1. What the object pages render, and where

- **Detail page.** `po-detail/src/spark-po-presenter.component.html`:
  - The tabs are one `<bs-tab-page>` per `resolvedTabs()` entry inside one `<bs-tab-control>` (`:13-20`).
  - A tab's content comes from the `#detailTabContent` template (`:23-56`).
  - The card of ungrouped attributes is rendered only on the default tab (`:24-37`).
  - One `<bs-card class="m-3">` per group follows (`:38-55`). **A group card is rendered only when the group has at
    least one visible attribute** (`:40`). It has a header only when the group has a label (`:42-44`).
  - The `compact` mode renders one flat grid, with no tabs and no cards (`:3-11`).
- **Sub-queries.** `po-detail/src/spark-po-detail.component.html:75-82` renders one `<spark-query-card>` per sub-query.
  The cards sit below the presenter (`:70`) and outside the tab control.
- **Edit and new pages.** The form has the same structure (`po-form/src/spark-po-form.component.html:4-41`, ts
  `:433-463`) and iterates `editableAttributes`. The routed pages that host it are:
  - `po-edit/src/spark-po-edit.component.html:36`;
  - `po-create/src/spark-po-create.component.html:11`.
- **No ids.** None of these elements gets an `id`, and nothing selects a tab from outside.
- **The model is complete.** `AttributeGroup { id, name, label?, tab?, order }` and `AttributeTab { id, name, label?,
  order }` (`models/src/entity-type.ts:114-130`).

### F2. The default tab has no usable name (corrects the issue)

The presenter puts a synthetic tab first, `{ id: '__default__', name: 'Algemeen' }`
(`po-detail/src/spark-po-presenter.component.ts:119`). It does so whenever any of these holds (`:127-137`):
- there are ungrouped attributes;
- a group has no tab;
- the type defines no tabs at all.

So the tab control is always there. The issue's rule "a tab page gets its id from its tab's name" breaks on this tab:
- `kebab('Algemeen')` gives `algemeen`, a hard-coded Dutch word.
- HR's Person defines a tab of its own named `General` (`apps/HR/HR/App_Data/Model/Person.json:11-40`), so `general`
  is not free either.

### F3. The tab control: every pane is in the DOM, and `activeTab` selects one

The control is `@mintplayer/ng-bootstrap`'s (`node_modules/@mintplayer/ng-bootstrap/types/mintplayer-ng-bootstrap-tab-control.d.ts`).
- **Selecting a tab.** `activeTab` is a two-way `model<string | null>`: the shown page's `tabId`, or its position (`d.ts:7-8, 18`).
  `bs-tab-page` takes a `tabId` input (`d.ts:52`). The presenter and the form set no `tabId` today.
- **Panes are not lazy.** Every page's content is rendered in its pane, and a native radio hides the inactive ones
  (`d.ts:7-12`; the template in `fesm2022/mintplayer-ng-bootstrap-tab-control.mjs:90`). A section on a hidden tab
  therefore exists in the DOM before its tab is shown, so it can be found first and its tab opened afterwards.
- **The id needs an inner wrapper.** The `bs-tab-page` host is `hidden`, and its content is moved into the pane
  (`fesm:45, 51-53`). An id on the host would sit on an empty, hidden element.
- **The control makes ids of its own,** with the prefix `bs-tab-control` (`fesm:83`). Their format is in §6.1, S1.

### F4. When the sections exist, and when they stop moving

- **State.** `sparkObjectPage` keeps the page state in signals (`po-detail/src/spark-object-page.ts:62-68`).
- **Loading.** The route is a `combineLatest` (`:83-90`), and `load()` is async (`:121-153`). It sets `entityType` and
  `item` after `beforePublish` (`:140-141`).
- **"Loaded"** means both are non-null: that is the gate in `spark-po-detail.component.html:6-7`.
- **The cards exist after the next render.**
- **Late layout shifts.** The presenter's labels load afterwards (`spark-po-presenter.component.ts:206-219`), and the
  query cards fetch their rows afterwards. Either can move a target that has already been scrolled to.
- **A precedent to reuse.** `afterRenderEffect` is already used, with a comment on why it beats `afterNextRender` and
  `effect` (`po-form/src/spark-primitive-list-editor.component.ts:70-81`).
- **Nothing reads a fragment yet.** The library has no fragment, `ViewportScroller` or `scrollIntoView` code.
- **The fragment survives the canonical-URL rewrite.** `objectUrl.apply` keeps any `?…#…` suffix when it rewrites the
  address bar (`services/src/spark-object-url.service.ts:18-26`).

### F5. The page does not scroll the window, so the router's anchor scrolling can never work (strengthens the issue)

ng-spark's shell scrolls its own `<main id="spark-main">` (`shell/src/spark-shell.component.html:52`, with
`height: 100%; overflow: auto` in `shell/src/spark-shell.component.scss:114-120`). The apps wrap the shell in a `100vh`
host (`apps/CodeCoverage/CodeCoverage/ClientApp/src/app/shell/shell.component.scss:4`; Fleet does the same).
- `withInMemoryScrolling({ anchorScrolling })` and `ViewportScroller` scroll the **window**, which never scrolls here.
  The issue said they would scroll too early. In fact they would not scroll at all.
- `element.scrollIntoView()` scrolls the nearest scrollable ancestor. CodeCoverage already uses it
  (`apps/CodeCoverage/CodeCoverage/ClientApp/src/app/pages/merge-queue-setup/merge-queue-setup.component.ts:194`).
- A PO detail page has a sticky action bar (`styles/_actionbar.scss:1, 25`). A section scrolled to `block: 'start'`
  may land under it (S2).

### F6. A sub-query's alias is not guaranteed kebab-case, and an entry may hold an id (corrects the issue)

- A sub-query entry's `query` is "the query's alias **or id**" (`models/src/sub-query.ts:7`; server side
  `libs/spark/.../SparkSubQuery.cs:23-26, 61-79`).
- An alias is either explicit (`SparkQuery.Alias`) or derived by `SparkQueryAliases.Derive`, which strips a `Get` prefix
  and lowercases (`libs/model/MintPlayer.Spark.Model/SparkQueryAliases.cs:18-24`). So `GetPrSizeLabels` → `prsizelabels`,
  which is lower-case but has no hyphens. A new alias that collides gets a `-2` suffix
  (`libs/spark/MintPlayer.Spark/Services/ModelSynchronizer.cs:1358-1360`).
- In the repo's apps, all 23 sub-query entries are explicit kebab aliases, and none is an id. "Already kebab-case" holds
  by convention, not by rule.
- An id-form entry is still possible. Its card would only learn its alias once its query has loaded (`SparkQuery.alias`,
  `models/src/spark-query.ts:18`), so the id would appear late.

### F7. Group and tab names are hand-typed, and the synchronizer does not own them (corrects the issue)

- The synchronizer copies `tabs` and `groups` from the existing file unchanged (`ModelSynchronizer.cs:843-845`, "Preserve
  manually-defined tabs and groups").
- `AttributeTab.Name` and `AttributeGroup.Name` keep the casing they were typed in
  (`libs/spark/MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs:564, 584`). Labels resolve from
  `model.{Type}.tabs|groups.{Name}.label`, with a fallback to `SparkText.Humanize` (`ModelLoader.cs:257-261`).
- The issue has "the model synchronizer fails" on a duplicate. A name the synchronizer only copies is better checked
  where the model is verified and loaded (F8).

**Scan of every `apps/*/*/App_Data/Model/*.json`** (entity types that have groups / tabs / sub-queries):

| App | Files | Groups | Tabs | Sub-queries |
|---|---|---|---|---|
| CodeCoverage | 35 | 2 | 1 | 6 |
| DemoApp | 8 | 0 | 0 | 1 |
| Fleet | 6 | 0 | 0 | 0 |
| HR | 7 | 1 | 1 | 1 |
| MintPlayer | 12 | 0 | 0 | 4 |
| QnA | 7 | 0 | 0 | 1 |
| SeoTester | 10 | 4 | 3 | 2 |
| SqlDemo | 2 | 0 | 0 | 0 |

- Three names have more than one word: `MergeQueue` → `merge-queue`, `DependencyUpdates` → `dependency-updates`,
  `PrSizeLabels` → `pr-size-labels`. The other 33 group and tab names are single words.
- No name has an acronym run, a digit, a space or a non-ASCII letter, and none is empty.
- **No two sections of one type would share an id today.**
- An acronym run would split differently by method: `URLPath` gives `u-r-l-path` with a naive split and `url-path` with
  an acronym-aware one.

### F8. Where the model is already validated, and no kebab helper exists

- **The synchronizer** throws on a duplicate attribute name, and names the file (`ModelSynchronizer.cs:876-886`). This
  is the closest precedent for a throw.
- **`--spark-verify-model`** collects problems and exits with 3 (`libs/spark/.../SparkDevelopmentExtensions.cs:166-190`;
  `VerifyQueryAliasesAreUnique` at `:981`; `ReportVerificationProblems` at `:1407`).
- **Startup** builds the alias index and validates it (`libs/spark/.../SparkMiddleware.cs:704-725`).
  `SparkSubQueries.ValidateParentReferences` is the precedent for one static validator called from both places
  (verify at `SparkDevelopmentExtensions.cs:1151`, startup at `SparkMiddleware.cs:708`).
- **Diagnostics.** SPARK045 and SPARK046 check only library model files. Nothing checks an app's group or tab names.
- **No kebab helper exists.** The nearest relatives:
  - `IsKebab` checks a string but is private (`libs/spark/Shared/Layering/SparkSecurityLayers.cs:620`).
  - `Slugify` does not split PascalCase (`libs/authorization/.../SparkExternalProviderPolicy.cs:92`).
  - `SparkText.Humanize` keeps acronym runs together (`libs/spark/MintPlayer.Spark.Abstractions/SparkText.cs:52`).
  - The TS library has none.

### F9. Where a form or presenter is not the page

- **`spark-po-form` in modals:**
  - the AsDetail row edit (`po-form/src/spark-po-form.component.html:480-498`), which is also where a nested AsDetail
    recurses (`:139-141`);
  - the new-object modal (`po-form/src/spark-po-new-modal.component.ts:62`);
  - the retry-action modal (`retry-action-modal/src/spark-retry-action-modal.component.ts:42`).
- **No flag says "in a modal".** `BsModalComponent` provides nothing a child could inject for it.
- **The routed pages are exactly three:** po-detail, po-edit and po-create (F1).

### F10. Where the fragment survives a rebuilt URL, and where it is lost

- **Lost:** CodeCoverage's three vanity redirect guards rebuild the URL with `queryParams` and no `fragment`
  (`apps/CodeCoverage/CodeCoverage/ClientApp/src/app/spark/vanity-redirects.ts:24, 39, 54`). The bell's link goes through
  the first one. Their spec has no fragment case (`app.routes.spec.ts:188-202`), and no host or E2E test covers them.
- **Dropped on purpose:** a 401 on a non-API request sends the user to sign in with `returnUrl` cut at `?` and `#`
  (`auth/interceptors/src/spark-auth.interceptor.ts:18-24`, "R2-L4: drop query string and fragment… leaks… into the next
  access log entry"). That is a documented security decision, and it stays as it is.
- **Survives:**
  - `sparkAuthGuard` puts `state.url` into `returnUrl`, hash included (`auth/guards/src/spark-auth.guard.ts:23-24`).
    `isSafeReturnUrl` allows `#` (`auth/models/src/return-url.ts:16-22`).
  - Sign-in, login and two-factor all end in `navigateByUrl`.
  - The server's `SanitizeReturnUrl` keeps `#` (`libs/spark/.../SparkAuthenticationExtensions.cs:694-701`).
  - `SparkUrlSerializer` passes `tree.fragment` through (`services/src/spark-url-serializer.ts:23`).
  - `objectUrl.apply` (F4).
  - The bell click and the push click both go through `sparkAppUrl`, which keeps the hash (`models/src/spark-app-url.ts:59`),
    then call `navigateByUrl`. The calls are at `notifications/src/spark-notifications.service.ts:317-328` and
    `pwa/src/spark-notification-clicks.ts:49-55`.
  - `SparkLinkUrl` only validates a URL, and allows `#` (`libs/spark/.../SparkLinkUrl.cs:29-51`).
- **Static `redirectTo` routes:** an absolute target drops the fragment, a relative one keeps it (§6.1, S4).
  - CodeCoverage's two `HOME_URL` redirects (`app.routes.ts:19, 28`) are the only absolute ones in the apps.
  - The other nine (`'home'`, `'account/passkeys'`) are relative.
- **Not confirmed:** the last hop of an external login.

### F11. Router, SSR and tests

- **Router.** Every app calls a bare `provideRouter(routes)`: CodeCoverage, Fleet, HR, QnA, DemoApp and MintPlayer.
- **SSR.** Only MintPlayer renders on the server (`withHydration()`, `apps/MintPlayer/MintPlayer/ClientApp/src/app/app.config.ts:37`). A fragment
  never reaches the server, so scrolling is browser-only. `afterRender*` hooks do not run on the server.
- **Vitest.** Zoneless, on jsdom (`src/test-setup.ts:3`, `vitest.config.ts:20-21`).
  - The presenter spec uses `createComponent` + `setInput` + `whenStable` (`po-detail/src/spark-po-presenter.component.spec.ts:30-44`).
  - The detail spec uses `RouterTestingHarness` (`po-detail/src/spark-po-detail.component.spec.ts:105-119`).
  - `settle()` lives in `src/test-utils.ts:46`.
  - jsdom probably has no `scrollIntoView`, so a spec would stub it (S1).
- **E2E.** `tests/MintPlayer.Spark.E2E.Tests` runs xUnit with Microsoft.Playwright 1.64.1. It hosts Fleet, HR, QnA,
  MintPlayer, SparkId, SqlDemo and CrossModule. Targets:
  - **A group on a second tab:** HR's Person. Its tabs are General and Employment, and the Career group sits on Employment
    (`Person.json:11-40`).
  - **A sub-query card:** HR's Company has `company-people` (`Company.json:101-103`). QnA's Question has
    `question-answers`, already tested in `QnASubQueryTests.cs:114`.

### F12. The bell

`RulesetNoticeBell.AccountUrl` (`apps/CodeCoverage/CodeCoverage/MergeQueue/RulesetNoticeBell.cs:111-112`) feeds the
notification's `Url` (`:87`) and carries no fragment yet. Once F10's redirect is fixed, the hash survives the whole path:
`SparkLinkUrl` → bell or push → `navigateByUrl` → vanity guard → `/po/account/{id}#account-repositories`.

## 3. Goals and non-goals

**Goals:**
- **G1.** On the object pages (detail, edit, new), every group card, every tab and every sub-query card carries a stable,
  readable id.
- **G2.** A URL with `#{id}` opens the tab that holds the section and scrolls the section into view. This happens when
  the page loads and when only the fragment changes.
- **G3.** The fragment survives every URL that the library and the apps rebuild, R2-L4 apart.
- **G4.** A model that would give two sections of one type the same id fails before it ships.
- **G5.** The ruleset bell links to `#account-repositories`.

**Non-goals:**
- **A URL that runs a query action on a chosen row** (the issue's out of scope; #714 Dec5's ideal). It needs a URL
  contract for actions.
- **Ids for single attributes,** and ids inside modals or nested forms.
- **Writing the fragment back to the URL** when the user switches tabs or scrolls (unless Q6 says otherwise).

## 4. Options

### Part A: where the id is computed (Q1)

- **A1. In both languages.** ng-spark computes `kebab(name)` in TS for rendering. The validator computes it in C#. One
  shared list of test vectors (name → id) keeps the two in step, and both suites read it.
- **A2. Once, on the server.** The type as served carries the id: an `anchor` on each group and tab, and each sub-query
  entry in its alias form even when the file holds an id (F6). The client renders what it gets, and the validator uses
  the same C# function. This depends on the served payload being able to differ from the model file, so that the
  synchronizer never writes `anchor` back. **S3 shows it can (§6.1):**
  - a contract modifier on the HTTP `JsonOptions` adds the property, as `SparkFileUrls.AddUrlProperty` already does for
    `url`;
  - the catalogue's per-caller copy swaps an id-form sub-query entry for its alias.
- **A3. An explicit `anchor` in the model JSON,** defaulting to `kebab(name)`, which an author may override. Nothing
  needs an override today (F7). This adds a field to every hand-typed model.

### Part B: the kebab rule (Q2)

- **Acronym-aware:**
  - A word boundary comes before an upper-case letter that follows a lower-case letter or a digit.
  - Inside an upper-case run, the boundary comes before its last letter when a lower-case letter follows.
  - Everything is then lower-cased.
  - Examples: `PrSizeLabels` → `pr-size-labels`, `URLPath` → `url-path`, `Step2Details` → `step2-details`.
- **A name whose result does not match `^[a-z][a-z0-9]*(-[a-z0-9]+)*$` is refused, not stripped.** Group and tab names
  are identifiers already: translation keys use them (F7).
- **Reserved prefixes:** `spark-` and `bs-`. The shell's own `spark-main` and the tab control's `bs-tab-control…` (F3,
  F5) can then never clash with a section id.

### Part C: which elements get an id (Q3)

- **A group card:** on the `bs-card` host. The host *is* the `.card`.
- **A tab:** on an inner wrapper inside its pane (F3).
- **A sub-query card:** on the `spark-query-card` host.
- **The ungrouped card:** none, because it has no name.
- **The default tab** (F2):
  - **C1. No id.** A plain link to the page already opens it.
  - **C2. A fixed `general`.** The validator would have to reserve it, because HR's Person already defines a `General`
    tab.

### Part D: where ids are rendered (Q4)

- **D1. Opt-in.** The presenter and the form get an input (working name `sectionIds`, default `false`). po-detail,
  po-edit and po-create set it. Modals, the nested AsDetail form, `compact` mode and an app's own embedding get no ids,
  and nothing has to detect a modal.
- **D2. On by default,** switched off in a modal by injecting `BsModalComponent`. It misses modals that apps build
  without `<bs-modal>`. **Chosen (Dec4).** S5 confirmed the detection works (§6.1).

### Part E: the scroll, through Angular's fragment support (Dec8)

> Rewritten after the owner's "use the angular fragments feature" (2026-11-28). The first version had a helper per
> routed page that read `ActivatedRoute.fragment` and listened for `NavigationSkipped` itself.

- **Angular triggers the scroll.** Every app adds `withInMemoryScrolling({ anchorScrolling: 'enabled' })` to its
  `provideRouter`. The router's scroller then calls `ViewportScroller.scrollToAnchor(fragment)`:
  - one frame after every `NavigationEnd` whose URL has a fragment;
  - after an ignored same-URL navigation (`NavigationSkipped`, code 0), so a second click on the bell's link scrolls
    again.

  Source: `@angular/router` 22.2.2, `RouterScroller.createScrollEvents` and `consumeScrollEvents` in
  `node_modules/@angular/router/fesm2022/_router_module-chunk.mjs`.
  - `scrollPositionRestoration` stays `'disabled'`, so a navigation without a fragment behaves as it does today.
  - Links inside the apps use `routerLink` with `[fragment]`, or `createUrlTree(…, { fragment })`. Nothing parses
    `location.hash`.
- **ng-spark supplies the `ViewportScroller`** (working name `SparkViewportScroller`). It is registered through
  `provideSpark`, and only in the browser. Angular's own implementation scrolls the window, which never scrolls in
  Spark's shell (F5). `ViewportScroller` is an abstract, injectable class, and it has `setOffset` and `ScrollOptions`
  (`@angular/common` `types/common.d.ts`). Spark's `scrollToAnchor`:
  1. **looks for the anchor inside `#spark-main`.** With no shell on the page, it falls back to Angular's window
     behaviour.
  2. **waits for the anchor when it is not there yet.** An object page renders its sections only after its data has
     loaded (F4; S2 measured 0.7–1.5 s). The wait is bounded: a `MutationObserver` on `#spark-main` for up to 10 s,
     cancelled by the next navigation. An unknown fragment times out silently.
  3. **opens the anchor's tab.** It dispatches a bubbling `spark-reveal` DOM event from the section. The presenter and
     the form listen on their host, find the tab wrapper (`data-spark-tab`) that holds the section, and set
     `activeTab` (S1). The tabs get `[tabId]="tab.id"`.
  4. **scrolls the section to the top of `#spark-main`, below the sticky action bar.** S2 found the bar covers 54 px
     otherwise. `setOffset` defaults to the bar's measured height plus a small gap. The scroll is smooth, and instant
     under `prefers-reduced-motion: reduce`.
  5. **keeps the section there while the page still settles** (S2). A sub-query card above the target loads its rows
     after the first render, and that moved the target by up to 800 px within a second. The first scroll can also stop
     short because the page is not tall enough yet. The browser's scroll anchoring does not compensate, because the
     card that grows is the anchor itself.
     - So the scroller re-aligns the section whenever it drifts.
     - It stops after 1 s without drift, after 5 s at most, or at the user's first wheel, touch, pointer-down or key in
       `#spark-main`.
  6. **outlines the section briefly** (Dec5): a class held for about 1.5 s. Under reduced motion the outline is static,
     with no transition.
- **Hydration.** The router skips the first `Scroll` event while the app hydrates (`RouterScroller.isHydrating`). Only
  MintPlayer hydrates (F11). For it, ng-spark asks for the first anchor itself once the app is stable, if the URL has a
  fragment.
- **No helper per page.** Any page that carries ids, an app's own pages included, gets fragment scrolling. The object
  pages only add the tab opening.
- **Edge cases:**
  - An unknown fragment does nothing.
  - A group without visible attributes renders no card (F1), so its fragment does nothing either.
  - On the server, Angular's `NullViewportScroller` stays in place and nothing runs.
  - A fragment-only navigation fires the router's `Scroll` event (S4 b).
  - The same fragment navigated twice fires it too, through `NavigationSkipped` (S4 c).

### Part F: the fragment through rebuilt URLs

- **The three vanity guards** pass `fragment: route.fragment ?? undefined`, each with a spec case.
- **CodeCoverage's two absolute `HOME_URL` redirects** become redirect functions that keep the fragment, each with a spec
  case. S4 shows that an absolute `redirectTo` string drops it, and that a function returning
  `createUrlTree([...], { fragment })` keeps it. The relative redirects in the apps already keep it.
- **R2-L4 stays as it is** (F10).

### Part G: validation (Q7)

**One static validator** (working name `SparkSectionIds.Validate`), modelled on `ValidateParentReferences`.
- **Per entity type,** it computes:
  - the kebab of every group name;
  - the kebab of every tab name;
  - every sub-query's alias, with id-form entries resolved.
- **It reports a problem for:**
  - two equal ids;
  - an id that breaks Part B's pattern;
  - a reserved prefix.
- **It runs in three places:**
  - **`--spark-verify-model`,** which collects the problems and exits with 3;
  - **startup,** which throws;
  - **the synchronizer,** which throws next to the duplicate-attribute check and names the file.

### Part H: the bell and the docs

- **The bell:** `AccountUrl(repository) + "#account-repositories"`, with `RulesetNoticeBellTests` updated.
- **The docs:**
  - `docs/guide-attribute-grouping.md`: the ids, the default tab, the validator.
  - `docs/guide-object-urls.md`: the fragment, and where it survives.
  - `docs/code-coverage/merge-queue.md`: the bell's link.

## 5. Questions for the owner

Each question has a recommendation and its trap. They will be asked one at a time.

- **Q1. Where is the id computed?** Recommended: **A2**, through an HTTP-only contract modifier. S3 found the
  precedent, and the synchronizer's idempotency test would catch an `anchor` that leaks into a file. A1 would keep two
  kebab functions in step. A3 adds a field that nothing needs.
  *Trap:* A2 changes the wire contract of every type, and a custom client of the protocol would see the new property.
  That is allowed while Spark is in preview. The MCP tools also return catalogue output, through a serializer that
  nobody has checked. They would show `anchor`, or not, depending on which options they use.
- **Q2. The kebab rule, refusing odd names, and the reserved prefixes:** recommended as Part B.
  *Trap:* an app whose group names contain spaces or accents stops starting until they are renamed. None exists in this
  repo (F7). Outside apps are unknown.
- **Q3. The default tab's id:** recommended **C1**, none.
  *Trap:* nobody can link "back to the first tab" from another tab of the same page. That is only a same-page case.
- **Q4. Where ids render:** recommended **D1**, opt-in on the three routed pages.
  *Trap:* an app that builds its own detail page from `spark-po-presenter` has to set the input to get ids.
- **Q5. A brief highlight of the target card on arrival?** Recommended: **no**. The card's header already says where
  the reader is, and nothing new needs styling under the ng-bootstrap rules.
  *Trap:* on a long page of similar cards, the eye may not find the one that was meant.
- **Q6. Should switching tabs write `#{tab}` back to the URL?** Recommended: **no**, as in the non-goals. History stays
  clean and the issue does not ask for it.
  *Trap:* a user cannot copy a link to the tab they are on. They would have to type the fragment.
- **Q7. Where does validation run?** Recommended: **all three,** as Part G.
  *Trap:* a duplicate in an external app now stops startup rather than rendering two equal ids.

### 5.1 Decisions (owner, 2026-11-28)

Asked one at a time, after spikes S1–S4. Each decision follows §4 unless it says otherwise.

- **Dec1 (Q1): A2. The server computes the id.**
  - Groups and tabs carry `anchor` on the wire only, through an HTTP-only contract modifier on ASP.NET's `JsonOptions`.
    The precedent is `SparkFileUrls.AddUrlProperty` (S3). The model files never get it, and the synchronizer's
    idempotency test guards that.
  - An id-form sub-query entry is served in its alias form, from the catalogue's per-caller copy.
  - The C# kebab function is the only one: the client renders what it receives.
- **Dec2 (Q2): Part B as written.**
  - The kebab split is acronym-aware.
  - A name whose result is not `^[a-z][a-z0-9]*(-[a-z0-9]+)*$` is a model problem, never stripped.
  - Ids starting with `spark-` or `bs-` are refused.
- **Dec3 (Q3): C1. The default tab and the card of ungrouped attributes get no id.**
- **Dec4 (Q4): D2, against the recommendation (D1). Ids are on by default** in the presenter and the form.
  - They are off when `inject(BsModalComponent, { optional: true })` finds a modal. S5 shows this works under `@if` and
    through a component nested inside the modal.
  - That covers ng-spark's three form modals, the nested AsDetail form (which opens in the row modal), and any app
    content inside a `<bs-modal>`.
  - Accepted trap: a modal built without `<bs-modal>` renders ids, and these can then duplicate the page's.
  - `compact` mode has no cards anyway.
- **Dec5 (Q5): a brief outline, against the recommendation (no highlight).**
  - The target is outlined for about 1.5 s, with no transition under `prefers-reduced-motion: reduce`.
  - The style lives in ng-spark's own stylesheets, as a shared partial next to `styles/_actionbar.scss`, and is
    `@use`d by the components that render sections. It uses Bootstrap's CSS variables, not utility classes.
- **Dec6 (Q6): no.** Switching tabs leaves the URL alone, as in the non-goals.
- **Dec7 (Q7): all three.** The validator runs in `--spark-verify-model`, at startup and in the synchronizer (Part G).
- **Dec8 (owner, during the questions): "Also: use the angular fragments feature".** Part E is rebuilt on Angular's own
  fragment support:
  - the router's anchor scrolling (`withInMemoryScrolling({ anchorScrolling: 'enabled' })` in every app);
  - `routerLink [fragment]` and `createUrlTree(…, { fragment })` for links;
  - an ng-spark `ViewportScroller` that does the scrolling in `#spark-main`.

  The per-page helper and its own `NavigationSkipped` listener are gone: the router covers both. This is my reading of
  a one-line instruction. If the owner meant something narrower, such as only `routerLink [fragment]` for links, Part E
  shrinks back to a page-level trigger that calls the same `ViewportScroller`.

## 6. Spikes

| # | Question | Where | Done when |
|---|---|---|---|
| S1 | Does an element inside `bs-tab-page` content keep its id once the control moves it into the pane, and appear exactly once in the DOM? Does setting `activeTab` to a page's `tabId` show that page? What format do the control's own ids have, and can a kebab id clash with them? Does jsdom lack `scrollIntoView`, and how do the existing specs stub DOM APIs? | `node_modules/@mintplayer/ng-bootstrap` and `@mintplayer/ui-core`, read-only, plus a throwaway vitest spec in ng-spark that is not committed | Each answered with file:line, and the throwaway spec's output recorded. **Done 2026-11-28 (§6.1).** |
| S2 | On a real page, where does the target land and when does it stop moving? On coverage.mintplayer.com's account page for MintPlayer (eight group cards, then three query cards): the `account-repositories` card's offset inside `#spark-main` at the first render of the cards, after the labels load, and after the query cards have loaded their rows. Does `scrollIntoView({ block: 'start' })` leave it under the sticky action bar? | `playwright_node`, read-only: navigation, an init script that records the offsets, no clicks that write | The offsets recorded. Part E's trigger chosen (one scroll, or a bounded re-scroll), and whether `scroll-margin-top` is needed. **Done 2026-11-28: a bounded re-scroll, and an offset is needed (§6.1).** |
| S3 | How are entity types served to the client (endpoint, serializer, the DTO or `EntityTypeDefinition` itself), and how does the synchronizer write the file? Can a group or tab carry a computed `anchor` on the wire but not in the file? Can a sub-query entry be served in its alias form while the file holds an id? | `libs/spark`, read-only | Answered with file:line. Decides Q1 between A1 and A2. **Done 2026-11-28: A2 is feasible (§6.1).** |
| S4 | Router behaviour. (a) Do CodeCoverage's static `redirectTo` routes keep the fragment? (b) Does a fragment-only navigation on the same object page reuse the component, with `ActivatedRoute.fragment` emitting? (c) Is the same fragment navigated twice ignored? | A throwaway vitest with `RouterTestingHarness`. Case (a) becomes a committed spec if the fragment survives | Three answers recorded. **Done 2026-11-28: an absolute redirect drops the fragment (§6.1).** |
| S5 | Added after Dec4 chose D2. Does `inject(BsModalComponent, { optional: true })` in a component inside `<bs-modal>` find the modal, even though the modal moves its DOM to the end of `<body>`? Does it work under `@if`, and through a component nested inside the modal? | A throwaway vitest in ng-spark, deleted after the run | The three cases recorded. **Done 2026-11-28: yes in all three cases, and `null` outside (§6.1).** |

### 6.1 Results (2026-11-28)

**S1. The tab control is safe for ids, and `activeTab` takes a page's `tabId`.** Throwaway spec: two `bs-tab-page`s with
`tabId`s `__default__` and `g-guid-2`, each holding a `<div id="sec-…">`.
- **The id renders exactly once,** inside the pane (`bs-tab-control-1-p1`) and not inside the `bs-tab-page` host. A page
  captures its content in `<ng-template #lent><ng-content/></ng-template>` (`fesm2022/mintplayer-ng-bootstrap-tab-control.mjs:45`),
  and the control stamps it into the pane with `ngTemplateOutlet` (`:90`). An id on a wrapper inside the page's content
  therefore works.
- **Selection.** Setting `activeTab` to `'g-guid-2'` flipped the radios from `true,false` to `false,true`.
  `node_modules/@mintplayer/ui-core/tab-control/index.mjs:101-113` keys each page by its `tabId`, or by its position when
  the `tabId` is empty, and selects the page whose key equals `activeTab`.
- **The control's own ids** are:
  - `{host}-r{i}` for a radio, `{host}-t{i}` for a tab and `{host}-p{i}` for a pane, plus the radio group's name
    `{host}-group` (`index.mjs:7-9, 43`);
  - `{host}` is the host's own `id` attribute, else `bs-tab-control-{n}` (`ui-core/component/index.mjs:117`), and
    `bs-tab-control-ssr` on the server (`:107`).

  All of them start with `bs-`, which Part B reserves.
- **jsdom has no `Element.prototype.scrollIntoView`** (it is `undefined`), so the specs stub it.

**S2. On the live account page, a target can move after it has been scrolled to, and the action bar covers it.**
Method: `playwright_node` (Chromium), signed in, `https://coverage.mintplayer.com/github/a/MintPlayer`, 2026-11-28 around
12:40. Read-only: an init script sampled positions every 25 ms. The only other actions were navigations and
`scrollIntoView` calls.
- **The cards appear 0.7–1.5 s after the navigation starts.** The redirect and the canonical rewrite end at
  `/po/account/…/mintplayer`.
- **`account-repositories`, the first sub-query card,** sat at 2528 px from its first render on and never moved. Neither
  the labels loading nor anything else above it shifted it.
- **The cards below it moved,** because `account-repositories` loads its rows after the first render:
  - `account-projects`: 2693 → 2841 → 3274 px;
  - `account-upload-tokens`: 2857 → 3006 → 3169 → 3656 px;
  - all within about 0.8 s, with gaps of up to 0.47 s between shifts.
- **One `scrollIntoView` on `account-upload-tokens` at its first render** stopped at `scrollTop` 2434, the maximum while
  the page was still short. That left the card 423 px below the top. It was then pushed to 1222 px while `scrollTop`
  stayed at 2434, although `overflow-anchor` was `auto`: the growing card was the anchor. So one scroll is not enough,
  and Part E re-aligns.
- **After `scrollIntoView({ block: 'start' })`,** the card's top was main's top (55 px), and the sticky
  `div.spark-actionbar` spanned 55–109 px: 54 px of the card were hidden. No `--spark-actionbar-height` variable exists.
  So Part E offsets by the bar's measured height.
- **Seen on the side:** each load logs the sparklines 500 (#723, already filed) and a 404 from
  `spark/notifications/push/key`.

**S3. A2 is feasible.**
- **Serving.** `GET /spark/types` and `GET /spark/types/{id}` return `Results.Json(EntityTypeDefinition)` directly, with
  ASP.NET's `JsonOptions` (`libs/spark/.../Endpoints/EntityTypes/List.cs:11, 21`, `Get.cs:12, 33-36`).
  `SparkPresentation.Install` adds one contract modifier to those options (`libs/spark/.../SparkMiddleware.cs:104-105`;
  `libs/spark/MintPlayer.Spark/Services/SparkPresentation.cs:55-56`).
- **Writing.** The synchronizer writes the same types with its own static options: camelCase, `WhenWritingNull`, no
  modifier (`ModelSynchronizer.cs:52-58, 809`).
- **So a plain `public string Anchor => …` would leak into the files.** An HTTP-only contract property would not. The
  precedent is `SparkFileUrls.AddUrlProperty`, a `url` that is served but never written (`libs/spark/MintPlayer.Spark/Services/SparkFileUrls.cs:123-133`,
  hooked at `SparkPresentation.cs:77-79`; re-read by hand).
- **The guard.** No golden covers the served types. `SynchronizeIdempotencyTests.cs:55` compares the model files' text,
  so it would catch a leak.
- **Sub-queries are served as written** (`SubQueryPruner.cs:43-47`; the converter writes `Query` verbatim,
  `SparkSubQuery.cs:102-119`). The catalogue's per-caller `kept` copy (`SubQueryPruner.cs:41-54`) is where an id-form
  entry can take its resolved alias.
- **Not checked:** the serializer of the MCP tools, which also return catalogue output
  (`Mcp/Tools/SparkGenericTools.cs:47, 84`).

**S4. The router.** Throwaway spec with `RouterTestingHarness`:
- **(a) Redirects.**
  - An **absolute** `redirectTo: '/po/x/1'` turned `/from#merge-queue` into `/po/x/1`: **the fragment is dropped.**
  - A **relative** `redirectTo: 'po/x/2'` kept it: `/po/x/2#merge-queue`.
  - A redirect **function** returning `createUrlTree([...], { fragment })` kept it too.
- **(b) A fragment-only navigation** created no new component, and `ActivatedRoute.fragment` emitted `[null,
  "dependency-updates"]`.
- **(c) The same URL and fragment again** resolved `false`, with no new emission. The router emitted
  `NavigationSkipped` with code 0, which the helper can listen for.
- **(d) Dropping the fragment** emitted `null`.

**S5. Modal detection works.** Throwaway spec: a probe component with
`readonly inModal = !!inject(BsModalComponent, { optional: true })`, placed in three spots:
- outside the modal: `false`;
- directly inside `<bs-modal>` under `@if (open())`: `true`;
- inside another component that sits inside the modal: `true`.

The element injector follows the template, not the DOM, so the modal's move to the end of `<body>` does not matter.
ng-spark's three form modals all put `<spark-po-form>` inside `<bs-modal>`:
- `po-form/src/spark-po-form.component.html:480-498`;
- `po-form/src/spark-po-new-modal.component.ts:52-81`;
- `retry-action-modal/src/spark-retry-action-modal.component.ts:33-72`.

## 7. Milestones (one pull request)

TDD throughout: each behaviour gets a failing test first. Only the touched test classes run along the way, and the run
over everything happens once, at the end.

- **M0.** Done 2026-11-28: S1–S5 run (§6.1), and Q1–Q7 answered (§5.1).
- **M1. Server.**
  - The kebab function and `SparkSectionIds.Validate`, wired into verify, startup and the synchronizer (Part G, Dec2,
    Dec7).
  - The served `anchor` on groups and tabs, through the HTTP-only contract modifier. The alias-form sub-query entries in
    the catalogue's copy (Dec1).
  - Tests:
    - the kebab vectors;
    - a collision between a group and a tab, between a group and a sub-query, and between two groups;
    - an odd name, and a reserved prefix;
    - an id-form sub-query;
    - the synchronizer's throw, and startup's throw;
    - `anchor` served by `/spark/types` but absent from a synchronized model file.
  - No new endpoint, so the route snapshot does not move. No golden covers the served types (S3).
- **M2. ng-spark ids.**
  - The presenter and form templates (Part C) render `anchor`. Ids are on by default and off inside `<bs-modal>` (Dec4).
  - The tabs get `[tabId]`, and each pane's content gets a wrapper carrying the id and `data-spark-tab`.
  - The sub-query cards get their alias.
  - Vitest: the ids are present on the three pages; absent in the three modals and in `compact` mode; the default tab and
    the ungrouped card have none.
- **M3. ng-spark scroll** (Part E, Dec8).
  - `SparkViewportScroller` in `provideSpark`, browser only. It covers:
    - finding the anchor inside `#spark-main`, with the fallback when there is no shell;
    - the bounded wait for the anchor;
    - `spark-reveal`, which makes the presenter or form set `activeTab`;
    - the action bar's offset;
    - re-aligning until the page settles, and stopping at the user's first input;
    - reduced motion;
    - the outline (Dec5);
    - the first anchor after hydration.
  - `withInMemoryScrolling({ anchorScrolling: 'enabled' })` in every app's `provideRouter`: CodeCoverage, DemoApp,
    Fleet, HR, MintPlayer, QnA, SeoTester, SparkId and SqlDemo.
  - Vitest, with the scroll APIs stubbed (S1). Router-driven cases go through `RouterTestingHarness`: a fragment on load,
    a fragment-only change, and the same fragment again.
- **M4. The fragment through rebuilt URLs.** The three vanity guards and CodeCoverage's two absolute `HOME_URL`
  redirects, each with a spec.
- **M5. CodeCoverage's bell.** `#account-repositories`, with `RulesetNoticeBellTests`.
- **M6. E2E.**
  - HR Person `#career`: the Employment tab is shown and the Career card is in the viewport.
  - HR Company `#company-people`: the card is in the viewport.
- **M7. Docs** (Part H), and this file's log.

Versions come from tags. ng-spark and the NuGet packages get a minor from the engine, and nobody bumps one by hand.

## 8. Log

- **2026-11-28. Investigation.**
  - Three read-only lookups ran in parallel: the ng-spark rendering, the model and validation, and the URLs, router and
    tests.
  - The load-bearing claims were re-read by hand:
    - the presenter and detail templates;
    - the default tab;
    - the tab control's `activeTab` and `tabId`;
    - the vanity guards;
    - the shell's scroll container;
    - the synchronizer's copying of tabs and groups, and its duplicate-attribute throw;
    - `SparkQueryAliases.Derive`;
    - the sub-query entry type;
    - the R2-L4 interceptor.
  - Branch `feat/issue721-section-fragment-links` was cut from master `c7620f9d1`.
- **2026-11-28. Spikes S1–S4** (owner: "Go ahead"). Results in §6.1.
  - S1 and S4 ran as one throwaway vitest spec in ng-spark. It was deleted after the run and never committed.
  - S3 was a read-only lookup. Its served-but-not-written precedent was re-read by hand.
  - S2 ran read-only on production through `playwright_node`.
  - Consequences:
    - Q1 now recommends A2 through an HTTP-only contract modifier.
    - Part E gains the action bar's offset, the bounded re-alignment and `NavigationSkipped`.
    - Part F gains CodeCoverage's two absolute `HOME_URL` redirects.
- **2026-11-28. Decisions.**
  - The owner answered Q1–Q7 one at a time (§5.1). Two answers went against the recommendation: Dec4, ids on by default
    with modal detection; Dec5, a brief outline.
  - During the questions the owner added "use the angular fragments feature", which became Dec8. Part E now rests on
    the router's anchor scrolling and an ng-spark `ViewportScroller`. Angular 22.2.2's `RouterScroller` and the
    `ViewportScroller` API were read in `node_modules` to confirm it.
  - S5 (a throwaway vitest, deleted) confirmed Dec4's modal detection.
  - Milestones M1–M3 were updated to match.
