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

A renderer registration has **three slots**:

| Slot | Required | Used in | Purpose |
|---|---|---|---|
| `detailComponent` | Yes | PO detail page | Read-only display of the attribute value |
| `columnComponent` | Yes | Query list | Compact cell display in data tables |
| `editComponent` | No | Create / Edit forms | Custom input control. When omitted, the default `<input>` for the attribute's `dataType` is used |

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
import { EntityAttributeDefinition, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark';

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

### Column Renderer (required)

Implement `SparkAttributeColumnRenderer`. This component is shown in query list table cells. Keep it compact.

```typescript
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { EntityAttributeDefinition, SparkAttributeColumnRenderer } from '@mintplayer/ng-spark';

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
  attribute = input<EntityAttributeDefinition>();
  options = input<Record<string, any>>();
}
```

### Edit Renderer (optional)

Implement `SparkAttributeEditRenderer`. This component replaces the default `<input>` on create/edit forms.

Since Spark uses `NgComponentOutlet` to render these components, **outputs are not supported**. Instead, value changes are communicated via a **callback function** passed as the `valueChange` input.

```typescript
import { ChangeDetectionStrategy, Component, effect, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { EntityAttributeDefinition, SparkAttributeEditRenderer } from '@mintplayer/ng-spark';

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

## Step 3: Register Renderers

In your `app.config.ts`, call `provideSparkAttributeRenderers()` with your registrations:

```typescript
import { provideSparkAttributeRenderers } from '@mintplayer/ng-spark';
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

| Input | Type | Detail | Column | Edit | Description |
|---|---|---|---|---|---|
| `value` | `any` | Yes | Yes | Yes | The current attribute value |
| `attribute` | `EntityAttributeDefinition` | Yes | Yes | Yes | Full attribute metadata (name, dataType, label, rules, etc.) |
| `options` | `Record<string, any>` | Yes | Yes | Yes | The `rendererOptions` object from the model JSON |
| `formData` | `Record<string, any>` | Yes | - | - | All attribute values (detail page only, for cross-field logic) |
| `valueChange` | `(value: any) => void` | - | - | Yes | Callback to report value changes to the parent form |

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
