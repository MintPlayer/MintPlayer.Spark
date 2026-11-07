# PRD: "Add new" from a Reference attribute

Status: **draft, spikes first** (2026-11-06). Plan: [reference_add_new_plan.md](reference_add_new_plan.md).

## 1. Why

A Reference picker (single `spark-reference-picker`, multi `bs-tree-select`) only offers objects that already exist.
When the value the user needs does not exist yet, they must leave the form, create the object on its own page, come
back and pick it, and lose the form's unsaved state on the way.

The first consumer is CodeCoverage's "Set up rulesets" dialog (#554). Its "Required checks" picker lists the check
names seen on pull requests, and the owner wants to add a name that is not on that list yet (owner, 2026-11-06: "I
would also let the user add a check-name himself"). A one-off "type a free value" flag was started and stopped by the
owner in favour of this general mechanism (owner, 2026-11-06: "I think we need another approach").

## 2. What (the flow)

1. A Reference attribute whose model sets `canAddNewReference: true` shows an **Add new** button beside its picker,
   when the user may create the referenced type.
2. The button asks the server for a blank object of the **referenced type** (`referenceType` → its entity type). The
   server builds it through that type's actions class: `OnNewAsync(SparkNewArgs)` (non-generic since D15)
   (`libs/spark/MintPlayer.Spark/Actions/DefaultPersistentObjectActions.cs:508`). The developer hides attributes or
   makes them read-only there, as `OnRefreshAsync` already can (`:461-465`, the #264 overlay). **No new hook.**
3. The client shows that object in a popup form. The user fills it in.
4. **Save** creates it at once through the ordinary create path (`POST /spark/po/create`,
   `Endpoints/PersistentObject/Create.cs:16`, `New` right at `:57`), and the response (201) carries the new id.
5. The client puts the id into the reference value: replaces it for a single reference, appends it for a multi
   reference. The label is the saved object's breadcrumb. The **parent form stays unsaved**: only the reference value
   changed, as if the user had picked an existing row.

Prior art (another framework, read 2026-11-06) works the same way: a model flag on the reference attribute, the
referenced type's own "new" hook builds the popup's object (it can tell it came from a picker), the object is saved
immediately, and the client then sets the reference on the still-unsaved parent.

## 3. Requirements

| # | Requirement |
|---|---|
| R1 | `canAddNewReference` (nullable bool) on a Reference attribute in the model JSON; hand-set, kept by synchronize (`ModelSynchronizer.cs:1161-1170`), not a structural hash field (`Model/ModelFileShape.cs:193-216`): it gates no write, `/po/create` checks `New` regardless. |
| R2 | The button shows only when the flag is set **and** the caller has `New` on the referenced type (`GET /spark/permissions/{type}` → `canCreate`, `Endpoints/Permissions/GetPermissions.cs:12,59`; it accepts a CLR name, `:34-35`). |
| R3 | The popup's object comes from the referenced type's `OnNewAsync`; attributes it hides or makes read-only are hidden or read-only in the popup. |
| R4 | `OnNewAsync` can tell that it runs for a picker, and from which parent object and attribute (to prefill or lock values). |
| R5 | Save creates the object immediately; a cancelled create (204, an interceptor said no, `Create.cs:90-92`) keeps the popup open with the reason; validation errors show in the popup. |
| R6 | Both reference shapes work (owner, 2026-11-06): a normal Reference (`spark-reference-picker`), whose value becomes the new id, and an array Reference (`isArray`, `bs-tree-select`), which gets the id appended. The chip/textbox shows the saved breadcrumb. |
| R7 | Works wherever `spark-po-form` renders a Reference: edit and create pages, AsDetail rows, and a retry prompt's form (the rulesets dialog). |
| R8 | A type with no document collection (CodeCoverage's `ForgeCheck`: names embedded in `Repository.KnownChecks`) can take part: see spike S4. |
| R9 | Cancelling the popup changes nothing. |
| R10 | The **referenced** type may have no C# class: a JSON-only virtual type (owner, 2026-11-06: "sometimes the PersistentObject-type … isn't backed by a C#-class"). Its popup object and its create must still run through its actions class. See spike S4. |
| R11 | The **parent** may have no C# class or no id either (a retry prompt's virtual form, an unsaved create page), so the picker context (R4) cannot depend on loading the parent. See spike S2. |

### What the code does today with a type that has no C# class (read 2026-11-06)

- `POST /spark/po/new` returns the model's bare scaffold and runs **no hook**: `ScaffoldAsync` and `InvokeHookAsync`
  return early when the CLR type resolves to null (`Endpoints/PersistentObject/New.cs:230`, `:259`). The reason is
  that `SparkNewArgs<T>` needed a `T` (fixed by D15 and `INewInvoker.InvokeVirtualAsync`).
- `POST /spark/po/create` **cannot** create one: the save throws "Could not resolve type"
  (`Services/DatabaseAccess.cs:228-229`). The 500 reaches the client.
- Precedent for these types: their load hook is resolved by the model name (`ActionsResolver.ResolveByEntityName`,
  `Services/ActionsResolver.cs:27-34`) and called duck-typed with the ordinary signature
  (`OnLoadAsync(string, PersistentObject?)`, `Services/DatabaseAccess.cs:1079-1105`). Their queries go through
  `OnQueryAsync` the same way (`QueryExecutor.cs:519`, `:805`). So the popup's object for such a type has a natural
  route, a name-resolved `OnNewAsync`. Its create has none yet: there is no document to write.
- **The `referenceType` lookups are by CLR name**, both on the server (`ModelLoader.GetEntityTypeByClrType`,
  `Services/ModelLoader.cs:246`) and in the single picker (`types.find(t => t.clrType === refType)`,
  `spark-reference-picker.component.ts:158`). For a JSON-only target they miss without failing:
  - the Read check on the target is skipped (`AttributeRightsEnforcement.cs:189-193`);
  - the error label falls back to the raw string (`SaveValidation.cs:154`);
  - the picker's grid still loads from the attribute's `query`.

  The fix: when no CLR type matches, resolve through the lookup query's row type (D9).

**The popup's object is not a problem for such a type (owner, 2026-11-06):** New always runs the type's actions-class
hook and always returns a PersistentObject to the client. The only gap today is the early return at `New.cs:259`. The
fix is to resolve the actions by model name, as the load already does. What remains open is the **create**
(spike S4).

## 4. Out of scope

- Editing an existing referenced object from the picker.
- Deleting the created object when the parent is never saved (prior art keeps it too). Decision D5 records it.

## 5. Decisions

| # | Decision | Evidence / source |
|---|---|---|
| D1 | The general mechanism replaces a free-text flag on the picker. | Owner, 2026-11-06. |
| D2 | The popup's object is built by the referenced type's existing `OnNewAsync`; no new hook method. | Owner, 2026-11-06 ("don't create new methods, won't be necessary"); hook at `DefaultPersistentObjectActions.cs:508`. |
| D3 | The object is saved immediately on the popup's Save, before the parent. | Prior art; the create path already returns the id (`Create.cs`, 201). |
| D4 | The flag is named `canAddNewReference`. | Prior art's name; open to the owner. |
| D5 | An object created from a picker whose parent is then discarded is kept. It is a valid object the user saved in the popup, listed and deletable from its own query. Rejected: client-side deletion on cancel (skipped by a closed tab or a crash, and needs the `Delete` right), and creating it together with the owner (the nested payload D10 rejects). Junk rows, if they become a problem, get a type-specific cleanup. Applies only to document types: D10 types are never stored. | Owner, 2026-11-06 (Q3: A); same as the prior art. |
| D7 (S1) | **The server sends the effective flag, on the definition and on the object.** `canAddNewReference` (bool?) is on `EntityAttributeDefinition`. `PersistentObjectAttribute` gets `CanAddNewReference` (written only when true, `PersistentObjectAttributeJsonConverter`), because a retry prompt's form (R7) is drawn from the object alone. The referenced type is **not** put on the object: see D9. `AttributeRightsEnforcement` switches the flag off when the caller lacks `New` on the target (or the target is undeclared), in `PresentAsync` (every construction path, retry prompts included: `EntityMapper.cs:221`, `RetryPresentation.cs:21`) and `ForFormAsync`. A hook may still switch it off per object. The flag is not a structural hash field, because it gates no write. | Answers Q1. Code on `feat/reference-add-new`; builds. |
| D8 (S2) | **The picker context is the owner type plus attribute name, verified like an AsDetail row.** `POST /spark/po/new` takes `ReferenceAttribute` next to the existing `ParentType` / `ParentId`. It is refused, the same way as an unknown type, unless: the attribute is a Reference of `ParentType` with `canAddNewReference`; its target is the constructed type; and neither `AsDetailAttribute` nor `QueryId` is sent. The one right checked is `New` on the constructed type. A sent `ParentId` is reloaded, which applies the owner's Read right and row filter. Without one (an unsaved or virtual owner) the hook gets `Parent = null`; the endpoint already gives that answer for an unsaved parent (`New.cs`, the `ParentId` doc). The owner's unsaved values are **not** posted. Prefill input travels in `Parameters` (e.g. the search term). The hook reads `args.ReferenceAttribute` (the verified `EntityAttributeDefinition`, `IsArray` tells single from array) and `args.ParentType`. These are properties, not a new method. | Answers Q2. Pattern: `New.cs` AsDetail branch (parent type + attribute verified against the model). |
| D9 | **The client never names the type to construct, and the target comes from the reference's lookup query when `referenceType` cannot name it.** In the prior art, a reference attribute carries its lookup query, and Add new runs "New" on that query with the attribute's id; the query's object type *is* the type. Spark's equivalent of that lookup already exists: a Reference's `query`. `[Reference(typeof(T), "Q")]` sets it, or synchronize falls back to T's plain root query (`ModelSynchronizer.cs:948-954`, SPARK056 when none), and it travels on the object (`PersistentObjectAttribute.Query`). So `POST /spark/po/new` with `ParentType` + `ReferenceAttribute` derives the type itself (`objectTypeId` may be omitted, and must match when sent). The target is resolved by `ModelLoaderExtensions.GetReferenceTarget`: the CLR `referenceType` when it resolves, otherwise the lookup query's `SparkQuery.EntityType` (model name). The second route is how a reference to a JSON-only type resolves (R10). It replaces the earlier idea of matching `referenceType` against a model name. | Owner, 2026-11-06 ("the Reference attributes need a Lookup name pointing to the query"); prior art read from its published client (`addNewReference` runs `Query.New` on `this.lookup` with `PersistentObjectAttributeId`). |
| D10 (S4) | **A referenced type without documents of its own is confirmed, not stored.** For a `[ValueObject]` type or a type with no C# class, the popup's Save runs the ordinary validation and returns the object with its key as the id; nothing is written. The value exists in the owner's reference value until the owner saves. The client keeps the chip label from the returned object and does not drop the value when the picker's options are reloaded. Rejected: storing it where it lives (the browser would have to post the whole parent tree up to the document, and the server would locate and rewrite the nested object; a manipulated payload could write where it should not), and a per-type hook (a new method). | Owner, 2026-11-06: "I tend to pick option B. option A would require us to construct the entire parent -> parent -> parent tree … the user can easily manipulate the payload". `ForgeCheck` is a `[ValueObject]` whose `[ValueKey] Id` is the check name (`ForgeCheck.cs:15,31`). |
| D11 | **A `[ValueObject]` type is never stored as its own document.** `/po/create` and the save path refuse it. No guard was found today, so a create would most likely write a stray standalone document (not reproduced). A type with no C# class has no `Query<T>` to read it back, so it is never stored either (D10); the save path's "Could not resolve type" 500 (`DatabaseAccess.cs:228-229`) becomes a clean refusal. Fixed in this PR. | Owner, 2026-11-06: "the Spark framework should prevent value-objects from being stored as main-documents"; "We may need to plug some holes". |
| D12 (supersedes D8's `Parent = null`) | **`OnNewAsync` always gets the picker's owner as `Parent`, with its chain.** The client posts the owner as it is now (a retry prompt with its typed values, an unsaved create page) with its `Parent` chain. Every level that has an id is replaced by the row-checked, presented server read, so stored levels are authoritative. A level without one keeps the posted values, typed against the model; the owner's type must equal `ParentType`. The stored levels are loaded in **one round trip**: one typed lazy load per level (`session.Advanced.Lazily.LoadAsync<T>`) run by a single `ExecuteAllPendingLazyOperationsAsync()`. The per-level reads then hit the scoped session's identity map (`DatabaseAccess.cs:22`), and a test pins `session.Advanced.NumberOfRequests`. Chain depth is bounded, like `AttributeRightsEnforcement.MaxDepth` (8). Hooks treat unsaved levels' values as input, never authority, the same as a refresh hook's object. | Owner, 2026-11-06: "The framework should always be able to supply the parent to the retryAction"; "make sure we don't do N+1 separate database calls". Custom actions already reload `Parent` (`ICustomAction.cs:23-27`); `PersistentObject.Parent` is serialized and chainable (`PersistentObject.cs:35`). |
| D13 | **A Reference whose lookup query lists a different type than its `referenceType` is a startup error**, when both resolve. The message names the type, attribute, query and both types, as the `parentReference` check does (`SparkSubQueries.cs:66-67`). Synchronize re-derives both fields from `[Reference(typeof(T), "Q")]`, which corrects a derived mismatch. Both are structural hash fields (`ModelFileShape.cs:212-214`), so a deployed model that drifts already fails the hash gate and does not start. While implementing, load every app model in this repo; if a legitimate different-row-type lookup turns up, narrow the rule (e.g. enforce it only with `canAddNewReference`) rather than drop it. | Owner, 2026-11-06 (Q4: A). Nothing checks this today (searched the Spark libraries for any comparison of the two). |
| D14 | **A Reference's visibility follows two rights on its target, for every kind of target (C# or virtual):** `Read` denied hides the attribute (removed from the response, as MCP PRD D19 does today). `Read` allowed but `Query` denied shows it **read-only**: the value and label are visible, but there is no picker, so no Add new either, and a save keeps the stored value, as for any read-only attribute. `GetHiddenReferencesAsync` resolves targets with `GetReferenceTarget` (D9), so a virtual target is checked too; today it never resolves there. That changes nothing in this repo: all 44 Reference attributes in the apps' models target C# types (no bare-name `referenceType`). **Rights are always named by the PersistentObject type's model name, never the C# name**; the C# name only *finds* the type (`target.Name`, `entityType.Name`). | Owner, 2026-11-06. |
| D15 | **The hook args lose their marker type parameter: `SparkNewArgs`, `SparkRefreshArgs`, `SparkDeleteRowArgs`.** `T` was never read or written in any of them; it only made the invokers close a generic type at runtime (`MakeGenericType` + a reflected constructor, cached by a tuple key that once handed one invoker's constructor to another: the `DeleteRowInvoker` note in `NewInvoker.cs`). One non-generic args class serves a C# actions class (`DefaultPersistentObjectActions<T>.OnNewAsync(SparkNewArgs)`) and a virtual type's name-resolved `{Name}Actions` (`public Task OnNewAsync(SparkNewArgs args)`, duck-typed like `OnLoadAsync`). A `{Name}Actions` method named `OnNewAsync` with another parameter type **throws** at resolution instead of silently never running, as the virtual `OnLoadAsync` lookup does (`DatabaseAccess.cs:1089-1091`). No actions class in the repository serves two entity types, so no overload separation is lost. Every override (6 app files), the invokers, the tests and the current guides (`guide-custom-actions`, `guide-asdetail-attributes`, `guide-triggers-refresh`, `AGENTS.md`) change in this PR; historical PRDs and release notes are left as written (D6). | Owner, 2026-11-06 (Q6: "just drop it altogether"). |
| D16 | **The popup is a new standalone `spark-po-new-modal`, opened through a service** (`open(ownerChain, attribute): Promise<PersistentObject \| null>`), shaped like the retry modal (`retry-action-modal/src/spark-retry-action-modal.component.ts:33`, service-driven, not tied to a form). It calls `/po/new` with the owner chain, shows the PO, saves with `/po/create` (validation errors shown in place), and resolves with the saved object or `null` on cancel. Every opener (single picker, `bs-tree-select`, a form inside the retry modal, another popup) uses the same call; each call stacks its own modal. Rejected: the AsDetail row modal (`spark-po-form.component.html:462`), which is bound to one form instance and only copies values back locally, and swapping the form into the picker's own modal, which needs separate code for `bs-tree-select` (no modal). Stacking is supported (checked 2026-11-06 in the installed `@mintplayer/ui-core`, `chunks/overlay-*.mjs`, regions `overlay-stack.ts` and `dismiss-stack.ts`): every open overlay gets `z-index` 1050 + 10 × depth, Escape and the focus trap act only when the overlay `isTop()` of the dismiss stack, and every body child below the topmost modal is hidden from interaction. So retry → picker → popup → popup needs no extra code. | Owner, 2026-11-06 (Q7: "Agreed, A"). |
| D17 | **Placement and prefill.** A [+] button sits in `bs-tree-select`'s existing `footer` slot (`*bsTreeSelectFooter`) for an array Reference, and in the single picker modal's footer for a single Reference. It is shown whether or not anything matched, so a near-match never hides it. The tree has no "under which node" question: the reference provider is flat (`ReferenceQueryTreeSelectProvider.loadChildren` returns `[]`, `reference-query-tree-select-provider.ts:36`), so a new object is one more root. A guard for providers with children is left until one gets Add new. The search term in the box **when [+] is clicked** (read then, not the debounced `searchStart` value) is sent as `Parameters["searchTerm"]`. A new model setting on the Reference attribute, **`defaultAttribute`**, names the attribute of the new object that receives it. The framework writes it into the scaffolded PO **before** `OnNewAsync`, so the hook can change or lock it. Startup checks that it exists on the target type and is a String. It only prefills: Save runs the normal create validation. Rejected: each `OnNewAsync` copying the term itself (boilerplate in every type, impossible for a type with no code), and deriving the field from the target's `Breadcrumb` (breaks silently on `"{Name} ({Code})"`). | Owner, 2026-11-06 (Q8: tree has no parent for a new item → flat provider; "A, but call it defaultAttribute"). |
| D18 | **A confirmed value object's id is its `[ValueKey]`.** The owner's reference stores the key and the lookup lists rows by it, so `ConfirmAsync` reads the key attribute from the confirmed values. An empty key is a 400 validation error on that attribute, never a made-up Guid. A type with no C# class has no key: it keeps the posted id or gets a new Guid. So that the key can be entered at all, startup refuses `canAddNewReference` on a value object whose `[ValueKey]` property is not a writable model attribute. It also refuses a read-only `defaultAttribute` (D17), because the confirm and the create keep only writable values. **Consequence for #554:** `ForgeCheck`'s model today has only `Name` (read-only) and its key `Id` is not a model attribute (`ForgeCheck.json`; `[ValueKey] Id` "the check's name, which is also its identity", `ForgeCheck.cs`). The rulesets branch must expose the key as a writable attribute (shown as the name) before it sets `canAddNewReference`. Rejected: deriving the key in the framework from another attribute (no general rule; `Id == Name` is ForgeCheck's convention, not Spark's), and a Guid fallback (the reference would store an id no lookup lists). | Found while wiring D10, 2026-11-06: `ConfirmAsync` would have given every new ForgeCheck a Guid and dropped its read-only Name. Not yet reviewed by the owner. |
| D6 | No backward compatibility. Public contracts are reshaped outright, with every caller updated in this PR: no overloads, optional parameters or `[Obsolete]` shims. This covers the `SparkNewArgs<T>` constructor, `INewInvoker.InvokeAsync` (public, `Services/NewInvoker.cs:21`), the `POST /spark/po/new` request and the TS models. | Owner, 2026-11-06: "no backward compatibility needed. The code is still in preview". |

## 6. Open questions (answered by the spikes)

- **Q1 (S1):** is the button's visibility decided by the client (`getPermissions`) or sent per object by the server
  (an effective flag on the scaffolded attribute, false without `New`)? A retry prompt's form is drawn from the
  object's attributes alone (`spark-retry-action-modal.component.ts:167-199`), so the flag must travel on the object
  either way.
- **Q2 (S2):** how `OnNewAsync` learns the picker context (parent object + attribute name) without a new method: a
  property on `SparkNewArgs<T>`, filled from the `POST /spark/po/new` request.
- **Q3 (S3):** which popup to reuse (the AsDetail row modal, `spark-po-form.component.html:462-485`, or the retry
  modal), and how a popup inside a popup behaves (the popup's own form can have an add-new reference).
- **Q4 (S4):** how a referenced type without its own documents is "created", in two cases:
  (a) a C# class with no collection (`ForgeCheck`, embedded in `Repository.KnownChecks`), and
  (b) no C# class at all (R10).
  Either its actions class persists it somewhere (e.g. the parent's embedded list), or the create returns an object
  that exists only as an id (a name). For (b), the new path is settled (§3, owner 2026-11-06). Only the create is
  open: today it has no route without a CLR save.
- **Q5 (S5):** where the button goes in the multi picker (`bs-tree-select` has `footerSlot` and `noResultsSlot`) and in
  the single picker's modal; whether the search term prefills the new object's name.
