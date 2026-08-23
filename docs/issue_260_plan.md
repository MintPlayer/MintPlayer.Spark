# Plan — `TriggersRefresh` + `OnRefreshAsync`: a form that reshapes itself

**PRD:** `docs/issue_260_PRD.md`
**Issue:** #260
**Branch:** `feat/issue-260-triggers-refresh` (from `master` @ `aadd54c`)
**Release:** `10.0.0-preview.64`, `@mintplayer/ng-spark@22.5.0`

**Commit policy:** one commit per milestone, referencing #260. The full suite runs once, at M11.
Intermediate milestones are verified by build, by type-check and by reading.

**Sequencing note.** The spikes run first and three of them are gates: S1 decides whether D-C's overlay is
viable at all, S3 decides whether D5's re-run-on-save is affordable, and S5 decides whether R20 (AsDetail
triggers) ships in this PR or moves to Out of scope. M1-M4 are server-side and independently testable; M5
is the enforcement change and is the riskiest single milestone; M6-M8 are the client; M9 is the demo, which
is also the only place the whole path is exercised end to end.

---

## Milestones

| | Milestone | Requirements | State |
|---|---|---|---|
| **S1** | Overlay reactivity without re-running the option effect | D-C, F10 | pending — **gate** |
| **S2** | `[NoInterfaceMember]` + reflection dispatch on a generic hook | D1, R3 | pending |
| **S3** | Cost of re-running the hook during Save | D5, R13 | pending — **gate** |
| **S4** | Value-merge semantics against a real form | D-D, R15 | pending |
| **S5** | A trigger inside an AsDetail row | R20 | pending — **gate** |
| **S6** | Option replacement for a `LookupReference` | R6, D8 | pending |
| **M1** | `triggersRefresh` on the schema, preserved by synchronize | R1, R2 | pending |
| **M2** | `OnRefreshAsync` + `SparkRefreshArgs<T>` + dispatch | R3, R4, R5 | pending |
| **M3** | `POST /spark/po/{objectTypeId}/refresh` | R7, R11 | pending |
| **M4** | Authorization, row security, redaction, rate limiting | R8, R9, R10, R21 | pending |
| **M5** | Effective-rule enforcement on Save | R12, R13 | pending |
| **M6** | Client: overlay, merge, coordinator | R14, R15, R16, R17, R18 | pending |
| **M7** | Client: option replacement + rule evaluation | R6, R19 | pending |
| **M8** | Client: AsDetail triggers | R20 | pending — gated on S5 |
| **M9** | Fleet sample | R23 | pending |
| **M10** | `--spark-verify-model` gate | R24 | pending |
| **M11** | Docs, AGENTS.md, versions, sweep | R22 | pending |

---

## Spikes

### S1 — can an overlay signal change what `po-form` renders without re-running the option-loading effect?

**Question.** F10: all option loading hangs off one constructor `effect` keyed on `entityType()` identity,
and `SparkService` caches nothing. D-C assumes a second signal (`refreshOverlay`) folded into the
`editableAttributes` computed changes rendering while leaving `entityType` untouched — so the effect never
re-runs. Does Angular's signal graph actually behave that way here, given the effect reads `entityType()`,
`parentId()` and `parentType()` and nothing else?

**Method.** In `spark-po-form`, add a throwaway `refreshOverlay` signal and fold it into
`editableAttributes`. Mount the component in vitest with a mocked `SparkService` counting calls to
`getEntityTypes` / `executeQuery` / `getLookupReference` / `getPermissions`. Set the overlay to flip one
attribute's `isVisible` and `isRequired`. Assert the rendered field set changes and the mock counters are
**unchanged from their post-init values**.

**Negative result →** the overlay cannot be a sibling signal. Fall back to splitting the constructor effect
into an option-loading effect keyed on a narrow `optionSources = computed(...)` derived from `entityType`,
and only then apply the overlay. That is strictly more work but does not change the wire protocol, so M1-M5
are unaffected. If *that* also fails, D-C collapses and the client applies refreshes by re-setting
`entityType` with an option cache in front of `SparkService` — which is a much larger change and would
justify re-scoping the PR.

### S2 — does reflection dispatch work for a hook whose argument is generic in the entity type?

**Question.** The `[NoInterfaceMember]` precedents (`GetDefaultIncludes`, `StreamItems`) are dispatched via
`ReflectionCache.GetOrAdd` with `GetMethod(name, Type.EmptyTypes)` or a fixed signature. `OnRefreshAsync`
takes `SparkRefreshArgs<T>`, where `T` is the entity type the actions class closes over. Does
`actions.GetType().GetMethod("OnRefreshAsync")` resolve unambiguously on a `PersonActions :
DefaultPersistentObjectActions<Person>`, and can the args instance be constructed and invoked without a
closed-generic `MakeGenericMethod` dance?

**Method.** A throwaway xunit test constructing `PersonActions` through `ActionsResolver.ResolveForType`,
resolving the method, building `SparkRefreshArgs<Person>` and invoking it; assert the override runs and the
base does not. Also assert the resolution result caches (two calls, one `GetMethod`).

**Negative result →** dispatch through a non-generic `ISparkRefreshDispatch` shim that
`DefaultPersistentObjectActions<T>` implements explicitly, keeping the public hook generic and the dispatch
surface non-generic. Slightly more machinery, same public API, no change to D1.

### S3 — how expensive is re-running `OnRefreshAsync` during Save?

**Question.** D5/R13 make Save run the hook for every attribute declaring a trigger, so enforcement does not
depend on the client. F17 says refresh handlers are chatty enough that Fleet had to lift RavenDB's
30-request session cap inside one. Does a Save on a type with several triggers blow the cap, and what does
it cost in wall-clock?

**Method.** Instrument a Fleet-shaped fixture: `Car` with three triggering attributes whose hook does one
`Session.Load` each. Save it. Count session requests and measure the delta against the same Save with the
hook absent. Then repeat with the hook doing no I/O, to separate framework overhead from handler cost.

**Negative result →** if the cap is the problem, lift it in the Save path as `ExecuteCustomAction` does
(`IgnoreMaxRequests(estimated, logger)`) — cheap and already precedented. If wall-clock is the problem,
narrow R13: run the hook once per Save rather than once per triggering attribute, passing a `null`
`Attribute` to mean "establish the full state" — which the idempotency contract (D-F) already makes
well-defined, and which is arguably the better design regardless. **Decide this in the spike, not during
M5.**

### S4 — does the sent-value comparison actually preserve in-flight typing?

**Question.** D-D/R15 preserve a user's concurrent edit by comparing the server's value against a snapshot
of what was *sent*, not against what is displayed (F12). Vidyano needs a per-attribute DTO snapshot and a
`#refreshServiceValue` backup to make this work. Is the flat `formData` snapshot enough in Spark, given
values are plain scalars and arrays rather than attribute objects?

**Method.** vitest against a mounted `po-form`: dispatch a refresh for A with a deferred mock response;
while it is pending, type into B and into C; resolve with a response that changes C but not B. Assert B
keeps the typed value and C takes the server's. Repeat with array-valued (multi-reference) B to check
reference equality does not produce a false "unchanged".

**Negative result →** snapshot per attribute with a structural comparison for arrays and objects rather
than `===`. Contained entirely within M6.

### S5 — can a trigger inside an AsDetail row work without destroying focus?

**Question.** R20. Inline detail cells bind `[(ngModel)]="row[col.name]"` and mutate the row object in
place; rows are `track $index`. A refresh that returns a replaced `objects` array would destroy and rebuild
every row's DOM and take focus with it. Can the overlay be applied to `asDetailTypes()` — a *different*
signal from `entityType()` — and the value merge applied per-cell without replacing the row array?

**Method.** Build the smallest real case in HR (`Person.Jobs`, which is already `[Sortable]` + inline) with
a trigger on `CarreerJob.ProfessionId`. Drive it in vitest: focus a sibling cell, trigger the refresh,
assert `document.activeElement` is unchanged and the row array is the same reference.

**Negative result →** R20 moves to Out of scope for this PR and M8 is dropped. The server side is
unaffected — `triggeredBy` already carries the `{attr}[{index}].{col}` path — so a later PR is additive.
**This is the one milestone allowed to fall out of scope**, because it is the only requirement whose
failure mode is a worse experience than not having the feature at all.

### S6 — can a `LookupReference`'s options be replaced from the overlay?

**Question.** R6/D8. Lookup values reach the client from `/spark/lookupref/{name}` into
`lookupReferenceOptions`, a signal the option effect owns. Can the overlay supersede an entry there per
attribute without the effect clobbering it on the next run, and does `bs-select` re-render when it does?

**Method.** vitest: seed `lookupReferenceOptions` via the effect, apply an overlay replacing one
attribute's options, assert the rendered `<option>` set changes and that a subsequent unrelated
`formData` write does not restore the original set.

**Negative result →** the overlay's option list wins by being read *at the point of use* in the template
(`optionsFor(attr)` helper) rather than by being merged into the signal — which is simpler anyway and is
the likely outcome. Contained within M7.

### Not spiked: the wire protocol shape

D-G is a POST of an existing model type through an existing envelope helper to an existing endpoint group.
Nothing about it is uncertain, and no design decision hangs on measuring it.

### Not spiked: `ModelSynchronizer` preservation

F19 establishes the mechanism by reading the code: the update branch mutates the existing attribute object
and reassigns a fixed field list. `TriggersRefresh` is preserved because it is absent from that list. That
is a property of code that already exists, so it is an **assertion (A1)**, not a question — a spike would
just be the test written early.

### Not spiked: whether the demo scenario is realistic

D9/F24 settle it from the code. `CarActions` already branches on `Status` + `IsValueChanged` and already
claims to lock the record.

---

## M1 — `triggersRefresh` on the schema, preserved by synchronize

**Files:** `libs/spark/MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs` (add `bool? TriggersRefresh`
to `EntityAttributeDefinition`, XML doc modelled on `ReferenceDisplayType`'s *"Hand-set in the model JSON
and preserved across synchronize"*); `libs/spark/MintPlayer.Spark/Services/ModelSynchronizer.cs` (**do not**
assign it in the update branch; seed nothing in the create branch);
`libs/node_packages/ng-spark/models/src/entity-type.ts` (mirror as `triggersRefresh?: boolean`);
`libs/spark/MintPlayer.Spark.Abstractions/Model/ModelFileShape.cs` (verify it is **absent** from
`StructuralAttributeFields`).

| Test | Pins |
|---|---|
| `TriggersRefresh_survives_synchronize_on_an_untouched_attribute` | A1 |
| `TriggersRefresh_survives_synchronize_when_dataType_is_rewritten` | A1 — **the discriminator; the trivial case passes either way** |
| `TriggersRefresh_survives_synchronize_when_order_is_reassigned` | A1 |
| `TriggersRefresh_is_not_part_of_the_structural_model_hash` | A2 |
| `Adding_triggersRefresh_to_a_demo_model_leaves_verify_green` | A2 |

## M2 — `OnRefreshAsync` + `SparkRefreshArgs<T>` + dispatch

**Files:** new `libs/spark/MintPlayer.Spark/Actions/SparkRefreshArgs.cs`;
`libs/spark/MintPlayer.Spark/Actions/DefaultPersistentObjectActions.cs` (the `[NoInterfaceMember] virtual
Task OnRefreshAsync`); new `libs/spark/MintPlayer.Spark/Services/RefreshInvoker.cs` (reflection dispatch via
`ReflectionCache.GetOrAdd`, keyed `("RefreshInvoker.OnRefreshAsync", actionsType)`, shaped on
`ReferenceResolver.cs:123`). `IPersistentObjectActions<T>` is **not** touched.

| Test | Pins |
|---|---|
| `A_legacy_hand_written_actions_implementer_still_compiles` | A3 — compile-only fixture |
| `OnRefreshAsync_is_invoked_once_with_the_named_attribute` | A4 |
| `OnRefreshAsync_receives_a_null_attribute_when_triggeredBy_is_unknown` | A5 |
| `OnRefreshAsync_is_not_invoked_when_the_actions_class_does_not_override_it` | A4 — cheap, and pins the reflection cache's null result |
| `The_resolved_method_is_cached_per_actions_type` | S2's finding, made permanent |

## M3 — `POST /spark/po/{objectTypeId}/refresh`

**Files:** new `libs/spark/MintPlayer.Spark/Endpoints/PersistentObject/Refresh.cs` (`IPostEndpoint,
IMemberOf<PersistentObjectGroup>`, `Path => "/{objectTypeId}/refresh"`, `Configure` adding
`RequireAntiforgeryTokenAttribute(true)`, returning `ClientResult.Envelope`); new
`RefreshPersistentObjectRequest` beside `PersistentObjectRequest.cs`; `Services/EntityMapper.cs` — no change
needed for the flag (D-A), but the scaffold path is the one this endpoint builds on.

The rebuild-from-model step (R10) lives here and is the Spark analogue of Vidyano's `Rematerialize`:
scaffold from `IEntityMapper.GetPersistentObject(objectTypeId)`, copy **only** `Value` and `IsValueChanged`
from the wire per attribute matched by name, discard everything else the client sent.

| Test | Pins |
|---|---|
| `Refresh_returns_the_object_reshaped_by_the_hook` | A6 |
| `Refresh_ignores_metadata_submitted_by_the_client` | A11 — **the discriminator for R10** |
| `Refresh_without_an_antiforgery_token_is_rejected` | A12 |
| `Refresh_emits_client_operations_through_the_envelope` | R11 |
| `Refresh_replaces_lookup_options_and_reassigns_a_reference_query` | A7 |

## M4 — authorization, row security, redaction, rate limiting

**Files:** `Endpoints/PersistentObject/Refresh.cs` (the gates); modelled line by line on
`Endpoints/Actions/ExecuteCustomAction.cs`, which is the only existing endpoint that both loads an existing
row and then acts on it.

Order, non-negotiable: type-level right (`New` when the submitted object has no id, `Read` when it has one)
→ for an existing row, load through `databaseAccess.GetPersistentObjectAsync(entityType.Id, id)` using the
**route's** `objectTypeId`, never the wire's → run the hook → `rowSecurity.RedactAsync` on the result →
envelope. Refusals go through `ClientResult.EnvelopeRefusal`.

| Test | Pins |
|---|---|
| `Refresh_of_a_new_object_requires_the_New_right` | A8 |
| `Refresh_of_an_existing_row_requires_the_Read_right` | A8 |
| `Refresh_of_a_row_excluded_by_the_row_filter_is_indistinguishable_from_not_found` | A9 |
| `Refresh_redacts_protected_attributes` | A10 — **the discriminator against a redaction bypass** |
| `Refresh_loads_by_the_route_type_not_the_submitted_one` | security sweep C3 |
| `Refresh_honours_the_rate_limiter_path_prefixes` | R21 |

## M5 — effective-rule enforcement on Save

**The riskiest milestone.** Gated on S3, whose outcome decides whether the hook runs once per Save or once
per triggering attribute.

**Files:** `libs/spark/MintPlayer.Spark/Services/ValidationService.cs` (an overload validating against a
supplied effective attribute set; the existing `Validate(PersistentObject)` keeps its signature and
delegates); `Endpoints/PersistentObject/Create.cs` and `Update.cs` (build the effective object, run the
hook, validate against the result); `Services/RefreshInvoker.cs` (reused).

| Test | Pins |
|---|---|
| `Save_enforces_a_rule_imposed_only_by_the_refresh_hook` | A13 — **the criterion the feature lives or dies on** |
| `Save_enforces_it_for_a_client_that_never_called_refresh` | A13 — the actual discriminator; the previous test passes even in a broken design |
| `Save_accepts_a_value_violating_a_model_rule_the_hook_removed` | A14 |
| `Validation_is_unchanged_for_a_type_with_no_triggering_attributes` | A15 |
| ~~`Omitted_attributes_are_validated_as_absent`~~ | Dropped: the behaviour change it was written for does not exist. The old path already read `Attributes.FirstOrDefault(...)?.Value`, so an omitted attribute already validated as null — scaffolding yields the identical null. See the PRD's retracted Migration item 1. |

## M6 — client: overlay, merge, coordinator

Gated on S1 and S4.

**Files:** `libs/node_packages/ng-spark/po-form/src/spark-po-form.component.ts` — add `refreshOverlay`
signal + `AttributeOverlay` type; fold it into `editableAttributes`; give `onFieldChange` its missing
`attr` parameter and thread it through all 11 template call sites in
`spark-po-form.component.html` (lines 55, 70, 124, 134, 140, 156, 162, 177, 185, 334, 344) and the four
`.ts` write paths (`onReferenceValueChange`, `onLookupValueChange`, `onReferenceTreeChange`, the
`valueChange` callbacks in `getEditRendererInputs` / `getAsDetailCellEditRendererInputs`); new
`libs/node_packages/ng-spark/po-form/src/refresh-coordinator.ts`;
`libs/node_packages/ng-spark/services/src/spark.service.ts` — a `refresh()` alongside `update()`, and widen
`postWithEnvelope`'s body type to carry `triggeredBy` rather than casting.

| Test | Pins |
|---|---|
| `Applying_a_refresh_issues_no_additional_service_requests` | A16 — **the discriminator for D4/F10** |
| `A_concurrent_edit_survives_when_the_server_did_not_change_it` | A17 |
| `A_server_changed_value_wins_over_a_concurrent_edit` | A18 |
| `Two_rapid_changes_produce_one_in_flight_request` | A19 |
| `A_superseded_response_is_discarded` | A19 |
| `A_text_input_refreshes_on_blur_not_on_keystroke` | A20 |
| `A_select_refreshes_immediately` | A20 |
| `Save_awaits_a_pending_refresh` | A21 |
| `Fields_stay_editable_and_focused_during_a_refresh` | A22 |
| `A_form_inside_the_retry_modal_refreshes_independently` | A27 — the coordinator is per-instance, not a service |

## M7 — client: option replacement + rule evaluation

Gated on S6.

**Files:** `spark-po-form.component.ts` — an `optionsFor(attr)` / `lookupOptionsFor(attr)` indirection that
prefers the overlay; a small rule evaluator covering the rule types `ValidationService` implements, wired
into `hasError` / `onSave`.

| Test | Pins |
|---|---|
| `Overlay_options_supersede_the_loaded_lookup_values` | A7 |
| `Overlay_options_survive_an_unrelated_form_write` | S6's finding, made permanent |
| `A_rule_imposed_by_refresh_blocks_save_with_a_per_field_message` | A23 |
| `Rule_evaluation_matches_the_server_for_each_implemented_rule_type` | R19 — parity is the point; a client that disagrees with the server is worse than one that stays silent |

## M8 — client: AsDetail triggers

Gated on S5; **droppable**. If S5 fails, this milestone does not ship and R20 moves to the PRD's Out of
scope table with the spike's result as the reason.

**Files:** `spark-po-form.component.html` (the `#inlineDetailCell` template, lines 106-191);
`spark-po-form.component.ts` (`asDetailTypes` overlay application, reusing `inlineErrorPath`'s
`{attr}[{index}].{col}` convention for `triggeredBy`).

| Test | Pins |
|---|---|
| `An_inline_cell_triggers_a_refresh_with_a_pathed_triggeredBy` | A24 |
| `An_inline_refresh_does_not_move_focus` | A24 — **the discriminator; the addressing is the easy half** |

## M9 — Fleet sample

**Files:** `Demo/Fleet/Fleet.Library/Entities/Car.cs` (add `PoliceReportNumber`, `[IgnoreForIndex]`
following the `Manager`/`CreatedBy` precedent); `Demo/Fleet/Fleet/App_Data/Model/Car.json`
(`"triggersRefresh": true` on `Status`; the new attribute with `"isVisible": false` as its default state);
`Demo/Fleet/Fleet/App_Data/modelHashes.json` (regenerated); `Demo/Fleet/Fleet/Service/CarActions.cs` (the
`OnRefreshAsync` override, with XML doc comments matching the density of its `OnBeforeSaveAsync` sibling).

⚠️ `tests/MintPlayer.Spark.E2E.Tests` links Fleet's model files directly
(`<Include="..\..\Demo\Fleet\Fleet\App_Data\Model\*.json">`), so the model edit lands in the E2E project
too. The browser E2E is viable as written — that project already carries `Microsoft.Playwright` with a
`PageFactory` and a `FleetTestHost` — so A26 is an automated check, not only a manual one.

The scenario, chosen because it makes an existing comment true (F24): `Status = Stolen` →
`PoliceReportNumber` visible + required; `LicensePlate` and `Manager` read-only; `PromoVideoUrl` hidden.
Leaving `Stolen` restores all four — which is the idempotency contract (D-F) demonstrated rather than
merely documented, and is the half a naive sample would omit.

| Test | Pins |
|---|---|
| `Setting_status_to_stolen_reshapes_the_car_form` | A26 |
| `Leaving_stolen_restores_the_form` | A26 — the idempotency half |
| `Saving_a_stolen_car_without_a_police_report_is_rejected` | A13 end to end, through the demo's real model |
| E2E: the same three, driven in a browser | A26 |

## M10 — `--spark-verify-model` gate

**Files:** `libs/spark/MintPlayer.Spark/Extensions/SparkDevelopmentExtensions.cs` (extend the verify path);
the check walks model files for `triggersRefresh: true` and asserts the entity's actions class overrides
`OnRefreshAsync`, reusing `RefreshInvoker`'s resolution so the two cannot disagree.

| Test | Pins |
|---|---|
| `Verify_fails_when_a_trigger_has_no_OnRefreshAsync_override` | A25 |
| `Verify_passes_for_the_Fleet_demo` | A25 |

## M11 — docs, AGENTS.md, versions, sweep

**Files:** `libs/spark/MintPlayer.Spark/AGENTS.md` — **this file only**; the seven copies under `Demo/` and
`tests/` are gitignored build artifacts regenerated by `CopySparkAgentsGuide` and must not be hand-edited.
One row in the `## Actions classes` hooks table, plus a `⚠️` paragraph after it carrying the idempotency
contract (D-F) and the "it also runs during Save, so no side effects" consequence (Migration item 2). New
`docs/guide-triggers-refresh.md` following `guide-custom-actions.md`'s shape. Version bumps:
`MintPlayer.Spark.csproj` → `10.0.0-preview.64`, `libs/node_packages/ng-spark/package.json` → `22.5.0`.
`docs/release-notes-preview-64.md`.

| Test | Pins |
|---|---|
| — | A27, by review |

---

## Verification

**Discriminating checks — each must be demonstrated failing before its milestone lands.**

1. **A13's second form** (`Save_enforces_it_for_a_client_that_never_called_refresh`). Revert M5 and confirm
   RED. This is the check that justifies D5's scope; if a design without server-side re-derivation passes
   it, D5 is wrong and the PRD should be amended rather than the test weakened. The *first* form of A13 is
   not discriminating — it passes in a broken design where the server trusts submitted metadata.
2. **A11** (`Refresh_ignores_metadata_submitted_by_the_client`). Revert the rebuild-from-model step in M3
   and confirm RED. This is what makes D2's "a client cannot claim a trigger" true rather than asserted.
3. **A10** (`Refresh_redacts_protected_attributes`). Remove the `RedactAsync` call in M4 and confirm RED.
   Refresh returns a freshly scaffolded object, so redaction is not inherited from the load — this is a
   genuine new bypass surface, not a defensive test.
4. **A16** (`Applying_a_refresh_issues_no_additional_service_requests`). Implement the naive version
   (`entityType.set(...)`) and confirm RED with a count in the tens. This is the check that justifies D-C
   existing at all; without it the overlay looks like unnecessary indirection.
5. **A17/A18 together.** Implement the naive merge (`formData.set(nestedPoToDict(response))`) and confirm
   A17 RED. Then implement "never overwrite anything the user touched" and confirm A18 RED. The design is
   correct only when both are GREEN simultaneously — either one alone is satisfiable by a wrong design.
6. **A1's second form** (`..._when_dataType_is_rewritten`). The untouched-attribute case passes even if the
   synchronizer's update branch clobbers the flag, because that branch is not reached. Only the rewritten
   case discriminates.
7. **A24's second form** (`An_inline_refresh_does_not_move_focus`). If S5 passed but this cannot be made
   GREEN, M8 is dropped rather than shipped with the focus bug — a detail grid that steals focus mid-typing
   is worse than one that does not refresh.

**Full suite.** Runs once, at M11: `MintPlayer.Spark.Tests` + `MintPlayer.Spark.Client.Tests` +
`MintPlayer.Spark.E2E.Tests` + the source-generator tests, plus vitest for `ng-spark`. Record the baseline
counts before M1 so the delta is attributable. ⚠️ The suite is known flaky under load — a failure must be
re-run in isolation before being called a regression, and the E2E teardown hang (host hangs *after* tests
pass) is a known post-#277 issue, not something this PR introduced.

**Not closed by this PR.** A26's browser run is manual and must actually be performed — Fleet is served
through `UseAngularCliServer`, so `dotnet run` the host and drive it; do not run `ng serve` or `ng build`.
The last two PRs both shipped with a browser check deferred and unperformed; this one should not.

---

## Outcome

_(written after implementation)_
