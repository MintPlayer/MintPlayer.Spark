# Release notes — `10.0.0-preview.65` / `@mintplayer/ng-spark@22.6.0` / `@mintplayer/ng-spark-auth@22.6.0`

Program units grow up (#324): a menu entry can now open a *page* — a specific object, a composed
start page, an external URL — and ng-spark ships the whole application shell instead of every app
copy-pasting it. The two npm packages move to the same version and stay in lockstep from here.

Full guide: `docs/guide-program-units.md`.

---

## A program unit can open a PersistentObject page

`ProgramUnit` gains two fields:

- **`objectId`** — on a `persistentObject` unit: the menu entry deep-links to
  `/po/{type}/{objectId}` instead of the type's list.
- **`url`** (with `type: "url"`) — an external link, rendered as a plain anchor. Deliberately
  its own field, not an overload of `objectId`.

The endpoint now filters by the right the unit's click will demand: `Query` for query units,
**`Read` for persistentObject units** (previously `Query` — which showed menu entries whose click
404'd), nothing for `url` units.

## `OnLoadAsync` is reshaped: id in, page out

The hook every actions class implements is now

```csharp
public virtual Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)
```

replacing `Task<T?> OnLoadAsync(IAsyncDocumentSession session, string id)`. The session is
`[Inject]`ed (it was a pass-through parameter), and **what the method returns is what the page
renders** — which finally lets an override touch the page: set a breadcrumb, fill a computed
attribute, tweak visibility, after `await base.OnLoadAsync(id, parent)`. The whole per-row read
pipeline (document load + declared includes, collection guard, row security, breadcrumbs,
mapping, redaction, per-row `can`, etag) now lives in
`DefaultPersistentObjectActions<T>.OnLoadAsync` — the base call carries it, and skipping the
base takes it over (the read-side twin of `OnSaveAsync`'s WITH CHECK caveat). The type-level
`Read` right stays framework-owned, checked before the hook runs. The base receives its
framework services via an internal `Attach(IServiceProvider)` that `ActionsResolver` calls
after construction — a subclass's hand-written constructor never threads framework plumbing.

## JSON-only virtual PO pages — no CLR class required

The start-page pattern: a type that exists **only as a model JSON file** — no `clrType`, no
entity, no documents — whose page is built in code. `EntityTypeDefinition.ClrType` is now
optional; a plain `{Name}Actions` class (no base class) is resolved by name and implements the
same `OnLoadAsync(id, parent)` — scaffolding its object via `IManager.GetPersistentObject`
(the dialog-PO idiom) instead of loading a document:

```csharp
public partial class StartPageActions
{
    [Inject] private readonly IManager manager;

    public async Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)
    {
        var obj = manager.GetPersistentObject("StartPage");
        obj["Welcome"].Value = "Hello!";
        return obj;
    }
}
```

The hook fills only attribute values; the framework squares the envelope, each piece only when
the hook left it unset: `Id` defaults to the requested id, `Breadcrumb` (the page title)
renders from the model file's `breadcrumb` template over the values just filled, and
`can.edit`/`can.delete` are forced false. Served under the type-level `Read` right. A
wrong-shaped `OnLoadAsync` throws loudly at first load; a virtual type with no actions class
404s. Everything document-shaped (query, save, delete) 404s for such a type. DemoApp's new
**Start** unit is the worked example: greeting + live collection counts, nothing behind it.
Fleet's `ConfirmDeleteCar` dialog PO also lost its marker class — JSON-only +
`IManager.GetPersistentObject` covers dialogs too.

## `programUnits.json` is validated at load

The loader canonicalizes `type` casing (`"Query"` → `"query"`) and throws
`SparkProgramUnitsConfigurationException` on an unknown type, a missing required field, or
malformed JSON — a silently dropped menu entry reads exactly like a rights problem. A missing
file stays fail-soft. This closed a real defect: the server matched `type` case-insensitively
while the client matched exactly, so a `"Query"` unit passed the filter and routed to `/`.

## `@mintplayer/ng-spark/shell` — the shipped application frame

New entry point with three components:

- **`spark-shell`** — topbar + sidebar + main over `bs-shell`, with slot structural directives
  (`*sparkShellTopbarStart/End`, `*sparkShellSidebarHeader/Top/Tabs/Footer`,
  `*sparkShellMainHeader`; default content = main). An omitted slot renders its default. Deletes
  from every host: the hand-rolled `shellState`/resize/768px block (the web component owns
  responsive behavior), the collapse-on-navigate handler (`dismissOnNavigate`), the
  `bsShellTopbar` workaround directive, and ~80 lines of shadow-DOM-seam SCSS. Theme via
  `--spark-shell-*` custom properties and `sidebarTheme`.
- **`spark-program-units`** — the server-driven menu, also usable standalone. Sorts groups AND
  units by `order`; renders `url` units as external anchors. **Consumers write zero router links
  for navigation.**
- **`spark-language-selector`** — the culture switcher every app had hand-rolled; hides itself
  with ≤ 1 language.

Auth re-fetch without a package dependency: new `SPARK_AUTH_STATE` token in `@mintplayer/ng-spark`
(root), supplied automatically by `provideSparkAuth()` — the menu re-fetches on sign-in/out.
`@mintplayer/ng-spark-auth` now peer-depends on `@mintplayer/ng-spark` for that token; the reverse
direction still doesn't exist and must not.

All four demo shells collapsed to `<spark-shell>` + slots. WebhooksDemo, whose sidebar had been
silently empty (no `programUnits.json`), gets a menu that appears on sign-in.

## Breaking / behavioral

- `RouterLinkPipe.transform` returns `string[] | null` (null for `url` units) and compares unit
  types exactly.
- A `programUnits.json` with an unknown unit type or missing required fields now fails loudly at
  first load instead of passing through.
- PersistentObject units are visible under `Read` instead of `Query` — grants that had one right
  but not the other will see menu visibility change (to match what the click always did).
