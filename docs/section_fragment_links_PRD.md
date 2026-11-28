# Object pages: DOM ids on sections, and fragment links that reach them (#721) — PRD and plan

Issue #721 (enhancement). One pull request. This file holds the findings, the options, the owner's questions, the spikes
and the milestones; the decisions and the log follow as they are made. Paths are under `libs/node_packages/ng-spark/`
unless they start with `apps/`, `libs/spark/`, `libs/model/`, `libs/authorization/`, `tests/`, `docs/` or `node_modules/`.

Status: **draft, 2026-11-28.** Nothing is decided yet. §2 holds the investigation: three read-only lookups, with every
load-bearing claim re-read by hand. It corrects four points of the issue (F2, F6, F7, F5). §5 holds the owner's questions
and §6 the spikes; none has run yet.

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
- **The control makes ids of its own,** with the prefix `bs-tab-control` (`fesm:83`). Their exact format lives in
  `@mintplayer/ui-core`, which nobody has read yet (S1).

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
- **Not confirmed:**
  - CodeCoverage's static `redirectTo` routes (`app.routes.ts:19, 28, 57`). Angular normally keeps the fragment there,
    but nothing tests it (S4).
  - The last hop of an external login.

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
  synchronizer never writes `anchor` back (S3).
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
- **D2. On by default,** switched off in a modal by injecting `BsModalComponent`. Nobody has confirmed that this works
  (F9), and it misses modals that apps build themselves.

### Part E: the scroll

- **The helper.** A small ng-spark helper (working name `sparkRevealSection`), used by the three routed pages. It reads
  `ActivatedRoute.fragment` as a signal. Once the page has loaded and a fragment is set, it:
  1. finds the section by id **within the page's host element**, not the whole document;
  2. opens the section's tab when it sits on one. The tab is derived from the model: a group's `tab`, or the default
     tab; a tab is its own. The tabs get `[tabId]="tab.id"`, and the control's `activeTab` is set;
  3. after the next render, calls `scrollIntoView({ block: 'start', behavior })`. `behavior` is `'smooth'`, or `'auto'`
     under `prefers-reduced-motion: reduce`.
- **Edge cases:**
  - An unknown fragment does nothing.
  - A group without visible attributes renders no card (F1), so its fragment does nothing either.
  - On the server nothing runs (F11).
  - A fragment-only navigation to another section re-runs the helper.
  - The same fragment clicked twice is a no-op for the router (S4). That is accepted.
- **Late layout shifts (F4) and the sticky bar (F5).** S2 decides between two approaches:
  - one scroll after the first render;
  - a bounded re-scroll while the content above the target still grows, stopped by the user's first wheel, touch or key.

  S2 also decides whether a `scroll-margin-top` is needed so that the sticky action bar does not cover the section.
- **A brief highlight of the target: Q5.**

### Part F: the fragment through rebuilt URLs

- **The three vanity guards** pass `fragment: route.fragment ?? undefined`, each with a spec case.
- **CodeCoverage's static `redirectTo` routes** get one spec case each, once S4 says how they behave.
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

- **Q1. Where is the id computed?** Recommended: **A2** if S3 shows the served type can carry `anchor` without the
  synchronizer writing it back; **A1** otherwise. A3 adds a field that nothing needs.
  *Trap:* A2 changes the wire contract of every type, and a custom client of the protocol would see the new property.
  That is allowed while Spark is in preview.
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

## 6. Spikes

| # | Question | Where | Done when |
|---|---|---|---|
| S1 | Does an element inside `bs-tab-page` content keep its id once the control moves it into the pane, and appear exactly once in the DOM? Does setting `activeTab` to a page's `tabId` show that page? What format do the control's own ids have, and can a kebab id clash with them? Does jsdom lack `scrollIntoView`, and how do the existing specs stub DOM APIs? | `node_modules/@mintplayer/ng-bootstrap` and `@mintplayer/ui-core`, read-only, plus a throwaway vitest spec in ng-spark that is not committed | Each answered with file:line, and the throwaway spec's output recorded |
| S2 | On a real page, where does the target land and when does it stop moving? On coverage.mintplayer.com's account page for MintPlayer (eight group cards, then three query cards): the `account-repositories` card's offset inside `#spark-main` at the first render of the cards, after the labels load, and after the query cards have loaded their rows. Does `scrollIntoView({ block: 'start' })` leave it under the sticky action bar? | `playwright_node`, read-only: navigation, an init script that records the offsets, no clicks that write | The offsets recorded. Part E's trigger chosen (one scroll, or a bounded re-scroll), and whether `scroll-margin-top` is needed |
| S3 | How are entity types served to the client (endpoint, serializer, the DTO or `EntityTypeDefinition` itself), and how does the synchronizer write the file? Can a group or tab carry a computed `anchor` on the wire but not in the file? Can a sub-query entry be served in its alias form while the file holds an id? | `libs/spark`, read-only | Answered with file:line. Decides Q1 between A1 and A2 |
| S4 | Router behaviour. (a) Do CodeCoverage's static `redirectTo` routes keep the fragment? (b) Does a fragment-only navigation on the same object page reuse the component, with `ActivatedRoute.fragment` emitting? (c) Is the same fragment navigated twice ignored? | A throwaway vitest with `RouterTestingHarness`. Case (a) becomes a committed spec if the fragment survives | Three answers recorded |

## 7. Milestones (one pull request)

TDD throughout: each behaviour gets a failing test first. Only the touched test classes run along the way, and the run
over everything happens once, at the end.

- **M0.** S1–S4 run, and Q1–Q7 answered (§5.1 once written).
- **M1. Server.**
  - The kebab function and `SparkSectionIds.Validate`, wired into verify, startup and the synchronizer (Part G).
  - Under A2, also the served `anchor` and the alias-form sub-query entries.
  - Tests: the shared vectors; a collision between a group and a tab, a group and a sub-query, and two groups; an odd
    name; a reserved prefix; an id-form sub-query; the synchronizer's throw; startup's throw.
  - No new endpoint, so the route snapshot does not move. Under A2, any golden of a served type moves in the same commit.
- **M2. ng-spark ids.**
  - The presenter and form templates (Part C); the `sectionIds` input on both; the routed pages opting in (D1); the tabs
    getting `[tabId]`.
  - Vitest: the ids are present on the three pages, absent in the modals and in `compact` mode, and the default tab has
    none.
- **M3. ng-spark scroll.**
  - `sparkRevealSection` in the three routed pages: opening the tab, reduced motion, fragment changes, an unknown
    fragment.
  - Vitest, with `scrollIntoView` stubbed.
- **M4. The fragment through rebuilt URLs.** The three vanity guards, plus S4's `redirectTo` cases, each as a spec.
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
