# Custom Attribute Renderers

Spark lets you replace the default display and editing of any PersistentObject attribute with your own Angular component. A **renderer** is identified by name in the model JSON and resolved at runtime from a global registry.

## Overview

Each attribute in a model JSON file can declare a `renderer` and optional `rendererOptions`:

```json
{
  "name": "PromoVideoUrl",
  "dataType": "string",
  "renderer": "video-player",
  "rendererOptions": { "width": 480, "height": 270 }
}
```

The Angular app registers components for that renderer name. Spark then uses those components automatically in the appropriate views.

A renderer registration has **three slots**, all optional (register only the slots you need):

| Slot | Used in | Purpose |
|---|---|---|
| `detailComponent` | PO detail page | Read-only display of the attribute value. When omitted, built-in rendering is used |
| `columnComponent` | Query list / sub-query / AsDetail sub-table cells | Compact cell display in data tables. When omitted, built-in rendering is used |
| `editComponent` | Create / Edit forms | Custom input control. When omitted, the default `<input>` for the attribute's `dataType` is used |

Renderer components declare only the inputs they need: Spark filters the input bag down to
what each component actually declares before handing it to `NgComponentOutlet`, so every
contract member is optional.

## Step 1: Configure the Model JSON

In your entity's model JSON file (e.g. `App_Data/Model/Car.json`), add `renderer` and optionally `rendererOptions` to the attribute:

```json
{
  "attributes": [
    {
      "name": "PromoVideoUrl",
      "label": { "en": "Promo Video", "nl": "Promotievideo" },
      "dataType": "string",
      "renderer": "video-player",
      "rendererOptions": {
        "width": 480,
        "height": 270,
        "autoplay": false
      },
      "order": 8,
      "showedOn": "Query, PersistentObject"
    }
  ]
}
```

- `renderer` -- a string name that matches a registration in Angular (see Step 3)
- `rendererOptions` -- an arbitrary JSON object passed to the component as `options` input
- `dataType` -- preserved as-is; the renderer does not change validation or storage behavior

## Step 2: Create Renderer Components

### Detail Renderer (required)

Implement `SparkAttributeDetailRenderer`. This component is shown on the PO detail page.

```typescript
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { EntityAttributeDefinition } from '@mintplayer/ng-spark/models';
import { SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';

@Component({
  selector: 'app-video-detail-renderer',
  standalone: true,
  imports: [/* your imports */],
  template: `
    @if (value(); as url) {
      <!-- your custom display -->
    } @else {
      <span class="text-muted">-</span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class VideoDetailRendererComponent implements SparkAttributeDetailRenderer {
  value = input<any>();                              // the attribute value
  attribute = input<EntityAttributeDefinition>();     // the attribute definition metadata
  options = input<Record<string, any>>();             // rendererOptions from the model JSON
  formData = input<Record<string, any>>({});          // all form data (for cross-field logic)
}
```

Every input is optional — declare only what you use. A detail renderer may also declare
`item = input<PersistentObject>()` to receive the full PersistentObject (ids and breadcrumbs
that the flattened `formData` drops).

### Column Renderer (required)

Implement `SparkAttributeColumnRenderer`. This component is shown in query list table cells. Keep it compact.

> **Breaking change (#327).** A column renderer receives **`column`**, not `attribute`. A query row is
> now a projection: the server sends the column metadata **once per result** instead of repeating a
> full attribute definition on every row, so there is no `EntityAttributeDefinition` to hand a cell
> any more. Detail and edit renderers are unaffected and keep `attribute` — those paths really are
> attribute-shaped. To migrate a column renderer, rename the input and change its type; the fields a
> cell actually reads (`name`, `label`, `dataType`, `isArray`, `renderer`, `rendererOptions`,
> `referenceType`, `lookupReferenceType`, `asDetailType`) are all on `SparkCellColumn`.

```typescript
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { SparkCellColumn } from '@mintplayer/ng-spark/models';
import { SparkAttributeColumnRenderer } from '@mintplayer/ng-spark/renderers';

@Component({
  selector: 'app-video-column-renderer',
  standalone: true,
  template: `
    @if (value(); as url) {
      <a [href]="url" target="_blank">{{ url }}</a>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class VideoColumnRendererComponent implements SparkAttributeColumnRenderer {
  value = input<any>();
  column = input<SparkCellColumn>();
  options = input<Record<string, any>>();
}
```

A column renderer may also declare `item` to receive the row it belongs to (see
[Row context](#row-context-the-item-input) below).

`SparkCellColumn` is a small structural interface, and both `QueryColumn` (from a query result) and
`EntityAttributeDefinition` (from an AsDetail sub-table) satisfy it. That is what lets one cell
component serve the query grid and the detail page's nested tables without either side knowing about
the other's shape.

### Edit Renderer (optional)

Implement `SparkAttributeEditRenderer`. This component replaces the default `<input>` on create/edit forms.

Since Spark uses `NgComponentOutlet` to render these components, **outputs are not supported**. Instead, value changes are communicated via a **callback function** passed as the `valueChange` input.

```typescript
import { ChangeDetectionStrategy, Component, effect, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { EntityAttributeDefinition } from '@mintplayer/ng-spark/models';
import { SparkAttributeEditRenderer } from '@mintplayer/ng-spark/renderers';

@Component({
  selector: 'app-color-edit-renderer',
  standalone: true,
  imports: [FormsModule, /* your control component */],
  template: `
    <my-color-picker
      [ngModel]="currentColor()"
      (ngModelChange)="onColorChange($event)">
    </my-color-picker>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ColorEditRendererComponent implements SparkAttributeEditRenderer {
  value = input<any>();
  attribute = input<EntityAttributeDefinition>();
  options = input<Record<string, any>>();
  valueChange = input<(value: any) => void>(() => {});

  currentColor = signal<string>('#000000');

  constructor() {
    // Sync initial value from parent form
    effect(() => {
      const v = this.value();
      if (v) this.currentColor.set(v);
    });
  }

  onColorChange(newValue: string): void {
    this.currentColor.set(newValue);
    this.valueChange()?.(newValue);  // notify the parent form
  }
}
```

Key points:
- Call `this.valueChange()?.(newValue)` whenever the user changes the value
- Use an `effect()` to sync the initial value from the `value()` input into local state
- The parent form handles persistence — your component only needs to report changes
- Not declaring `valueChange` silently disables write-back (a read-only edit renderer) — it
  never throws

## Step 3: Register Renderers

In your `app.config.ts`, call `provideSparkAttributeRenderers()` with your registrations:

```typescript
import { provideSparkAttributeRenderers } from '@mintplayer/ng-spark/renderers';
import { VideoDetailRendererComponent } from './renderers/video-detail-renderer.component';
import { VideoColumnRendererComponent } from './renderers/video-column-renderer.component';
import { ColorDetailRendererComponent } from './renderers/color-detail-renderer.component';
import { ColorColumnRendererComponent } from './renderers/color-column-renderer.component';
import { ColorEditRendererComponent } from './renderers/color-edit-renderer.component';

export const appConfig: ApplicationConfig = {
  providers: [
    // ... other providers ...
    provideSparkAttributeRenderers([
      {
        name: 'video-player',                         // matches "renderer" in model JSON
        detailComponent: VideoDetailRendererComponent,
        columnComponent: VideoColumnRendererComponent,
        // no editComponent -- plain <input type="text"> is used on create/edit
      },
      {
        name: 'color-swatch',
        detailComponent: ColorDetailRendererComponent,
        columnComponent: ColorColumnRendererComponent,
        editComponent: ColorEditRendererComponent,     // custom color picker on create/edit
      },
    ]),
  ]
};
```

The `name` must match the `renderer` value in your model JSON exactly.

## Inputs Provided to Renderers

Every input is **passed only when the component declares it** — declare exactly the subset
you need.

| Input | Type | Detail | Column | Edit | Description |
|---|---|---|---|---|---|
| `value` | `any` | Yes | Yes | Yes | The current attribute value (see [AsDetail values](#asdetail-values) for AsDetail attributes) |
| `attribute` | `EntityAttributeDefinition` | Yes | - | Yes | Full attribute metadata (name, dataType, label, rules, etc.). **Not passed to column renderers** -- see `column` |
| `column` | `SparkCellColumn` | - | Yes | - | The column being rendered: `name`, `label`, `dataType`, `isArray`, `renderer`, `rendererOptions`, `referenceType`, `lookupReferenceType`, `asDetailType` |
| `options` | `Record<string, any>` | Yes | Yes | Yes | The `rendererOptions` object from the model JSON |
| `formData` | `Record<string, any>` | Yes | - | - | All attribute values (detail page only, for cross-field logic); AsDetail keys carry the nested PO(s) |
| `item` | `PersistentObject \| Record<string, any>` | Yes | Yes | AsDetail cells only | The row/object this attribute belongs to (see [Row context](#row-context-the-item-input)) |
| `valueChange` | `(value: any) => void` | - | - | Yes | Callback to report value changes to the parent form. Omitting it disables write-back |

## AsDetail Values

For an **AsDetail** attribute the server intentionally sends no flat value — the data lives in
the nested PersistentObject(s). Renderers receive those instead:

- In **query-list**, **sub-query**, and **po-detail field** hosts, `value` is the nested
  `PersistentObject` (single) or `PersistentObject[]` (when `isArray`), i.e.
  `{ attributes: [{ name, value }, ...] }`.
- In **AsDetail sub-table cells** and **po-form** hosts, values are already flattened to plain
  dicts, so `value` is the flat cell/attribute value as before.

A renderer serving both kinds of host normalizes the shape, e.g.:

```typescript
import { PersistentObject } from '@mintplayer/ng-spark/models';

/** name→value dict from either a nested PO or an already-flat record. */
function toDict(v: PersistentObject | Record<string, any> | null | undefined): Record<string, any> {
  if (!v) return {};
  return Array.isArray((v as PersistentObject).attributes)
    ? Object.fromEntries((v as PersistentObject).attributes.map(a => [a.name, a.value]))
    : (v as Record<string, any>);
}
```

Note: when a query projection lacks the AsDetail property, the renderer sees a *scaffolded*
child (a structured PO with null values / empty arrays), never `undefined` — include the
property in the index/projection to get real data (see the index requirement below).

## Row Context: the `item` Input

A renderer that needs **other fields of the same row** (a name cell with an inline badge, a
`runId.attempt` composite, a sha that links using the repo's full name) declares `item`:

```typescript
export class RepoNameColumnRendererComponent implements SparkAttributeColumnRenderer {
  value = input<any>();
  item = input<QueryResultItem | Record<string, any>>();

  // valueFor returns the CELL ({ key, value, objectId, breadcrumb }), not the bare value --
  // a reference cell needs its objectId as much as its text, so the cell is what it hands back.
  isPrivate = computed(() => {
    const row = this.item();
    return isQueryRow(row) ? valueFor(row, 'IsPrivate')?.value === true : row?.['IsPrivate'] === true;
  });
}

// A grid row has `values`; an AsDetail row is a flat record.
function isQueryRow(row: unknown): row is QueryResultItem {
  return Array.isArray((row as QueryResultItem | undefined)?.values);
}
```

What `item` is depends on the host:

- **query-list / sub-query grids**: the row, a `QueryResultItem` — `{ id, breadcrumb, values }`,
  where `values` is a list of `{ key, value, objectId, breadcrumb }`. **Changed in #327**: it used
  to be a `PersistentObject` with an `attributes` array. Reach into it with the `valueFor(item, key)`
  helper from `@mintplayer/ng-spark/models` rather than by hand — the shape is a wire contract, not
  a convenience.
- **po-detail field**: the full `PersistentObject` being displayed (unchanged — a detail page really
  does load a document)
- **AsDetail sub-table cells** (detail and form): the flat row record (which may include the
  reserved `__sparkBreadcrumbs` key — ignore it)

A renderer used in **both** a grid and an AsDetail table sees two different shapes, which is why the
example above narrows through a helper instead of indexing directly.

**To read a sibling value the grid does not draw**, mark its attribute `"showedOn": "Query",
"isVisible": false`. It then ships on every row and no column is rendered for it. A value marked
`"showedOn": "PersistentObject"` is not on the query wire at all and `valueFor` returns `undefined`
— that is the failure to check first when a renderer's sibling read comes back empty.

⚠️ **An option that names a sibling attribute only works if that attribute is on the query surface.**
A configurable renderer — one whose `rendererOptions` carry the *name* of another attribute —
splits the declaration in two: the renderer is written once, and the attribute it needs is chosen
per call site in the model JSON.

```jsonc
{ "renderer": "short-sha", "rendererOptions": { "titleAttribute": "Message" } }
```

Whoever writes that must also mark `Message` as `"showedOn": "Query"`. Forget it and the value is
simply absent — the renderer is fine, the model is fine, the tooltip just never appears.

The framework cannot check this: it has no way to know which option keys name attributes rather than
holding ordinary configuration. So it is on the author, and it is worth a second look precisely on
configurable renderers, where the two declarations are furthest apart.

## Using `rendererOptions`

Options are passed from the model JSON as-is. Access them in your component:

```typescript
template: `
  <video-player
    [width]="options()?.['width'] ?? 480"
    [height]="options()?.['height'] ?? 270">
  </video-player>
`
```

This lets the same renderer component behave differently for different attributes (e.g. different video sizes, different color picker sizes).

## RavenDB Index Requirement

When a rendered attribute appears in a **query list**, the attribute value must be included in the RavenDB index. If the index doesn't project the field, the query returns `null` and the renderer has nothing to display.

Add the property to both the index map and the projection class:

```csharp
public class Cars_Overview : AbstractIndexCreationTask<Car>
{
    public Cars_Overview()
    {
        Map = cars => from car in cars
                      select new VCar
                      {
                          // ...
                          PromoVideoUrl = car.PromoVideoUrl,  // include in index
                      };
        StoreAllFields(FieldStorage.Yes);
    }
}

[FromIndex(typeof(Cars_Overview))]
public class VCar
{
    // ...
    public string? PromoVideoUrl { get; set; }  // include in projection
}
```

## System.Drawing.Color Support

C# properties of type `System.Drawing.Color` are automatically detected as `dataType: "color"`. Values are serialized as `"#rrggbb"` hex strings in both RavenDB and the HTTP API. No manual `dataType` configuration is needed in the model JSON.

```csharp
public class Car
{
    public Color? Color { get; set; }
    public Color? InteriorColor { get; set; }
}
```

After synchronization, the model JSON will contain `"dataType": "color"` for these attributes. You can then add a `"renderer": "color-swatch"` to customize how they're displayed.

## Rendering Priority

Spark checks for a custom renderer before applying built-in rendering:

**Detail page / Query list:**
1. Custom renderer (if `attr.renderer` matches a registration)
2. Built-in rendering (boolean toggle, color swatch, Reference link, etc.)
3. Plain text

**Create / Edit form:**
1. Custom edit renderer (if `attr.renderer` matches a registration with `editComponent`)
2. Built-in input (boolean toggle, Reference selector, etc.)
3. Default `<input>` based on `dataType`

## Complete Example

See the Fleet demo app for working examples:
- `Demo/Fleet/Fleet/ClientApp/src/app/renderers/` -- all renderer components
- `Demo/Fleet/Fleet/ClientApp/src/app/app.config.ts` -- registration
- `Demo/Fleet/Fleet/App_Data/Model/Car.json` -- model JSON with `renderer` fields
