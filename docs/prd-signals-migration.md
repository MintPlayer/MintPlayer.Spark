# PRD: Angular Signals Migration & RxJS Removal

## Overview
Migrate all Angular projects in MintPlayer.Spark to a fully modern Angular 21 architecture:
1. Ensure zoneless change detection is explicitly configured
2. Remove all RxJS usage — replace with async/await + signals
3. Convert all template-bound variables to signals (signal() for read, model() for two-way bindings)
4. Convert `resolveTranslation` function calls in templates to a pure pipe

## Scope
- `packages/ng-spark-auth/` — shared auth library
- `Demo/DemoApp/DemoApp/ClientApp/` — DemoApp
- `Demo/HR/HR/ClientApp/` — HR app
- `Demo/Fleet/Fleet/ClientApp/` — Fleet app

## Current State
- **Angular 21.0.6**, standalone components, Vite build, npm workspaces
- **Zone.js**: NOT imported — apps are already zoneless by default (Angular 21)
- **RxJS 7.8.2**: Used extensively in services (Observable return types, switchMap, forkJoin, tap, map, catchError, Subject, fromEvent)
- **Signals**: Partially adopted (auth service, shell component, some UI state)
- **resolveTranslation**: Called as a function directly in templates (causes re-evaluation every change detection cycle)
- **Two-way bindings**: Heavy use of `[(ngModel)]` in form components, `[(isOpen)]` on modals, custom `[(formData)]` bindings

## Requirements

### 1. Zoneless Change Detection (Already Done — Verify Only)
Angular 21 is zoneless by default. Verify:
- No `zone.js` in any polyfills or imports
- No `provideZoneChangeDetection()` in app configs
- Consider adding explicit `provideExperimentalZonelessChangeDetection()` if Angular 21 requires it (check docs)

**Status**: Already complete. Just verify.

### 2. Remove All RxJS

#### 2a. Services — Replace Observable<T> with Promise<T>
All HTTP service methods currently return `Observable<T>`. Convert to `async` methods returning `Promise<T>`.

**Pattern — Before:**
```typescript
getEntityTypes(): Observable<EntityType[]> {
  return this.http.get<EntityType[]>('/api/entity-types');
}
```

**Pattern — After:**
```typescript
async getEntityTypes(): Promise<EntityType[]> {
  return firstValueFrom(this.http.get<EntityType[]>('/api/entity-types'));
}
```

NOTE: Since Angular's HttpClient still returns Observable internally, use `firstValueFrom` from rxjs as a bridge ONLY in the service layer. The goal is that **no component or template** touches RxJS. If Angular 21 provides a native `fetch`-based HttpClient or resource API, prefer that.

Actually, Angular 21 has the `resource()` and `httpResource()` APIs. Prefer using:
- `httpResource()` for simple GET requests that can be signal-based
- `async/await` with `firstValueFrom` for mutations (POST, PUT, DELETE) and complex flows

#### 2b. Components — Replace subscribe() with async/await
**Pattern — Before:**
```typescript
ngOnInit() {
  this.sparkService.getEntityTypes().subscribe(types => {
    this.entityTypes = types;
  });
}
```

**Pattern — After (option A — resource):**
```typescript
entityTypes = httpResource<EntityType[]>(() => '/api/entity-types');
// In template: @if (entityTypes.value(); as types) { ... }
```

**Pattern — After (option B — async init):**
```typescript
entityTypes = signal<EntityType[]>([]);

async ngOnInit() {
  this.entityTypes.set(await this.sparkService.getEntityTypes());
}
```

Prefer `resource()`/`httpResource()` for data loading. Use async/await for imperative actions (create, update, delete).

#### 2c. Replace forkJoin with Promise.all
**Before:**
```typescript
forkJoin([this.svc.getTypes(), this.svc.getPerms()]).subscribe(([types, perms]) => { ... });
```

**After:**
```typescript
const [types, perms] = await Promise.all([this.svc.getTypes(), this.svc.getPerms()]);
```

#### 2d. Replace Subject/BehaviorSubject with signals
**Before:**
```typescript
private retrySubject = new Subject<void>();
retry$ = this.retrySubject.asObservable();
triggerRetry() { this.retrySubject.next(); }
```

**After:**
```typescript
retryCount = signal(0);
triggerRetry() { this.retryCount.update(c => c + 1); }
// Consumers use effect() or computed() to react
```

#### 2e. Replace fromEvent with HostListener or manual event listeners
**Before:**
```typescript
fromEvent(window, 'resize').pipe(takeUntilDestroyed()).subscribe(() => { ... });
```

**After:**
```typescript
constructor() {
  const destroyRef = inject(DestroyRef);
  const listener = () => { this.updateLayout(); };
  window.addEventListener('resize', listener);
  destroyRef.onDestroy(() => window.removeEventListener('resize', listener));
}
```

### 3. Signals for All Bound Variables

#### 3a. Convert @Input() to input() / input.required()
**Before:**
```typescript
@Input() entityType!: EntityType;
```

**After:**
```typescript
entityType = input.required<EntityType>();
// Access in code: this.entityType()
```

#### 3b. Convert @Output() to output()
**Before:**
```typescript
@Output() saved = new EventEmitter<PersistentObject>();
```

**After:**
```typescript
saved = output<PersistentObject>();
// Emit: this.saved.emit(obj);
```

#### 3c. Convert two-way bound properties to model()
**Before:**
```typescript
@Input() formData: Record<string, any> = {};
@Output() formDataChange = new EventEmitter<Record<string, any>>();
```

**After:**
```typescript
formData = model<Record<string, any>>({});
// Template: [(formData)]="formData" still works
// Code: this.formData(), this.formData.set(newValue)
```

For `[(ngModel)]` bindings on native elements — these stay as-is since ngModel is a directive, but the backing property should be a signal where possible. Use `[(ngModel)]="formData()[attr.name]"` pattern or restructure to use signal-based form state.

**IMPORTANT**: For `[(ngModel)]` bindings that write into object properties like `formData[attr.name]`, we need careful consideration. Options:
- Keep formData as a plain object but wrap in signal for change tracking
- Use a `WritableSignal<Record<string, any>>` and update immutably

#### 3d. Convert all template-bound class properties to signals
Any property read in a template (`{{ prop }}`, `[attr]="prop"`, `*ngIf="prop"`) must be a signal.

### 4. resolveTranslation Pipe

Create a shared `ResolveTranslationPipe`:

```typescript
@Pipe({ name: 'resolveTranslation', standalone: true, pure: true })
export class ResolveTranslationPipe implements PipeTransform {
  transform(value: TranslatedString | undefined, lang?: string): string {
    if (!value) return '';
    const language = lang ?? localStorage.getItem('spark-lang') ?? navigator.language?.split('-')[0] ?? 'en';
    return value[language] ?? value['en'] ?? Object.values(value)[0] ?? '';
  }
}
```

**Template usage — Before:**
```html
{{ resolveTranslation(attr.label) }}
```

**After:**
```html
{{ attr.label | resolveTranslation }}
```

Place the pipe in a shared location accessible to all demo apps. Since these are independent workspace projects, either:
- Add it to the `@mintplayer/ng-spark-auth` library (if appropriate), OR
- Create a small shared pipe file that each app imports

Since `resolveTranslation` is a general i18n utility tied to the Spark data model (TranslatedString), it belongs alongside the Spark models. Add it to each demo app's `core/pipes/` folder or create a shared package.

**Recommendation**: Put it in a `core/pipes/resolve-translation.pipe.ts` in each demo app (they already share the same structure). Since these demo apps have identical code patterns, create the pipe once and replicate.

## Implementation Plan

### Phase 1: Shared Infrastructure
1. Create `ResolveTranslationPipe` in each project's core/pipes/
2. Verify zoneless configuration across all apps

### Phase 2: Auth Library (ng-spark-auth)
3. Convert SparkAuthService — remove Observable returns, use async/await + signals
4. Convert auth components (login, register, etc.) — signals for all state
5. Convert interceptor/guard if needed (guard already uses computed)

### Phase 3: Demo App Services
6. Convert SparkService in each demo app — async methods
7. Convert RetryActionService — signal-based

### Phase 4: Demo App Components (per app × per component)
8. ShellComponent — signals, remove fromEvent
9. PoFormComponent — model() for formData, input()/output(), signals
10. PoCreateComponent — signals, async data loading
11. PoDetailComponent — signals, async data loading
12. PoEditComponent — signals, async data loading
13. QueryListComponent — signals, async data loading
14. HomeComponent — signals

### Phase 5: Cleanup
15. Remove unused RxJS imports
16. Verify RxJS is only used as bridge in service layer (firstValueFrom)
17. Run build for all projects
18. Test all apps

## Files to Modify

### ng-spark-auth library
- `packages/ng-spark-auth/src/lib/services/spark-auth.service.ts`
- `packages/ng-spark-auth/src/lib/components/login/login.component.ts`
- `packages/ng-spark-auth/src/lib/components/login/login.component.html`
- `packages/ng-spark-auth/src/lib/components/register/register.component.ts`
- `packages/ng-spark-auth/src/lib/components/register/register.component.html`
- `packages/ng-spark-auth/src/lib/components/forgot-password/forgot-password.component.ts`
- `packages/ng-spark-auth/src/lib/components/forgot-password/forgot-password.component.html`
- `packages/ng-spark-auth/src/lib/components/reset-password/reset-password.component.ts`
- `packages/ng-spark-auth/src/lib/components/reset-password/reset-password.component.html`
- `packages/ng-spark-auth/src/lib/guards/spark-auth.guard.ts`
- `packages/ng-spark-auth/src/lib/interceptors/spark-auth.interceptor.ts`

### Per Demo App (DemoApp, HR, Fleet — identical structure)
- `src/app/app.config.ts` — verify zoneless
- `src/app/app.ts` — signals
- `src/app/core/services/spark.service.ts` — async/Promise
- `src/app/core/services/retry-action.service.ts` — signals
- `src/app/core/models/translated-string.ts` — keep function, add pipe
- `src/app/core/pipes/resolve-translation.pipe.ts` — NEW
- `src/app/components/shell/shell.component.ts` — signals, remove fromEvent
- `src/app/components/shell/shell.component.html` — pipe usage
- `src/app/components/po-form/po-form.component.ts` — model(), signals
- `src/app/components/po-form/po-form.component.html` — pipe, signal reads
- `src/app/pages/home/home.component.ts` — signals
- `src/app/pages/po-create/po-create.component.ts` — signals, async
- `src/app/pages/po-create/po-create.component.html` — pipe
- `src/app/pages/po-detail/po-detail.component.ts` — signals, async
- `src/app/pages/po-detail/po-detail.component.html` — pipe
- `src/app/pages/po-edit/po-edit.component.ts` — signals, async
- `src/app/pages/po-edit/po-edit.component.html` — pipe
- `src/app/pages/query-list/query-list.component.ts` — signals, async
- `src/app/pages/query-list/query-list.component.html` — pipe

## Risk & Considerations
- **HttpClient still returns Observable**: We use `firstValueFrom()` as bridge in service layer only
- **ngModel compatibility**: `[(ngModel)]` works with signals via `[(ngModel)]="prop()"` with manual `.set()` on change, or we can use the new Angular forms signal integration if available
- **Breaking changes in auth library**: The public API of SparkAuthService changes (Observable → Promise/Signal). Consumers must update.
- **Template syntax**: Signal reads require `()` — every `{{ prop }}` becomes `{{ prop() }}`
- **Build verification**: Must build all 4 projects after changes
