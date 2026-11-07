# Plan: "Add new" from a Reference attribute

PRD: [reference_add_new_PRD.md](reference_add_new_PRD.md). Branch `feat/reference-add-new` (from master `5fd324b13`).
One pull request. CodeCoverage's "Set up rulesets" required-checks picker (#554) is the first consumer and is built
after this lands (branch `fix/554-required-checks-picker`).

## Resume here

Merged as PR #580 (`ae99722f1`). The #554 consumer is on `fix/554-required-checks-picker`; see "#554: the
required-checks picker" below. Ruleset 26719109 stays disabled until that ships.

## Context (why the rulesets work is waiting)

On 2026-11-06 "Set up rulesets" on MintPlayer.Spark created ruleset 2 (26719109) with 26 required checks taken from
master's head commit, push-only jobs among them ("Deploy to VPS", "build release"…), so no pull request could ever
merge. It was disabled at once and stays disabled. The fix agreed with the owner: GitHub's ruleset stays the one list
(the queue already reads it with Metadata read, `GitHubForgeMergeQueue.cs:117`); the dialog gets a "Required checks"
picker over the checks seen on pull requests, plus names the user adds; the plan prints the list; no guess from the
head commit (`MergeQueueRulesets.Defaults`, `CodeCoverage.Library/Forge/MergeQueueRulesets.cs:157`); a link to the
forge's settings page wherever the list cannot be written. The "names the user adds" part is this feature.

## Spikes

- **S1: flag and rights.** Add `canAddNewReference` to `EntityAttributeDefinition`, `schemas/model.schema.json`, the TS
  `EntityAttributeDefinition` (`ng-spark/models/src/entity-type.ts`) and the scaffolded `PersistentObjectAttribute`
  (`EntityMapper.FromDefinition`, `Services/EntityMapper.cs:513`). Decide: server sends the *effective* flag (false
  without `New` on the referenced type) vs the client calling `getPermissions`. Recommendation: server-side, as the
  prior art does, because the retry form reads attributes only.
- **S2: picker context in `OnNewAsync`.** Today `POST /spark/po/new` (`Endpoints/PersistentObject/New.cs:27`,
  `HandleStandaloneAsync` :85) scaffolds and calls `INewInvoker` (:262). Prototype a request field naming the parent
  object (type, id or the in-progress object) and the attribute, exposed on `SparkNewArgs` as properties (no new
  method). Check: the parent may be unsaved (create page) or a virtual retry object with no C# class and no id (R11),
  so the context is the parent's **type and attribute name**, verified against the model (the attribute exists, is a
  Reference, has `canAddNewReference`, and targets this type), never a loaded parent. The in-progress parent values,
  if passed, are the caller's claim, like `Parameters`. The referenced type's `New` right is the only gate.
  `INewInvoker` used to build `SparkNewArgs<T>` by reflection from a fixed positional list (`Services/NewInvoker.cs:93-116`),
  so a new property means a new constructor argument there. The `AsDetailAttribute` verification
  (`New.cs:174`) is the pattern to copy.
- **S3: the popup.** Decided (PRD D16): a standalone `spark-po-new-modal` opened through a service, like the retry
  modal, reusing po-create's payload builder (`spark-po-create.component.ts:183-226`, `SparkService.create` at
  `spark.service.ts:254`). Stacked `bs-modal`s are handled by ui-core's overlay stack (PRD D16), so no prototype is
  needed.

- **S4: referenced types without their own documents.** There are two cases.
  - **S4a, a C# class with no collection.** `ForgeCheck` (`CodeCoverage.Library/Entities/ForgeCheck.cs`) is embedded in
    `Repository.KnownChecks` and served by `ForgeCheckActions` (`CodeCoverage/Actions/ForgeCheckActions.cs`) with no
    document. Find what `/po/create` does for such a type today.
  - **S4b, no C# class (R10).** The new path is not a question. M1 makes `/po/new` run the name-resolved
    `{Name}Actions.OnNewAsync` instead of returning early at `New.cs:259`, following `OnLoadAsync` at
    `DatabaseAccess.cs:1079`, with the non-generic `SparkNewArgs` (PRD D15). The target already resolves
    through the lookup query (D9, `GetReferenceTarget`). The single picker's own lookup
    (`spark-reference-picker.component.ts:158`, by `clrType`) should resolve the same way: via the attribute's query.
    The open part is the **create**: `/po/create` throws for such a type (`DatabaseAccess.cs:228-229`), so prototype a
    create that hands the object to its actions class instead of the CLR save.

  Decided (PRD D10, D11, D18): `/po/create` confirms such an object without storing it. A value object's id is its
  `[ValueKey]`, which must be a writable model attribute (startup check).
- **S5: picker UI.** Decided (PRD D17): [+] in `bs-tree-select`'s `footer` slot and the single picker modal's footer
  (`spark-reference-picker.component.html:17`), always shown; the term in the box at click time goes as
  `Parameters["searchTerm"]` and into the Reference attribute's `defaultAttribute` before `OnNewAsync`. M1 adds
  `defaultAttribute` to the model, schema and TS definition, plus its startup check (exists on the target, String).

## Milestones (after the spikes)

- **M1 Server:**
  - Done:
    - S1/S2 (D7/D8): the flag end to end (model, schema, scaffold, effective value) and the picker context on
      `SparkNewArgs` (`ReferenceAttribute`, `ParentType`).
    - D15: `SparkNewArgs`, `SparkRefreshArgs` and `SparkDeleteRowArgs` are non-generic; every app, test and guide is
      updated.
    - R10/D15: `INewInvoker.InvokeVirtualAsync` runs the name-resolved `{Name}Actions.OnNewAsync` for a type with no
      C# class.
    - D12: `NewOwnerChain` resolves the owner and its chain (max depth 8). Stored levels are re-read with one batch of
      lazy loads; unsaved levels go through `IEffectiveObjectFactory.Build`.
    - D17: `defaultAttribute` (C# + TS) is prefilled from `Parameters["searchTerm"]` before `OnNewAsync`.
    - D9 / name resolution: `GetReferenceTarget` and the startup check resolve a Reference's `query` by **name**
      first (that is what it holds), then by id/alias.
    - D13/D17/D18: startup checks (`SparkReferences.Validate`, also in the dev-time model check without D18's CLR
      part).
    - D14: an unpickable reference (no Query right on its target) is read-only on Edit/New.
    - D11: `DatabaseAccess.SaveCoreAsync` refuses a value object.
    - D10/D18: `IPersistentObjectSavePath.ConfirmAsync`, called by `Create.cs` for a type with no class or a value
      object. It applies the create's WITH CHECK row gate (the actions class's `MapAsync`, then
      `IRowSecurity.IsAllowedAsync(New)`) before the key check. CI on PR #580 caught it missing: CodeCoverage's
      embedded row types refuse every standalone row, and the confirm skipped that refusal
      (`EmbeddedRowStandaloneWriteTests`, 400 instead of 404).
  - The solution builds (2026-11-06, 0 errors).
- **M2 Client:** the Add new button in both pickers, the popup, assigning the id (single replace, multi append, label
  from the saved breadcrumb), cancel changes nothing.
  - Done (ng-spark type-checks with `ngc -p … --noEmit`):
    - `resolveReferenceTarget` (po-form): query name first, then `referenceType`, the same as the server. The
      single picker uses it, so a target with no C# class resolves.
    - `SparkPoNewModalComponent` + `SparkPoNewModalService` (po-form). One `createComponent` per call, attached to
      `document.body`, so the popups stack and the app shell needs nothing. It runs `/po/new` with the picker context,
      `getEntityType(…, 'new')` and the overlay, then saves with `/po/create`: 400 errors are shown in place, and a
      204 keeps the popup open with "Nothing was created".
    - `spark-po-form.ownerChain()`: the form's object with its `parent` chain. A row form finds its outer form
      through DI (`skipSelf`). A popup's form gets `ownerParent`. A row or unsaved level goes without an id, and
      AsDetail values are left out.
    - Single picker: an `addNew` callback input and [+] in the modal footer. It uses the search box's live text
      (`SparkSearchBoxComponent.currentText()`) and picks the created row like a clicked one.
    - Array Reference: [+] in `*bsTreeSelectFooter`. The term comes from the textbox's bubbling `input` events plus
      `searchStart` (`props` is protected). The created row is appended with its breadcrumb as the chip label.
    - `canAddNewReference` is in the refresh overlay, so a hook can switch it off per object. The retry form's
      synthesized definitions copy it.
    - The translations `common.addNewReference` and `common.createCancelled`.
  - Known gap: a confirmed value object is not in its lookup query until the owner is saved. Its label then lives in
    the form's picked options and can be lost if a refresh replaces them.
- **M3 Demo + docs:** done.
  - HR `Person.Company` (single, `defaultAttribute: Name`) and `Person.Professions` (array,
    `defaultAttribute: Label`). `CompanyActions.OnNewAsync` hides the brand colours in a picker popup.
  - `docs/guide-reference-attributes.md`, "Add new from the picker", plus a row in the survives-sync table.
  - The C# doc and the generated schema now say "writable String".
  - The repository has no CHANGELOG file (releases come from tags, #153), so there is nothing to add there.
- **M4 Tests:** server (flag scaffolding, effective flag without `New`, context reaching `OnNewAsync`, create from a
  picker), ng-spark specs (button visibility, id assignment single/multi, cancel), one E2E: add new from a picker on a
  create page, then save the parent.
  - Done, green (2026-11-06, run once):
    - `ReferenceAddNewEndpointTests` (12): prefill and context, stored owner re-read, chain in one round trip, the four
      refusals, virtual `OnNewAsync`, effective flag, value object confirmed by key and not stored, empty key = 400,
      virtual create confirmed with a new id.
    - `SparkReferencesTests` (7): D13 query mismatch, D17 `defaultAttribute` (missing, not String, read-only, without
      the flag), D18 value-object key.
    - `spark-reference-picker.component.spec.ts` (20, 4 new): virtual target via the query, no [+] without a callback,
      created row picked with the trimmed term, cancel changes nothing.
    - `spark-po-form.component.spec.ts` (63, 5 new): `ownerChain()` (stored id, AsDetail left out, popup owner as
      parent; a row form goes without an id), a stable `addNewFor` callback only with the flag, the array [+]
      (closes the panel, sends the trimmed term and picker context, appends the created row), cancel changes nothing.
    - E2E `ReferencePicker/ReferenceAddNewTests` (1, HR, already in `E2E_APPS`): on Person's create page, the
      Company picker's [+] opens the popup with Name prefilled by the term; its Save stores the company and picks it
      while the person stays on `/new`; saving the person stores it pointing at that company.

## #554: the required-checks picker (branch `fix/554-required-checks-picker`, 2026-11-07)

Decisions (owner, 2026-11-07, in this session):

- **One prompt per selected repository** ("Show the custom-action on the query page, let the user select at least one
  repo => let the user walk through each selected repo (using a retryAction?)"). `MergeQueueRulesetSetup.ChooseAsync`
  asks one retry step per repository, titled with its full name, prefilled from that repository's
  `MergeQueueRulesetPlanner.Defaults`; each plan takes its own answers. Consecutive retry steps replay in order
  (`RetryAccessor.Action`, step counter). Evidence: `SetUpRulesetsActionTests.Each_repository_gets_its_own_prompt_and_its_own_answers`.
- **Options = checks seen on a pull request** ("Seen on pull requests (Recommended)"). `ForgeCheck.OnPullRequest` (sticky,
  never cleared by the upsert patch) is set by a `check_run` webhook whose `pull_requests` is non-empty and by the
  backfill for a pull request's head commit (not the default branch's head). The engine's own reads do not set it: the
  PR head's `ExpectedChecks` come from the current protection, which can hold the very push-only names that broke
  ruleset 26719109. A fork's runs list no pull request, so they count once the backfill sees them. Query
  `Repository_PullRequestChecks` (the manager gate of `Repository_ForgeChecks`, filtered). Evidence:
  `KnownChecksTests.A_known_name_first_seen_on_a_pull_request_is_due`, `.Seen_on_a_pull_request_sticks`,
  `MergeQueueWebhookTests.A_check_run_on_a_pull_request_is_reported_as_seen_on_one`,
  `GitHubForgeMergeQueueTests.Reported_checks_are_read_through_one_graphql_query_*`,
  `RemainingActionsTests.Repository_PullRequestChecks_offers_only_checks_seen_on_a_pull_request`.
- **Key derived from the name** (PRD D18 revised): `ForgeCheck : IHasNaturalId`, `Name` writable and required in
  `ForgeCheck.json`, `New/ForgeCheck` granted to signed-in users (`security.json` key `…00ad`); the confirm stores
  nothing. The detail page stays read-only (`Can.Edit = false`, no Edit right).
- **No guess from the head commit:** `Defaults(state)` lost its `headChecks` parameter (ruleset 2 → classic → none), and
  `ReadAsync` no longer reads the default branch's head.
- **Summary:** a ruleset 2 creation names its required checks; a blocked repository gets a line with its GitHub rules
  settings page (`rulesets.settingsPage`).
- **Client:** the retry modal passes the prompt PO's `parent` to `spark-po-form` as `referenceParentId/Type`, so a
  picker in a retry form queries under it (`spark-retry-action-modal.component.spec.ts`).
