# PRD — Server-side lifecycle for New and Delete

**Status: IMPLEMENTED** on `feat/issue-386-server-side-row-lifecycle`. ✅ The blocker,
`PRD-AsDetail-Row-Identity.md`, landed as `4f9e9319` (#382) and has been built on since (#384, #391,
#392). Tracked as issue #386.
**Origin:** sidestepped from PR #381; commits `0f013ffe`, `69749631`, `89d7b235` on the abandoned
branch `feat/coverage-account-po-and-branch-deletion`.

---

## 1. Problem

**Spark has no server-side construction hook.** Not for a root persistent object, not for an AsDetail
row. The hook surface in `DefaultPersistentObjectActions` is `OnLoadAsync`, `OnSaveAsync`,
`OnDeleteAsync`, `IsAllowedAsync`, `OnQueryAsync`, `GetRowFilterAsync`, `GetProtectedAttributesAsync`,
`OnBeforeSaveAsync`, `OnAfterSaveAsync`, `OnBeforeDeleteAsync`, `OnRefreshAsync` — eleven hooks, and
none of them fires when a user clicks **New**.

Clicking New is purely a client-side act today:

- root: `spark-po-create.component.ts:60 initFormData()` builds a blank form from entity-type
  metadata (`''`, `false`, `[]`, `null`); no HTTP call happens before the user types;
- AsDetail row: `spark-po-form.component.ts:691 addInlineRow` pushes `{}`, `:700 addArrayItem` opens
  a modal on `{}`, `:715 removeArrayItem` splices by index.

So there is nowhere to put a server-computed default, a parent-derived value, or a veto — and the
`New/{Type}` right is never consulted for a click the server never sees. (It *is* consulted at save
time once `PRD-AsDetail-Row-Identity.md` R5 lands; this PRD is about the click.)

## 2. Why it *was* blocked, not merely unscheduled (resolved)

The New half is mechanically independent — it constructs a persistent object and writes nothing. The
**Delete** half is not, and the two ship together or the feature is half a feature.

A delete round-trip has to answer "which row was removed", and until row identity lands that is not a
computable question: the client sends an array, the server replaces it wholesale, and no row has a
stable key to be absent. Building the hook first produces the same defect that
`PRD-AsDetail-Row-Identity.md` §3 W1 dissects — a mechanism whose unit tests pass because they
construct the wire object by hand, over a path that never carries a key.

**Row identity enables this feature.** ✅ It landed first, and the prediction held: the delete
half is built entirely on the row key, and `SparkDeleteRowArgs.RowKey` is the argument that would
have had nothing to hold before #382.

## 3. What already exists, and what it is worth

The three abandoned commits are unusually complete on the server and untouched on the client. None of
their code has moved since, so re-applying is close to trivial.

| Commit | Contents | State |
|---|---|---|
| `0f013ffe` | `SparkNewArgs<T>` (sealed, get-only, internal ctor); `OnNewAsync` as a default interface method so no hand-written implementer breaks; `PersistentObject.SetValue` marks `IsValueChanged`, new `SetOriginalValue` does not | reads finished; no tests |
| `69749631` | `POST /spark/po/{objectTypeId}/new` (206 lines): standalone and AsDetail-row paths, every failure collapsed to one refusal so none is an existence oracle, `SparkValidationException` → 400 so a hook may veto; `NewInvoker` mirroring `RefreshInvoker`'s exact-signature resolution | reads finished; no tests |
| `89d7b235` | `EntityTypeDefinition.ServerSideRowLifecycle`; reversed the authorization decision | flag inert — declared, documented, **read by nothing** |

⚠️ **The authorization decision flipped between the two commits, and the later one is right.**
`69749631` used the *parent's* right (`New/Person` to add a phone number), reasoning that nested types
are absent from `security.json`. That reasoning is false — `apps/HR/HR/App_Data/security.json:83`
grants `QueryReadEditNewDelete/CarreerJob`, and `GetPermissions.cs:34-40` already serves it.
`89d7b235` reversed to the row type's own right, which matches both the button the client renders and
`PRD-AsDetail-Row-Identity.md` R5. Keep the reversal; discard the original rationale.

⚠️ **Do not resurrect `488ebd35`** from the same branch. That is the `EnforceRowRightsAsync` the
row-identity PRD dissects as W1. These three commits are unaffected by it.

## 4. Design

### L1 — Both halves, or neither

New round-trips to `OnNewAsync`; Delete round-trips to a delete hook. Shipping New alone leaves the
asymmetry that motivated the work.

### L2 — Opt in per row type, and the flag is never a security control

`ServerSideRowLifecycle` on the row type's own model file decides whether the client round-trips.
⚠️ Its own doc comment already states the rule and it must survive implementation: **save-time
enforcement must never read this flag.** It is an unhashed model field; gating a rights check on it
would let a one-line model edit switch that check off. R5's enforcement stays unconditional.

### L3 — The row arrives keyed

⚠️ Today `/new` returns a row with **no key at all**: `New.cs` scaffolds through
`entityMapper.GetPersistentObject(entityType.Id)`, which builds a blank PO from the *model* and never
constructs the CLR entity — so the `Guid.NewGuid()` field initializer (row-identity R2) never runs.

That happens to be consistent with the save-path merge (an unmatched row correctly takes the
`Activator.CreateInstance` branch), but only by accident, and it means `OnNewAsync` cannot see or set
the identity of the row it is building. Mint the key and set `po.Id` at the top of
`HandleAsDetailRowAsync`, before the hook runs, so a hook can reference the row — for an audit entry,
a cross-row default, or a parent-scoped `(root id, child key)` pair.

### L4 — Authorization is the row type's own right

`New/{RowType}`, with the parent loaded as a **separate** gate: the row type's right says the caller
may create rows of this kind, not which parent they may attach one to.

### L5 — Non-goals

- Re-litigating where per-row rights are enforced on save. That is row-identity R5 and it is settled.
- A general client-side "server round trip on every field change". `OnRefreshAsync` already exists.

## 5. What was missing, and what was built

- ~~**Zero tests** across all three commits.~~ ✅ 20 added: 3 on `SetValue`/`SetOriginalValue`, 9 on the two invokers, 3 on the flag's hash exclusion, 4 on the row arriving keyed, plus 10 client specs and 8 E2E facts.
- **No client wiring at all.** `po-create` still builds the form locally; `addInlineRow` /
  `addArrayItem` still push `{}`. Nothing in `ng-spark` posts to `/new` — the endpoint registers
  through `IPostEndpoint` + `PersistentObjectGroup`, so it exists at runtime and is simply unreached.
- **No Delete counterpart**, though `ServerSideRowLifecycle`'s doc comment promises one.
- **No model-file emitter** for the flag and no exposure of it on the client `EntityType`.
- ✅ **`SetValue` becoming dirty-marking was audited and is safe.** Measured on the current tree:
  `EntityMapper` never reads `IsValueChanged` — the write path writes every attribute it is given —
  so marking dirty changes nothing about what is persisted. Exactly one production caller exists
  (`HR/Actions/CarreerJobActions.cs:47`, clearing a field on refresh, where dirty-marking is right),
  and `SetOriginalValue` had zero. The flag changes an outcome in only two places, neither reachable
  from a `SetValue` call site: `SyncActionInterceptor` (which properties a replicated save reports)
  and `CarActions.OnBeforeSaveAsync` (a retry prompt), both reading the client's flag.
  ⚠️ The residual risk is real and is why the hook's doc insists on `SetOriginalValue`: a
  construction hook using `SetValue` on a **replicated** type would widen that `Properties[]` array
  with fields nobody touched. Nothing enforces this but the doc comment and the test.

## 6. Acceptance criteria

1. Clicking New on an opted-in AsDetail row type issues one request and the returned row carries
   server-set defaults.
2. A hook throwing `SparkValidationException` surfaces as a 400 the user can read, not a discarded
   save.
3. Removing a row on an opted-in type invokes the delete hook, and a hook that refuses leaves the
   collection unchanged.
4. A type **not** opted in behaves exactly as today — no request.
5. ⚠️ Toggling `ServerSideRowLifecycle` changes no rights outcome whatsoever. Test it explicitly:
   the flag is unhashed, so this is the property that stops it becoming a security control.
6. At least one E2E through a real detail grid. `tests/MintPlayer.Spark.E2E.Tests` has only smoke and
   return-url coverage today, and per the row-identity PRD's own criteria a green unit suite over a
   hand-built wire object proves nothing about the path.

## 7. Sizing

One PR, medium. The server half is written; the work is the half never started.

| | |
|---|---|
| Re-apply the three commits (no touched code has moved) | trivial |
| Mint and propagate the row key into `/new`; expose on `SparkNewArgs` if hooks need it | small |
| Delete round-trip + make the flag actually gate it + model emitter + client `EntityType` exposure | medium |
| Client wiring, error and veto surfacing | medium — touches every AsDetail form |
| Tests, including the E2E | medium |

## 8. Decisions taken during implementation

### D1 — The flag gates the client, and the server does not check it

The plan's L4 said "a gate in `New.cs` and the delete path". **That was not built, deliberately.**

`ServerSideRowLifecycle` decides whether the *client* round-trips. The endpoints check rights and
nothing else, so they behave identically whether the flag is on, off or absent. Two reasons:

1. **A server gate buys nothing.** Both endpoints already require the row type's own `New`/`Delete`
   right and re-load the parent through its Read right, collection guard and row filter. A caller who
   reaches them with the flag off learns only what those rights already permit.
2. **It is the drift that the flag's own doc warns about.** Once an unhashed model field decides
   whether a request is *served*, the distance to it deciding whether a request is *checked* is one
   plausible-looking refactor. Keeping the endpoints unconditional means there is no such precedent
   to follow.

`ServerSideRowLifecycleFlagTests` pins the hash exclusion that makes this safe, with a control case
so the suite cannot pass by having stopped reading the file.

### D2 — The delete round-trip is an affordance, and says so

`OnDeleteRowAsync` can refuse, but a refusal only stops a cooperating client: a caller who never
posts to `/delete-row` and submits the parent with the row already gone reaches the same end state.
What covers that caller is the save path's per-row `Delete/{RowType}` enforcement (row identity R5,
`EntityMapper`), which runs on every embedded collection unconditionally.

This is stated three times — on `SparkDeleteRowArgs`, on the interface member, and in
`ServiceEntryActions` — because the failure mode is an author reasonably believing the hook is the
enforcement point and putting the only check there.

### D3 — `/new` constructs the CLR instance rather than minting a guid

L2 said "mint the key and set `po.Id`". Constructing the entity through `Activator.CreateInstance`
and running `PopulateAttributeValues` over it does that *and* one more thing: every C# property
initializer becomes the default the user sees, which is how a .NET developer expects to state one.
A type with no accessible parameterless constructor falls back to the model's shape rather than
failing — it simply cannot contribute defaults.

### D4 — `POST /{type}/delete-row`, not `DELETE`

The framework's real delete route is `DELETE /{objectTypeId}/{**id}`, whose catch-all segment
swallows any sibling `DELETE` route. The operation also needs a body. Both point the same way.

### D5 — `OnDeleteRowAsync`, never an overload of `OnDeleteAsync`

`DatabaseAccess` resolves the document delete hook by **name alone** (`GetCachedActionMethod(...,
"OnDeleteAsync")`). A second `OnDeleteAsync` would make every document delete in the framework throw
`AmbiguousMatchException` at runtime, on a path this feature does not otherwise touch.

### D6 — The demo lives in Fleet, because that is the only app the E2E suite hosts

`ServiceEntry` on `Car` is a real embedded collection with a real reason for both hooks: a new entry
wants a server-set date and the vehicle's plate; an invoiced entry must not silently vanish. Fleet's
`security.json` grants administrators `QueryReadEditNewDelete/ServiceEntry` and fleet managers only
`ReadEdit` — so the row type's own right is observably the one that governs, not the parent's.

## 9. Defects the tests found, none of which was in the feature as designed

All three were latent, all three were silent, and none would have been found by reading the code.

### F1 — Two invokers shared one constructor cache and handed each other the wrong constructor

`ReflectionCache.GetOrAdd<TKey, TValue>` is **one dictionary per `(TKey, TValue)` pair**, so three
call sites keying a `ConstructorInfo` by bare entity `Type` — `RefreshInvoker`, `NewInvoker`,
`DeleteRowInvoker` — shared it. Whichever ran first for a given type won; the others got its
constructor and invoked it with their own arguments.

It surfaced as `DeleteRowInvoker` calling `SparkNewArgs`'s constructor and failing on the third
argument (`PersistentObject` vs `string`). ⚠️ **`RefreshInvoker` and `NewInvoker` were already
colliding on master**, from the salvaged commits — it had simply never fired, because nothing called
`/new`. Any type with both a refresh hook and a construction hook would have hit it on the first
request. All three keys are now discriminated.

### F2 — A hook's refusal surfaced as a 500, not a 400

`MethodBase.Invoke` wraps whatever the invoked method throws in `TargetInvocationException`, so
`catch (SparkValidationException)` in `New.cs` matched nothing. The veto path the salvaged commit
advertised — "a hook may refuse; `SparkValidationException` becomes a 400 the user can read" — had
never worked. Both invokers now pass `BindingFlags.DoNotWrapExceptions`, which also keeps the hook's
own stack trace.

### F3 — `[ValueObject]` is silently inert without the generator reference

`Fleet.Library` did not reference `MintPlayer.Spark.LibraryGenerators`, and analyzer
`ProjectReference`s are not transitive. `[ValueObject]` on `ServiceEntry` therefore compiled cleanly
and generated **nothing**: no key property, no registration.

Nothing failed. The startup key gate only inspects types that registered, so an unregistered one is
never looked at; the rows simply reached the client with a null id. `HR.Library` carries the
reference with a comment explaining exactly this, which is the only reason HR works.

⚠️ **This is a framework-level gap, not a Fleet typo.** SPARK017 answers "should this type be a value
object"; nothing answers "this type is marked and the generator never ran". The E2E is what caught
it, and only because it asserted on the key *as it arrives over the wire* — every in-memory check
passes, since a keyless row deserialises with a freshly minted guid.
