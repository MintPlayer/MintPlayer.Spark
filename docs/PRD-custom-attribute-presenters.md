# PRD: Custom Attribute Renderers

> **Status**: Implemented (merged in PR #39)

## Problem Statement

Developers using MintPlayer.Spark need the ability to customize how specific PersistentObject attributes are **displayed** and **edited** across all views (detail page, query list, and create/edit forms). For example, a `Car` entity with a `PromoVideoUrl` attribute (stored as a string) should render a `<video-player>` component on the detail page, a YouTube thumbnail in the query list -- rather than just showing the raw URL text -- and still use a plain `<input>` on the edit form.

### Previous Approach (replaced)

Three inline template directives existed for per-component customization:
- `SparkFieldTemplateDirective` (`sparkFieldTemplate`) -- used in `spark-po-form` (create/edit)
- `SparkDetailFieldTemplateDirective` (`sparkDetailFieldTemplate`) -- used in `spark-po-detail`
- `SparkColumnTemplateDirective` (`sparkColumnTemplate`) -- used in `spark-query-list`

**Problems with the previous approach:**
- Per-component: the same template must be copy-pasted into every page that uses it
- Not backend-driven: no metadata in the model JSON signals *how* an attribute should be rendered
- Not pluggable: you cannot install a renderer from a library and have it "just work"

**All three directives have been replaced** with a single global renderer registry system.

## Design Principles

1. The **model JSON** (backend) declares *what* renderer an attribute should use -- independent of the PersistentObject, independent of the attribute name, independent of the DataType
2. The **Angular app** registers renderer components **once globally** -- a single registration handles all attributes that reference that renderer name
3. The underlying `dataType` is preserved for validation, serialization, and storage
4. Each renderer registration provides **up to three components**:
   - **detailComponent** (required): for the PO detail page
   - **columnComponent** (required): for the query list column
   - **editComponent** (optional): for the create/edit form -- when omitted, the default `<input>` based on `dataType` is used

## Scope

### In Scope
- Backend: `renderer` and `rendererOptions` fields on attribute definitions
- Backend: `System.Drawing.Color` support with automatic `dataType: "color"` detection
- Backend: `ColorNewtonsoftJsonConverter` for RavenDB hex string serialization
- Angular: Global renderer registry with `provideSparkAttributeRenderers()`
- Angular: Integration in `spark-po-detail`, `spark-query-list`, and `spark-po-form`
- Angular: Optional `editComponent` for custom create/edit form rendering
- Removal of all three existing template directives and their usages
- Demo: Video player renderer in Fleet (detail + column with YouTube thumbnail, plain `<input>` on edit)
- Demo: Color swatch renderer (detail + column for read-only display, `<bs-color-picker>` on edit)

### Out of Scope
- Dynamic/computed `rendererOptions`
- C# `[Renderer]` attribute decorator (JSON model approach is sufficient)

## Architecture

### Rendering Priority

**Detail view (`spark-po-detail`):**
```
1. Global attribute renderer (matched by attr.renderer)
2. Built-in rendering (boolean, color, Reference, AsDetail, etc.)
3. Default text display
```

**Query list view (`spark-query-list`):**
```
1. Global attribute renderer (matched by attr.renderer)
2. Built-in rendering (boolean, color)
3. Default text display
```

**Create/edit views (`spark-po-form`):**
```
1. Global attribute edit renderer (matched by attr.renderer, if editComponent is registered)
2. Built-in inputs based on dataType (default)
```

### Data Flow

```
┌──────────────────────────────────────────────────┐
│  Model JSON (App_Data/Model/Car.json)            │
│  { "name": "PromoVideoUrl",                     │
│    "dataType": "string",                         │
│    "renderer": "video-player",                   │
│    "rendererOptions": { "width": 480 } }         │
└──────────────────┬───────────────────────────────┘
                   │ JSON deserialization
                   ▼
┌──────────────────────────────────────────────────┐
│  C# EntityAttributeDefinition                    │
│  → copied to PersistentObjectAttribute           │
│  → serialized to HTTP JSON response              │
└──────────────────┬───────────────────────────────┘
                   │ HTTP
                   ▼
┌──────────────────────────────────────────────────┐
│  Angular: attr.renderer = "video-player"         │
│                                                  │
│  Detail page:                                    │
│    SPARK_ATTRIBUTE_RENDERERS registry             │
│    → "video-player".detailComponent              │
│    → renders VideoPlayerDetailComponent          │
│                                                  │
│  Query list:                                     │
│    SPARK_ATTRIBUTE_RENDERERS registry             │
│    → "video-player".columnComponent              │
│    → renders VideoPlayerColumnComponent          │
│                                                  │
│  Create/Edit page:                               │
│    → if editComponent registered: renders it     │
│    → otherwise: default <input> based on dataType│
└──────────────────────────────────────────────────┘
```

## Detailed Design

### 1. Backend: C# Model Changes

**`MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs`** -- added to `EntityAttributeDefinition`:
```csharp
public string? Renderer { get; set; }
public Dictionary<string, object>? RendererOptions { get; set; }
```

**`MintPlayer.Spark.Abstractions/PersistentObject.cs`** -- added to `PersistentObjectAttribute`:
```csharp
public string? Renderer { get; set; }
public Dictionary<string, object>? RendererOptions { get; set; }
```

**`MintPlayer.Spark/Services/EntityMapper.cs`** -- copies fields during mapping:
```csharp
var attribute = new PersistentObjectAttribute
{
    // ... existing fields ...
    Renderer = attrDef.Renderer,
    RendererOptions = attrDef.RendererOptions,
};
```

### 2. Backend: System.Drawing.Color Support

Properties of type `System.Drawing.Color` are automatically detected as `dataType: "color"` by both `ModelSynchronizer` and `EntityMapper`.

**`MintPlayer.Spark/Converters/ColorNewtonsoftJsonConverter.cs`** -- serializes `Color` as `"#rrggbb"` hex strings for RavenDB document storage:
```csharp
internal sealed class ColorNewtonsoftJsonConverter : Newtonsoft.Json.JsonConverter<Color>
{
    public override Color ReadJson(...) => ColorTranslator.FromHtml(reader.Value.ToString()!);
    public override void WriteJson(JsonWriter writer, Color value, ...) {
        if (value.IsEmpty) { writer.WriteNull(); return; }
        writer.WriteValue($"#{value.R:x2}{value.G:x2}{value.B:x2}");
    }
}
```

Registered in `SparkMiddleware.cs` via `NewtonsoftJsonSerializationConventions`.

**`EntityMapper.cs`** handles Color ↔ hex conversion at the API layer:
- `ToPersistentObject()`: `Color` → `"#rrggbb"` string
- `SetPropertyValue()`: hex string → `Color` via `ColorTranslator.FromHtml()`

### 3. Model JSON Example

In `Demo/Fleet/Fleet/App_Data/Model/Car.json`:
```json
{
  "name": "PromoVideoUrl",
  "label": { "en": "Promo Video", "nl": "Promotievideo", "fr": "Vidéo promotionnelle" },
  "dataType": "string",
  "renderer": "video-player",
  "rendererOptions": {
    "width": 480,
    "height": 270,
    "autoplay": true
  },
  "order": 8
}
```

Color attributes use `System.Drawing.Color` in C#, auto-detected as `dataType: "color"`:
```json
{
  "name": "Color",
  "dataType": "color",
  "renderer": "color-swatch"
}
```

### 4. Angular: Renderer Component Contracts

**File**: `node_packages/ng-spark/src/lib/interfaces/spark-attribute-renderer.ts`

Three contracts -- detail and column are **read-only**, edit uses a **callback input** for value changes (since `NgComponentOutlet` doesn't support outputs):

```typescript
import { InputSignal } from '@angular/core';
import { EntityAttributeDefinition } from '../models/entity-type';

/** Contract for detail-page renderers (spark-po-detail). */
export interface SparkAttributeDetailRenderer {
  value: InputSignal<any>;
  attribute: InputSignal<EntityAttributeDefinition | undefined>;
  options: InputSignal<Record<string, any> | undefined>;
  formData: InputSignal<Record<string, any>>;
}

/** Contract for query-list column renderers (spark-query-list). */
export interface SparkAttributeColumnRenderer {
  value: InputSignal<any>;
  attribute: InputSignal<EntityAttributeDefinition | undefined>;
  options: InputSignal<Record<string, any> | undefined>;
}

/** Contract for edit-form renderers (spark-po-form on create/edit pages). */
export interface SparkAttributeEditRenderer {
  value: InputSignal<any>;
  attribute: InputSignal<EntityAttributeDefinition | undefined>;
  options: InputSignal<Record<string, any> | undefined>;
  /** Callback to notify parent form of value changes. */
  valueChange: InputSignal<(value: any) => void>;
}
```

### 5. Angular: Global Renderer Registry

**File**: `node_packages/ng-spark/src/lib/providers/spark-attribute-renderer-registry.ts`

```typescript
import { InjectionToken, Provider, Type } from '@angular/core';

export interface SparkAttributeRendererRegistration {
  /** The renderer name (must match attr.renderer in model JSON) */
  name: string;
  /** Component for the PO detail page. Must implement SparkAttributeDetailRenderer. */
  detailComponent: Type<any>;
  /** Component for query-list column cells. Must implement SparkAttributeColumnRenderer. */
  columnComponent: Type<any>;
  /** Optional component for create/edit forms. Must implement SparkAttributeEditRenderer. */
  editComponent?: Type<any>;
}

export const SPARK_ATTRIBUTE_RENDERERS = new InjectionToken<SparkAttributeRendererRegistration[]>(
  'SparkAttributeRenderers',
  { factory: () => [] }
);

export function provideSparkAttributeRenderers(
  renderers: SparkAttributeRendererRegistration[]
): Provider {
  return {
    provide: SPARK_ATTRIBUTE_RENDERERS,
    useValue: renderers,
  };
}
```

### 6. Angular: Component Integration

All three Spark view components (`spark-po-detail`, `spark-query-list`, `spark-po-form`) inject `SPARK_ATTRIBUTE_RENDERERS` and use `NgComponentOutlet` to render custom components.

**spark-po-detail** and **spark-query-list**: Look up `detailComponent` / `columnComponent` by `attr.renderer` name and render via `*ngComponentOutlet` with `inputs`.

**spark-po-form**: Checks for `editComponent` before the default `<input>`. When found, renders it with a `valueChange` callback input that updates the form data:

```typescript
getEditRendererComponent(attr: EntityAttributeDefinition): Type<any> | null {
  if (!attr.renderer) return null;
  return this.rendererRegistry.find(r => r.name === attr.renderer)?.editComponent ?? null;
}

getEditRendererInputs(attr: EntityAttributeDefinition): Record<string, any> {
  return {
    value: this.formData()[attr.name],
    attribute: attr,
    options: attr.rendererOptions,
    valueChange: (newValue: any) => {
      const data = { ...this.formData() };
      data[attr.name] = newValue;
      this.formData.set(data);
    },
  };
}
```

Template:
```html
} @else if (getEditRendererComponent(attr); as editComp) {
  <ng-container *ngComponentOutlet="editComp; inputs: getEditRendererInputs(attr)"></ng-container>
} @else {
  <input [type]="attr.dataType | inputType" ...>
}
```

### 7. Angular: Public API Exports

Added to `node_packages/ng-spark/src/public-api.ts`:
```typescript
// Attribute Renderers
export type { SparkAttributeDetailRenderer, SparkAttributeColumnRenderer, SparkAttributeEditRenderer }
  from './lib/interfaces/spark-attribute-renderer';
export type { SparkAttributeRendererRegistration }
  from './lib/providers/spark-attribute-renderer-registry';
export { SPARK_ATTRIBUTE_RENDERERS, provideSparkAttributeRenderers }
  from './lib/providers/spark-attribute-renderer-registry';
```

## Demo: Fleet App Renderers

### Video Player Renderer

**Detail component** -- renders an embedded video player with configurable dimensions:
```typescript
@Component({
  standalone: true,
  imports: [VideoPlayerComponent],
  providers: [provideVideoApis(youtubePlugin, vimeoPlugin, dailymotionPlugin, soundCloudPlugin, filePlugin)],
  template: `
    @if (value(); as url) {
      <video-player [width]="options()?.['width'] ?? 480" [height]="options()?.['height'] ?? 270"
        [autoplay]="options()?.['autoplay'] ?? false" [url]="url">
      </video-player>
    } @else { <span class="text-muted">-</span> }
  `,
})
export class VideoPlayerDetailRendererComponent implements SparkAttributeDetailRenderer { ... }
```

**Column component** -- extracts YouTube video ID and shows a low-res thumbnail:
```typescript
@Component({
  standalone: true,
  template: `
    @if (thumbnailUrl(); as thumb) {
      <a [href]="value()" target="_blank" title="Watch video">
        <img [src]="thumb" alt="Video thumbnail" style="height: 40px;" />
      </a>
    } @else if (value(); as url) {
      <a [href]="url" target="_blank">{{ url }}</a>
    }
  `,
})
export class VideoPlayerColumnRendererComponent implements SparkAttributeColumnRenderer {
  // ...
  private static readonly YT_REGEX = /(?:youtube\.com\/watch\?v=|youtu\.be\/)([\w-]+)/;

  thumbnailUrl = computed(() => {
    const url = this.value();
    if (!url) return null;
    const match = VideoPlayerColumnRendererComponent.YT_REGEX.exec(url);
    if (!match) return null;
    return `https://img.youtube.com/vi/${match[1]}/default.jpg`;
  });
}
```

**No edit component** -- the video URL is entered via a plain `<input type="text">` on create/edit pages.

### Color Swatch Renderer

**Detail component** -- shows a colored swatch + hex code:
```typescript
@Component({
  standalone: true,
  template: `
    @if (value(); as colorVal) {
      <span class="d-inline-block align-middle border rounded me-2"
            [style.background-color]="colorVal"
            style="width: 1.5em; height: 1.5em;"></span>
      {{ colorVal }}
    } @else { - }
  `,
})
export class ColorDetailRendererComponent implements SparkAttributeDetailRenderer { ... }
```

**Column component** -- shows just the swatch:
```typescript
@Component({
  standalone: true,
  template: `
    @if (value(); as colorVal) {
      <span class="d-inline-block align-middle border rounded"
            [style.background-color]="colorVal"
            style="width: 1.5em; height: 1.5em;"></span>
    }
  `,
})
export class ColorColumnRendererComponent implements SparkAttributeColumnRenderer { ... }
```

**Edit component** -- renders `<bs-color-picker>` with live preview:
```typescript
@Component({
  standalone: true,
  imports: [FormsModule, BsColorPickerComponent],
  template: `
    <div class="d-flex align-items-start gap-3">
      <bs-color-picker [size]="options()?.['size'] ?? 150"
        [ngModel]="currentColor()" (ngModelChange)="onColorChange($event)">
      </bs-color-picker>
      @if (currentColor(); as c) {
        <div class="d-flex align-items-center gap-2 mt-2">
          <span class="d-inline-block border rounded"
                [style.background-color]="c" style="width: 2em; height: 2em;"></span>
          <code>{{ c }}</code>
        </div>
      }
    </div>
  `,
})
export class ColorEditRendererComponent implements SparkAttributeEditRenderer {
  value = input<any>();
  attribute = input<EntityAttributeDefinition>();
  options = input<Record<string, any>>();
  valueChange = input<(value: any) => void>(() => {});

  currentColor = signal<string>('#000000');

  constructor() {
    effect(() => { const v = this.value(); if (v) this.currentColor.set(v); });
  }

  onColorChange(hex: string): void {
    this.currentColor.set(hex);
    this.valueChange()?.(hex);
  }
}
```

### Registration (Fleet `app.config.ts`)

```typescript
import { provideSparkAttributeRenderers } from '@mintplayer/ng-spark';

export const appConfig: ApplicationConfig = {
  providers: [
    // ...
    provideSparkAttributeRenderers([
      {
        name: 'color-swatch',
        detailComponent: ColorDetailRendererComponent,
        columnComponent: ColorColumnRendererComponent,
        editComponent: ColorEditRendererComponent,
      },
      {
        name: 'video-player',
        detailComponent: VideoPlayerDetailRendererComponent,
        columnComponent: VideoPlayerColumnRendererComponent,
        // No editComponent -- uses default <input type="text"> for URL entry
      },
    ]),
  ]
};
```

## Important: Index Projection

When a custom renderer is used on a query list column, the corresponding attribute **must be included in the RavenDB index**. Otherwise the query returns `null` for that attribute value and the renderer has nothing to display.

Example: `Cars_Overview` index must include `PromoVideoUrl` in both the map and the `VCar` projection class:

```csharp
public class Cars_Overview : AbstractIndexCreationTask<Car>
{
    public Cars_Overview()
    {
        Map = cars => from car in cars
                      select new VCar
                      {
                          // ... other fields ...
                          PromoVideoUrl = car.PromoVideoUrl,
                      };
        StoreAllFields(FieldStorage.Yes);
    }
}

[FromIndex(typeof(Cars_Overview))]
public class VCar
{
    // ... other properties ...
    public string? PromoVideoUrl { get; set; }
}
```

## Key Files

| File | Purpose |
|---|---|
| `MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs` | `Renderer` and `RendererOptions` on C# attribute definition |
| `MintPlayer.Spark.Abstractions/PersistentObject.cs` | `Renderer` and `RendererOptions` on C# PO attribute |
| `MintPlayer.Spark/Services/EntityMapper.cs` | Copies renderer fields + Color ↔ hex conversion |
| `MintPlayer.Spark/Services/ModelSynchronizer.cs` | `System.Drawing.Color` → `"color"` auto-detection |
| `MintPlayer.Spark/Converters/ColorNewtonsoftJsonConverter.cs` | RavenDB Color serialization |
| `MintPlayer.Spark/SparkMiddleware.cs` | Registers color converter with DocumentStore |
| `node_packages/ng-spark/src/lib/interfaces/spark-attribute-renderer.ts` | TypeScript renderer contracts |
| `node_packages/ng-spark/src/lib/providers/spark-attribute-renderer-registry.ts` | DI token + provider function |
| `node_packages/ng-spark/src/lib/components/po-form/spark-po-form.component.ts` | Edit renderer integration |
| `node_packages/ng-spark/src/lib/components/po-detail/spark-po-detail.component.ts` | Detail renderer integration |
| `node_packages/ng-spark/src/lib/components/query-list/spark-query-list.component.ts` | Column renderer integration |
| `Demo/Fleet/Fleet/ClientApp/src/app/renderers/` | All Fleet demo renderer components |
| `Demo/Fleet/Fleet/ClientApp/src/app/app.config.ts` | Renderer registration |
