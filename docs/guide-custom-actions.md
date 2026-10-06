# Custom Actions

Spark lets you define server-side actions that users can trigger from entity detail pages or query list views. A custom action combines a C# implementation with JSON-based metadata for display name, icon, selection rules, and authorization.

## Overview

A custom action has three parts:

1. **C# implementation** -- a class that implements `ICustomAction` (or extends `SparkCustomAction`)
2. **JSON configuration** -- an entry in `App_Data/actions.json` (icon, where it shows, selection rule), with its texts in `translations.json`
3. **Authorization** (optional) -- entries in `App_Data/security.json` controlling who can execute the action

## Step 1: Create the Action Class

Implement `ICustomAction` or extend `SparkCustomAction`. The class name determines the action name: strip the optional `Action` suffix. For example, `CarCopyAction` maps to action name `CarCopy`.

```csharp
// Fleet/CustomActions/CarCopyAction.cs
using Fleet.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Actions;

namespace Fleet.CustomActions;

public partial class CarCopyAction : SparkCustomAction
{
    [Inject] private readonly IDatabaseAccess dbAccess;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken)
    {
        // Coalesce on IDS. A selected row is a QueryResultItem and the parent is a
        // PersistentObject, so the two deliberately do not unify: a row is not a document.
        var carId = args.Parent?.Id ?? args.SelectedItems.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException("No item selected");

        var car = await dbAccess.GetDocumentAsync<Car>(carId);
        if (car == null)
            throw new InvalidOperationException("Car not found");

        var copy = new Car
        {
            LicensePlate = $"{car.LicensePlate} (copy)",
            Model = car.Model,
            Year = car.Year,
            Color = car.Color,
            Brand = car.Brand,
            Status = car.Status,
        };

        await dbAccess.SaveDocumentAsync(copy);
    }
}
```

Key points:
- Use `[Inject]` for dependency injection (requires `partial` class)
- `args.Parent` is populated when invoked from a detail page
- `args.SelectedItems` is populated when invoked from a query list -- the rows the grid had,
  as `QueryResultItem`s. Call `MaterializeAsync` when you need the entity behind a row
- Use `IDatabaseAccess` (or your own services) for data operations
- Throw exceptions for errors -- they are caught and returned as 500 responses

### Writing so the next query sees it

The grid that ran the action re-fetches as soon as the action answers. A write through `IDatabaseAccess`
(`SavePersistentObjectAsync`, `DeletePersistentObjectAsync`, `DeletePersistentObjectsAsync`,
`SaveDocumentUncheckedAsync`, `DeleteDocumentUncheckedAsync`) commits, then waits — bounded at 15 s,
never failing the committed write — for the indexes over the collections it wrote, so that re-fetch
already contains the new or changed row
([interceptors guide §1a](guide-interceptors.md#1a-the-callers-next-query-sees-the-write)).

An action that calls `session.SaveChangesAsync()` on the injected `IAsyncDocumentSession` itself gets
no such wait: under load the re-fetched grid can miss the row the action just wrote. To get the same
behaviour, make the write through `IDatabaseAccess` instead — `SavePersistentObjectAsync` for a
persistent object (the full pipeline: rights, interceptors, row rules), or
`SaveDocumentUncheckedAsync(entity)` for a plain document the action has already authorized. Both
commit everything else pending in the request session in the same `SaveChanges`, so side documents
stored in that session first are covered too:

```csharp
await session.StoreAsync(auditEntry);              // pending in the request session
await dbAccess.SaveDocumentUncheckedAsync(order);  // commits both, then waits for their indexes
```

### The CustomActionArgs Class

```csharp
public class CustomActionArgs
{
    /// The parent PersistentObject (when invoked from a detail view),
    /// re-loaded server-side and row-checked. Null when the request named none.
    public PersistentObject? Parent { get; set; }

    /// The rows selected in a query, as the grid had them. Empty on a detail view.
    public QueryResultItem[] SelectedItems { get; set; } = [];

    /// The parent exactly as the client submitted it -- untrusted, for actions that edit.
    public PersistentObject? SubmittedParent { get; set; }

    /// The ids the client named, before resolution. Rarely needed -- see below.
    public string[] SubmittedSelectedItemIds { get; set; } = [];
}
```

`SelectedItems` holds the **rows the grid displayed** -- `QueryResultItem`s: an id, a display string,
and a value per query column. The client posts `selectedItemIds` and the query it came from; the
server **re-runs that query narrowed to those ids** and hands you the result.

Re-running rather than loading documents is what keeps the rows faithful. A column computed inside an
index and stored there exists on no document, so loading by id would return it as null -- silently.
And a composed query has no documents at all, so it could not be materialized by id in the first
place.

The query id is client-supplied but narrowing-only: the `Query` right is enforced on it
independently, and its entity type must match the action's type or the request is refused.

To act on the **entity** behind a row, materialize it -- see below. A row is deliberately weak: no
attribute metadata, no `can` block, no etag. It is not a document and cannot be saved.

### Returning a result (#460, T5)

An action can hand a value back to its caller:

```csharp
public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken ct = default)
{
    var job = await exports.StartAsync(args.SelectedItems.Select(r => r.Id), ct);
    args.SetResult(new { jobId = job.Id, rows = args.SelectedItems.Length });
}
```

It travels as the response envelope's `result`. In Angular,
`sparkService.executeCustomAction<T>(...)` resolves to it (`undefined` when nothing was set); in .NET,
`SparkActionResult.Result` holds the JSON and `GetResult<T>()` reads it typed. The last `SetResult`
wins, and a retry prompt (449) never carries one — the attempt that completes does.

⚠️ **The value bypasses redaction and row security.** The framework serializes it as given. Returning
an entity hands the caller every field on it, including ones `GetProtectedAttributesAsync` hides on
every read path — return a purpose-built shape.

### SparkCustomAction vs ICustomAction

You can either extend `SparkCustomAction` (convenience base class) or implement `ICustomAction` directly. Both approaches work identically. The base class currently provides the same abstract method, but in a future phase it will add helper methods for navigation and notifications (same mechanism as PersistentObject Actions classes).

## Step 2: Configure actions.json

Add the action to `App_Data/actions.json` in your application. Each key is the action name (must match the C# class name minus the `Action` suffix).

```json
{
  "CarCopy": {
    "icon": "Copy",
    "showedOn": "both",
    "selectionRule": "=1",
    "refreshOnCompleted": true
  }
}
```

The file holds no translated text (#467, D1). The label, the description and the confirmation live in
`translations.json`, under conventional keys:

```json
{
  "actions": {
    "CarCopy": {
      "label":        { "en": "Copy Car", "fr": "Copier la voiture", "nl": "Auto kopiëren" },
      "description":  { "en": "Creates a copy of the selected car" },
      "confirmation": { "en": "Copy {count} car(s)?" }
    }
  }
}
```

An untranslated label shows the humanized name (`CarCopy` → `Car Copy`). The confirmation is asked
only when `actions.{Name}.confirmation` is translated, or when the file names a `confirmation` key;
`{count}` is replaced with the number of rows the action is about to act on. The file refuses
embedded text, and the pre-#467 `displayName` and `confirmationMessageKey` properties, with a
message naming the key to use.

### Configuration Properties

| Property | Type | Description |
|---|---|---|
| `label` | string | An explicit translation key for the label, instead of `actions.{Name}.label` |
| `description` | string | An explicit translation key for the description, instead of `actions.{Name}.description` |
| `confirmation` | string or `false` | An explicit translation key for the confirmation, instead of `actions.{Name}.confirmation`; `false` never asks |
| `icon` | string | Icon name (displayed next to the action label) |
| `showedOn` | string | Where the action appears: `"detail"`, `"query"`, or `"both"` (default: `"both"`) |
| `selectionRule` | string | For query views: how many items must be selected. See below. |
| `refreshOnCompleted` | boolean | Whether the UI should refresh after successful execution |
| `variant` | string | `"primary"`, `"secondary"`, `"danger"`, `"warning"`: presentation only |
| `offset` | number | Display order (lower values appear first). Default: `0` |

### Layers: the libraries' actions.json and yours (#467, D7)

`actions.json` is composed in layers. The core library ships the first one, with the built-in New,
Edit and Delete; any library may ship its own; your application's file composes on top **per
property**:

- A property your file states replaces the inherited one; one it leaves out keeps it.
- A property set to `null` resets it to the default.
- `"Edit": null` removes the inherited action from every page. Removal is presentation: rights still
  decide who may run what.
- A name no library declares adds an action.

Libraries stack in dependency order: the core first, each library above the libraries it depends
on, by assembly name between unrelated ones. A library restating an action of a library it depends
on overrides it silently. Two **unrelated** libraries that state the same property of the same
action differently get warning SPARK036 at build time and a warning in the log at startup; the
later one wins, and your file decides by stating the property. Run the application with
`--spark-print-effective-actions` to print the composed catalogue, with the layer each property
came from.

A library ships its layer by keeping the file in its own `App_Data/actions.json` and naming itself
with an alias; the Spark source generator compiles every `App_Data` layer file into the assembly
(SPARK041 when the alias is missing):

```xml
<PropertyGroup>
  <SparkLibraryAlias>my-library</SparkLibraryAlias>
</PropertyGroup>
```

### Selection Rules

`selectionRule` is a **cardinality expression over the number of selected rows**. It disables the
action's button while unsatisfied, and — since `10.0.0-preview.61` — is **enforced on the server**,
which answers `400` to a request that violates it.

`X` is the count. Whitespace is insignificant, terms split on `X` are combined with AND (so a range
is `1<X<5`), and a number-first term is mirrored (`0<X` means `>0`).

| Rule | Meaning |
|---|---|
| omitted / `null` | No requirement |
| `"=0"` | Exactly zero — the action is **disabled once anything is selected** |
| `"=1"` | Exactly one |
| `">0"` / `">=1"` | One or more |
| `"<=5"` | At most five |
| `"!=0"` | Any non-empty selection |
| `"1<X<5"` | Between two and four |

Operators are `<=`, `>=`, `<`, `>`, `!=`, `=`.

**A malformed rule is refused when the configuration loads, not silently permitted.** `"1-5"`,
`"*"` and `"=abc"` are all rejected, and every offender in the catalogue is named at once. The
catalogue is composed at startup, so this refuses to start rather than failing on a click. (Vidyano, where this syntax comes from, treats anything
unparseable as "always true" — safe for a greyed-out button, wrong for a server-side gate, where it
would let any selection through.)

**The rule applies to the query path only.** An action invoked from a detail page names a parent
rather than a selection, so its count is zero by definition; enforcing there would break every
`showedOn: "both"` action. `Demo/Fleet`'s `CarCopy` is exactly that shape.

⚠️ **`selectionRule` is not an authorization boundary.** It is input validation and a UI affordance.
The gate is the action's grant, enforced regardless of which query the caller clicked from — a
caller can always POST directly.

### File Watching

Your `actions.json` is cached in memory and watched with `FileSystemWatcher` (written, created, renamed or deleted). A change invalidates the cache; no restart is needed. The library layers are compiled in and change only with a rebuild.

## Step 3: Authorization (Optional)

If your application uses Spark Authorization, add entries to `App_Data/security.json` to control who can execute each action. The authorization resource follows the pattern `{ActionName}/{EntityTypeName}`:

```json
{
  "groups": {
    "a1b2c3d4-0000-0000-0000-000000000001": "Administrators",
    "a1b2c3d4-0000-0000-0000-000000000002": "FleetManagers"
  },
  "rights": [
    {
      "id": "ca000001-0000-0000-0000-000000000001",
      "resource": "CarCopy/Car",
      "groupId": "a1b2c3d4-0000-0000-0000-000000000001",
      "isDenied": false
    },
    {
      "id": "ca000001-0000-0000-0000-000000000002",
      "resource": "CarCopy/Car",
      "groupId": "a1b2c3d4-0000-0000-0000-000000000002",
      "isDenied": false
    }
  ]
}
```

In this example, both Administrators and Fleet managers can execute the `CarCopy` action on `Car` entities. Other groups are denied by default.

If no authorization is configured, the default is **deny**, not allow — `PermissionService` refuses
everything until `security.json` grants it. A custom action's resource is `{ActionName}/{Type}`.

**A custom action that names rows also requires `Read/{Type}`.** Every parent and selected item is
re-loaded through the row-gated read path before the action runs, and that load applies the
type-level `Read` right first. So granting `CarCopy/Car` alone is not sufficient for an action that
receives a parent or a selection — the caller needs `Read/Car` too. An action that names no rows (a
pure command) has no such requirement.

## Default actions, selection and sub-queries (#460, M15)

An action is defined once per type, and every query of that type offers it, whether the query is a
top-level list or a sub-query on a parent's detail page. That is the Vidyano model. M15 adds the
parts that were missing: the built-in `New` and `Delete` as catalogue entries, a declared selection
mode, and a toolbar and row menu shared by both grids.

### New, Edit and Delete are catalogue entries (#460 D18, #467 D7)

`/spark/actions/list` also returns the framework's three built-in actions, which the core library's
`actions.json` declares. Each is marked `"isDefault": true` and appears only when the caller holds
the ordinary right, `New/T`, `Edit/T` or `Delete/T`:

| Name | `showedOn` | `selectionRule` | Runs through |
|---|---|---|---|
| `New` | `query` | none | the create page → `POST /spark/po/new` → `POST /spark/po/create` |
| `Edit` | `both` | `=1` | the edit page → `POST /spark/po/update` |
| `Delete` | `both` | `>0` | `POST /spark/po/delete-many` (a query), `POST /spark/po/delete` (the detail page) |

The detail page's Edit and Delete buttons are these entries (D8): an app that removes `Edit` or
moves it to `"showedOn": "query"` removes the detail page's button too. The row's own `can.edit` /
`can.delete` (row security) must also allow it, and the selection rule does not apply there.

**To override one**, state the properties to change in your `actions.json`; it needs no C# class.
The texts are `actions.New.label`, `actions.Edit.label`, `actions.Delete.label` and
`actions.Delete.confirmation` (with `{count}`), which your `translations.json` may override per
language.

```json
{
  "Delete": { "selectionRule": "=1" },
  "Edit": null
}
```

An `ICustomAction` class named `New`, `Edit` or `Delete` is never executed: `/spark/actions/execute`
answers 404 for those names.

### The bulk Delete

```
POST /spark/po/delete-many
{ "objectTypeId": "Answer",
  "items": [{ "id": "answers/1-A", "etag": "A:12-…" }, { "id": "answers/2-A", "etag": "A:15-…" }],
  "queryId": "question-answers", "parentId": "questions/1-A", "parentType": "Question" }
```

Each row carries the `etag` the list showed it with (#467, D14): the delete removes *that version*.
A row someone changed or deleted since the list loaded refuses the whole request with **409**, and
the grid asks the user to reload and tick again. A row is never removed in a version its user did
not see. The single `/spark/po/delete` and `/spark/po/purge` take an `etag` the same way.

One request runs every row through the ordinary delete pipeline, in this order:
1. The **200-row cap**, the `Delete` entry's **rule**, a **`queryId`** and an **`etag` on every row**
   are checked first. Any failure is a 400, and nothing touches the database. `queryId` is required
   (#467, D12).
2. The sub-query's container, loaded through its own gated read.
3. The rows are fetched **through the named query** — its right, filter, row filter and parent — and
   must all be **readable**: the `Read` right and the `Read` row rule (#467, D11). A row the query does
   not return, or the caller cannot read, refuses the lot exactly like a missing id (404, M-3).
   Naming another query of the same type therefore cannot dodge a query's own decisions.
4. The `Delete/T` right, the collection guard and the `Delete` row rule.
5. `OnDisableActionsAsync` is asked about the query target (with the parent) and about every row, in
   one batched call. **One row that withholds `Delete` refuses the whole request with 403.**
6. The delete replacement and the before-delete interceptors run for each row, so a soft-deletable type is
   soft-deleted, with the request's one `reason` on every row (#467, D20).
7. Every write is committed by **one `SaveChanges`**, each row at the version its etag names: all
   rows or none (a stale row is the 409 above).

**One refusal names every row that failed (#467, D18).** A row the `Delete` rule refuses, a row whose
interceptor withholds `Delete`, and a row a before-delete interceptor refuses (a Moderation lock) are listed by breadcrumb
— with the reason, when there is one — so the user knows what to untick: "These items cannot be
deleted: Re: pricing (This post is locked)." Every row named passed the Read gate, so naming it
discloses nothing; a row that is missing or unreadable is never named. The server never deletes 198
of 200 and says nothing.

A custom action on a selection follows the same rules: with ids and no parent it needs `queryId`
(400 otherwise), and its rows come through that query and must be readable.

No interceptor and no Actions class can commit a row early (#482): the framework owns the single commit. A
before-delete interceptor that prompts with `Retry.Action` refuses its row instead (a per-row prompt across a
selection is unworkable), and one that throws `SparkCancelException` cancels the whole batch.

There is no bulk Purge. A purge deletes revisions with an admin operation that cannot join the
transaction, so it stays one row at a time through `/spark/po/purge`.

### Selection mode (D17)

A query declares `"selectionMode": "auto" | "none" | "multiple"` (`single` was removed in #467, D10).
The parent type's `queries` entry can override it for one parent:

```json
"persistentObject": {
  "name": "Question",
  "queries": [
    "question-tags",
    { "query": "question-answers", "selectionMode": "multiple" }
  ]
}
```

- An entry is a bare alias, as before, or an object.
- Model sync writes a bare alias back for an entry without overrides, so existing model files do not
  change.
- `auto`, the default, derives the mode from the actions the caller can use **on this list**
  (#467, R1). An action counts when all of these hold:
  1. the caller holds its right (the list endpoint only returns those);
  2. its `showedOn` includes the query;
  3. the result does not withhold it (`disabledActions`; Edit also not when `Save` is), and the
     recycle bin does not hide it;
  4. its `selectionRule` accepts at least one row (`=0` does not).
- Built-in Edit and Delete count like any other action. One counting action gives `multiple`;
  none gives `none`, so a read-only user sees a list without checkboxes.
- The mode is recomputed when `disabledActions` or the deleted mode changes, and the selection is
  cleared when it becomes `none`.
- There is no single selection: a selectable list always has the checkbox column, and an action
  whose rule does not match the count is disabled (Edit with two rows ticked), never trimmed (D10).
- A row click always opens the row; only the checkbox cell selects (D9).
- Selection is presentation only. Every action that takes rows still enforces its own rule at submit.

### The toolbar, the chip and the row menu

The grid builds one toolbar model, and both hosts render it: the sub-query card's header and the
query-list page's action bar.
- **Toolbar:** `New`, `Edit`, `Delete` and the custom actions, with the catalogue's labels and
  icons. Each is enabled live from the selection count.
  - `New` needs the right, and a result that does not withhold `New`.
  - `Edit` (`=1`) opens the ticked row's edit page, with the list as the return state. It is
    withheld when the result withholds `Edit` or `Save`.
  - `Edit` and `Delete` are shown whenever they count; a list declared `none` leaves them to the
    row menu.
  - The card puts its caption on the left and the actions on the right, with the overflow in the
    priority nav under the translated "More" label (`common.more`), as on the list and detail pages.
- **Selection bar:** an "N selected" chip while rows are selected. The selection survives paging
  and virtual scroll (D19), so the chip counts every ticked row, says how many are on other pages
  ("7 selected · 4 on other pages"), and its ⊗ clears them all. Actions receive exactly what the
  chip counts. Search, a column filter and a deleted-mode change clear the selection. There is no
  select-all (with paged, lazy or virtual-scrolled rows it could only tick the loaded rows).
- **Row menu (`⋮`):** every offered action whose rule accepts exactly one row, including Edit and Delete. It
  runs on that row only and leaves the checkbox selection alone. An action without a rule acts on the
  query, not on a row, so it is not in the menu.
- **Search box:** the card's header ends with `<spark-search-box>` (`@mintplayer/ng-spark/grid`), the
  same component the query-list page uses (300 ms debounce, clear button, Escape clears). It feeds the
  grid's `search`, which `/spark/queries/execute` applies together with `parentId`/`parentType`. The
  column filters' value lists get the same term as `querySearch` on `/spark/queries/distinct-values`,
  so they only list values from rows the grid shows. A new term clears the selection.
  `[searchable]="false"` hides the box.
- **Recycle bin:** while the grid lists `deleted: 'only'`, or sits under a deleted parent (the card's
  `[parentDeleted]`, set by the detail page for a row opened with `?deleted=only`), the toolbar and
  the row menu are empty: no New, no Delete, no custom action. The recycle bin offers only Restore and
  Purge. The server agrees: `/spark/po/delete-many` and `/spark/actions/execute` judge live rows only,
  so an action aimed at a deleted row is a 404.
- **`parentDeleted`:** a sub-query under a deleted parent sends `parentDeleted: 'include'`
  (`exclude | include | only`) on execute and distinct-values, the mode the *parent* is resolved
  under. Only a holder of `ViewDeleted/{ParentType}` gets the widening; anyone else gets the same 404
  as a missing parent. The rows' own `deleted` is independent. Actions and delete-many always resolve
  their parent as a live row.

### New from a sub-query: `OnNewAsync` (D19)

Pressing New on a sub-query opens the create page and passes the parent along as query parameters:
`?parentId=…&parentType=…&queryId=…`. The parent therefore survives the navigation and a reload of
the page. The create page asks the server for its blank object:

```
POST /spark/po/new
{ "objectTypeId": "Answer", "parentId": "questions/1-A", "parentType": "Question", "queryId": "question-answers" }
```

- The parent is loaded through its gated read.
- The query must be one of the parent type's `queries`, and must list the type being created.
  Otherwise the request is refused, like a missing row.
- The Actions class's existing `OnNewAsync(SparkNewArgs<T> args)` then receives `args.Parent`,
  `args.ParentType`, `args.Query` and `args.ParentReference`.

**The base `OnNewAsync` fills the parent reference** through `args.FillParentReference()`:
- It looks for the one `Reference` attribute of the new object whose target is the parent's type.
- If there is exactly one, it is set to the parent's id.
- If there are none, or several, nothing is filled, and that is logged at Debug.
- When a type references the parent more than once, name the attribute with `"parentReference"` on
  the query or on the sub-query entry. A name that does not exist, or that references another type,
  fails at startup and in `--spark-verify-model`.

```csharp
public override async Task OnNewAsync(SparkNewArgs<Answer> args)
{
    await base.OnNewAsync(args);                 // keeps the auto-fill
    args.PersistentObject[nameof(Answer.Body)].SetOriginalValue("Thanks for asking!");
}
```

⚠️ An override that does not call `base.OnNewAsync` loses the auto-fill, which is intended: the hook
then owns the initialisation. Call `args.FillParentReference()` to keep the auto-fill without calling
base.

A standalone New and an `AsDetail` row get no auto-fill. An embedded row's parent owns the save.

## REST API

Spark exposes two endpoints for custom actions under the `/spark/actions` prefix:

### List Available Actions

```
POST /spark/actions/list

{ "objectTypeId": "Car" }
```

Returns the list of custom actions available for the given entity type. Only actions with a matching C# implementation **and** authorized for the current user are included. The response is sorted by `offset`.

**Response:**

```json
[
  {
    "name": "CarCopy",
    "label": { "en": "Copy Car", "fr": "Copier la voiture", "nl": "Auto kopiëren" },
    "icon": "Copy",
    "description": { "en": "Creates a copy of the selected car" },
    "showedOn": "both",
    "selectionRule": "=1",
    "refreshOnCompleted": true,
    "confirmation": { "en": "Are you sure?", "fr": "Êtes-vous sûr ?", "nl": "Weet u het zeker?" },
    "offset": 0
  }
]
```

### Execute an Action

```
POST /spark/actions/execute
```

Executes the action. This endpoint requires an antiforgery token (`X-XSRF-TOKEN` header).

**Request body:**

```json
{
  "objectTypeId": "Car",
  "actionName": "CarCopy",
  "parent": { "id": "cars/1-A", "name": "Car" },
  "selectedItems": []
}
```

`objectTypeId` and `actionName` used to be path segments. They are ordinary body fields now — every
Spark path is literal, with no route variables at all — so an action name containing a space or a slash
needs no escaping and cannot reshape the route.

⚠️ `objectTypeId` here is a **request parameter**, and it is not the same thing as an `objectTypeId`
nested inside `parent`. The server authorizes against this one and overwrites the nested one; a payload
claiming a different type changes nothing and buys no access.

The `parent` field is set when executing from a detail view. The `selectedItems` array is set when executing from a query list with selected rows.

**Responses:**

| Status | Description |
|---|---|
| 200 | Action executed successfully |
| 401 | Authentication required |
| 403 | Access denied (user lacks permission) |
| 404 | Entity type or action not found |
| 449 | Retry action required (see Manager & Retry Actions guide) |
| 500 | Action threw an exception |

## Action Name Resolution

The `CustomActionResolver` discovers action classes at startup by scanning all loaded assemblies for types that implement `ICustomAction`. The action name is derived from the class name:

- `CarCopyAction` -> `CarCopy` (strips `Action` suffix)
- `ExportData` -> `ExportData` (no suffix to strip)

Name matching is case-insensitive.

The resolved name may not be a reserved verb (`Edit`, `Delete`, `Restore`, `RevertContribution`, …):
the action would share that verb's right. SPARK023 refuses it at build time and `UseSpark()` at
startup; see [reserved verbs](guide-authorization.md#reserved-verbs).

The key in `actions.json` must match this resolved name. A custom action is listed only when it has both a C# implementation **and** a catalogue entry.

## Angular Integration

On the Angular side, the `CustomActionDefinition` model represents an action:

```typescript
export interface CustomActionDefinition {
  name: string;
  label: TranslatedString;
  icon?: string;
  description?: TranslatedString;
  showedOn: string;
  selectionRule?: string;
  refreshOnCompleted: boolean;
  confirmation?: TranslatedString;
  variant?: string;
  offset: number;
  isDefault?: boolean;
}
```

The frontend fetches available actions via `POST /spark/actions/list`, renders buttons or menu items based on `showedOn`, evaluates `selectionRule` against the current selection, shows the `confirmation` (with `{count}` substituted) when there is one, and executes via `POST /spark/actions/execute`. Both name the type — and the second also the action — in the request body: every Spark path is literal, with no route variables at all.

## Complete Example

See the Fleet demo app for a working example:
- `apps/Fleet/Fleet/CustomActions/CarCopyAction.cs` -- C# implementation
- `apps/Fleet/Fleet/App_Data/actions.json` -- action metadata
- `apps/Fleet/Fleet/App_Data/translations.json` -- the `actions.CarCopy.*` texts
- `apps/Fleet/Fleet/App_Data/security.json` -- authorization entries
- `MintPlayer.Spark.Abstractions/Actions/ICustomAction.cs` -- interface definition
- `MintPlayer.Spark/Actions/SparkCustomAction.cs` -- base class
- `MintPlayer.Spark/Models/CustomActionDefinition.cs` -- metadata model
- `MintPlayer.Spark/Services/CustomActionResolver.cs` -- action discovery
- `MintPlayer.Spark/Endpoints/Actions/ListCustomActions.cs` -- list endpoint
- `MintPlayer.Spark/Endpoints/Actions/ExecuteCustomAction.cs` -- execute endpoint

## Disabling actions: `OnDisableActionsAsync` (#460, D13)

The action catalogue (`POST /spark/actions/list`) and `security.json` are per **type**. Whether an
action applies to *this* repository, *this* query or *this* selection is answered by one hook on the
entity's Actions class — the only place in Spark that can disable an action:

```csharp
public override Task OnDisableActionsAsync(IDisablable target, DisableActionsContext context)
{
    // context.Entity is the STORED entity for an object target; null for a query or a create.
    if (context.TargetKind == DisableActionsTargetKind.PersistentObject
        && context.Entity is Repository { Connection: not RepositoryConnection.Disconnected })
    {
        target.DisableActions("DeleteData");
    }

    if (context.TargetKind == DisableActionsTargetKind.Query && context.Query?.Name == "ArchivedRepositories")
        target.DisableActions("Resync", "New");

    return Task.CompletedTask;
}
```

The framework asks it twice, and the two answers are meant to be the same:

| When | Targets | What happens |
|---|---|---|
| **Load** — `POST /spark/po/load`, `POST /spark/queries/execute` | the object; the query | the answer is returned as `disabledActions`; ng-spark hides those custom actions, and `Edit`/`Save`/`Delete` (detail) and `New` (query) |
| **Submit** — update, delete, create, every custom action | update/delete: the stored object; create: an object with no entity; custom action: the object it runs on, the query it was invoked from, and **each selected row** | a disabled action is refused with **403** `{ result: { error, action } }` |

Names at submit: an update is refused when `Edit` or `Save` is disabled, a create when `New` or
`Save` is, a delete when `Delete` is, a custom action when its own name is — on **any** evaluated
target (the union).

⚠️ **403 only after the row gate.** A row the caller may not see is still a 404 — the hook is never
asked about it, so a 403 never confirms that a hidden row exists.

⚠️ **Decide from the entity, the user and stored state only.** `context.Entity` at submit is what is
stored, never what the client posted; a hook that looked at anything the load had and the submit does
not would offer a button that then refuses. Two consequences:

- `New` has no entity and a create names no query, so a hook that withholds `New` only for one query
  hides the button but cannot enforce it. Decide `New` on the user and stored state.
- There are no rows in scope for a query target (the hook runs before the query does), so "hide the
  action when the result is empty" cannot be expressed — deliberately, because it could never be
  re-derived at submit.

**Object level or query level? (#467, D13)** Forbid an action on an *object* at the object target:
that decision is enforced wherever the action runs. The query target controls what that *list
offers*. For a bulk call the two coincide — delete-many and a custom action on a selection fetch their
rows through the named query, so its decision always applies. **Edit** is the exception: it runs only
as the object's update, where the object target decides. A query-level "Edit disabled" hides Edit
from that list and has no execution of its own to block, so put "this row may not be edited" on the
object target.

For a large selection, override the **batched** form `OnDisableActionsAsync(IReadOnlyList<DisableActionsItem>)`
— every target of one request in one call — to answer with one round-trip instead of one per row. The
default calls the single form per item.

Not asked at submit in the system context (module sync, background work). A write an action makes
through `IDatabaseAccess` on an object whose own hook disables that write is refused the same way
(403), so an action that must edit a locked row writes through the session instead.

### Migrating from the old entry points

These are **deleted** (breaking, 11.0.0-preview): they emitted answers nothing enforced, and the
`IClientAccessor` ones rode a client operation no client honoured and the detail path dropped.

| Removed | Instead |
|---|---|
| `PersistentObject.DisableActions(...)` in `OnLoadAsync` | `OnDisableActionsAsync`, object target, `context.Entity` |
| `SparkQueryContext.DisableActions(...)` in `OnQueryAsync` | `OnDisableActionsAsync`, query target, `context.Query` / `context.Parent` |
| `CustomQueryArgs.DisableActions(...)` in a custom query | `OnDisableActionsAsync`, query target (no rows in scope, see above) |
| `IClientAccessor.DisableActionsOn` / `DisableQueryActions` / `DisableActions` / `DisableActionsForSession` | `OnDisableActionsAsync`; for session-wide rules, `security.json` |
| the `disableAction` client operation (ng-spark `DisableActionOperation`, `SparkDisableActionOperation`) | `disabledActions` on the object / result, and the 403 at submit |

`PersistentObject` still implements `IDisablable` — explicitly, so only the framework (handing the
object to the hook at load) calls it. `disabledActions` on the wire is unchanged.

## Row-level security: nothing an action receives came from the browser (#236, #327)

`CustomActionArgs.Parent` and `CustomActionArgs.SelectedItems` are both produced **server-side** from
the ids the client named — the parent through the row-gated read path, the selection by re-running
its query (#327). Neither is ever the object the browser posted:

- an id the caller may not see (or that does not exist) fails the whole request with **404** -- your action is never invoked, and denial is indistinguishable from not-found;
- what your action receives is the **current server state**, not whatever the client typed;
- the submitted parent remains available as `SubmittedParent` for actions that need edited, possibly unsaved values -- treat it as untrusted input;
- a parent submitted **without an id** (unsaved form state) is not resolved: `Parent` is `null` and the submitted values are in `SubmittedParent`.

**There is no `SubmittedSelectedItems`, by design (#327).** A selected row is named by an **id** and
nothing else; `SubmittedSelectedItemIds` carries those raw ids, and they are rarely what you want,
since `SelectedItems` is what those ids resolved to. There is no submitted-object form
of a selection because a row was never a document: the grid renders a projection with no attribute
metadata, no `can` block and no etag, so there is nothing meaningful a client could submit back.
An action that wants edited values wants a *detail form*, which is `SubmittedParent`.

### Getting the entity behind a row

```csharp
// In {Type}Actions -- one batched load, never one per row.
var entities = await MaterializeAsync([.. args.SelectedItems.Select(i => i.Id)]);
```

Override `MaterializeAsync` to change *how* entities are found, not *which*: the framework applies
the collection guard, the row rule and redaction to whatever comes back, and refuses the whole
request unless every requested id resolved.

### When a query's rows have identity the framework cannot filter on

Re-running a query narrowed to ids works by default when a row's `Id` is a document id. A composed
query mints its own -- `"collections/people"` -- and composite keys (`"{a}|{b}"`) are common. Declare
the hook so the framework can find those rows again:

```csharp
// In {Type}Actions
public object RestrictToIds(object source, IReadOnlyCollection<string> ids)
    => ((IEnumerable<MyRow>)source).Where(r => ids.Contains(r.Id)).ToList();
```

Without it, such a query **throws** naming this hook, rather than returning nothing and letting the
all-or-nothing rule report a refusal that reads like a permission problem.

⚠️ **Where the hook has to live differs by type, and getting it wrong is silent.**

- **Entity-backed type** — the hook must be on the class that *is* the type's actions class, i.e. one
  implementing `IPersistentObjectActions<T>` (normally by deriving from
  `DefaultPersistentObjectActions<T>`). A standalone `{Name}Actions` carrying only the hook is
  **skipped**: the resolver requires the interface, falls back to the default actions, and your hook
  is never consulted — with no error.
- **Composed type** (no `clrType`) — any class named `{Name}Actions` is used, since there is no
  entity to close the interface over.

Rarely needed at all now: a readable `Id` of any type narrows without a hook, matched by its
`ToString()` — the same value the row id on the wire was minted from.

### Three shapes fall back to a document load

A query owning its own paging (`SparkQueryPage<T>`) and a streaming query
cannot be re-run. Those selections are materialized by id instead, and **lose index-computed column
values** -- the column is present, the value is null.

### A sub-query's container: `QueryParent`

A grid rendered on another object's detail page — a company's Cars tab — invokes its actions with
**two** pieces of context, and they are deliberately different things:

| | What it is | When it is set |
|---|---|---|
| `SelectedItems` | The ticked rows, re-materialized by re-running the query | Any query invocation |
| `QueryParent` | The object whose page the grid was on | Sub-query only |
| `Parent` | An object **of this action's own type** | Detail-page invocation only |

```csharp
public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken ct)
{
    var cars    = args.SelectedItems;      // the rows the user ticked
    var company = args.QueryParent;        // the Company whose page they were on
}
```

⚠️ **`QueryParent` is not `Parent`, and putting the container in `Parent` does not work.** `Parent`
means an object of the action's own type, and the server loads it under the *route's* type on
purpose (a caller must not be able to name a foreign type and have it accepted as fact). The cars
listed on a company's page are Cars; the container is a Company. Asking the server to load a Company
id as a Car is refused by the collection guard — correctly.

So the container travels as **id + type**, exactly as `GET …/execute` names it, and is resolved
under **its own** type with that type's own `Read` gate and row rule. A container the caller may not
see refuses the whole request rather than arriving as a fact.

`QueryParent` is null on a top-level query page, so an action offered in both places should treat it
as optional — see DemoApp's `CopyCarsToCompany`, which assigns the copies to the container when
there is one and keeps each car's existing owner when there is not.

**All or nothing.** If any named id fails to resolve -- missing, foreign collection, or refused by
the row rule -- the whole request is refused. An action never silently receives 498 of the 500 rows
the user selected.

**Migration note (breaking).** Two changes, both from #327:

- Actions that relied on client-supplied state arriving in `Parent` must read `SubmittedParent`.
- `SelectedItems` is `QueryResultItem[]`, so `args.Parent ?? args.SelectedItems.FirstOrDefault()`
  no longer compiles -- `Parent` is a `PersistentObject` and the two do not unify. Coalesce on ids:
  `args.Parent?.Id ?? args.SelectedItems.FirstOrDefault()?.Id`. To act on the entity behind a row,
  call `MaterializeAsync`.
