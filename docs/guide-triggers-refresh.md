# Guide — forms that reshape themselves (`triggersRefresh` + `OnRefreshAsync`)

A Spark form is normally the shape its model file declares. This is how you make the shape depend on
what the user has typed: mark an attribute as a trigger, and override one hook.

---

## The two halves

**1. Declare the trigger** in `App_Data/Model/{Entity}.json`. It is hand-set — the synchronizer
preserves it, like `editMode` and `referenceDisplayType`:

```json
{
  "name": "Status",
  "dataType": "string",
  "lookupReferenceType": "CarStatus",
  "triggersRefresh": true
}
```

**2. Override the hook** on the entity's actions class:

```csharp
public override Task OnRefreshAsync(SparkRefreshArgs<Car> args)
{
    var obj = args.PersistentObject;
    var stolen = obj[nameof(Car.Status)].Value?.ToString() == CarStatus.Stolen;

    obj[nameof(Car.PoliceReportNumber)].IsVisible = stolen;
    obj[nameof(Car.PoliceReportNumber)].IsRequired = stolen;
    obj[nameof(Car.LicensePlate)].IsReadOnly = stolen;
    obj[nameof(Car.PromoVideoUrl)].IsVisible = !stolen;

    return Task.CompletedTask;
}
```

That is the whole feature. Two live samples:

- `Demo/Fleet/Fleet/Actions/CarActions.cs` — a top-level trigger (`Car.Status`).
- `Demo/HR/HR/Actions/CarreerJobActions.cs` — a trigger inside an inline detail grid.

---

## What you can change

| | |
|---|---|
| `IsRequired` | make a field mandatory, or stop being mandatory |
| `IsReadOnly` | freeze a field without hiding it |
| `IsVisible` | show or hide |
| `Rules` | add, remove or replace validation rules |
| `Options` | replace what a dropdown offers |
| `Value` | set a dependent value |

Replacing options:

```csharp
obj[nameof(Car.Garage)].Options = maintenance
    ? [ new PersistentObjectAttributeOption { Key = "garages/1", Label = TranslatedString.Create("North") } ]
    : null;
```

⚠️ `null` and empty mean different things. **`null` = "I did not touch these"**, and the client keeps
whatever it loaded. An empty list means "there are genuinely none". Returning empty for everything
you did not think about blanks every dropdown on the form.

---

## The one rule that matters

⚠️ **Establish the complete presentation state on every call. Never patch the previous one.**

Your hook is handed a **freshly scaffolded object** each time — never the result of the last call. So
this is a bug:

```csharp
// WRONG — only ever turns things on
if (stolen)
{
    obj[nameof(Car.PoliceReportNumber)].IsRequired = true;
}
```

One stray selection of `Stolen` locks the form, and selecting anything else never unlocks it. Because
the same rules are re-derived on save, that is not merely cosmetic: the record becomes unsaveable.
Set both sides, every time:

```csharp
// RIGHT
obj[nameof(Car.PoliceReportNumber)].IsRequired = stolen;
```

If you shape the form on load too, share one private helper between the two paths so they cannot
drift.

---

## Triggers inside a detail grid

A trigger can live on a column of an inline AsDetail grid. Declare it in the **nested type's** model
file, exactly as you would for a top-level attribute:

```json
// App_Data/Model/CarreerJob.json
{ "name": "ProfessionId", "dataType": "Reference", "triggersRefresh": true }
```

The refresh then runs against the **row's own type** — so it is `CarreerJobActions` that implements
the hook, not the `PersonActions` that owns the collection. The hook that owns a type's shape is that
type's own; the row is handed its owner for the context it cannot have alone:

```csharp
public class CarreerJobActions : DefaultPersistentObjectActions<CarreerJob>
{
    public override async Task OnRefreshAsync(SparkRefreshArgs<CarreerJob> args)
    {
        var obj = args.PersistentObject;      // the row that changed
        var person = obj.Parent;              // the object it lives in — read-only context

        var isFreelance = /* … */;
        obj[nameof(CarreerJob.ContractEnd)].IsReadOnly = isFreelance;
        obj[nameof(CarreerJob.ContractEnd)].IsRequired = !isFreelance;
    }
}
```

⚠️ **`args.PersistentObject.Parent` is context, not a target.** Nothing you change on it is applied —
the response describes the row.

⚠️ **Metadata changes apply to the whole column, not to one row.** Making `ContractEnd` read-only in
response to row 1 makes it read-only in every row, because the grid renders from one column
definition. **Values** are per-row and behave as you would expect. If you need genuinely per-row
shaping, this is not the mechanism.

⚠️ **Authorization uses the owning type, not the row's.** Nested AsDetail types are not in
`security.json` — nobody grants rights on `CarreerJob` — so the right that governs editing a row is
the one governing the object that owns it. Only the *dispatch* follows the row.

---

## Triggers inside a single embedded object

An `AsDetail` attribute that is **not** an array — one object, not a grid of them — works the same
way, with one difference: there is no row to index, so the path has no brackets.

```json
// App_Data/Model/Repository.json — the owning attribute
{ "name": "Gate", "dataType": "AsDetail", "asDetailType": "CodeCoverage.Entities.GateSettings",
  "isArray": false, "isReadOnly": false }

// App_Data/Model/GateSettings.json — the trigger, on the embedded type
{ "name": "ProjectMode", "dataType": "string", "lookupReferenceType": "ProjectComparison",
  "triggersRefresh": true }
```

The path is `Gate.ProjectMode`, and everything else is unchanged: the hook is
`GateSettingsActions.OnRefreshAsync`, `obj.Parent` is the Repository, and authorization is the
Repository's.

```csharp
public partial class GateSettingsActions : DefaultPersistentObjectActions<GateSettings>
{
    public override Task OnRefreshAsync(SparkRefreshArgs<GateSettings> args)
    {
        var obj = args.PersistentObject;
        var isFixed = obj[nameof(GateSettings.ProjectMode)].GetValue<string>() == "fixed";

        obj[nameof(GateSettings.ProjectTarget)].IsVisible = isFixed;
        obj[nameof(GateSettings.ProjectTarget)].IsRequired = isFixed;
        return Task.CompletedTask;
    }
}
```

⚠️ **The owning attribute must not be `isReadOnly`.** The edit form filters read-only attributes out
before rendering, so a read-only AsDetail never opens and the trigger has nowhere to fire from. This
looks exactly like a broken refresh and is not one.

⚠️ **The path shape must match `isArray`.** `Gate[0].ProjectMode` on a single object, or
`Jobs.Title` on an array, resolves to nothing and falls through to the *root* hook — which will not
recognise the trigger and will leave the object alone. The symptom is a refresh that returns 200 and
changes nothing.

⚠️ **A single embedded object is edited in a modal, and the modal can be cancelled.** A refresh
applies to the modal's working copy, not to the parent's data, so a refreshed value is discarded
along with everything else if the user dismisses the dialog. That is the intended behaviour; do not
"fix" it by writing through to the parent.

---

## What the framework does with it

- **On refresh** the client POSTs the in-progress object to `/spark/po/refresh`, naming the type in the body. The
  server rebuilds it from the model — taking only *values* from the wire — runs your hook, and
  returns it. The client applies the metadata as an overlay and merges the values.
- **On save** Spark runs your hook again, once per triggering attribute, and validates against the
  result. This is what makes a refresh-imposed rule real: a client that never calls `/refresh` is
  still held to it.

⚠️ **Therefore your hook must have no side effects.** Anything it writes, sends or logs happens on
every save as well as every refresh.

⚠️ **It is called far more often than load or save** — potentially on every field blur. Database
access inside it is a cost, not a convenience. If you must load, consider whether the value you need
is already on the object.

---

## Client behaviour you get for free

- Discrete editors (select, checkbox, date, reference, lookup) refresh **immediately**; free-text
  fields mark pending and refresh **on blur**, so typing does not issue a request per keystroke.
- Anything still pending is **flushed before save**.
- Refreshes are **serialized per form**, and a superseded response is discarded.
- The form is **never frozen** — the user keeps typing during the round trip. A value they changed
  while a refresh was in flight is kept, unless your hook changed that same attribute.
- Rules are evaluated **in the browser** as well as on the server, so an imposed rule blocks save
  with a per-field message rather than only failing the round trip.

---

## Gotchas

⚠️ **`args.Attribute` is nullable.** A stale client can name an attribute the model no longer
declares. Branch on `args.Attribute?.Name`, not `args.Attribute.Name`.

⚠️ **`args.IsNew`** tells you whether there is a persisted row behind this object. A hook that loads
the entity must check it first.

⚠️ **The flag is schema-only.** It never travels on a `PersistentObjectAttribute`, so a client cannot
claim a trigger the model did not declare.

⚠️ **`--spark-verify-model` fails (exit 3)** if a model declares `triggersRefresh` on a type whose
actions class has no `OnRefreshAsync` override — including a nested AsDetail type, which needs its
own actions class. This deliberately is *not* a Roslyn analyzer: the
flag lives in JSON, outside the compilation, so an analyzer would have nothing to read.

⚠️ **Redaction still applies.** An attribute hidden by `GetProtectedAttributesAsync` stays hidden and
valueless in a refresh response, even if your hook sets it. For a trigger addressed *inside* an
AsDetail attribute, redaction is coarser than the path: it can withhold `Gate`, never
`Gate.PatchTarget`. A refresh addressed inside a withheld attribute is refused outright rather than
answered with a row scaffolded from the model.

⚠️ **A rule imposed on a detail row is presentation only.** Spark re-runs the refresh hook while
validating a save — which is what makes an imposed rule real for a client that never calls
`/refresh` — but that re-derivation covers the **root** type and does not descend into AsDetail
rows. If a rule inside a nested type has to hold whatever a client posts, enforce it in the owning
type's `OnBeforeSaveAsync`, on the object that actually saves. `RepositoryActions` does exactly this
for the coverage gate.
