# PRD — `TriggersRefresh` + `OnRefreshAsync`: a form that reshapes itself

**Status:** Not implemented — PRD and plan written, nothing built
**Issues:** [#260](https://github.com/MintPlayer/MintPlayer.Spark/issues/260)
**PR:** _(pending)_
**Branch:** `feat/issue-260-triggers-refresh` (from `master` @ `aadd54c`)
**Plan:** `docs/issue_260_plan.md`
**Base:** `master` @ `aadd54c` (`10.0.0-preview.63`, `@mintplayer/ng-spark@22.4.0`)
**Release:** `10.0.0-preview.64`, `@mintplayer/ng-spark@22.5.0` (non-breaking; major stays 22 — Angular lockstep)

---

## Problem

**A Spark form is static: the shape it is rendered with is the shape the model file declared, and nothing a
user does can change it.** Every real business form contradicts this. Picking a contract type decides
whether a VAT number is required. Marking a car stolen should lock the plate and demand a police report.
Choosing a brand decides which models are selectable. Today the only expression available for any of that
is a comment in the entity class and a server-side rejection *after* the user presses Save.

The issue asks for the mechanism Vidyano has had for a decade:

1. `TriggersRefresh: true` on an attribute declares "when this value changes, ask the server".
2. A new `OnRefreshAsync` hook on `{Entity}Actions` receives the in-progress object and the attribute that
   changed, and may reshape the object: toggle `IsRequired`, add or remove validation rules, show/hide,
   enable/disable, repopulate a dropdown, or set a dependent value.
3. A sample in a demo app that shows it working.

Three defects surfaced during the investigation that the feature cannot be built on top of without fixing,
and which are therefore in scope:

- **Server-side validation ignores the object it is validating** (F6). `ValidationService.Validate` walks
  `entityType.Attributes` and reads `attrDef.IsRequired` / `attrDef.Rules` from the *model file*; the
  submitted attribute contributes only a value. A refresh that makes a field required would decorate the
  client and change nothing that is enforced.
- **The client never evaluates a validation rule** (F9). `attr.rules` is copied onto outgoing objects and
  is otherwise dead. "Change an attribute's validation rules" currently lands on neither side.
- **Dropdown options are not on the wire** (F5). `PersistentObjectAttribute` carries `Query` — the *name*
  of a SparkQuery — and nothing else; lookup values are fetched separately. "Change the options in a
  dropdown box" is inexpressible in the current protocol.

## Origin

Issue #260 was filed with a title and **no body**. The specification is the owner's brief: add
`TriggersRefresh` to `PersistentObjectAttribute`, invoke a new `OnRefresh` on the corresponding actions
class when such a value changes, hand it the current state of the object, and let the developer reshape it.
The brief named Vidyano as the reference implementation and pointed at the DeCronosGroep repositories for
worked examples.

That reference was taken literally: the mechanism below is derived from decompiled
`Vidyano.Service.dll` v6.0.20260820.6492, the shipped `@vidyano/vidyano` client bundle, and **121 real
`OnRefresh` overrides** across `Fleet`, `Facility`, `KidsDaycare` and `CronosLegacy`. Where Spark departs
from Vidyano it is deliberate and recorded in Decisions, and in two places (async hooks, F17; server-side
rule enforcement, F6) Spark can start where Vidyano cannot retrofit.

---

## Investigation findings

### F1 — there is no prior art in Spark; the seams are unusually clean

`TriggersRefresh` / `triggersRefresh` / `OnRefresh` / `onRefresh` return **zero** matches across C#, TS,
JSON and docs. Everything is greenfield. Offsetting that: the schema→wire copy has exactly one owner
(`EntityMapper.FromDefinition`, whose comment claims the title: *"Single canonical owner of which fields
travel from schema to wire"*), actions resolution has exactly one entry point (`ActionsResolver.Resolve<T>`),
and there is already a scaffold-a-blank-object path (`IEntityMapper.GetPersistentObject(Guid)`).

### F2 — Vidyano's hook is `void`, sync, and takes one argument

```csharp
/// <summary>
/// Called when an attribute with <see cref="PersistentObjectAttribute.TriggersRefresh"/> is changed.
/// </summary>
public virtual void OnRefresh(RefreshArgs args) { }
```

`RefreshArgs` is deliberately tiny — `PersistentObject PersistentObject` and `PersistentObjectAttribute
Attribute` (the latter `Lazy`, so the id→attribute lookup only runs if the handler touches it). No
cancellation token, no old value, no return value. There is no `OnRefreshAsync` anywhere in the assembly;
handlers make blocking `Session.Load` calls. Vidyano's own note is that this cannot be retrofitted without
a breaking change.

### F3 — `"TriggersRefresh": true` is JSON-only, on the attribute, and has no C# attribute equivalent

88 occurrences in Fleet, all under `Fleet/App_Data/Model/Fleet/*.json`, as a sibling of `Name` / `Rules` /
`Visibility`:

```json
{
  "Id": "823ecbb4-2bab-36e7-9ad8-f69391b8fb84",
  "Rules": "Required",
  "Name": "Values",
  "TriggersRefresh": true,
  "Visibility": 5
}
```

Vidyano's generated schema doc describes it as `// bool, omitted if false. Triggers server roundtrip on change`.
A grep for `TriggersRefresh` across `*.cs` in the app repos returns **no declarations** — only runtime
mutation (bulk edit switches every trigger off: `obj.Attributes.Run(attr => attr.TriggersRefresh = false)`).

### F4 — the protocol is a full round-trip of the whole object, and a full replacement back

`POST /ExecuteAction` with `action = "PersistentObject.Refresh"`, the entire object as `data.parent`, and one
parameter naming the trigger: `RefreshedPersistentObjectAttributeId`. Server-side:

```csharp
case "Refresh":
{
    PersistentObject persistentObject3 = lazy.Value;   // = cache.Rematerialize(obj, context, forAction: true)
    using (IPersistentObjectActions a = ActionsHandler.Get(context, persistentObject3, parameters))
    {
        a.OnRefresh(new RefreshArgs(persistentObject3, new Lazy<PersistentObjectAttribute>(delegate
        {
            Guid attrId = parameters["RefreshedPersistentObjectAttributeId"].FromServiceString<Guid>();
            return persistentObject3.Attributes.FirstOrDefault(a => a.Id == attrId);
        })));
        a.PreClient(persistentObject3);
    }
    retVal = persistentObject3;
    break;
}
```

`Rematerialize` is the load-bearing part: it constructs a **fresh object from the server's cached model**
and copies only *state* from the client (`Value`, `IsReadOnly`, `IsValueChanged`, `Options`, `Visibility`,
`TriggersRefresh`). Metadata is never trusted from the wire. The response is a complete replacement object,
not a diff.

### F5 — Spark cannot express "these are the new options"

`PersistentObjectAttribute` has 19 serialized fields and **no `Options` / `Values`**. Reference attributes
carry `Query` (a SparkQuery name) and the client executes `/spark/queries/...`; lookup attributes carry
nothing at all — `EntityAttributeDefinition.LookupReferenceType` is schema-only and the client fetches
`/spark/lookupref/{name}`. Vidyano, by contrast, ships `attr.options` on the wire and handlers call
`attr.RefreshOptions<T>(Context, obj, out var items)` (`CarActions.cs:389`, `DamageActions.cs:95`).

**This is the single biggest protocol gap.** Without an options payload, the third bullet of the brief
("change the options in an attribute's dropdown box") is not implementable.

### F6 — server-side validation reads the model, not the object

`ValidationService.Validate` (`Services/ValidationService.cs:25`), verbatim structure:

```csharp
foreach (var attrDef in entityType.Attributes)
{
    var attribute = persistentObject.Attributes.FirstOrDefault(a => a.Name == attrDef.Name);
    var value = attribute?.Value;
    if (attrDef.IsRequired && IsEmpty(value)) { … }
    foreach (var rule in attrDef.Rules ?? []) { … }
}
```

The submitted attribute supplies **only the value**. `IsRequired` and `Rules` come from the model file.
So a refresh that sets `IsRequired = true` on the wire changes what the form renders and **nothing** about
what Save enforces — and a refresh that *relaxes* a rule is likewise ignored, so a legitimately-optional
field still rejects. Both directions are wrong.

### F7 — `IsRequired` / `Rules` / `IsVisible` / `IsReadOnly` are already on the wire and already copied

`EntityMapper.FromDefinition` is a 14-field copy and already carries `IsRequired`, `IsVisible`,
`IsReadOnly`, `Rules`, `Order`, `ShowedOn`, `Group`, `Renderer`, `RendererOptions`, `Query`. Four of the
five reshaping verbs in the brief therefore need **no new wire fields** — only a hook that mutates them and
a client that re-reads them.

### F8 — the Spark client does not render from a `PersistentObject`

`spark-po-form` is driven by two independent signals: `entityType: input<EntityType | null>` (the schema —
`EntityAttributeDefinition[]`, carrying `isRequired`/`isVisible`/`isReadOnly`/`rules`/`renderer`) and
`formData: model<Record<string, any>>` (a flat value dict). The `PersistentObject` exists only at the
edges — `po-edit` flattens one in via `nestedPoToDict` and rebuilds one on save; `po-create` never has one.

Consequence: a returned object cannot be "merged into the form". It must be **split** — a metadata delta
onto the rendered schema, a value delta onto `formData` — and neither merge exists today. The nearest
thing, `mergeAttributeMetadata` in the retry modal, merges the *opposite* direction (keeps server metadata,
overlays client values).

### F9 — the client never evaluates a rule

`attr.rules` appears in exactly three places across the library, all of them copying the array onto an
outgoing object (`as-detail-conversions.ts:169`, `po-create:101`, `retry-action-modal:182`). The only
client-side validation is the native `[required]="attr.isRequired"` on `<input>`/`<textarea>`, and it does
not even block submit — `onSave()` emits unconditionally. Everything else arrives as a 400 and is rendered
per-field by `ErrorForAttributePipe`.

### F10 — re-setting `entityType` re-fetches every option; mutating it in place re-fetches none

A single constructor effect keyed on whole-`EntityType` identity owns all option loading:

```ts
effect(() => {
  const et = this.entityType();
  const _pid = this.parentId(); const _ptype = this.parentType();
  if (et) { this.loadReferenceOptions(); this.loadAsDetailTypes(); this.loadLookupReferenceOptions(); }
});
```

`SparkService` caches nothing (`as-detail-conversions.ts` says so explicitly: *"`getEntityTypes()` is NOT
cached — it issues a request per call"*). So the obvious implementation — apply the refresh by setting a
new `EntityType` — re-issues every reference query, every lookup fetch, a full `getEntityTypes()` and a
`getPermissions()` per array-AsDetail attribute, **on every refresh**. The equally obvious alternative,
mutating the existing object, is not reactive at all: `editableAttributes` is a `computed` over
`entityType()` and will not re-run.

This finding is why the design does not touch `entityType` at all (D-C).

### F11 — free-text triggers on blur, discrete editors immediately

Vidyano's string/numeric/multiline editors call `setValue(value, /*allowRefresh*/ false)` on every keystroke,
which only sets a pending `_shouldRefresh` flag; the round-trip fires from `_editInputBlur`. Dropdowns,
booleans, dates and references call `setValue(x, true)` and refresh immediately. Files and AsDetail
add/delete call `triggerRefresh(true)`. Pending refreshes are flushed before Save:

```js
const attributesToRefresh = this.attributes.filter(attr => attr.shouldRefresh);
for (let i = 0; i < attributesToRefresh.length; i++) await attributesToRefresh[i].triggerRefresh(true);
const po = await this.service.executeAction("PersistentObject.Save", this, null, null, null);
```

### F12 — in-flight edits survive by comparing against what was *sent*, not against what is displayed

`#prepareAttributesForRefresh` backs up the sent value of every attribute *except the trigger*
(`#refreshServiceValue`). On merge, metadata is always taken from the server, but a value is overwritten
only when `resultWins` (never true for refresh) **or** the server's value differs from the backed-up sent
value. So typing into field B during a refresh for field A keeps B — unless the server actually changed B.
Getting this comparison wrong is the classic "refresh eats my typing" bug.

### F13 — the client serializes refreshes and drops superseded ones

Every refresh goes through `queueWork(work, /*blockActions*/ false)` — a serial promise queue per object —
and the queued work opens with a supersession guard: `const attrValue = attr.value; … if (attrValue !==
attr.value) return false;`. At most one refresh is in flight per object, and obsolete ones are dropped.
Refresh is also the one action that does **not** freeze the form (`const isFreezingAction = isObjectAction
&& action !== "PersistentObject.Refresh"`), so the user keeps typing during the round-trip.

Spark has none of this: no debounce, no in-flight guard, no cancellation anywhere in `ng-spark`. RxJS is
used only as `firstValueFrom`, so a stale response cannot be cancelled — only discarded.

### F14 — rules get clobbered, and Fleet documents it twice

Because the server rebuilds each attribute from the model and re-sends `rules` wholesale, any rule added in
`OnLoad` but not re-added during refresh disappears. `CarNoteActions.cs:178` and `:271`, verbatim:

> `// Note: Set every Car_/OtherCar_ property as required because Required Rule gets lost after Refresh.`

plus `// Note: Workaround. Rules got lost somewhere.` The lesson is a contract, not a bug: **the refresh
hook must establish the complete presentation state on every call, idempotently**, never patch it
incrementally. Real handlers already do this by sharing a helper between `OnLoad` and `OnRefresh`
(`ContractPartActions.HandleOccupancyTypeVisibility`).

### F15 — there is no re-entrancy guard anywhere in Vidyano

`SetValueWithRefresh` re-enters `OnRefresh` synchronously, in-process, with a fresh actions handler:

```csharp
public void SetValueWithRefresh(object? value, ITargetContext? context = null)
{
    SetValue(value);
    if (IsValueChanged && TriggersRefresh)
    {
        using IPersistentObjectActions a = ActionsHandler.Get(context ?? PersistentObject.Context, PersistentObject);
        a.OnRefresh(new RefreshArgs(PersistentObject, new Lazy<PersistentObjectAttribute>(this)));
    }
}
```

Infinite recursion is prevented only by handler discipline — real code guards by hand
(`CarNoteActions.cs:158`). There is no `isRefreshing` flag on either side.

### F16 — the refresh right is `New` for a new object, `Read` for an existing one

`obj.CheckRight(action)` maps `"Refresh"` → `IsNew ? "New" : "Read"`. New objects are fully supported:
`isNew` is transmitted, `Rematerialize` constructs accordingly, and handlers routinely branch on
`args.PersistentObject.IsNew`. The client accepts `isNew` back only if it was already new.

Note the corollary Vidyano lives with and Spark should not: `TriggersRefresh` is round-tripped from the
client and `Rematerialize` copies it verbatim, so a hostile client can claim a trigger the model never
declared. The only gate is the `Read`/`New` right.

### F17 — refresh handlers are chatty enough to hit RavenDB's request ceiling

Fleet ships `using var _ = Context.Session.IgnoreMaxRequests();` inside `OnRefresh` with the explanatory
comment: *"Refreshing missing Car attributes calls CarActions.PreClient repeatedly (each querying
VCarNotes), which exceeds the default 30-request limit."* A refresh runs a full object construction plus
handler work **on every blur** — far more often than load or save. `ExecuteCustomAction.cs` already has the
matching Spark idiom (`session.IgnoreMaxRequests(estimatedRequests, logger)`).

### F18 — an `OnRefresh` override silently disables bulk edit in Vidyano

```csharp
public static bool DisableBulkEdit(IPersistentObjectActions actionsHandler)
{ … bool flag3 = IsOverridenMethod(t, "OnRefresh"); … return (flag || flag2 || flag3) && !flag4; }
```

Spark has no bulk edit, so this is informational — but it is evidence that a reshaping hook interacts badly
with any "one form, many rows" surface, which is worth remembering if bulk edit is ever built.

### F19 — the flag survives synchronize for free

`ModelSynchronizer` looks up the existing attribute, **mutates that same object in place**, and reassigns
only a fixed set of fields (`DataType`, `Order` when `<= 0`, `ReferenceType`, `Query` provenance-gated,
`IsArray`, `IsSortable`, `AsDetailType`, `LookupReferenceType`, `InCollectionType`/`InQueryType`, and
`ShowedOn` intersect-only). Its comment reads `// Update existing attribute, preserving custom settings.`
Everything it does not assign — `Label`, `Rules`, `Renderer`, `Group`, `EditMode`, `ColumnSpan`,
`ReferenceDisplayType`, `IsRequired`, `IsReadOnly` — survives automatically. `ReferenceDisplayType`'s own
doc states the pattern: *"Hand-set in the model JSON and preserved across synchronize (like `EditMode`)."*

### F20 — the flag belongs on the schema, not on the wire attribute

`editMode`, `referenceDisplayType` and `isSortable` are schema-only: `po-form` reads them off
`EntityAttributeDefinition`, never off a `PersistentObjectAttribute`. Putting `triggersRefresh` on the wire
attribute as well would additionally require **three** hand-edits in
`PersistentObjectAttributeJsonConverter` — the `PopulateFromJson` switch, `WriteSharedFields`, and the
hardcoded `KnownFieldNames` array — each of which fails *silently* when missed. Schema-only is smaller,
consistent with precedent, and closes F16's spoofing corollary by construction.

### F21 — a "declared but not implemented" analyzer is not implementable as a Roslyn analyzer

`triggersRefresh` lives in `App_Data/Model/*.json`, which is not part of the compilation unless added as an
`AdditionalFile`. The realistic gate is a startup/verify check alongside `VerifySparkModelHash` and
`VerifySparkSecurityConfiguration`, or an extension to `--spark-verify-model`. (`SPARK003` is unused and
free, should an analyzer ever be wanted for something adjacent.)

### F22 — `AddAttribute` and `RetainAttributes` are `internal`

An actions class lives in a different assembly, so a hook cannot add or remove attributes. The only public
route is `attribute.CloneAndAdd(name, label)` on an existing attribute. Vidyano handlers do add and remove
attributes (`CarNoteActions`), so this bounds what the Spark hook can offer in v1.

### F23 — the domain already contains a form-reshaping rule expressed only as a comment

`WebhooksDemo.Entities.EventColumnMapping.MoveLinkedIssues`:

> *"For pull request events: also move the issues that the PR closes/references. **Ignored for issue
> events.**"*

The field stays visible and editable on an issue-event mapping and is then silently ignored. This is the
gap the feature closes, present in the repo today, independent of the demo chosen to host the sample.

### F24 — the retry-action modal embeds a `spark-po-form`, so a refresh can re-enter the form recursively

`spark-retry-action-modal.component.ts:41` renders `<spark-po-form>` inside the modal, seeded from the
server-supplied object. Because the refresh endpoint returns the client-operation envelope (R11), a refresh
can carry a retry operation, which opens that modal, which renders a form, whose attributes may themselves
declare `triggersRefresh` — firing a second refresh from inside the modal that the first refresh opened.

D6 removes the *server-initiated* cascade; this is a **client-initiated** one and is not covered by it. It
is reachable today the moment any object reachable through a retry prompt declares a trigger. The
coordinator (D-H) must therefore be scoped per form instance rather than global, and the nested form must
not be able to resolve the outer form's pending refresh.

### F25 — Fleet's `CarActions` already tells half the story

`CarActions.OnBeforeSaveAsync` branches on `statusAttr?.IsValueChanged == true && entity.Status ==
CarStatus.Stolen` and its comment says *"This will lock the vehicle record"* — which today is only a message
string. Fleet is also the only demo with dedicated `pages/po-create` and `pages/po-edit` and with custom
edit renderers. `CarActions` is already the demonstration of the write pipeline.

---

## Requirements

| | Requirement |
|---|---|
| **R1** | `EntityAttributeDefinition` gains `bool? TriggersRefresh`, authored in `App_Data/Model/*.json` as `"triggersRefresh": true` and omitted when false. |
| **R2** | The flag survives `--spark-synchronize-model` unchanged, including on an attribute the synchronizer otherwise updates. |
| **R3** | `IPersistentObjectActions<T>` gains `Task OnRefreshAsync(SparkRefreshArgs<T> args)`, with `DefaultPersistentObjectActions<T>` supplying a completed-task default. |
| **R4** | `SparkRefreshArgs<T>` exposes the in-progress `PersistentObject`, the triggering `PersistentObjectAttribute`, whether the object is new, and a `CancellationToken`. |
| **R5** | A hook may set `IsRequired`, `IsReadOnly`, `IsVisible` and `Rules` on any attribute of the object, and set any attribute's `Value`. |
| **R6** | A hook may replace an attribute's selectable options — for a `LookupReference` by supplying an explicit option list, for a `Reference` by reassigning `Query`. |
| **R7** | `POST /spark/po/{objectTypeId}/refresh` accepts the in-progress object plus the name of the triggering attribute and returns the reshaped object. |
| **R8** | The endpoint authorizes as `New` when the submitted object has no id and as `Read` when it has one, matching Vidyano (F16), and refuses indistinguishably from not-found via the existing `SparkDenial` / `ClientResult.EnvelopeRefusal` helpers. |
| **R9** | For an existing row the endpoint applies row security exactly as `ExecuteCustomAction` does — load through `DatabaseAccess.GetPersistentObjectAsync` (which gates `Read`, the `CollectionGuard` and `IsAllowedAsync`), then re-run `RedactAsync` on the reshaped result so refresh cannot become a redaction bypass. |
| **R10** | The server rebuilds the object's metadata from the model and accepts only *values* and `IsValueChanged` from the wire, so a client cannot claim metadata — including a `triggersRefresh` the model never declared (F16, F20). |
| **R11** | The endpoint carries antiforgery metadata (`RequireAntiforgeryTokenAttribute(true)`) and returns the client-operation envelope, so notifications and retry prompts work during a refresh. |
| **R12** | `ValidationService` enforces the *effective* rules, not only the model's, so a refresh-imposed `IsRequired` or rule is actually enforced on Save and a refresh-relaxed one is actually relaxed. |
| **R13** | On Save the server re-derives the effective rules by running the refresh hook itself, so enforcement never depends on the client having called `/refresh`. |
| **R14** | `spark-po-form` applies a returned object as a **metadata overlay** plus a **value merge**, without replacing or mutating the `entityType` input, and therefore without re-fetching reference options, lookup values, AsDetail types or permissions (F10). |
| **R15** | A value the user changed while a refresh was in flight is preserved unless the server changed that same attribute relative to the value that was sent (F12). |
| **R16** | Refreshes are serialized per form and superseded refreshes are dropped; the form is never frozen during one (F13). |
| **R17** | Free-text editors trigger on blur; discrete editors (select, checkbox, date, reference, lookup) trigger immediately (F11). |
| **R18** | Any pending refresh is flushed before Save (F11). |
| **R19** | The client evaluates `rules` and `isRequired` from the effective (overlaid) metadata and blocks Save on failure, so a refresh-imposed rule is visible before the round-trip (F9). |
| **R20** | An attribute inside an AsDetail row may trigger a refresh, addressed by the existing `{attr}[{index}].{col}` path convention, without destroying row focus. |
| **R21** | Refresh traffic is rate-limitable via the existing `SparkRateLimiterOptions.PathPrefixes` and the endpoint lifts the RavenDB per-session request cap as `ExecuteCustomAction` does (F17). |
| **R22** | `libs/spark/MintPlayer.Spark/AGENTS.md` documents the hook in the existing hooks table and carries a `⚠️` for the idempotency contract (F14) and the latency cost (F17). |
| **R23** | Fleet's `CarActions` ships a working `OnRefreshAsync` sample covering visibility, required, read-only and option replacement, with the entity and model-file changes it needs. |
| **R24** | `--spark-verify-model` reports an attribute declaring `triggersRefresh: true` whose entity has no `OnRefreshAsync` override (F21). |
| **R25** | The refresh coordinator is scoped to a single form instance, so a form rendered inside the retry-action modal refreshes independently of the form that opened it and cannot resolve or supersede its pending refresh (F24). |

---

## Design

### D-A — the flag is schema-only

`EntityAttributeDefinition.TriggersRefresh` is `bool?`, so it is absent from JSON when unset and round-trips
untouched through synchronize (F19). It is **not** added to `PersistentObjectAttribute`: `po-form` reads it
off the `EntityType` exactly as it reads `editMode`, `referenceDisplayType` and `isSortable` (F20), which
avoids the three silent-failure edits in `PersistentObjectAttributeJsonConverter` and removes the spoofing
corollary — a client cannot claim a trigger, because the flag never travels on the object.

`ModelFileShape` treats it as **presentational** (not added to `StructuralAttributeFields`). It cannot
weaken server-side enforcement, because D-E makes enforcement re-derive the rules server-side regardless of
what the client did. Hand-edits therefore stay free and no demo's `modelHashes.json` churns.

### D-B — the hook is `[NoInterfaceMember]`, async, and non-breaking

```csharp
[NoInterfaceMember]
public virtual Task OnRefreshAsync(SparkRefreshArgs<T> args) => Task.CompletedTask;
```

on `DefaultPersistentObjectActions<T>` only, dispatched by the repo's existing reflection pattern
(`ReflectionCache.GetOrAdd` keyed on `(op, actionsType)`, as `ReferenceResolver` does for
`GetDefaultIncludes`). This is the established shape for a hook nothing outside the framework dispatches,
and it keeps the interface — and every hand-written implementer — untouched.

Async from the outset: Vidyano's `void OnRefresh` forces blocking `Session.Load` inside handlers and cannot
be retrofitted (F2). Spark has no such constraint and every other Spark hook is already `Task`-returning.

```csharp
public sealed class SparkRefreshArgs<T> where T : class
{
    public PersistentObject PersistentObject { get; }
    public PersistentObjectAttribute? Attribute { get; }   // null-tolerant by design — see below
    public bool IsNew { get; }
    public CancellationToken CancellationToken { get; }
}
```

`Attribute` is nullable rather than `Lazy`: Vidyano's `Lazy` returns `null` when the trigger id is not in
the rematerialized set and handlers NRE on it (F-2 gotcha 11). Making the nullability visible in the type
is the cheaper fix, and the trigger is looked up by **name**, not id, because Spark's scaffolded attributes
have no `Id` (`EntityMapper.ScaffoldFrom` never assigns one).

### D-C — the client applies an overlay, never a new `EntityType`

This is the design's load-bearing decision, and it exists because of F10. `po-form` gains one signal:

```ts
refreshOverlay = signal<Record<string, AttributeOverlay>>({});

interface AttributeOverlay {
  isRequired?: boolean;
  isReadOnly?: boolean;
  isVisible?: boolean;
  rules?: ValidationRule[];
  query?: string;
  options?: LookupReferenceValue[];
}
```

`editableAttributes` becomes a `computed` over `entityType()` **and** `refreshOverlay()`, applying the
overlay per attribute. The `entityType` input is never written and never re-set, so the option-loading
effect never re-runs and no reference query, lookup fetch, `getEntityTypes()` or `getPermissions()` is
re-issued by a refresh. Option *replacement* (R6) is served from the overlay, which is why `options` is a
field on it rather than a `query` change alone.

The rejected alternative — synthesize a new `EntityType` from the response, as `entityTypeFromPo` in the
retry modal does — additionally drops `tabs`, `groups`, `queries`, `referenceType`, `editMode`,
`referenceDisplayType`, `isSortable`, `lookupReferenceType` and `columnSpan`, which would collapse HR's
tabbed form into a flat one and turn Modal reference pickers into dropdowns.

### D-D — the value merge compares against what was sent

`po-form` snapshots `formData()` immediately before dispatching a refresh (excluding the trigger, per F12).
On response, for each attribute:

- if the server's value equals the snapshot value → **keep whatever is in `formData` now** (the user may
  have typed since);
- if it differs → **take the server's value** (the hook deliberately changed it).

Metadata from the overlay is always applied. This is Vidyano's rule (F12) with `resultWins` fixed at
`false`, since Spark's refresh is never a save or a cancel.

### D-E — enforcement re-derives the rules on the server

R12/R13 are the difference between a feature and a decoration. `ValidationService.Validate` gains an
overload taking the effective attribute set, and the Create/Update path calls it with the result of running
the refresh hook server-side:

```
Create/Update
  → build the effective object (scaffold from model + submitted values)
  → for each attribute with TriggersRefresh, run OnRefreshAsync once
  → Validate against the resulting IsRequired / Rules
  → existing authorize → save pipeline
```

The hook is idempotent by contract (D-F), so running it during Save is safe, and running it there means
enforcement never depends on the client having called `/refresh` at all — closing the bypass that F6 would
otherwise leave wide open. The per-attribute cost is bounded by the number of triggering attributes on the
type, which is small in practice (Fleet's busiest object has a handful).

⚠️ This changes `ValidationService` behaviour for types with **no** triggering attributes too, in one
respect: it validates against a model-scaffolded object rather than the raw wire object. Attributes the
client omitted are then validated as absent rather than skipped. That is the correct behaviour and it is a
behaviour change; it is called out in Migration.

### D-F — the hook's contract is "establish the whole presentation state, idempotently"

Documented in `AGENTS.md` with a `⚠️`, derived from F14. A handler must not assume it is patching the
result of a previous call: every invocation starts from a model-scaffolded object, so every invocation must
re-establish everything it cares about. The idiom to recommend — taken from `ContractPartActions` — is a
private helper shared between the refresh hook and any load-time shaping.

No server-initiated cascade (`SetValueWithRefresh`) is provided. Vidyano's cascade exists because its
handlers are written per-attribute and re-enter with a fresh handler, and it ships with no re-entrancy guard
at all (F15). A single idempotent pass expresses the same outcomes without the recursion hazard.

### D-G — request/response shape

```
POST /spark/po/{objectTypeId}/refresh
{ "persistentObject": { …, "attributes": [ { "name": …, "value": …, "isValueChanged": … } ] },
  "triggeredBy": "Status" }
→ 200 ClientOperationEnvelope<PersistentObject>
```

`triggeredBy` is the attribute **name** (D-B), and for a detail-row trigger it is the existing
`{attr}[{index}].{col}` path used by `inlineErrorPath` — reusing the one addressing convention the codebase
already has for nested attributes. The envelope means a refresh can emit notifications and can open the
retry-action modal, consistent with every other mutating Spark endpoint.

The endpoint is a `IPostEndpoint, IMemberOf<PersistentObjectGroup>` with `Path => "/{objectTypeId}/refresh"`,
registered by the existing source generator with no generator change (the generator discovers actions
classes by base type, not by member).

### D-H — scheduling on the client

A `SparkRefreshCoordinator` owned by `po-form`:

- `onFieldChange(attr)` — the funnel gains its missing argument (F8/§3); every template call site already
  has `attr` in scope.
- Discrete editors dispatch immediately; text editors mark pending and dispatch on `blur` (R17/F11).
- A serial promise chain per form, with a monotonic sequence number: a response whose sequence is not the
  latest is discarded (Promise-based `firstValueFrom` cannot be cancelled — F13).
- A superseded-value guard before dispatch, mirroring Vidyano's `if (attrValue !== attr.value) return false`.
- `isRefreshing` drives a subtle busy affordance but **never** disables fields — the form is not frozen.
- `po-form.save()` awaits any pending refresh first (R18).

---

## Decisions

**D1 — the hook goes on `IPersistentObjectActions<T>`.** ~~Originally decided the other way~~ — placed
on the base class under `[NoInterfaceMember]`, to spare hand-written implementers the breaking change #301
paid deliberately for the row-security hooks. **Revised mid-implementation on the owner's instruction that
the packages are in preview and backward compatibility is not wanted.** With that constraint gone the
argument inverts: `OnRefreshAsync` is a lifecycle hook, lifecycle hooks live on the interface, and
`GetDefaultIncludes` / `StreamItems` are off it because they are opt-in *capabilities*, not lifecycle.
Note this does **not** remove the reflection in `RefreshInvoker` — the entity type is only known at
runtime, so dispatch goes through `Type` either way, exactly as `RowSecurity` does for `IsAllowedAsync`,
which is on the interface. What it buys is an honest contract, not simpler code.

**D2 — schema-only flag; the wire attribute is not extended.** Consistent with `editMode` /
`referenceDisplayType` / `isSortable`, avoids three silently-failing converter edits (F20), and makes the
"hostile client claims a trigger" corollary (F16) unreachable by construction. *Rejected: mirroring
Vidyano's wire-level `TriggersRefresh`, which exists there only because Vidyano lets handlers toggle it at
runtime for bulk edit — a feature Spark does not have (F18).*

**D3 — the response is a full object, not a diff.** Matches Vidyano (F4), and a diff would have to invent
an addressing and deletion vocabulary for something the client must recompute wholesale anyway. The cost is
bandwidth on a chatty endpoint; the mitigation is that no *option data* rides along unless the hook
replaced it.

**D4 — the client overlays; it never rebuilds `EntityType`.** F10 makes rebuilding actively harmful (a
storm of refetches per keystroke) and mutating inert (no reactivity). The overlay signal is the only shape
that is both reactive and cheap. This is a departure from Vidyano, whose client *does* replace the object
wholesale — it can afford to because its attributes carry their own options.

**D5 — server-side enforcement re-runs the hook (R13).** Without it the feature is client-side decoration
and F6 leaves the rules trivially bypassable by any client that skips `/refresh`. This is the largest piece
of scope in the PRD and the one most likely to be questioned; the alternative — trusting the submitted
`IsRequired`/`Rules` — is a client-controlled authorization decision and is not acceptable.
*Rejected: shipping the client half only and filing enforcement as a follow-up. The one-PR rule applies,
and a validation feature that does not validate is worse than none.*

**D6 — no server-initiated cascade in v1.** Vidyano's `SetValueWithRefresh` re-enters the hook with no
guard whatsoever (F15) and real handlers hand-roll recursion breaks. An idempotent single pass (D-F) covers
the same ground. If a cascade is ever wanted it can be added with a depth cap without changing the wire.

**D7 — no dynamic attribute add/remove in v1.** `AddAttribute` / `RetainAttributes` are `internal` (F22)
and making them public is a real API commitment. The five verbs in the brief are all served by mutating
existing attributes. *Deferred, not rejected — recorded in Out of scope.*

**D8 — options are replaced through the overlay, not by re-querying.** R6 for a `LookupReference` supplies
an explicit list; for a `Reference` it reassigns `Query`, which the client resolves by executing that query
once and caching it into the overlay. Reassigning `Query` alone without the overlay would not reload
anything (F10).

**D9 — Fleet hosts the sample.** `CarActions` is already the demonstration of the write pipeline and
already branches on `Status` + `IsValueChanged` with a comment about locking the record that is currently
untrue (F24); Fleet is the only demo with dedicated form pages and custom edit renderers.
*Rejected: HR, which has richer tabs/groups and a `Profession.Regime` field that maps almost exactly onto
the brief's "Contract Type = Freelance" example, but whose `PersonActions` has no lifecycle overrides at
all, so the sample would land without context. Recorded as the secondary scenario.*

**D10 — the "declared but not implemented" gate is a verify-time check, not an analyzer.** F21: the flag is
not in the compilation. It rides `--spark-verify-model`, which already exits 3 on drift.

---

## Acceptance criteria

| | Criterion | Verified by |
|---|---|---|
| **A1** | `"triggersRefresh": true` in a model file round-trips through `--spark-synchronize-model` unchanged, including on an attribute whose `dataType` or `order` the synchronizer rewrites. | integration test — **the discriminator for F19** |
| **A2** | `triggersRefresh` does not appear in `ModelFileShape`'s structural hash; adding it to a demo model file leaves `modelHashes.json` valid. | unit test + `--spark-verify-model` on the demos |
| **A3** | A hand-written `IPersistentObjectActions<T>` implementer that predates this change still compiles and runs. | compile-only fixture in the test project |
| **A4** | `OnRefreshAsync` is invoked exactly once per refresh request, with `Attribute.Name` equal to the submitted `triggeredBy`. | integration test |
| **A5** | `Attribute` is `null`, and the hook still runs, when `triggeredBy` names an attribute not present on the object. | integration test — pins D-B's null-tolerance |
| **A6** | Setting `IsRequired` / `IsReadOnly` / `IsVisible` / `Rules` / `Value` in the hook is reflected in the response. | integration test, one assertion per verb |
| **A7** | Replacing a `LookupReference`'s options in the hook is reflected in the response; reassigning a `Reference`'s `Query` is too. | integration test |
| **A8** | A refresh on a brand-new object (no id) succeeds for a caller holding `New` and is refused for one that does not. | integration test |
| **A9** | A refresh on an existing row is refused — **indistinguishably from not-found** — for a caller the row filter excludes. | integration test |
| **A10** | An attribute redacted by `GetProtectedAttributesAsync` is still redacted in a refresh response. | integration test — **the discriminator against a redaction bypass (R9)** |
| **A11** | Metadata submitted on the wire (`isRequired`, `isReadOnly`, `rules`) is ignored; only values and `isValueChanged` are honoured. | integration test — pins R10 |
| **A12** | A refresh request without an antiforgery token is rejected. | integration test |
| **A13** | Save enforces a rule that only the refresh hook imposes, **for a client that never called `/refresh`**. | integration test — **the discriminator for D5; this is the criterion the feature lives or dies on** |
| **A14** | Save accepts a value that violates a model rule the refresh hook removed. | integration test — the relaxing direction of F6 |
| **A15** | Validation behaviour is unchanged for a type with no triggering attributes. | existing suite green |
| **A16** | Applying a refresh response issues **zero** additional `/spark/queries`, `/spark/lookupref`, `/spark/types` or `/spark/permissions` requests. | vitest with a mocked service — **the discriminator for D4/F10** |
| **A17** | A value typed into field B during an in-flight refresh for field A survives, when the hook did not change B. | vitest |
| **A18** | A value the hook *did* change is applied even if the user edited it during the round-trip. | vitest |
| **A19** | Two rapid changes produce at most one in-flight request, and the superseded response is discarded. | vitest |
| **A20** | A text input does not refresh on keystroke and does refresh on blur; a select refreshes immediately. | vitest |
| **A21** | Save awaits a pending refresh before dispatching. | vitest |
| **A22** | Fields remain editable and focused during an in-flight refresh. | vitest |
| **A23** | A rule imposed by refresh blocks Save client-side with a per-field message. | vitest — pins R19 |
| **A24** | A trigger inside an AsDetail row refreshes with a `{attr}[{index}].{col}` `triggeredBy` and does not lose focus in the edited cell. | vitest |
| **A25** | `--spark-verify-model` exits non-zero when an attribute declares `triggersRefresh: true` and the entity's actions class has no `OnRefreshAsync` override. | CLI test |
| **A26** | Fleet's sample works end to end in the browser: setting `Status = Stolen` makes `PoliceReportNumber` visible and required, makes `LicensePlate` read-only, and hides `PromoVideoUrl`. | E2E test + manual browser run |
| **A27** | A form rendered inside the retry-action modal refreshes independently: its refresh neither resolves nor supersedes a refresh pending on the form that opened it. | vitest — pins R25/F24 |
| **A28** | `AGENTS.md` lists the hook in the hooks table and carries the idempotency and latency `⚠️`s. | review |

---

## Migration

**No breaking API changes.** The hook is `[NoInterfaceMember]` on the base class (D1), the flag is additive
and optional (D-A), and the endpoint is new. One behaviour change rides along:

1. ~~**`ValidationService` now validates a model-scaffolded object.** Attributes a client omitted from the
   payload are validated as absent rather than skipped.~~ **Retracted during M5 — there is no such change.**
   The original `Validate` already looked each model attribute up in the submitted object with
   `persistentObject.Attributes.FirstOrDefault(...)?.Value`, so an omitted attribute already validated as a
   null value. Scaffolding produces exactly the same null. For a type with no triggering attributes the two
   paths are point-for-point identical, which is why A15 passes unchanged rather than needing the new
   pinning test the plan budgeted for.
2. ⚠️ **Save now runs `OnRefreshAsync` for types that declare a trigger.** A hook with side effects — a
   write, a notification, an external call — will therefore run on Save as well as on refresh. The
   idempotency contract (D-F) already forbids side effects, but it is newly load-bearing and must be stated
   loudly in `AGENTS.md`.

Release-note prose:

> **`TriggersRefresh` + `OnRefreshAsync` — forms that reshape themselves.** Mark an attribute
> `"triggersRefresh": true` in your model file and override `OnRefreshAsync` on the entity's actions class.
> When that value changes, Spark calls your hook with the in-progress object and the attribute that changed,
> and you may toggle `IsRequired`, `IsReadOnly` and `IsVisible`, add or remove validation rules, replace a
> dropdown's options, or set dependent values. The rules your hook establishes are enforced on Save,
> whether or not the client ever asked for a refresh.
>
> Your hook must establish the **complete** presentation state on every call — it is handed a freshly
> scaffolded object each time, never the result of the previous call — and it must be free of side effects,
> because Spark also runs it while validating a Save.
>

---

## Out of scope / follow-ups

| Item | Why |
|---|---|
| Dynamic attribute add/remove inside the hook | `AddAttribute` / `RetainAttributes` are `internal` (F22) and making them public is an API commitment worth taking on its own evidence. All five verbs in the brief are served by mutating existing attributes. |
| Server-initiated cascade (`SetValueWithRefresh`) | D6 — Vidyano ships it with no re-entrancy guard at all (F15) and an idempotent single pass covers the same outcomes. Addable later behind a depth cap without a wire change. |
| Refreshing the *owner* object from a detail row (`TriggerRefreshOnOwner`) | A second addressing problem stacked on R20's. Worth doing only once R20 has real usage; nothing in the brief needs it. |
| `QueueQueryRefresh` — hook asks a detail grid to re-search | Adjacent and genuinely useful (F-4 note 17), but it is a *query* concern and `refreshOnCompleted` already covers the custom-action case. Separate evidence needed. |
| An `[TriggersRefresh]` C# attribute on the entity property | Vidyano has no C# equivalent either (F3); the flag is presentation, and presentation lives in the model file in this codebase. Would additionally drag `SparkModelShape` hashing into scope. |
| Client-side evaluation of rule types beyond those the server implements | R19 covers the rule types `ValidationService` already knows. A richer client rule engine is a feature in its own right. |
| Bulk-edit interaction (F18) | Spark has no bulk edit. Recorded because the interaction is real if one is ever built. |
