# PRD: Custom Attribute Renderers

## Problem Statement

Developers using MintPlayer.Spark need the ability to customize how specific PersistentObject attributes are **displayed** in read-only views (detail page and query list). For example, a `Car` entity with a `PromoVideoUrl` attribute (stored as a string) should render a `<video-player>` component on the detail page, and perhaps a thumbnail in the query list -- rather than just showing the raw URL text.

### Current State (to be replaced)

Three inline template directives exist today for per-component customization:
- `SparkFieldTemplateDirective` (`sparkFieldTemplate`) -- used in `spark-po-form` (create/edit)
- `SparkDetailFieldTemplateDirective` (`sparkDetailFieldTemplate`) -- used in `spark-po-detail`
- `SparkColumnTemplateDirective` (`sparkColumnTemplate`) -- used in `spark-query-list`

These are used in the Fleet demo to render a color picker for `InteriorColor` in create/edit views.

**Problems with the current approach:**
- Per-component: the same template must be copy-pasted into every page that uses it
- Not backend-driven: no metadata in the model JSON signals *how* an attribute should be rendered
- Not pluggable: you cannot install a renderer from a library and have it "just work"

**This PRD replaces all three directives** with a single global renderer registry system.

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
- Angular: Global renderer registry with `provideSparkAttributeRenderers()`
- Angular: Integration in `spark-po-detail`, `spark-query-list`, and `spark-po-form`
- Angular: Optional `editComponent` for custom create/edit form rendering
- Remove: All three existing template directives (`SparkFieldTemplateDirective`, `SparkDetailFieldTemplateDirective`, `SparkColumnTemplateDirective`)
- Remove: All existing usages of those directives in demo apps (Fleet color picker templates)
- Remove: `externalFieldTemplates` input and `@ContentChildren` for field templates in `spark-po-edit` and `spark-po-create`
- Demo: Video player renderer in Fleet demo app (detail + column only, plain `<input>` on edit)
- Demo: Color swatch renderer for read-only views + color picker edit renderer using `<bs-color-picker>`

### Out of Scope
- Dynamic/computed `rendererOptions`
- C# `[Renderer]` attribute decorator (JSON model approach is sufficient)

## Architecture

### Rendering Priority

**Detail view (`spark-po-detail`):**
```
1. Global attribute renderer (matched by attr.renderer)    -- THIS FEATURE
2. Built-in rendering (boolean, color, Reference, AsDetail, etc.)
3. Default text display
```

**Query list view (`spark-query-list`):**
```
1. Global attribute renderer (matched by attr.renderer)    -- THIS FEATURE
2. Built-in rendering (boolean, color)
3. Default text display
```

**Create/edit views (`spark-po-form`, `spark-po-create`, `spark-po-edit`):**
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

**`MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs`** -- add to `EntityAttributeDefinition`:
```csharp
/// <summary>
/// Optional renderer name that tells the frontend which custom component to use
/// for read-only display (detail page and query list).
/// Example: "video-player", "color-swatch", "markdown"
/// </summary>
public string? Renderer { get; set; }

/// <summary>
/// Optional configuration for the renderer (passed as-is to the frontend component).
/// Example: { "width": 480, "height": 270, "autoplay": false }
/// </summary>
public Dictionary<string, object>? RendererOptions { get; set; }
```

**`MintPlayer.Spark.Abstractions/PersistentObject.cs`** -- add to `PersistentObjectAttribute`:
```csharp
public string? Renderer { get; set; }
public Dictionary<string, object>? RendererOptions { get; set; }
```

**`MintPlayer.Spark/Services/EntityMapper.cs`** -- copy fields during mapping (at the `new PersistentObjectAttribute` block, ~line 105):
```csharp
var attribute = new PersistentObjectAttribute
{
    // ... existing fields ...
    Renderer = attrDef.Renderer,
    RendererOptions = attrDef.RendererOptions,
};
```

### 2. Model JSON Example

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
    "autoplay": false
  },
  "order": 10
}
```

The existing `InteriorColor` attribute (currently `dataType: "color"`) gets a renderer too:
```json
{
  "name": "InteriorColor",
  "dataType": "color",
  "renderer": "color-swatch"
}
```

### 3. Angular: TypeScript Model Changes

**`node_packages/ng-spark/src/lib/models/entity-type.ts`** -- add to `EntityAttributeDefinition`:
```typescript
/** Renderer component name for custom display in detail/list views */
renderer?: string;
/** Options passed to the renderer component */
rendererOptions?: Record<string, any>;
```

**`node_packages/ng-spark/src/lib/models/persistent-object-attribute.ts`** -- add to `PersistentObjectAttribute`:
```typescript
renderer?: string;
rendererOptions?: Record<string, any>;
```

### 4. Angular: Renderer Component Contracts

**New file**: `node_packages/ng-spark/src/lib/interfaces/spark-attribute-renderer.ts`

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

**New file**: `node_packages/ng-spark/src/lib/providers/spark-attribute-renderer-registry.ts`

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

/**
 * Register custom attribute renderers globally.
 *
 * @example
 * // app.config.ts
 * provideSparkAttributeRenderers([
 *   { name: 'video-player', detailComponent: VideoDetailComponent, columnComponent: VideoColumnComponent },
 *   { name: 'color-swatch', detailComponent: ColorDetailComponent, columnComponent: ColorColumnComponent, editComponent: ColorEditComponent },
 * ])
 */
export function provideSparkAttributeRenderers(
  renderers: SparkAttributeRendererRegistration[]
): Provider {
  return {
    provide: SPARK_ATTRIBUTE_RENDERERS,
    useValue: renderers,
  };
}
```

### 6. Angular: spark-po-detail Integration

**`spark-po-detail.component.ts`** changes:
- Remove `@ContentChildren(SparkDetailFieldTemplateDirective)`
- Remove import of `SparkDetailFieldTemplateDirective`
- Add `inject(SPARK_ATTRIBUTE_RENDERERS)`
- Add `getDetailRendererComponent(attr)` method

```typescript
private readonly rendererRegistry = inject(SPARK_ATTRIBUTE_RENDERERS);

getDetailRendererComponent(attr: EntityAttributeDefinition): Type<any> | null {
  if (!attr.renderer) return null;
  return this.rendererRegistry.find(r => r.name === attr.renderer)?.detailComponent ?? null;
}
```

**`spark-po-detail.component.html`** -- replace the `getDetailFieldTemplate` check in the `#detailAttrField` template:

```html
<ng-template #detailAttrField let-attr let-currentItem="item">
  <dt [sm]="3">{{ (attr.label | resolveTranslation) || attr.name }}</dt>
  <dd [sm]="9">
    @if (getDetailRendererComponent(attr); as rendererType) {
      <ng-container
        *ngComponentOutlet="rendererType;
          inputs: {
            value: (attr.name | rawAttributeValue:currentItem),
            attribute: attr,
            options: attr.rendererOptions,
            formData: currentItem
          }">
      </ng-container>
    } @else if (attr.dataType === 'AsDetail' && attr.isArray) {
      <!-- ... existing built-in rendering unchanged ... -->
```

### 7. Angular: spark-query-list Integration

**`spark-query-list.component.ts`** changes:
- Remove `@ContentChildren(SparkColumnTemplateDirective)`
- Remove import of `SparkColumnTemplateDirective`
- Add `inject(SPARK_ATTRIBUTE_RENDERERS)`
- Add `getColumnRendererComponent(attr)` method

```typescript
private readonly rendererRegistry = inject(SPARK_ATTRIBUTE_RENDERERS);

getColumnRendererComponent(attr: EntityAttributeDefinition): Type<any> | null {
  if (!attr.renderer) return null;
  return this.rendererRegistry.find(r => r.name === attr.renderer)?.columnComponent ?? null;
}
```

**`spark-query-list.component.html`** -- replace the `getColumnTemplate` check:

```html
<td>
  @if (getColumnRendererComponent(attr); as rendererType) {
    <ng-container
      *ngComponentOutlet="rendererType;
        inputs: {
          value: (attr.name | attributeValue:item:entityType():lookupReferenceOptions():allEntityTypes()),
          attribute: attr,
          options: attr.rendererOptions
        }">
    </ng-container>
  } @else if (attr.dataType === 'boolean') {
    <!-- ... existing built-in rendering unchanged ... -->
```

### 8. Angular: Remove Old Template Directives

**Delete files:**
- `node_packages/ng-spark/src/lib/directives/spark-field-template.directive.ts`
- `node_packages/ng-spark/src/lib/directives/spark-detail-field-template.directive.ts`
- `node_packages/ng-spark/src/lib/directives/spark-column-template.directive.ts`

**Remove from `public-api.ts`:**
```typescript
// DELETE these lines:
export { SparkFieldTemplateDirective } from './lib/directives/spark-field-template.directive';
export type { SparkFieldTemplateContext } from './lib/directives/spark-field-template.directive';
export { SparkDetailFieldTemplateDirective } from './lib/directives/spark-detail-field-template.directive';
export type { SparkDetailFieldTemplateContext } from './lib/directives/spark-detail-field-template.directive';
export { SparkColumnTemplateDirective } from './lib/directives/spark-column-template.directive';
export type { SparkColumnTemplateContext } from './lib/directives/spark-column-template.directive';
```

**Remove from components:**
- `spark-po-form.component.ts`: Remove `@ContentChildren(SparkFieldTemplateDirective)`, `externalFieldTemplates` input, `getFieldTemplate()`, `getFieldTemplateContext()`, and the `sparkFieldTemplate` check in the template
- `spark-po-edit.component.ts`: Remove `@ContentChildren(SparkFieldTemplateDirective)`, `fieldTemplates`, and `[externalFieldTemplates]` from template
- `spark-po-create.component.ts`: Remove `@ContentChildren(SparkFieldTemplateDirective)`, `fieldTemplates`, and `[externalFieldTemplates]` from template
- `spark-po-detail.component.ts`: Remove `@ContentChildren(SparkDetailFieldTemplateDirective)`, `getDetailFieldTemplate()`, `getDetailFieldContext()`
- `spark-query-list.component.ts`: Remove `@ContentChildren(SparkColumnTemplateDirective)`, `getColumnTemplate()`, `getColumnTemplateContext()`

**Remove from demo apps:**
- `Demo/Fleet/Fleet/ClientApp/src/app/pages/po-edit/po-edit.component.html`: Remove the `<ng-template sparkFieldTemplate="InteriorColor">` block
- `Demo/Fleet/Fleet/ClientApp/src/app/pages/po-create/po-create.component.html`: Remove the `<ng-template sparkFieldTemplate="InteriorColor">` block

### 9. Angular: Public API Exports

**Add to `node_packages/ng-spark/src/public-api.ts`:**
```typescript
// Attribute Renderers
export type { SparkAttributeDetailRenderer, SparkAttributeColumnRenderer, SparkAttributeEditRenderer } from './lib/interfaces/spark-attribute-renderer';
export type { SparkAttributeRendererRegistration } from './lib/providers/spark-attribute-renderer-registry';
export { SPARK_ATTRIBUTE_RENDERERS, provideSparkAttributeRenderers } from './lib/providers/spark-attribute-renderer-registry';
```

### 10. Angular: spark-po-form Integration (Edit Renderers)

`NgComponentOutlet` supports `inputs` but not `outputs`. For edit renderers, the value change is communicated via a **callback function passed as an input**:

**`spark-po-form.component.ts`** changes:
- Inject `SPARK_ATTRIBUTE_RENDERERS`
- Add `NgComponentOutlet` to imports
- Add `getEditRendererComponent(attr)` and `getEditRendererInputs(attr)` methods

```typescript
private readonly rendererRegistry = inject(SPARK_ATTRIBUTE_RENDERERS);

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
      this.formData()[attr.name] = newValue;
      this.onFieldChange();
    }
  };
}
```

**`spark-po-form.component.html`** -- in the `@else` branch (default input), check for edit renderer first:
```html
} @else if (getEditRendererComponent(attr); as editComp) {
  <ng-container *ngComponentOutlet="editComp; inputs: getEditRendererInputs(attr)"></ng-container>
} @else {
  <input [type]="attr.dataType | inputType" ...>
}
```

**Key design decision:** The edit renderer receives a `valueChange` callback as an input signal, NOT an output. This is because `NgComponentOutlet` cannot bind to outputs. The component calls `valueChange()(newValue)` when the user changes the value.

## Demo: Example Renderers

### Video Player Renderer (Fleet Demo)

**Detail component** -- shows the video player:
```typescript
@Component({
  selector: 'app-video-detail-renderer',
  standalone: true,
  imports: [VideoPlayerComponent],
  providers: [provideVideoApis(youtubePlugin, vimeoPlugin)],
  template: `
    @if (value(); as url) {
      <video-player
        [width]="options()?.['width'] ?? 480"
        [height]="options()?.['height'] ?? 270"
        [autoplay]="options()?.['autoplay'] ?? false"
        [url]="url">
      </video-player>
    } @else {
      <span class="text-muted">-</span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class VideoPlayerDetailRendererComponent implements SparkAttributeDetailRenderer {
  value = input<any>();
  attribute = input<EntityAttributeDefinition>();
  options = input<Record<string, any>>();
  formData = input<Record<string, any>>({});
}
```

**Column component** -- shows a compact link/icon in the list:
```typescript
@Component({
  selector: 'app-video-column-renderer',
  standalone: true,
  template: `
    @if (value(); as url) {
      <a [href]="url" target="_blank" title="Watch video">
        <spark-icon name="play-circle" />
      </a>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class VideoPlayerColumnRendererComponent implements SparkAttributeColumnRenderer {
  value = input<any>();
  attribute = input<EntityAttributeDefinition>();
  options = input<Record<string, any>>();
}
```

### Color Swatch Renderer (replaces existing color picker template)

**Edit component** -- shows `<bs-color-picker>` on create/edit forms:
```typescript
@Component({
  selector: 'app-color-edit-renderer',
  standalone: true,
  imports: [BsColorPickerComponent],
  template: `
    <bs-color-picker
      [ngModel]="value()"
      (ngModelChange)="valueChange()?.($event)">
    </bs-color-picker>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ColorEditRendererComponent implements SparkAttributeEditRenderer {
  value = input<any>();
  attribute = input<EntityAttributeDefinition>();
  options = input<Record<string, any>>();
  valueChange = input<(value: any) => void>(() => {});
}
```

**Detail component** -- shows color swatch + hex value:
```typescript
@Component({
  selector: 'app-color-detail-renderer',
  standalone: true,
  template: `
    @if (value(); as colorVal) {
      <span class="d-inline-block align-middle border rounded me-2"
            [style.background-color]="colorVal"
            style="width: 1.5em; height: 1.5em;"></span>
      {{ colorVal }}
    } @else {
      -
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ColorDetailRendererComponent implements SparkAttributeDetailRenderer {
  value = input<any>();
  attribute = input<EntityAttributeDefinition>();
  options = input<Record<string, any>>();
  formData = input<Record<string, any>>({});
}
```

**Column component** -- shows just the swatch:
```typescript
@Component({
  selector: 'app-color-column-renderer',
  standalone: true,
  template: `
    @if (value(); as colorVal) {
      <span class="d-inline-block align-middle border rounded"
            [style.background-color]="colorVal"
            style="width: 1.5em; height: 1.5em;"></span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ColorColumnRendererComponent implements SparkAttributeColumnRenderer {
  value = input<any>();
  attribute = input<EntityAttributeDefinition>();
  options = input<Record<string, any>>();
}
```

### Registration (Fleet app.config.ts)

```typescript
import { provideSparkAttributeRenderers } from '@mintplayer/ng-spark';

export const appConfig = {
  providers: [
    provideSparkAttributeRenderers([
      {
        name: 'video-player',
        detailComponent: VideoPlayerDetailRendererComponent,
        columnComponent: VideoPlayerColumnRendererComponent,
      },
      {
        name: 'color-swatch',
        detailComponent: ColorDetailRendererComponent,
        columnComponent: ColorColumnRendererComponent,
        editComponent: ColorEditRendererComponent,
      },
    ]),
  ]
};
```

## Implementation Plan

### Phase 1: Backend -- C# Model + Mapping
1. Add `Renderer` and `RendererOptions` to `EntityAttributeDefinition` in `EntityTypeDefinition.cs`
2. Add `Renderer` and `RendererOptions` to `PersistentObjectAttribute` in `PersistentObject.cs`
3. Copy `Renderer` / `RendererOptions` in `EntityMapper.cs` during attribute mapping
4. Add `"renderer": "color-swatch"` to `InteriorColor` in Fleet `Car.json`
5. Add `PromoVideoUrl` attribute with `"renderer": "video-player"` to Fleet `Car.json`

### Phase 2: Angular -- Models, Contracts, Registry
1. Add `renderer` and `rendererOptions` to TypeScript `EntityAttributeDefinition` and `PersistentObjectAttribute`
2. Create `SparkAttributeDetailRenderer` and `SparkAttributeColumnRenderer` interfaces
3. Create `SPARK_ATTRIBUTE_RENDERERS` token and `provideSparkAttributeRenderers()` function
4. Export from `public-api.ts`

### Phase 3: Angular -- Remove Old Template Directives
1. Delete `spark-field-template.directive.ts`, `spark-detail-field-template.directive.ts`, `spark-column-template.directive.ts`
2. Remove all `@ContentChildren`, `getFieldTemplate()`, `getDetailFieldTemplate()`, `getColumnTemplate()` from components
3. Remove `externalFieldTemplates` input from `spark-po-form`
4. Remove `[externalFieldTemplates]` binding from `spark-po-edit` and `spark-po-create` templates
5. Remove `sparkFieldTemplate` usage from Fleet demo `po-edit` and `po-create` pages
6. Remove exports from `public-api.ts`

### Phase 4: Angular -- Integrate Renderer Registry
1. Update `spark-po-detail` to inject `SPARK_ATTRIBUTE_RENDERERS` and use `NgComponentOutlet` for renderer lookup
2. Update `spark-query-list` to inject `SPARK_ATTRIBUTE_RENDERERS` and use `NgComponentOutlet` for renderer lookup
3. Update `spark-po-form` to inject `SPARK_ATTRIBUTE_RENDERERS` and use `NgComponentOutlet` for optional edit renderers (with `valueChange` callback input)

### Phase 5: Demo -- Renderer Components
1. Create `ColorDetailRendererComponent`, `ColorColumnRendererComponent`, and `ColorEditRendererComponent` in Fleet demo
2. Create `VideoPlayerDetailRendererComponent` and `VideoPlayerColumnRendererComponent` in Fleet demo (no edit component -- uses default `<input type="text">`)
3. Install `@mintplayer/ng-video-player` + plugins in workspace
4. Register all renderers via `provideSparkAttributeRenderers()` in Fleet app config
5. Add `PromoVideoUrl` property to `Car` C# entity class
6. Verify end-to-end: video player on detail, color picker on edit, color swatch on detail/list

### Phase 6: Backend -- System.Drawing.Color Support
1. Add `System.Drawing.Color` → `"color"` mapping in `GetDataType()` (ModelSynchronizer + EntityMapper)
2. Create `ColorNewtonsoftJsonConverter` for RavenDB hex string serialization
3. Register converter in `SparkMiddleware.cs` DocumentStore conventions
4. Add Color ↔ hex string conversion in EntityMapper (`SetPropertyValue` + `ToPersistentObject`)
5. Change `Car.Color` / `Car.InteriorColor` from `string?` to `Color?`
6. Re-synchronize models -- `dataType: "color"` now auto-detected from C# type
