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
3. The underlying `dataType` is preserved for validation, serialization, and storage -- the renderer only affects **read-only display**
4. **Create/edit pages render plain inputs** based on the underlying `dataType`, exactly as they do on master today. Custom renderers do NOT apply to create/edit views.
5. Each renderer registration provides **two components**: one for the PO detail page, one for the query list column

## Scope

### In Scope
- Backend: `renderer` and `rendererOptions` fields on attribute definitions
- Angular: Global renderer registry with `provideSparkAttributeRenderers()`
- Angular: Integration in `spark-po-detail` and `spark-query-list` only
- Remove: All three existing template directives (`SparkFieldTemplateDirective`, `SparkDetailFieldTemplateDirective`, `SparkColumnTemplateDirective`)
- Remove: All existing usages of those directives in demo apps (Fleet color picker templates)
- Remove: `externalFieldTemplates` input and `@ContentChildren` for field templates in `spark-po-edit` and `spark-po-create`
- Demo: Video player renderer in Fleet demo app
- Demo: Color renderer replacing the existing custom color picker template

### Out of Scope
- Custom renderers in create/edit views (create/edit always use plain inputs)
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
No custom renderers. Always use built-in inputs based on dataType.
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
│    → ignores renderer, renders <input type=text> │
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

Both contracts are **read-only** (no `valueChange`):

```typescript
import { InputSignal } from '@angular/core';
import { EntityAttributeDefinition } from '../models/entity-type';

/**
 * Contract for detail-page renderers (spark-po-detail).
 * Displays a single attribute value in the PO detail view.
 */
export interface SparkAttributeDetailRenderer {
  /** The current attribute value */
  value: InputSignal<any>;
  /** The attribute definition metadata */
  attribute: InputSignal<EntityAttributeDefinition>;
  /** Renderer-specific options from rendererOptions */
  options: InputSignal<Record<string, any> | undefined>;
  /** The full form data (for cross-field dependencies) */
  formData: InputSignal<Record<string, any>>;
}

/**
 * Contract for query-list column renderers (spark-query-list).
 * Displays a compact cell value in the list/grid view.
 */
export interface SparkAttributeColumnRenderer {
  /** The current attribute value */
  value: InputSignal<any>;
  /** The attribute definition metadata */
  attribute: InputSignal<EntityAttributeDefinition>;
  /** Renderer-specific options from rendererOptions */
  options: InputSignal<Record<string, any> | undefined>;
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
 *   { name: 'color-swatch', detailComponent: ColorDetailComponent, columnComponent: ColorColumnComponent },
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
export type { SparkAttributeDetailRenderer, SparkAttributeColumnRenderer } from './lib/interfaces/spark-attribute-renderer';
export type { SparkAttributeRendererRegistration } from './lib/providers/spark-attribute-renderer-registry';
export { SPARK_ATTRIBUTE_RENDERERS, provideSparkAttributeRenderers } from './lib/providers/spark-attribute-renderer-registry';
```

### 10. Dynamic Component Output Binding

`NgComponentOutlet` supports `inputs` but not `outputs`. Since all renderers in this design are **read-only** (detail view and query list), there is no `valueChange` output to bind. This eliminates the output binding problem entirely.

If a future enhancement requires edit-mode renderers, option **(a)** (a thin wrapper component using `ViewContainerRef.createComponent()` for imperative output subscription) would be the approach.

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
3. No changes to `spark-po-form` / `spark-po-create` / `spark-po-edit` rendering logic (they keep plain inputs)

### Phase 5: Demo -- Renderer Components
1. Create `ColorDetailRendererComponent` and `ColorColumnRendererComponent` in Fleet demo
2. Create `VideoPlayerDetailRendererComponent` and `VideoPlayerColumnRendererComponent` in Fleet demo
3. Install `@mintplayer/ng-video-player` + plugins in workspace
4. Register all renderers via `provideSparkAttributeRenderers()` in Fleet app config
5. Add `PromoVideoUrl` property to `Car` C# entity class
6. Verify end-to-end rendering in detail page and query list
