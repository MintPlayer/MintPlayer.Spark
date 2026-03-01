# PRD: @mintplayer/ng-spark - Shared Angular Library

## 1. Problem Statement

The three demo applications (DemoApp, HR, Fleet) each contain their own copy of the core Spark Angular components, services, pipes, and models. This results in:

- **~50 duplicated files** across 3 apps (12 models, 22 pipes, 3 services, 3 components, icon registry)
- **Divergence risk**: when a fix is applied to one app, the others become stale
- **Onboarding friction**: new Spark-based apps must copy-paste from an existing demo
- **Translation service inconsistency**: DemoApp uses `TranslationsService` while HR/Fleet use a more capable `LanguageService` with multi-language switching and `localStorage` persistence

## 2. Goal

Extract all shared, generic Spark UI code into a new `@mintplayer/ng-spark` Angular library so that:

1. Demo apps import from a single source of truth
2. New Spark-based apps get a full CRUD UI by adding one dependency
3. The library is maximally reusable through configuration, injection tokens, and content projection

## 3. Scope

### 3.1 In Scope (extract to library)

| Category | Items | Status across apps |
|---|---|---|
| **Models** (12) | `PersistentObject`, `PersistentObjectAttribute`, `EntityType`, `EntityAttributeDefinition`, `TranslatedString` + `resolveTranslation()`, `ValidationError`, `ValidationRule`, `ShowedOn` + `hasShowedOnFlag()`, `SparkQuery`, `ProgramUnit`, `LookupReference` + `LookupReferenceValue` + `LookupReferenceListItem`, `RetryAction` + `RetryActionPayload` + `RetryActionResult`, `EntityPermissions`, `CustomActionDefinition` | Identical |
| **Services** (3) | `SparkService`, `LanguageService`, `RetryActionService` | Trivially different (see 4.1) |
| **Components** (3) | `PoFormComponent`, `RetryActionModalComponent`, `IconComponent` + `IconRegistry` | Identical / Trivially different |
| **Pipes** (22) | `translate-key`, `resolve-translation`, `input-type`, `attribute-value`, `raw-attribute-value`, `reference-display-value`, `reference-attr-value`, `reference-link-route`, `router-link`, `as-detail-type`, `as-detail-columns`, `as-detail-cell-value`, `as-detail-display-value`, `can-create-detail-row`, `can-delete-detail-row`, `lookup-display-type`, `lookup-display-value`, `lookup-options`, `inline-ref-options`, `error-for-attribute`, `icon-name`, `array-value` | Identical / Trivially different |
| **Provider function** | `provideSpark()` - one-liner to configure the library | New |
| **Route factory** | `sparkRoutes()` - preconfigured lazy-loaded CRUD routes | New |

### 3.2 Out of Scope (remain app-specific)

| Category | Items | Reason |
|---|---|---|
| **Page components** | `PoDetailComponent`, `PoEditComponent`, `PoCreateComponent`, `QueryListComponent` | Each app has different layouts, custom actions, navigation patterns |
| **Shell / layout** | `ShellComponent`, `HomeComponent` | App-specific branding, menus, sidebar |
| **App config** | `app.config.ts`, `app.routes.ts` | Per-app configuration |

### 3.3 Future Consideration

| Item | Notes |
|---|---|
| **Page components as base classes or defaults** | Could provide default `PoDetailComponent`, etc. that apps can extend or replace. Deferred to Phase 2 to keep Phase 1 simple. |
| **Schematic / ng-add** | `ng add @mintplayer/ng-spark` to scaffold a new Spark app. Phase 3. |

## 4. Design Decisions

### 4.1 Translation Service Standardization

**Decision**: Adopt the HR/Fleet `LanguageService` pattern as the library standard, renamed to `SparkLanguageService`.

**Rationale**: DemoApp's `TranslationsService` is a simplified version that:
- Only fetches translations (no culture config)
- Uses the global `resolveTranslation()` function (relies on `localStorage` and `navigator.language`)
- Has no explicit language switching API

The HR/Fleet `LanguageService` is strictly more capable:
- Fetches culture configuration (`/spark/culture`) with available languages
- Exposes `language` signal, `languages` signal, and `setLanguage()` method
- Resolves translations using the current language signal
- Persists language choice to `localStorage`

**Migration**: DemoApp will switch from `TranslationsService` to `SparkLanguageService`. Pipes that reference the translation service will use `SparkLanguageService` via injection.

### 4.2 SparkService Unification

**Decision**: The library `SparkService` will include custom actions support (present in Fleet, absent in DemoApp/HR).

**Rationale**: Custom actions are part of the Spark framework backend (`MintPlayer.Spark/Endpoints/Actions/`). Not including them would make the service incomplete. Apps that don't use custom actions simply won't call those methods.

**Fleet-specific additions** to include:
- `getCustomActions(objectTypeId)` method
- `executeCustomAction(objectTypeId, actionName, parent?, selectedItems?)` method
- `deleteWithRetry()` helper (DemoApp's delete doesn't support retry; Fleet's does)
- `retryResults` array accumulation pattern (Fleet accumulates across multi-step retries)

### 4.3 PoFormComponent: State Management Approach

**Decision**: Use DemoApp/Fleet's pure signals + `effect()` approach.

**Rationale**: HR uses `ngDoCheck()` + `ChangeDetectorRef` which is a legacy pattern incompatible with zoneless change detection. The signals approach is modern, predictable, and aligns with the project's direction (the recent migration commit `1ea5d17` explicitly moved to Angular signals).

### 4.4 Component Selectors

**Decision**: Prefix all component selectors with `spark-` instead of `app-`.

| Current (app-specific) | Library |
|---|---|
| `app-po-form` | `spark-po-form` |
| `app-retry-action-modal` | `spark-retry-action-modal` |
| `app-icon` | `spark-icon` |

### 4.5 Pipe Names

**Decision**: Keep existing pipe names unchanged (e.g., `resolveTranslation`, `inputType`, `attributeValue`). This is the least disruptive and pipe names don't need a prefix since they're scoped to imports.

The `translate-key` pipe (selector: `t`) is used extensively in templates as `{{ 'key' | t }}`. Keep the `t` selector.

### 4.6 Icon Registry

**Decision**: Provide a default `SparkIconRegistry` with built-in Bootstrap Icons SVGs, but allow apps to override or extend it via DI.

**API**:
```typescript
// Library provides:
@Injectable({ providedIn: 'root' })
export class SparkIconRegistry {
  register(name: string, svg: string): void;
  get(name: string): string | undefined;
}

// Apps can register additional icons in their app.config.ts:
provideSparkIcons([
  { name: 'custom-icon', svg: '<svg>...</svg>' }
])
```

### 4.7 Configuration via Injection Token

**Decision**: Follow the `ng-spark-auth` pattern with injection tokens and provider functions.

```typescript
// Configuration interface
export interface SparkConfig {
  baseUrl: string;  // default: '/spark'
}

// Injection token
export const SPARK_CONFIG = new InjectionToken<SparkConfig>('SPARK_CONFIG');

// Provider function
export function provideSpark(config?: Partial<SparkConfig>): Provider[];
```

### 4.8 Route Factory

**Decision**: Provide a `sparkRoutes()` factory function similar to `sparkAuthRoutes()` that apps can spread into their route config.

```typescript
// Library exports:
export function sparkRoutes(options?: SparkRouteOptions): Routes;

// Usage in app:
export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,  // app-specific shell
    children: [
      { path: '', redirectTo: 'home', pathMatch: 'full' },
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      ...sparkRoutes()  // adds query/:queryId, po/:type, po/:type/new, etc.
    ]
  }
];
```

However, since page components (PoDetail, PoCreate, PoEdit, QueryList) remain app-specific, `sparkRoutes()` would need component references passed in. A simpler approach: **do not provide a route factory in Phase 1**. Apps will define their own routes as they do now. This can be revisited once page components are extractable.

## 5. Library Structure

```
node_packages/ng-spark/
  ng-package.json
  package.json
  tsconfig.lib.json
  src/
    public-api.ts
    lib/
      models/
        persistent-object.ts
        persistent-object-attribute.ts
        entity-type.ts
        translated-string.ts
        validation-error.ts
        validation-rule.ts
        showed-on.ts
        spark-query.ts
        program-unit.ts
        lookup-reference.ts
        retry-action.ts
        entity-permissions.ts
        custom-action.ts
        index.ts
      services/
        spark.service.ts
        spark-language.service.ts
        retry-action.service.ts
      components/
        po-form/
          spark-po-form.component.ts
          spark-po-form.component.html
        retry-action-modal/
          spark-retry-action-modal.component.ts
        icon/
          spark-icon.component.ts
          spark-icon-registry.ts
      pipes/
        translate-key.pipe.ts
        resolve-translation.pipe.ts
        input-type.pipe.ts
        attribute-value.pipe.ts
        raw-attribute-value.pipe.ts
        reference-display-value.pipe.ts
        reference-attr-value.pipe.ts
        reference-link-route.pipe.ts
        router-link.pipe.ts
        as-detail-type.pipe.ts
        as-detail-columns.pipe.ts
        as-detail-cell-value.pipe.ts
        as-detail-display-value.pipe.ts
        can-create-detail-row.pipe.ts
        can-delete-detail-row.pipe.ts
        lookup-display-type.pipe.ts
        lookup-display-value.pipe.ts
        lookup-options.pipe.ts
        inline-ref-options.pipe.ts
        error-for-attribute.pipe.ts
        icon-name.pipe.ts
        array-value.pipe.ts
      providers/
        provide-spark.ts
      models/
        spark-config.ts
```

## 6. Public API (`public-api.ts`)

```typescript
// Models
export type { PersistentObject } from './lib/models/persistent-object';
export type { PersistentObjectAttribute } from './lib/models/persistent-object-attribute';
export type { EntityType, EntityAttributeDefinition } from './lib/models/entity-type';
export type { TranslatedString } from './lib/models/translated-string';
export { resolveTranslation } from './lib/models/translated-string';
export type { ValidationError } from './lib/models/validation-error';
export type { ValidationRule } from './lib/models/validation-rule';
export { ShowedOn, hasShowedOnFlag } from './lib/models/showed-on';
export type { SparkQuery } from './lib/models/spark-query';
export type { ProgramUnit, ProgramUnitsConfiguration } from './lib/models/program-unit';
export type { LookupReference, LookupReferenceValue, LookupReferenceListItem } from './lib/models/lookup-reference';
export type { RetryActionPayload, RetryActionResult } from './lib/models/retry-action';
export type { EntityPermissions } from './lib/models/entity-permissions';
export type { CustomActionDefinition } from './lib/models/custom-action';

// Services
export { SparkService } from './lib/services/spark.service';
export { SparkLanguageService } from './lib/services/spark-language.service';
export { RetryActionService } from './lib/services/retry-action.service';

// Components
export { SparkPoFormComponent } from './lib/components/po-form/spark-po-form.component';
export { SparkRetryActionModalComponent } from './lib/components/retry-action-modal/spark-retry-action-modal.component';
export { SparkIconComponent } from './lib/components/icon/spark-icon.component';
export { SparkIconRegistry } from './lib/components/icon/spark-icon-registry';

// Pipes (all standalone, importable individually)
export { TranslateKeyPipe } from './lib/pipes/translate-key.pipe';
export { ResolveTranslationPipe } from './lib/pipes/resolve-translation.pipe';
export { InputTypePipe } from './lib/pipes/input-type.pipe';
export { AttributeValuePipe } from './lib/pipes/attribute-value.pipe';
export { RawAttributeValuePipe } from './lib/pipes/raw-attribute-value.pipe';
export { ReferenceDisplayValuePipe } from './lib/pipes/reference-display-value.pipe';
export { ReferenceAttrValuePipe } from './lib/pipes/reference-attr-value.pipe';
export { ReferenceLinkRoutePipe } from './lib/pipes/reference-link-route.pipe';
export { RouterLinkPipe } from './lib/pipes/router-link.pipe';
export { AsDetailTypePipe } from './lib/pipes/as-detail-type.pipe';
export { AsDetailColumnsPipe } from './lib/pipes/as-detail-columns.pipe';
export { AsDetailCellValuePipe } from './lib/pipes/as-detail-cell-value.pipe';
export { AsDetailDisplayValuePipe } from './lib/pipes/as-detail-display-value.pipe';
export { CanCreateDetailRowPipe } from './lib/pipes/can-create-detail-row.pipe';
export { CanDeleteDetailRowPipe } from './lib/pipes/can-delete-detail-row.pipe';
export { LookupDisplayTypePipe } from './lib/pipes/lookup-display-type.pipe';
export { LookupDisplayValuePipe } from './lib/pipes/lookup-display-value.pipe';
export { LookupOptionsPipe } from './lib/pipes/lookup-options.pipe';
export { InlineRefOptionsPipe } from './lib/pipes/inline-ref-options.pipe';
export { ErrorForAttributePipe } from './lib/pipes/error-for-attribute.pipe';
export { IconNamePipe } from './lib/pipes/icon-name.pipe';
export { ArrayValuePipe } from './lib/pipes/array-value.pipe';

// Providers
export { provideSpark } from './lib/providers/provide-spark';
export type { SparkConfig } from './lib/models/spark-config';
export { SPARK_CONFIG } from './lib/models/spark-config';
```

## 7. Package Configuration

### 7.1 `package.json`
```json
{
  "name": "@mintplayer/ng-spark",
  "private": false,
  "version": "0.0.1",
  "description": "Angular component library for MintPlayer.Spark CRUD applications",
  "repository": {
    "type": "git",
    "url": "https://github.com/MintPlayer/MintPlayer.Spark",
    "directory": "node_packages/ng-spark"
  },
  "scripts": {
    "build": "ng-packagr -p ng-package.json"
  },
  "peerDependencies": {
    "@angular/core": "^21.0.0",
    "@angular/common": "^21.0.0",
    "@angular/router": "^21.0.0",
    "@angular/forms": "^21.0.0",
    "@mintplayer/ng-bootstrap": "^21.9.0",
    "@mintplayer/pagination": "*",
    "rxjs": "~7.8.0"
  },
  "sideEffects": false
}
```

### 7.2 Root `package.json` update
Add `"node_packages/ng-spark"` to the `workspaces` array.

### 7.3 `tsconfig` path mapping
Each demo app's `tsconfig.json` needs:
```json
{
  "compilerOptions": {
    "paths": {
      "@mintplayer/ng-spark": ["../../../../node_packages/ng-spark/src/public-api"]
    }
  }
}
```

## 8. Migration Strategy

### Phase 1: Create library with models, services, pipes (low risk)

1. **Create `node_packages/ng-spark/`** with the directory structure from Section 5
2. **Copy models** verbatim from DemoApp (all identical across apps)
3. **Create `SparkLanguageService`** based on HR/Fleet's `LanguageService` (the superset)
4. **Copy `SparkService`** based on Fleet's version (includes custom actions + multi-step retry)
5. **Copy `RetryActionService`** (identical across apps)
6. **Copy all 22 pipes**, updating import paths. For pipes that reference the translation service, inject `SparkLanguageService` instead of the app-local service
7. **Set up `ng-package.json`**, `tsconfig.lib.json`, `package.json`**
8. **Add workspace** to root `package.json`
9. **Add tsconfig path** mappings in each demo app
10. **Verify build**: `npm run build` from `node_packages/ng-spark/`

### Phase 2: Add components (medium risk)

1. **Move `RetryActionModalComponent`** -> `SparkRetryActionModalComponent` (identical across apps, trivial)
2. **Move `IconComponent` + `IconRegistry`** -> `SparkIconComponent` + `SparkIconRegistry`
3. **Move `PoFormComponent`** -> `SparkPoFormComponent` (this is the most complex piece)
   - Update selector from `app-po-form` to `spark-po-form`
   - Self-referencing in template (recursive for AsDetail) must use new selector
   - Update all pipe/service imports to library-internal paths

### Phase 3: Migrate demo apps (high visibility)

For each demo app (start with DemoApp as it's simplest):

1. **Replace model imports**: `from '../../core/models'` -> `from '@mintplayer/ng-spark'`
2. **Replace service imports**: inject `SparkService` and `SparkLanguageService` from library
3. **Replace pipe imports**: import from `@mintplayer/ng-spark'`
4. **Replace component imports** in templates: `app-po-form` -> `spark-po-form`, etc.
5. **Delete local copies** of migrated files
6. **Run and test** the app
7. **Repeat** for HR and Fleet

### Phase 4: Clean up

1. Delete all local copies of extracted code from demo apps
2. Verify all 3 apps build and run correctly
3. Run any existing tests

## 9. Detailed Component API

### 9.1 SparkPoFormComponent

```typescript
@Component({
  selector: 'spark-po-form',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SparkPoFormComponent {
  // Inputs
  entityType = input<EntityType | null>(null);
  formData = model<Record<string, any>>({});
  validationErrors = input<ValidationError[]>([]);
  showButtons = input(false);
  isSaving = input(false);

  // Outputs
  save = output<void>();
  cancel = output<void>();
}
```

**Template**: Uses `@mintplayer/ng-bootstrap` components exclusively for all UI rendering. Handles all Spark data types: string, number, boolean, decimal, Reference, AsDetail (single/array, inline/modal), LookupReference (dropdown/modal).

### 9.2 SparkRetryActionModalComponent

```typescript
@Component({
  selector: 'spark-retry-action-modal',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SparkRetryActionModalComponent {
  // No inputs/outputs - driven by RetryActionService signals
}
```

**Usage**: Place once in the app shell. It listens to `RetryActionService.payload()` and shows a modal when an HTTP 449 retry-action response is received.

### 9.3 SparkIconComponent

```typescript
@Component({
  selector: 'spark-icon',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SparkIconComponent {
  name = input.required<string>();
}
```

### 9.4 SparkLanguageService

```typescript
@Injectable({ providedIn: 'root' })
export class SparkLanguageService {
  readonly language: Signal<string>;           // current language code
  readonly languages: Signal<Record<string, TranslatedString>>;  // available languages

  setLanguage(lang: string): void;             // switch + persist to localStorage
  resolve(ts: TranslatedString | undefined): string;  // resolve using current language
  t(key: string): string;                      // translate a key
}
```

### 9.5 SparkService

```typescript
@Injectable({ providedIn: 'root' })
export class SparkService {
  // Entity Types
  getEntityTypes(): Promise<EntityType[]>;
  getEntityType(id: string): Promise<EntityType>;
  getEntityTypeByClrType(clrType: string): Promise<EntityType | undefined>;

  // Permissions
  getPermissions(entityTypeId: string): Promise<EntityPermissions>;

  // Queries
  getQueries(): Promise<SparkQuery[]>;
  getQuery(id: string): Promise<SparkQuery>;
  getQueryByName(name: string): Promise<SparkQuery | undefined>;
  executeQuery(queryId: string, sortBy?: string, sortDirection?: string): Promise<PersistentObject[]>;
  executeQueryByName(queryName: string): Promise<PersistentObject[]>;

  // Program Units
  getProgramUnits(): Promise<ProgramUnitsConfiguration>;

  // CRUD
  list(type: string): Promise<PersistentObject[]>;
  get(type: string, id: string): Promise<PersistentObject>;
  create(type: string, data: Partial<PersistentObject>): Promise<PersistentObject>;
  update(type: string, id: string, data: Partial<PersistentObject>): Promise<PersistentObject>;
  delete(type: string, id: string): Promise<void>;

  // Custom Actions
  getCustomActions(objectTypeId: string): Promise<CustomActionDefinition[]>;
  executeCustomAction(objectTypeId: string, actionName: string, parent?: PersistentObject, selectedItems?: PersistentObject[]): Promise<void>;

  // Lookup References
  getLookupReferences(): Promise<LookupReferenceListItem[]>;
  getLookupReference(name: string): Promise<LookupReference>;
  addLookupReferenceValue(name: string, value: LookupReferenceValue): Promise<LookupReferenceValue>;
  updateLookupReferenceValue(name: string, key: string, value: LookupReferenceValue): Promise<LookupReferenceValue>;
  deleteLookupReferenceValue(name: string, key: string): Promise<void>;
}
```

## 10. Risks and Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Breaking changes during migration | Demo apps fail to build | Migrate one app at a time; keep local copies until verified |
| Pipe purity mismatch | Some pipes marked impure in HR/Fleet but pure in DemoApp | Standardize all pipes as pure (aligns with signals approach where pipe inputs change when state changes) |
| `PoFormComponent` template divergence | Template might have subtle differences between apps | Use DemoApp's template as baseline (most recent/cleanest); verify all data types work in all apps |
| Circular dependency: `PoFormComponent` references itself | ng-packagr build fails | Self-reference via selector in template is fine; Angular handles recursive components |
| `@mintplayer/pagination` peer dependency | Library users might not have this package | Already installed at root; add as peer dependency |

## 11. Success Criteria

1. All 3 demo apps build successfully importing from `@mintplayer/ng-spark`
2. No duplicated Spark-specific code remains in demo apps (only app-specific pages and shell)
3. Creating a new Spark app requires only: `npm install @mintplayer/ng-spark` + writing page components and routes
4. Library builds independently with `ng-packagr`
5. All existing functionality (CRUD, references, AsDetail, lookups, retry actions, custom actions, translations) works identically to before
