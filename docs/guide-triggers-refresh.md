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

That is the whole feature. The live sample is `Demo/Fleet/Fleet/Actions/CarActions.cs`.

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

## What the framework does with it

- **On refresh** the client POSTs the in-progress object to `/spark/po/{objectTypeId}/refresh`. The
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
actions class has no `OnRefreshAsync` override. This deliberately is *not* a Roslyn analyzer: the
flag lives in JSON, outside the compilation, so an analyzer would have nothing to read.

⚠️ **Redaction still applies.** An attribute hidden by `GetProtectedAttributesAsync` stays hidden and
valueless in a refresh response, even if your hook sets it.
