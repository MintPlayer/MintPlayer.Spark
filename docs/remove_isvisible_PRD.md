# PRD: remove `IsVisible`, so hiding is `security.json` or the action class (#264)

Issue: [#264](https://github.com/MintPlayer/MintPlayer.Spark/issues/264) · Plan: [remove_isvisible_plan.md](remove_isvisible_plan.md)

**Status 2026-10-05: implemented (M0–M9) on `feat/264-remove-isvisible`; see §10 for the record, findings,
operator steps and verification.** Design final since the grill (§0). §1–§3 below are the v2 analysis, kept for
evidence. Where they differ from §0, §0 and the `G-Q*` rows of §7 win.
- **v1** of this PRD (same day) proposed new `showedOn` flags and keeping a runtime `IsVisible` flag on the wire.
  The owner rejected parts of v1 (§9), and v1 was superseded after a second investigation round. That round had
  three agents:
  - a breadcrumb prototype, with tests that fail before it and pass after
  - a prototype of hiding through the action class, verified in the browser
  - a per-attribute mapping under the owner's model
- Superseded statements are kept in §9, marked.

**Prototypes. Neither is pushed; both are folded into the one implementation PR:**
- `worktree-agent-a4b724a97914c0919` @ `1f732374`: breadcrumbs render statically denied tokens (§3.3).
- `spike/264-runtime-visibility` @ `dd2807d8`: actions hide an attribute by removing it from the PO (§3.4).

## 0. Final design after the grill (2026-10-04): this section wins over §1–§3 wherever they differ

The decision log in §7 (rows `G-Q*`) holds each choice with the owner's words. The principle is **keep the Spark API surface
small and lean, and copy Vidyano's split**: rights decide who receives a value, layout decides where it is drawn.

| Concern | Final answer |
|---|---|
| `IsVisible` | **Deleted** everywhere: model, seed, model hash, wire `PersistentObjectAttribute.IsVisible`, `QueryColumn.IsVisible`, TS models |
| Hide from a group, or from everyone | `security.json` deny; "everyone" = a deny on both `wellKnown` groups (§2.4, proven by `AttributeRightsWellKnownGroupsTests`) |
| Breadcrumbs | **Unchanged**: denied tokens stay blank (G-Q6). The breadcrumb change in `1f732374` is dropped; its well-known-group tests are kept |
| Layout for everyone | `showedOn` (Vidyano `Visibility`), gaining **only an explicit `None`** (ships on the PO, drawn nowhere; sync stops healing it). **No `QueryValue`** (G-Q4) |
| State-dependent visibility | The developer sets the **runtime `attr.ShowedOn`** (already on the wire, `PersistentObject.cs:201`) in `OnLoad`, and in `OnNew`/`OnRefresh` where state can change; the model says `None` (G-Q3). **No** `RemoveAttributes`, refresh fill, presence overlay or shape hook (G-Q2, G-Q3a). The client applies the runtime `ShowedOn`/`IsRequired`/`IsReadOnly` from the **first** render, not only after a refresh (G5/G7) |
| Write protection | `isReadOnly` or an `Edit`/`New` deny. The visibility write gate is deleted, behind a guard test (G-Q7) |
| Sorting | Unchanged engine: sort only on a grid column that the index carries. HR sorts by `FullName` (G-Q5); **D7 withdrawn** |
| Legacy `"isVisible": false` | Startup error with the replacement in the message; `true` is stripped by sync (G-Q8) |
| Library-created attributes | Libraries don't decide visibility; the seed API is deleted (G-Q16) |
| Audit fields | The developer's choice. QnA shows `CreatedBy`/`ModifiedBy` as `SparkUser` references (`{UserName}`). **No right on `SparkUser`** is needed: reference labels resolve through row security only (G-Q14/15, corrected in §10.3) |
| CodeCoverage renderer inputs | `IsPrivate` → its own narrow 🔒 column; `MyAccountRow.Type` folded into the avatar cell server-side (G-Q4) |
| **JSON schemas (new)** | Generated from the C# types (`JsonSchemaExporter`), strict with `^_` comment escape, for the 6 hand-edited files. **Build artifacts, never tracked in git.** A global revision is a git tag `schemas/v{n}` that CI applies automatically when the generated set differs from the latest release; each revision is a **GitHub release** (the archive), and `apps/SparkSchemas` (nginx, CORS `*`) rebuilds from all releases and serves `https://schemas.spark.mintplayer.com/v{n}/<file>.schema.json`. The package embeds `n`; sync writes and updates `$schema` to that URL. This repo's files reference unversioned, gitignored, locally generated `../../../schemas/<file>.schema.json` (G-Q9–Q13, G-Q17, G-Q19–Q21) |
| Gaps fixed in the same PR | G1–G4 (New/Edit/Query metadata, selection columns), G5/G7 (load-time state), G6 (errors on undrawn attributes become a form-level error, as Vidyano promotes them to a notification) |

## 1. Problem

`isVisible` (model) and `PersistentObjectAttribute.IsVisible` (wire) should not exist. Hiding an attribute is one
of three things:

| Kind of hiding | Mechanism | Owner's words |
|---|---|---|
| From a group, or from everyone | `security.json` three-segment right with `IsDenied: true` (core since #465) | the issue |
| Depends on the object's state ("only when the car is stolen") | the action class (`OnLoad`/`OnNew`/`OnRefresh`) | "That's something developers need to handle through the action-classes" |
| A field nobody should ever see | not an attribute at all (`[IgnoreProperty]`, or delete the model attribute) | — |
| A value the client needs (custom TS / renderer) but that is not drawn as a field | **layout, not security**: `showedOn` (D9) | owner, 2026-10-04: "Hiding attributes and using attribute values from typescript seems to be a different scope after all … I'll leave it up-to-you" |

**The principle (D9):** `security.json` decides **who receives** a value. The action class decides **whether this
object has** the attribute. `showedOn` decides **where** a received value is drawn. `showedOn` is never security:
anyone with the right receives the value.

Today `isVisible` also does jobs that are not hiding. Each needs its own replacement (§2.2).

## 2. Evidence (`master` @ `963cb35b`)

### 2.1 Definitions
- Model: `EntityAttributeDefinition.IsVisible` (`libs/spark/MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs:220`).
  Seed: `SparkNewAttributeSeed.IsVisible` (`libs/model/MintPlayer.Spark.Model/Model/SparkModelSatellites.cs:41-42`).
- Wire: `PersistentObjectAttribute.IsVisible` (`PersistentObject.cs:186`, converter `PersistentObjectAttributeJsonConverter.cs:132,158,208`)
  and `QueryColumn.IsVisible` (`QueryResult.cs:67`).
- TS: `ng-spark/models/src/entity-type.ts:39`, `persistent-object-attribute.ts:14`, `query-result.ts:82`, `refresh-overlay.ts:27`.
- Structural model-hash field because it is a write gate (`ModelFileShape.cs:40-46,160-185`).
- Full reader/writer inventory with file:line: v1 investigation, preserved in §9.1.

### 2.2 The jobs `isVisible` does, and their replacements

| # | Job | Where | Replacement (v2) |
|---|---|---|---|
| J1 | Draw / don't draw in the UI | ng-spark po-detail :333, po-edit :224, po-create :141, po-form :231, grid :658-662, AsDetail pipe :19, picker :67 | rights removal (group/everyone); action-class removal (state) |
| J2 | **Server write gate** (`if (!def.IsVisible) return false;`, reads the *model*) | `EntityMapper.cs:589-597`, used at :523,:547 | deleted. `isReadOnly` (everyone) or an `Edit`/`New` deny (group) |
| J3 | Ship a query value without drawing a column | `QueryResultProjector.cs:54-75`; `guide-queries-and-sorting.md:66-84` | per attribute, §3.6 |
| J4 | Per-object runtime toggle | `CarActions.cs:79,85`; `GateSettingsActions.cs:41`; `HomeActions.cs:50-51,67-68` | the action removes the attribute (§3.4); group cases go to `security.json` |

### 2.3 Two live bugs, reproduced in the browser on master (details §8)
1. **Fleet:** after Status → Stolen, the refresh reveals the required "Police Report Number". The client sends
   `PV-264-0001`, the server drops it (J2 overrides J4), and the stored car has `PoliceReportNumber: null`.
2. **HR:** `Person.LastName` (`isVisible: false` since #441, for "sortable without a column") is missing from the
   create form, and writes to it are blocked. **No Person can be created through the generic UI.** The server
   returns 400 "Last Name is required" and the page shows nothing.

### 2.4 The "everyone" group (corrected from v1)
`security.json` expresses "everyone" through its `wellKnown` block. An example is
`apps/CodeCoverage/CodeCoverage/App_Data/security.json:2-5`, which has `anonymous` + `authenticated`. Every caller
gets exactly one of the two: `authenticated` when signed in, otherwise `anonymous` (`SecurityFileAccessControl.cs:69-73`).
"Everyone" therefore means a deny on **both**. This is deliberate (#298: `anonymous` is *"Not the old Everyone"*,
`SparkWellKnownGroups.cs:16`). Proven by `AttributeRightsWellKnownGroupsTests`, all green, on the breadcrumb prototype branch:
- `A_deny_on_anonymous_only_hides_the_attribute_from_visitors_but_not_from_signed_in_callers`
- `A_deny_on_both_well_known_groups_hides_the_attribute_from_every_caller_including_admins` (an ordinary
  deny outranks ordinary grants across all groups)
- `An_important_admin_grant_outranks_the_everyone_deny` (2 cases). ⚠️ An important **type** grant (`Read/Person`) also
  unlocks every attribute denied below important level (`RightsDecision.Chain` checks the type and attribute
  probes in each tier). That is worth a line in the guide.

Cost: denials do not imply each other (only grants do: Read ⇒ Query). A `Query, PersistentObject` attribute hidden
from everyone therefore takes `QueryRead/T/A` on both groups. A combined verb counts as one right per group, so it is
**2 rights**. SPARK024 notes every type that has a partial restriction.

## 3. Design

### 3.1 D1: delete `isVisible` everywhere (owner, issue #264)
- **Removed:** the model flag, the seed, the model-hash field, the wire `PersistentObjectAttribute.IsVisible`,
  `QueryColumn.IsVisible`, and the TS counterparts.
- **Legacy model files fail loudly.** `"isVisible": false` refuses startup with a message that names the attribute
  and its replacement. `"isVisible": true` is stripped by `--spark-synchronize-model`. Silently ignoring `false`
  would draw, and accept writes into, a field the author meant to hide.
- Breaking API inside .NET 11 / Angular 22, so it ships as a **minor** bump.

### 3.2 D2: hiding from a group or from everyone is a `security.json` deny
`{ "resource": "QueryRead/Person/Salary", "groupId": "<group>", "isDenied": true }`. Behaviour since #465:
- The attribute is removed from every response and from metadata.
- Writes to it are dropped silently by `AttributeWriteShield`.
- It is not validated (`A_required_attribute_a_static_right_refuses_does_not_fail_validation`).

For everyone, see §2.4. `guide-authorization.md` gains a "Hide an attribute" section.

### 3.3 D3: breadcrumbs render statically denied tokens (owner's question, answered yes; prototyped)
**Finding.** Breadcrumbs were *already* resolved server-side before any scrub, on every surface:

| Surface | Breadcrumb resolved | Redaction | Rights removal |
|---|---|---|---|
| PO GET | `DefaultPersistentObjectActions.cs:163` | :172 | `Get.cs:90` |
| Grid rows | `RowSecurityGate.cs:209` | :217 | :226 |
| History | `SparkHistory.cs:87` → `PersistentObjectPresenter.cs:48` | :52 | :58 |

Reference chips, AsDetail rows (`EntityMapper.cs:233-243`) and save / custom-action responses take the same path.

Denied tokens came out blank only because `BreadcrumbResolver.ResolveDeniedTokensAsync` /
`EmbeddedBreadcrumbRenderer.cs:62` blanked them on purpose. That blanking was a #465 audit decision
(`contributions_PRD.md` Q14 item 3, "Blank denied or protected tokens"). It was never weighed against using
denies as the way to hide.

**Decision.** Render tokens of **static** denies. Keep blanking **per-row protected** tokens
(`GetProtectedAttributesAsync`) and every token of an unverifiable row.
- A static deny is per caller and per type, so it says nothing about any one row.
- The template is a developer-authored projection in model JSON, visible in review.
- The per-row hook is the only per-row confidentiality mechanism, so voiding it is not an option.

**No oracle.** Search only matches breadcrumb text on the in-memory fallback (`QueryExecutor.cs:1959-1964`), and
that text is already in the response. Distincts (`:211`) list labels that are displayed anyway. Nothing sorts by
breadcrumb (a reference column sorts on its `[Breadcrumb]` companion). The disclosure is exactly what is shown.

**Rejected:**
- Render per-row protected tokens too. That voids the hook.
- An opt-in flag. It keeps the HR regression as the default.
- A separate "hide" right flavour. It adds a fifth tier concept.
- Client-side breadcrumbs. The client never has the removed values.

**Evidence.** `1f732374`. Before the change 5 of 36 tests failed for the expected reason (e.g. `"Anna   Kay "`); after
it 198 of 198 passed (AttributeRights*, AttributeWrite*, RowLevelRedaction, Breadcrumb*, AsDetailRowRights,
History, AttributeVerbMatrix, ContributionsHiddenVisibility). New tests:
- `The_grid_breadcrumb_blanks_a_protected_token_only_on_the_rows_that_protect_it`
- `A_statically_denied_attribute_still_renders_in_the_embedded_rows_breadcrumb`

**Follow-up in the same PR:** an analyzer note (next to SPARK024) when a `Read`/`Query` deny names an attribute used
in a breadcrumb template. Such a deny does not hide the value from the breadcrumb, and the author should know.
Needs breadcrumb templates in the analyzer's `ModelIndex`.

### 3.4 D4: state-dependent visibility is the action class removing the attribute (owner; prototyped)
`PersistentObject.RemoveAttributes(params string[] names)` is idempotent and ignores unknown names. An action applies
one "shape" in `OnLoadAsync`, `OnNewAsync` and `OnRefreshAsync`.

**The rule is presence, not a flag:** an attribute that is not on the PO is not drawn, not posted, not validated,
and not written. Its stored value is kept, because `EntityMapper.PopulateObjectValues*` only walks posted attributes.

| Prototype change (`dd2807d8`) | Why |
|---|---|
| `CarActions.ApplyStatusShape`: Stolen → police report required, PromoVideoUrl removed, plate + manager read-only. Otherwise → police report removed. | one shape for load/new/refresh. Showing = doing nothing, because the refresh PO is scaffolded with every attribute |
| `Refresh.cs` `FillUnpostedFromStoredAsync`: for an existing object, attributes the client did not post start from their stored value; redaction + rights still run after | revealing a field removed at load must show its stored value, which the client never had |
| `SaveValidation`: attributes that were not posted are validated on their stored value | a client posting Stolen without a police report still fails |
| ng-spark: `presenceOverlay()`; `overlayFromResponse(type attrs)` hides attributes missing from a response; po-edit/po-create seed the overlay from the loaded/new PO; po-detail draws only attributes the PO carries; po-edit posts shown + refresh-revealed attributes only; refresh payload omits hidden ones | render purely from presence. This also closes v1 gap G5 (load-time flips ignored) |
| po-edit `formDataFrom` seeds every attribute, not only editable ones | a stolen car's plate is read-only at load; switching to In use then showed an empty required plate |
| `EntityMapper.IsWritableBySchema` loses `!def.IsVisible` | J2 |

**Browser evidence** (Fleet, car `1-AAL-329`), RavenDB document checked after every save:
- Load as Stolen: Police Report Number shown and required; no Promo Video.
- Status → In use: refresh has no police field; Promo Video and an editable plate are back.
- → Stolen, save `PV-264-SPIKE`: **stored** `PoliceReportNumber: "PV-264-SPIKE"` (the bug in §2.3 is fixed).
- → In use + promo URL, save: the request had no police field; the stored police value was **kept** and the promo
  URL was written.
- → Stolen again: the refresh showed the stored `PV-264-SPIKE` (the fill works). `/po/car/new` has no police field.

**Rejected:**
- (B) A wire-only `IsVisible` flag. It is a second visibility concept next to rights removal, a hidden attribute is
  still posted and validated, and every hook must set both branches.
- (C) Vidyano's per-request Visibility flag. It is (B) plus "render from the PO". (A) is the same idea expressed as
  presence, and it matches how rights already remove attributes.

**Known costs, to handle in the plan:**
- `obj[name]` throws on a removed attribute, so hooks must branch, or use a `TryGet` helper.
- Overriding `OnLoadAsync` disables batched loads (`DefaultPersistentObjectActions.cs:70-81`). Measure this, or add a
  per-object shape hook that keeps batching.
- No meaning for grid columns (per type). AsDetail per-row removal is untested.
- A load-time `IsRequired` flip is not shown until the first refresh (prototype `presenceOverlay` carries only
  presence). This is gap G7.

### 3.5 D5: the write gate is `isReadOnly` + rights + presence, never visibility
`!def.IsVisible` is deleted from `IsWritableBySchema`. Before deleting it, M2 asserts in a guard test that
**every** attribute that is `isVisible: false` today is `isReadOnly: true`, denied `Edit`/`New`, or ignored.
- Today 22 of the 24 distinct hidden attributes are `isReadOnly: true`. The other two are the §2.3 bugs, whose
  fix is "be writable".
- `ModelFileShape.cs:164-167` records the history: four attributes were once protected by `isVisible` alone, among them
  `Repository.IsPrivate`, which is now read-only.

### 3.6 D6: migration of the 24 hidden attributes
The v1 count of 25 double-counted: the Contributions row key *is* `QuestionTranslation.Key` (`ContributionDescriptor.cs:58,279-282`).
Readers were mapped with file:line by the v2 mapping agent.

**`[IgnoreProperty]` caveat (new):** it also drops the property from the `[GenerateIndex]` index
(`GenerateIndexGenerator.cs:269`). It is only possible for properties no index predicate uses.

| Attribute | v2 decision | Evidence |
|---|---|---|
| CC Account.GitHubId, Account.InstallationId, Repository.GitHubId | `[IgnoreProperty]` | entity-only readers; no index predicate. `AccountActions.cs:51-53` names InstallationId: delete that line |
| Fleet Car.CreatedBy; QnA Question/Answer CreatedBy, DeletedBy | `[IgnoreProperty]` | entity-only readers / interceptors (`HistoryInterceptor.cs:83-106`, `SoftDeleteInterceptor.cs:58-114`) |
| QnA Question/Answer ModifiedBy | `[IgnoreProperty]` | only reader is ng-spark `spark-po-edit.component.ts:550` (the conflict-dialog `showChangedBy` cross-check, off by default; when absent it is skipped) |
| CC Account.OwnerKey, Repository.PreviousFullNames | deny `QueryRead` on both well-known groups | index predicates (`ApiTokenActions.cs:123`, `GitHubIndexQueries.cs:26,32`, …) rule out ignore; no client reader |
| CC ForgeAccounts.Provider | deny `Read` on `authenticated` (the only group with `Read/ForgeAccounts`) | sole reader `MyAccountRowActions.cs:79-81` via `args.Parent`, loaded server-side unscrubbed (`SubQueryParent.cs:51`) |
| CC Home.Title, ForgeAccounts.Title | **delete** the attribute | dead: the breadcrumb is set directly (`HomeActions.cs:43`, `ForgeAccountsActions.cs:61`) |
| CC SparkUser.Id | **delete** from `SparkUser.json` | no SparkUser right exists in CodeCoverage; identity travels as `po.Id` |
| QnA QuestionTranslation.Key, QuestionTranslationsContribution.ContributorId | the Contributions generator emits `[IgnoreProperty]` (a library cannot write the app's `security.json`) | identity is `po.Id` (`EntityMapper.cs:219-220`, `AttributeWriteShield.cs:194,241`, as-detail `__sparkRowKey`); entity-only readers |
| Fleet Car.PoliceReportNumber | action class (§3.4) | prototyped |
| HR Person.LastName | `showedOn: PersistentObject` (form again, writable) + **declared sort may use a non-column attribute** (§3.7) | bug 2; `Person.json:219` sortColumns |
| CC Repository.OwnerKey, MyAccountRow.Provider | client derives it from the id (`Repositories/{provider}/{id}`, `Repository.cs:301`; MyAccountRow id becomes `{provider}/{login}`, `MyAccountRowActions.cs:46-49`), then deny on both groups | readers: `po-detail-page.component.ts:66`, `commit-files-extras.component.ts:47`, `coverage-sparkline-renderer.component.ts:59`, `short-sha-renderer.component.ts:54`, `account-link-renderer.component.ts:54` |
| CC Repository.IsPrivate, MyAccountRow.Type | `showedOn` layout (D9): `QueryValue` (value in the row, no column); IsPrivate also drops `PersistentObject` | `repo-name-renderer.component.ts:37` (lock badge, grid + detail); `account-avatar-renderer.component.ts:45` (org vs person icon when AvatarUrl is null) |

CodeCoverage `Home` AccountCount/RepoCount for anonymous visitors (`HomeActions.cs:50-51,67-68`) becomes a `QueryRead`
deny on `anonymous`. GateSettings `ProjectTarget` (`GateSettingsActions.cs:41`) becomes `RemoveAttributes` in its shape.

### 3.7 D7: a query's own `sortColumns` may name an attribute that is not a column
Today `IsSortableAttribute` → `FindQuerySurfaceAttribute` requires `ShowedOn.Query` (`ColumnCapabilities.cs:106-116`),
**before** the model-declared exemption (`QueryExecutor.cs:2606-2617`). For **server-declared** sort columns only
(`SortColumnsAreCallerSupplied == false`), the lookup accepts any attribute on the caller's rights-pruned
surface that the index carries.
- A Query-denied attribute is still refused: it is not on the pruned surface. That keeps the #294-#296 sort oracle
  closed, because the caller cannot choose a declared sort.
- This removes the last reason for the J3 shape in HR, and keeps `showedOn` a pure layout flag with its two
  existing values.

### 3.7a D9: "client needs it, nobody draws it" is layout, so `showedOn` takes it (decided by Claude, at the owner's delegation)
- `showedOn` is Spark's equivalent of Vidyano's model-level `Visibility`, and it gets that flag's missing cases:
  - **`QueryValue`**: the value ships in query rows, but there is no column.
  - **an explicit `None` (0)**: drawn on no page. On PO endpoints every received attribute already ships.
- Sync stops "healing" an explicit `None` (`ModelSynchronizer.cs:889-890`). Only an absent `showedOn` is derived.
- `QueryResultProjector.cs:54-75`: values ship for `Query | QueryValue`; columns are drawn only for `Query`.
- Sort, filter and distinct gates are unchanged (`Query` only). A declared sort uses D7.

**Why not `security.json`?** A deny removes the value from the wire, so custom TypeScript can no longer use it
(owner, 2026-10-04). **Why not keep `IsVisible`?** That boolean fused four jobs, including a write gate, and caused both
bugs in §2.3. `showedOn` is already per-page layout; this completes it.

**Prior art.** Vidyano keeps a model-level `Visibility` next to its per-group denies:
- About 23% of about 7,200 attributes across 6 apps are `Never`, and 721 are `Query`-only.
- Denies on attributes appear in only 4 of 42 apps.

So Vidyano does not rely on `security.json` alone for visibility.

**Users:** CodeCoverage `Repository.IsPrivate` and `MyAccountRow.Type`, the only two that remain after §3.6.

### 3.7b How Vidyano actually does it (decompiled `Vidyano.Service` 6.0 net10.0 with ilspycmd; 5.48 has the same logic, obfuscated)
These are behaviours only; no code was copied. Vidyano runs **two independent mechanisms**:

| | Rights (`security.json`) | `Visibility` (model or runtime, `[Flags] Never=0, Read=1, Query=2, New=4`) |
|---|---|---|
| Role | the only data protection | presentation only |
| PO, Read denied | attribute **removed**, value never sent (`VidyanoModelContext.ConstructPersistentObject`) | — |
| PO, `Never` | — | attribute **and value sent**; the client computes `isVisible` from `visibility` + isNew |
| Query, Query denied | column removed, no values (`GetColumnsByPersistentObjectId`) | — |
| Query, not `Query`-visible | — | column metadata sent with `IsHidden = true`; **item values are left out** (`ExecuteQueryHandler`: `Columns.Where(c => c.IsHidden != true)`) |
| Edit denied | attribute read-only (`PersistentObject.UpdateReadOnlyAttributes`) | — |
| New denied on a new object | read-only + `Visibility = Never` (still sent) | — |
| Save | — | **not enforced**: a hidden, non-read-only attribute accepts a posted value. Only `IsReadOnly` and rights protect |
| Validation | — | hidden required attributes are still validated; errors on invisible attributes are **promoted to an object-level notification** |

**Consequences for this PRD:**
- **D2/D3/D9 match Vidyano.** A deny removes; layout ships the value on the PO.
- **D5 matches Vidyano.** Visibility is not a write gate.
- **G6 is exactly Vidyano's notification promotion.**
- **`QueryValue` goes beyond Vidyano.** Vidyano never ships values of hidden query columns. Spark keeps it because the
  existing J3 users need it, and because shipping is still gated by `Query` rights (a Query-denied attribute never
  ships). Without it, IsPrivate/Type would have to be visible columns.
- **G1 differs.** For a New-denied attribute on a new object, Vidyano keeps it read-only and hidden. Spark removes it
  (M7). Either way it cannot be set.
- **D4 differs in mechanism, not intent.** Vidyano toggles `Visibility` per request. Spark removes the attribute, which
  also gives "not posted, not validated, not written" for free. Vidyano gets that only through `IsReadOnly`.
- **The cascade differs, by the owner's choice.** Vidyano: once a group has only attribute-level Edit rights,
  every other attribute is read-only. Spark keeps "type right required; attribute rights compose over it"
  (`contributions_PRD.md`). This is unchanged.

### 3.8 D8: attribute-rights gaps found along the way, fixed in the same PR
- **G1.** A New-denied attribute still shows on the create form. `ForFormAsync` only considers Read/Edit,
  `AttributeRightsEnforcement.cs:137-138`.
- **G2.** An Edit-denied/New-allowed attribute is read-only on create.
- **G3.** Query-denied attributes are not pruned from metadata (`visibleGridAttributes`, `spark-grid-columns.ts:21-25`).
- **G4.** `SparkSelectionResolver.cs:94` uses the unpruned definition.
- **G5.** Load-time flips are ignored by the client. Closed by D4.
- **G6.** A validation error for an attribute that is not on the form is swallowed (§8). It becomes a form-level
  error.
- **G7.** A load-time `IsRequired` flip is not shown before the first refresh (spike finding).

## 4. Non-goals
- No single "everyone" group token (#298 stands). No change to row-level redaction semantics.

## 5. Acceptance criteria
1. No `isVisible`/`IsVisible` in model, seeds, hash, wire, `QueryColumn`, or TS models. `"isVisible": false` refuses
   startup with a migration hint (red→green).
2. Fleet: Stolen + police report persists; switching back keeps the stored value; `/po/car/new` has no police field (E2E, red on master).
3. HR: the create form shows LastName and saves; the People grid is ordered by LastName and has no LastName column.
4. A statically denied token renders in breadcrumbs; a per-row protected token stays blank (the prototype tests).
5. The guard test from D5 is green; every formerly hidden attribute is migrated per §3.6.
6. G1–G7 have tests; G6 shows a form-level error.
7. Docs: `guide-authorization.md` (hide an attribute, everyone = both well-known groups, the important-type-grant
   caveat, breadcrumb rule); `guide-triggers-refresh.md` (the shape pattern, `RemoveAttributes`);
   `guide-queries-and-sorting.md:66-84` (declared sort on a non-column attribute); the stale `Right.cs:22-27`,
   `model-hash.md:40`, `guide-row-security.md:180`.
8. `npm run test:affected` green, E2E included.

## 6. Open decisions for the owner
1. ~~**Repository.IsPrivate and MyAccountRow.Type**~~: **resolved 2026-10-04 → D9.** The owner delegated it, and it
   uses `showedOn: QueryValue`. The original options:
   - (a) make it a real column / detail row ("Private", "Type"). This is the simplest.
   - (b) the server folds it into the rendered column's value, e.g. the Name cell carries `{ name, isPrivate }`.
     This needs a row-shaping hook or a computed attribute.
   - (c) a narrow `showedOn` value for "ship without drawing". This is v1's `QueryValue`, which the owner has not
     ruled on.

   Recommendation: (a) for IsPrivate, (b) for Type.
2. `OnLoadAsync` overrides disable batched loads. Is a dedicated per-object shape hook (`ApplyShapeAsync(obj)`) that
   keeps batching wanted, or is the measured cost acceptable?
3. QnA audit `*By` fields → `[IgnoreProperty]` (recommended), or shown read-only on detail?

## 7. Decision log
| # | Decision | By / date | Evidence |
|---|---|---|---|
| D1 | Delete model + wire `IsVisible` | owner (#264), 2026-10-04 | issue; owner reply rejecting the v1 wire flag |
| D2 | Group/everyone hiding = deny | owner, 2026-10-04 | `AttributeRightsWellKnownGroupsTests` (§2.4) |
| D3 | Render static-deny breadcrumb tokens, blank per-row ones | owner asked; Claude recommends; prototyped 2026-10-04 | `1f732374`, 5/36 red → 198/198 green |
| D4 | State visibility = action removes attribute | owner ("through the action-classes"); design A chosen by Claude | `dd2807d8`; browser + stored-doc evidence §3.4 |
| D5 | No visibility write gate | Claude | §2.3 bug 1; guard test in M2 |
| D6 | Per-attribute migration | Claude | v2 mapping (§3.6); `[IgnoreProperty]` index caveat `GenerateIndexGenerator.cs:269` |
| D7 | Declared sort may use a non-column attribute | Claude | `ColumnCapabilities.cs:106-116`, `QueryExecutor.cs:2606-2617` |
| D8 | Fix G1–G7 here | one-PR rule | §3.8 |
| G-Q1 | ~~State visibility = removal by presence (`RemoveAttributes`)~~ **superseded by G-Q3** | owner, grill 2026-10-04 ("A") | §3.4 |
| G-Q3 | State visibility = the **runtime `ShowedOn`** on the wire attribute (already exists, `PersistentObject.cs:201`): the model says `showedOn: None` (Vidyano `Never`), and the developer sets `attr.ShowedOn = PersistentObject` conditionally in `OnLoad`, and also in `OnNew`/`OnRefresh` where state can change. This is Vidyano's `Visibility` pattern 1:1; no `IsVisible`, no removal, no refresh fill | owner, grill 2026-10-04: "The developer can simply set the attribute visibility to Never, and use the OnLoad action-hook to show the attribute conditionally" | Vidyano §3.7b |
| G-Q3a | **Lean API surface** (owner: "keep the Spark api surface small and lean"): no `RemoveAttributes`, no refresh fill from stored values, no presence overlay, no unposted-attribute validation. The spike `dd2807d8` is kept only for its write-gate removal and its ng-spark finding (load-time state must be applied on first render, G5/G7) | owner, grill 2026-10-04 | G-Q3 makes them unnecessary |
| G-Q4 | **No `QueryValue`.** `EShowedOn` gains only an explicit `None` (= Vidyano `Never`; ships on the PO, drawn nowhere; sync no longer heals it). `Repository.IsPrivate` becomes its own narrow 🔒 column (renderer on IsPrivate); `MyAccountRow.Type` is folded server-side into the avatar cell (`MyAccountsService.cs:108-110`). Supersedes D9's `QueryValue` | owner, grill 2026-10-04 ("B") | Vidyano ships no values for hidden query columns (§3.7b) |
| G-Q5 | **No D7.** A sort is always on a column shown in the grid **and** indexed. HR `GetPeople` sorts by `FullName` (`[Search]` computed, so it has a `FullNameSort` companion, `Person.cs:23-24`) instead of `LastName`; `LastName` becomes `showedOn: PersistentObject`. The sort engine is unchanged | owner, grill 2026-10-04 ("C - Sort by column shown in the grid, and indexed in the ravendb index") | `ColumnCapabilities.cs:113`; supersedes D7 |
| G-Q6 | **Breadcrumbs keep blanking denied tokens (status quo); D3 is withdrawn.** A deny means the group never sees the value anywhere. The breadcrumb change in `1f732374` is dropped; its `AttributeRightsWellKnownGroupsTests` are kept (evidence for §2.4). HR's Person breadcrumb becomes `{FullName} @ {Company}`. The guide states that a denied token renders blank | owner, grill 2026-10-04 ("B"; "HR and Fleet are just demo applications") | no production breadcrumb depends on a hidden attribute (v2 mapping: the CodeCoverage Title attributes are dead) |
| G-Q7 | **D5 confirmed**: the visibility write gate is deleted; write protection = `isReadOnly` or an `Edit`/`New` deny (Vidyano's model). A guard test first asserts every attribute hidden on master is read-only, denied, ignored or deleted after migration. Guide line: "`showedOn` is layout; protect with `isReadOnly` or a deny" | owner, grill 2026-10-04 ("A") | 22/24 already `isReadOnly`; `ModelFileShape.cs:164-167` history |
| G-Q8 | `"isVisible": false` in a model file **fails startup** with a message naming the attribute and the replacement (`showedOn: None` + `isReadOnly`, or a deny); `"isVisible": true` is stripped by sync. Release notes call it out with the migration table | owner, grill 2026-10-04 ("A") | Q7: ignoring it would silently make hidden fields writable |
| G-Q9 | **JSON schemas for Spark's JSON files, hosted at `https://schemas.spark.mintplayer.com/…`** by a new `apps/SparkSchemas` app (Dockerfile; same VPS as CodeCoverage; DNS added by the owner 2026-10-04). External apps put that URL in `$schema`; this repo's model files reference the schema files by **relative path**, so the repo never depends on the deployed site. Editors then flag `isVisible` while typing | owner, grill 2026-10-04 | no schema exists today (`issue_348_PRD.md:96`, F10) |
| G-Q10 | **Versioned schema URLs** `…/{packageVersion}/<file>.schema.json` + `…/latest/…`; sync writes the URL for the installed package version (rewritten on upgrade); every published version stays online; the site deploy is wired to the NuGet publish | owner, grill 2026-10-04 ("A") | minors carry breaking model changes (versioning rule), so unversioned/major-only URLs drift |
| G-Q11 | Schemas are **generated** from the C# types the server deserializes (`EntityTypeFile`, `SecurityConfiguration`, …) with `System.Text.Json.Schema.JsonSchemaExporter`; a guard test fails when the committed schema differs from a fresh generation. Flags enums written as strings (`showedOn: "Query, PersistentObject"`) get a pattern via `JsonSchemaExporterOptions.TransformSchemaNode` (code, not an overlay) | owner, grill 2026-10-04 ("A") | model files are read with System.Text.Json (`SparkDevelopmentExtensions.cs:382`) |
| G-Q12 | Schemas are **strict** (`additionalProperties: false`) with `patternProperties: { "^_": {} }`, so underscore properties stay allowed as comments (10× `_comment`, 1× `_aliasComment` in this repo). `isVisible` and typos are flagged in the editor | owner, grill 2026-10-04 ("A") | the server ignores unknown properties (System.Text.Json default) |
| G-Q13 | Schemas for **every hand-edited file**: `Model/*.json`, `security.json`, `programUnits.json`, `translations.json`, `culture.json`, `actions.json`. Not for generated files (`modelHashes.json`, `oidc-signing-key.json`) | owner, grill 2026-10-04 ("B") | one generator, one exported type per file |
| G-Q14 | Audit fields are the **developer's choice**, not a framework rule. QnA (the `IAuditable` demo) **shows** them, like a Q&A site; supersedes PRD §6.3 / the `[IgnoreProperty]` row for QnA | owner, grill 2026-10-04 | QnA is a non-deployed demo |
| G-Q15 | QnA `CreatedBy`/`ModifiedBy` become `[Reference(typeof(SparkUser))]`, `showedOn: PersistentObject`, `isReadOnly: true`, rendered by the user's breadcrumb (`{UserName}`); `DeletedBy` → `showedOn: None`. Anonymous visitors get `Read/SparkUser` **plus attribute denies on everything but `UserName`**, using only shipped Spark mechanisms. **Corrected 2026-10-05 (§10.3):** no `SparkUser` right at all; reference labels resolve through row security only, and the grant is refused at startup | owner, grill 2026-10-04 ("A - yes, fully use the shipped Spark mechanisms") | ids are stored (`Answer.cs:43`, `Question.cs:57`); `ApiToken.cs:130` precedent |
| G-Q16 | Libraries do not decide visibility; the application does. The Contributions seeds go, and with them the whole new-attribute seed API (`SparkNewAttributeSeed`, `SparkModelSatellites.SeedNewAttribute`/`NewAttributeSeedFor`, `ModelSynchronizer.cs:936-940`), whose only callers were `ContributionDescriptor.cs:273,280`. QnA's model decides how `ContributorId` / `Key` are shown | owner, grill 2026-10-04 ("It's up to the application to decide what's shown in the frontend - so I guess C") | public API removal → listed in the release notes (minor) |
| G-Q16a | G-Q16 is documented where it applies: `libs/contributions/MintPlayer.Spark.Contributions/README.md:247` (seed paragraph → "the application decides how ContributorId/Key are shown"), the release notes (seed API removed), and the guide that covers model sync | owner, grill 2026-10-04 | — |
| G-Q17 | `--spark-synchronize-model` manages `$schema` in all six file kinds: it adds the versioned URL when missing and updates the version on a `schemas.spark.mintplayer.com` URL, so **the URL always matches the installed Spark version**; any other `$schema` (this repo's relative `../../..` paths) is left alone. Only the `$schema` line is touched (formatting, key order and `_comment`s byte-for-byte) | owner, grill 2026-10-04 ("A - The schema url assigned by the synchronize tool will always be in-line with the Spark version") | Q10 |
| G-Q18 | ~~**superseded by G-Q19**~~ Schemas of every revision are **committed to git** under `schemas/`; the image copies the folder and serves it statically. A guard test (in the publishing workflow too) fails when the committed latest revision differs from a fresh generation, and forbids editing a committed revision | owner, grill 2026-10-04 ("A") | a volume or NuGet-history rebuild cannot guarantee "never 404" |
| G-Q18a | **One global schema revision for the whole set**: URLs are `https://schemas.spark.mintplayer.com/v{n}/<file>.schema.json` (`model`, `security`, `programUnits`, `translations`, `culture`, `actions`). A new `v{n+1}` set is written **only when any generated schema changes**; the revision is a generated constant compiled into the package, and sync writes the revision the installed package declares. **Amends G-Q10** (URL keyed on schema revision, not package version), which also removes the `$schema` churn on upgrades that did not change the schema | owner, grill 2026-10-04 ("A - But perhaps version over-all schema files … .../v3/model.schema.json") | — |
| G-Q19 | **Schemas are build artifacts, never tracked in git.** Each revision is published as a **GitHub release** (assets = the six files), which is the durable archive, and served by the `apps/SparkSchemas` image. **Supersedes G-Q18** (git-committed revisions) | owner, 2026-10-04: "They're build artifacts, and shouldn't be tracked. The docker-container and optionally a github-release should be satisfactory" | — |
| G-Q20 | This repo's files reference **unversioned, gitignored, locally generated** schemas: the build (and sync) writes the current set to `schemas/` at the repo root (in `.gitignore`), and files use `"$schema": "../../../schemas/model.schema.json"`, so a PR validates against its own branch. Sync leaves relative paths alone (G-Q17); the guide says a model file copied from this repo needs its `$schema` line removed so sync adds the hosted URL | owner, grill 2026-10-04 ("A") | the hosted URL would make every file in a schema-changing PR (like this one) disagree until deploy |
| G-Q21 | Revisions are **git tags `schemas/v{n}` (plain counter)**, applied automatically: CI on master generates the set, compares it with the release assets of the latest `schemas/v*` tag, and on any difference tags the commit `schemas/v{n+1}`, creates the GitHub release with the six files, deploys the site and builds the package with `n+1` embedded; otherwise the package embeds `n`. A workflow `concurrency` group serialises master pushes; local builds read `n` via `git describe --tags --match "schemas/v*"`; the first run bootstraps `v1`. The release is **required** (the image build downloads every `schemas/v*` release's assets, so the site is rebuildable from scratch). `schemas/*` tags get a tag-protection rule. Amends G-Q18a (counter state = tags, not committed folders) | owner, grill 2026-10-04 ("Can we use git-tags … automatically applied whenever a schema changes", then "A") | semver rejected: apps are pinned to an exact revision by sync, so a compatibility range has no consumer, and auto-classifying JSON Schema diffs is unreliable |
| G-Q22 | **Email addresses are never shown or sent to the browser for another user.** `UserName` becomes a **public handle**: registration asks for it; a framework rule refuses any `UserName` containing `@`; an email change no longer rewrites `UserName` (`SparkUserManager.cs:50`); existing users whose `UserName` is an email get a generated handle through a one-time migration and can change it on the account page. Login keeps accepting email or user name. No new field | owner, 2026-10-05 ("email-addresses should never be shown/leaked to the browser"; chose "UserName = public handle") | QnA labels showed author emails (§10.3); registration set `UserName = email` |
| G-Q23 | **SPARK024 is silent for a deny on both well-known groups** (the documented "hide from everyone" pattern); a deny on one group only still warns. Analyzer and runtime posture note agree | owner, 2026-10-05 | 22 warnings in CodeCoverage were all deliberate |
| G-Q24 | **The application decides how users sign in**: `SparkAuthenticationOptions.SignInIdentifiers` (`[Flags] Email \| UserName`, default both). A disallowed identifier kind is refused exactly like an unknown account (same 401 body, no failed-attempt count). The capabilities endpoint publishes the allowed kinds, and the ng-spark-auth and OIDC login pages adapt their label, input type and autocomplete. The old email → user-name fallback is removed (dead since G-Q22) | owner, 2026-10-05: "username OR email - We cannot make this decision for the final application-developers" | §10.1c |
| G-Q2 | No dedicated shape hook: the developer overrides `OnLoad` (and calls `RemoveAttributes`); losing batched selection loads (`SparkSelectionResolver.cs:93`, the only batched caller) for such types is accepted; `OnQuery` is not involved | owner, grill 2026-10-04 | `DefaultPersistentObjectActions.cs:76-87`; supersedes PRD §6.2 |
| D9 | "Client needs it, not drawn" = `showedOn` (`QueryValue`, explicit `None`), never `security.json` | Claude, delegated by the owner, 2026-10-04; owner agreed ("Vidyano has the Visibility + the security.json") | owner: a deny strips TS-needed values; decompiled Vidyano keeps the two mechanisms separate (§3.7b) |

## 8. Verification on master (playwright_node, 2026-10-04)
Both apps were run with `dotnet run --launch-profile http` against the local dev RavenDB and driven in a real
browser. In each app a throwaway user (`isvisible-check@{fleet,hr}.example`) was registered and given the
`Administrators` role and group claim, the same way `SparkAppTestHost.SeedUserAsync` does it.

**Bug 1, Fleet: confirmed.** In the edit form of car `1-AAL-329`, setting Status to *Stolen* makes the refresh reveal
"Police Report Number \*" and hide "Promo Video". Then `PV-264-0001` was entered, Save was clicked, "Report vehicle
as stolen" was confirmed, and "No, skip" was chosen for notification.
- The final `POST /spark/po/update` (200) **sent** `PoliceReportNumber = "PV-264-0001"`. The response returned
  `null`, and the stored document `Cars/d05a0696-…` has `"Status": "Stolen", "PoliceReportNumber": null`.
- The save still succeeded, because the runtime `IsRequired` from the refresh is not part of the server-side
  validation on save.

**Bug 2, HR: confirmed, and worse than read from the code.**
- The People grid shows Full Name / Email / Company / Address, with no LastName column.
- The create form shows `First Name * | Date Of Birth | Email | Address | Company | Jobs | Professions`. There is no
  Last Name.
- Saving gives `POST /spark/po/create` → **400** `{"attributeName":"LastName","errorMessage":{"en":"Last Name is required."},"ruleType":"required"}`,
  and **nothing appears on the page** (G6).

## 9. Superseded (v1, 2026-10-04): kept for the record
- ~~"There is no everyone group"~~ → **corrected**. "Everyone" is a deny on both `wellKnown` groups (§2.4).
- ~~"A right cannot express per-object state, so keep a wire-only `IsVisible` (D4 v1)"~~ → **superseded by D4 v2.**
  The owner puts state visibility in the action class, and the spike shows removal-by-presence works without a flag.
- ~~"Breadcrumb tokens blank, so denies break HR/CodeCoverage breadcrumbs"~~ → **superseded by D3.** Breadcrumbs
  were already computed pre-scrub; only the deliberate blanking for static denies is dropped. The two
  CodeCoverage breadcrumb attributes turned out to be dead (§3.6).
- ~~"`showedOn` gains `QueryValue` + `None` (D2 v1)"~~ → **withdrawn as the default.** `None` is unnecessary, because
  presence and rights cover it. ~~`QueryValue` survives only as option (c) in §6.1~~ → **reinstated, narrowed, by D9 (§3.7a)**, as layout for values the
client needs but does not draw.
- ~~"`[IgnoreProperty]` for OwnerKey / PreviousFullNames / IsPrivate"~~ → **wrong.** It drops index fields (§3.6 caveat).
- ~~"25 hidden attributes"~~ → 24 distinct.

### 9.1 v1 inventory (still accurate)
- **Model JSON `isVisible: false`:** CodeCoverage 13, QnA 9, Fleet 2, HR 1, DemoApp 0.
- **Sync** writes `IsVisible` only on create (`ModelSynchronizer.cs:916,939`).
- **Contributions seeds** set `ShowedOn = PersistentObject, IsVisible = false` (`ContributionDescriptor.cs:273-282`).
- **Server readers:** `EntityMapper.cs:442,476,589-597`; `QueryResultProjector.cs:54-75`;
  `SparkComposedQueries.cs:49-58` (error text recommends `isVisible: false`).
- **Client readers:**
  - po-detail :333, po-edit :224, po-create :141/:164, po-form :231/:639-718
  - grid :658-662, `spark-grid-columns.ts:23`, `preset-filters.ts:19`
  - `as-detail-columns.pipe.ts:19`, `row-renderers.ts:30`, `as-detail-conversions.ts:284`
  - reference picker :67, conflict dialog :266/:290, retry modal :176, history `revision-diff.ts:17`
- **Tests that assert on it:**
  - `PersistentObjectAttributeTests.cs:13`, `ModelSynchronizerTests.cs:633`, `ContributionsModelSyncTests.cs:99-133`
  - `ModelFileShapeWriteGateTests.cs:57-100`, `StringPresentationColumnTests.cs:131-185`
  - `RefreshEndpointTests.cs:84-133,270,308`, `NestedRefreshEndpointTests.cs:120-122,234,397`
  - `RowLevelRedactionTests.cs:106,156,218`, `AttributeWriteEnforcementTests.cs:341`
  - E2E: `TriggersRefreshTests.cs:138-161`, `MassAssignmentTests.cs:8-24`, `AttributeWriteProtectionTests.cs:8`
  - CodeCoverage: `RemainingActionsTests.cs:126-144`, `GateSettingsTests.cs:86-146`, `ModelColumnGuardTests.cs:39`,
    `AttributeTabGuardTests.cs:129`
  - the ng-spark specs for grid, po-form, po-edit, po-create, pipes and history
- **Stale docs:** `Right.cs:22-27`, `model-hash.md:40`, `guide-row-security.md:180`, `guide-authorization.md:269-270`.
  All fixed in M9 (`d4e485b2`).

## 10. Implementation record (2026-10-04/05, branch `feat/264-remove-isvisible`)

| Milestone | Commit | What landed |
|---|---|---|
| M0 | `41d85a73` | Tests that fail on master: Fleet stolen-car E2E (`Visibility/StolenCarPoliceReportTests`), **a new HR E2E host** (`HRTestHost`, `HRE2ECollection`; `HR` added to `E2E_APPS` in `tools/test-local.mjs`; hr-host coverage entry in `tools/verify-coverage-paths.mjs`) with `HRCreatePersonTests`, G6 specs in po-create/po-edit |
| M1 | `545156a0` | Write gate deleted (`EntityMapper.IsWritableBySchema`). Guard `HiddenAttributesStayProtectedTests`: 22 must stay protected, 2 must be writable, plus a forward rule (`showedOn: None` ⇒ protected unless allow-listed). The test project's nx inputs now include the apps' model/security files. `AttributeRightsWellKnownGroupsTests` taken from `1f732374` (that file only) |
| M2 | `f97a5425` | `EShowedOn.None`; sync keeps an explicit `None`; ng-spark draws from the runtime `showedOn` and applies load-time `showedOn`/`isRequired` from the first render (G5/G7). The runtime `ShowedOn` already reached responses, so no server pipeline change was needed |
| M3 | `0c7e448b` | `IsVisible` deleted everywhere; seed API deleted; `ModelLoader.RefuseLegacyIsVisible` (startup and sync refuse `false`; sync strips `true`; `LegacyIsVisibleTests`); every shipped query column is drawn; Fleet `CarActions.ShapeForStatus` in OnLoad/OnNew/OnRefresh; HR migrated |
| M4 | `1da60fef` | G1/G2 `ForFormAsync(definition, verb)` + `GET types/{id}?for=new\|edit\|read` (narrowing; unknown = edit), ng-spark `getEntityType(id, purpose)`; G3 `visibleGridAttributes` deleted (no users); G4 `SparkSelectionResolver` uses `ForQueryAsync` (new internal ctor parameter); G6 form-level errors for undrawn attributes |
| M5 | `1ca1c92a`, `ce17e66e` | CodeCoverage and QnA migrated per §0 (details below) |
| M6 | `8310cf56` | `tools/SchemaGenerator` (not packable), an incremental after-build target writing `/schemas/` (gitignored), `SparkSchemaRevision.Current`, `SparkSchemaGeneratorTests` (JsonSchema.Net 9.4.0) |
| M7 | `4ddd72df` | `SparkSchemaReference` (line-level `$schema` management); 71 app files carry a relative `$schema`; loaders tolerate `$schema`/`_` comments |
| M8 | `d0bc7ed7` | `schema-revision` job in `dotnet-build-master.yml`; `apps/SparkSchemas` (Dockerfile, `fetch-releases.sh`, nginx); `spark-schemas-deploy.yml`, `spark-schemas-image-check.yml`; the `SparkSchemaRevision` env is an nx input of `build:release` |
| M9 | `35c43fb5`, `d33aa014`, `d4e485b2` | SPARK024 counts model-declared attributes (red→green `A_CLR_property_the_model_does_not_declare_is_not_listed`); every changed package bumped (NuGet `11.0.0-preview.97`, `@mintplayer/ng-spark` 22.28.0; no major moved); docs, a new `guide-json-schemas.md`, `release-notes-preview-97.md` |

### 10.1 Findings during implementation (each verified in code)
- **Plan premise wrong: the MyAccountRow id** is already `{provider}:{login}` (`MyAccountsService.cs:108-110`, pinned by
  `MyAccountsProviderScopeTests`). The remark that said "Login" was stale and is corrected. The client splits the id on `:`; no id change.
- **MyAccountRow.Provider/Type were removed from the model**, not denied: it is a hand-authored virtual type, so removal is leaner.
  `Type` is folded into `AvatarUrl` as `icon:group` in `MyAccountRowActions.WithAvatarFallback`; `/api/me/accounts` still uses `Type`.
- **Only declared attributes can be denied** (the validator refuses others). QnA therefore declares the SparkUser fields it denies
  (`Email`, `CreatedAtUtc`, `PreferredCulture`) and never declares credential fields.
- **Privacy (resolved by G-Q22, §10.1a):** registration sets `UserName = email` (`SparkAccountEndpoints.cs:111`). With G-Q15,
  anonymous QnA visitors see authors' email addresses. Implemented as decided (QnA is a non-deployed demo); not a pattern
  to copy until UserName and email are separate.
- **SPARK024 fired on every deliberate single-attribute deny (resolved by G-Q23, §10.1a)** (22 warnings, all CodeCoverage). Its list is now correct
  (M9). Whether a well-known-group deny should warn at all is an **open owner question**.
- **Repo-relative `$schema` depth** is `../../../../schemas/` (App_Data) and `../../../../../schemas/` (App_Data/Model), not `../../../`.
- **Schema types:** `translations.json` has no C# type, so its schema is a hand-written tree. The `culture`/`actions` loaders walk nodes,
  so mirror types are kept in step by guard tests. `TranslatedString` is a key (string|null) because the loaders refuse inline text.
- **CI:** a release created with `GITHUB_TOKEN` raises no `release` event, so the publish job dispatches the deploy workflow explicitly.
- **Behaviour changes worth knowing:**
  - the reference picker no longer lists PersistentObject-only attributes as columns (their cells were always empty)
  - AsDetail columns are per row type, so a per-row runtime `showedOn` cannot add or remove a column
  - `/po/new` still presents New-denied attributes as read-only (`New.cs:104`), while the `for=new` type omits them; the form draws from the type
  - Repository now overrides `OnLoadAsync` (GateSettings shape) and so loses batched selection loads (accepted, G-Q2)
- **Pre-existing bugs fixed on the way:**
  - QnA's load-time read-only fields had no form slot, so a refresh lifting one showed an empty field (po-edit `formDataFrom`)
  - GateSettings `ProjectTarget` showed on load in auto mode
  - `guide-queries-and-sorting.md` documented the obsolete `sortBy`/`sortDirection` query shape (now `sortColumns`)
  - `Spark-API-Specification.md` documented `showedOn` as an int (it's a string)
- **Pre-existing, not changed:** sync rewrites model files from objects, so `_comment`s in a model file it regenerates are lost.

### 10.1a G-Q22 / G-Q23 (2026-10-05): commits `0c39c341`, `39106610`, `99d6a40e`
**G-Q22, email addresses never reach the browser for another user.** Where `UserName` was set:
- registration: `SparkAccountEndpoints.cs:111`, which used `UserName = email`
- the email-change rewrite: `SparkUserManager.cs:43-61`
- external sign-up: `ExternalLoginCallback.cs:129`. A provider handle could contain `@`, and an email used as
  display name became `jane-example-com`, which still exposes the address.

Now:
- **Rule:** `SparkUserNameValidator` refuses any `@` (`UserNameContainsAt`) on every save. It was chosen over
  `AllowedUserNameCharacters` because an app's Identity options cannot switch it off.
- **Registration:** `/register` takes `SparkRegisterRequest { email, password, userName }`. A missing or blank user
  name is a 400. The ng-spark-auth register form gains a "User name" field (en/fr/nl).
- **Email change:** `SparkUserManager<TUser>` is **deleted**; Identity's own manager is used, so an email change
  never touches `UserName`.
- **External sign-up:** never uses a name containing `@` (it gets `user-` plus six hex digits instead), and appends
  `-2`, `-3`… on a collision. A collision used to fail the sign-up.
- **Rename and login:** renaming already existed (`POST /manage/profile`). Login already accepts email or user name.
- **Migration:** `M_202610051200_UserNamesAreNotEmails` (Authorization package) patches email-shaped names to unique
  handles, `NormalizedUserName` included. It is idempotent.
- **Supersedes #460 D4's rule** "a user name containing `@` must equal that user's own email" (marked in
  `issue_460_PRD.md`).
- **Breaking API:**
  - `SparkUserManager<TUser>` removed
  - `SparkClient.RegisterAsync(email, password, userName)`
  - new `SparkRegisterRequest`

  All three are in `release-notes-preview-97.md`.
- **Versions:**
  - `MintPlayer.Spark.Authorization`, `.Client` and `.Client.Authorization`: `11.0.0-preview.97`
  - `@mintplayer/ng-spark-auth`: 22.19.0
- **Leak audit:**
  - No endpoint or DTO returns another user's email; the account page shows only the caller's own.
  - QnA's author labels, history and contributor names (`QnAUserNames.cs:26`) all showed emails and now show the handle.
  - CodeCoverage's `{UserName}` labels were already safe, because GitHub logins never contain `@`.
  - ⚠️ **Not changed:** moderation's fraud notes name a shared email **domain** (not an address, webmail excluded),
    `FraudDetector.cs:148-153`.

**G-Q23.** SPARK024 (build) and the posture note (runtime) both skip a finding when the other well-known group
denies the same attributes; they read the `wellKnown` block (`SecurityJsonReader.ReadWellKnownCounterparts`,
`StaleAttributeDenials`). This is proven by 4 red→green tests. 22 CodeCoverage warnings became **3**, all deliberate
one-group denies: Home on `anonymous` (Query, Read) and ForgeAccounts on `authenticated` (Read).

### 10.1b RQL injection audit (2026-10-05): commits `4271c2e8`, `7df7272d`
Owner, on `ForgeQualifiedIdVerifier`: "Make sure nobody can inject malicious strings here. You're not escaping untrusted
inputs here".

All 30 raw-RQL and patch-script sites in `libs/` and `apps/` were audited. **No request- or caller-controlled string
reaches query text.** Spark's sort, filter and search go through LINQ with real properties and parameters.

Hardened anyway:
- **Values as parameters:**
  - `M_202609210900` prefixes → `$legacy`/`$qualified`
  - `M_202609230900` reasons → `$oldReasons`/`$newReasons`
  - HR `M_202609091210` key suffix → `$suffix`
- **Identifiers validated:** every spliced collection and field name now goes through the new public
  `RqlIdentifier.Collection` / `FieldPath` (Abstractions). It is public because Spark, Authorization, CodeCoverage and HR
  all splice identifiers, and RavenDB's `QueryFieldUtil` is internal.
- **Tests:** `RqlIdentifierTests` rejects quotes, `;`, braces, comments, newlines, `x' or true or '` and
  `") update { this.X = 1 } //`. The touched sites' tests are green (73 + 22).
- **Rule recorded:** the rule is now in the repo's `CLAUDE.md`.
- **Also fixed on the way** (`4271c2e8`): the verifier's dynamic query waits (bounded) for the auto-index it builds.
  The load-only `ForgeQualifiedIdVerifierTests` failure is proven fixed with 8 CPU burners (7/7).

### 10.1c Sign-in identifiers (2026-10-05): commit `5d616aca`
**Callers audited.** No path bypasses `SparkSignInManager.FindUserForSignInAsync`:
- Identity API `/spark/auth/login` (cookie and bearer, via the overridden string `PasswordSignInAsync`)
- OIDC `/connect/login` (the same virtual overload)
- 2FA and recovery-code steps, which use the user the password step resolved
- passkeys need no identifier
- re-authentication works on the signed-in user
- forgot/reset password and resend-confirmation stay **email-based by design**, because they are not sign-in

**Startup:** with local credentials enabled, `SignInIdentifiers = 0` refuses startup.

**Timing:** a disallowed kind returns before any lookup. It is slightly faster than an unknown account, which reveals
only what the capabilities endpoint publishes anyway.

**Tests:**
- 3 settings × email or user name × cookie or bearer → 200 or an identical 401, with `AccessFailedCount` unchanged
- an OIDC theory
- capabilities cases
- ng-spark-auth label, type and autocomplete specs
- 49/49 server, 23/23 component

**Version:** `MintPlayer.Spark.IdentityProvider` is bumped to `11.0.0-preview.97`, its first change on this branch.

**Finding:** `FindByEmailAsync` reads a compare-exchange entry (`UserStore.cs:266`), not an index, so it is consistent
right after a create. `FindByNameAsync` is an index query.

**Note:** a not-yet-migrated `@`-shaped user name can no longer sign in *by that name*; its email still works. The
migration renames those names at startup.

### 10.2 Operator steps (done 2026-10-05 with the owner's go-ahead, except where noted)
1. ✅ VPS `root@188.245.190.60`: `/var/www/spark-schemas/docker-compose.yml`, service **`spark-schemas`** (image
   `ghcr.io/mintplayer/spark-schemas:latest`, Traefik router `Host(\`schemas.spark.mintplayer.com\`)`, `websecure`,
   `letsencrypt`, port 80, external network `web`), modelled on `/var/www/karnaugh`. `docker compose config` validates.
   DNS resolves to the VPS (IPv4 `188.245.190.60` and IPv6). The VPS is already logged in to GHCR.
2. ⏳ GHCR: the `spark-schemas` package does not exist until the first image push, which only happens from `master`
   (a workflow cannot be dispatched before it is on the default branch). The deploy's best-effort step tries to
   make it public; verify after the first run and flip it in the package settings if needed.
3. ✅ Repository ruleset **24486043** "Spark schema revisions (schemas/*)": tag rules `update` + `deletion` on
   `refs/tags/schemas/*`, with a bypass for repository admins. **Creation is deliberately not restricted:** GitHub
   refuses the GitHub Actions app as a bypass actor on a repository ruleset ("must be part of the ruleset source or
   owner organization"). The history guarantee (a revision can be neither moved nor deleted) holds.
4. ✅ Workflow permissions: the repository default is already `write` (`default_workflow_permissions: write`), so the
   `schema-revision` job's `contents: write` / `actions: write` need no change.
5. The first master run after merge publishes `schemas/v1`.

### 10.3 Verification (2026-10-05)
**Local sweep** (`npm run test:affected`, Developer licence, E2E included):
1. **First run:** every project green except `MintPlayer.Spark.E2E.Tests:build`. Its second invocation failed with only
   `Misconfigured remote cache endpoint: Unexpected response status: 499` (Nx remote cache, not a compile error; the
   same target had built fine earlier in the run).
2. **Second run:** E2E executed, **32 of 152 failed**:
   - **31 QnA tests: the QnA host refused to start**, correctly. `security.json` granted `Read/SparkUser` to
     `anonymous`, and Spark's startup check refuses that because `SparkUserActions` declares no row rule, which would
     publish every user (`SparkMiddleware.cs:742`). **Fixed by removing the grant entirely:** a reference label is
     resolved through row security on the target only (`BreadcrumbResolver.cs:210`), not through a type right, so the
     `{UserName}` breadcrumb renders without anyone being able to load or list users. QnA's `SparkUser.json` now
     declares only `UserName`; the denies and their translations are gone; the posture baseline was regenerated.
     `QnAAuditFieldsTests` now asserts that visitors and other users **cannot** load a user, and that the breadcrumb
     shows the author.
   - **1 Fleet test:** `MassAssignmentTests.PUT_does_not_modify_isreadonly_attribute` targeted `Car.CreatedBy`, which
     is `[IgnoreProperty]` now. It now forges a `CreatedBy` attribute the model does not declare, and checks the
     **stored** document is unchanged (stronger than before).
3. **Third run: exit 0, all 64 tasks green.** 61 came from the Nx cache of the earlier green runs; `MintPlayer.Spark.Tests`
   and `MintPlayer.Spark.E2E.Tests` re-ran.

**Browser** (playwright_node, Fleet `dotnet run`, car `1-AAL-329`, which is stored as Stolen):
- **Detail page on load:** shows Police Report Number and no Promo Video (runtime `ShowedOn` from `OnLoad`, first render).
- **Edit form on load:** "Police Report Number \*" is already required before any refresh (G7).
- **Status → In use:** the police field is gone; Promo Video, License Plate and Manager are editable.
- **→ Stolen:** the field reappears with its **stored** value `PV-264-SPIKE`, because it ships while hidden
  (`showedOn: None`), so no refresh fill was needed.
- **Saved `PV-264-FINAL`:** the RavenDB document holds `"PoliceReportNumber": "PV-264-FINAL"` and still has the hidden
  promo URL. **§2.3 bug 1 is fixed.** Bug 2 (HR create) is covered by the browser-driven E2E test `HRCreatePersonTests`.

**Correction to G-Q15:** `Read/SparkUser` for visitors is neither needed nor allowed (above). QnA shows authors with
**no** rights on `SparkUser`. ⚠️ The label is still the email address, because registration sets `UserName = email`
(resolved by G-Q22: the label is now the public handle).
