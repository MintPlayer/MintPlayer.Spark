# PRD: Fix Translation Reactivity on Language Change (Issue #43)

## Problem

When changing language via the language dropdown, the following labels do NOT update until a full page reload (F5):

1. **PersistentObject attribute labels** (e.g., "First Name" → "Voornaam") in forms and detail views
2. **AttributeTab labels** and **AttributeGroup labels** in tabbed/grouped layouts
3. **Login/Logout button text** in the auth bar component
4. **Query descriptions**, **action display names**, **column headers** — all inline `TranslatedString` values

## Root Cause Analysis

### Issue 1: `ResolveTranslationPipe` is `pure: true` (ng-spark)

**File:** `node_packages/ng-spark/src/lib/pipes/resolve-translation.pipe.ts`

The `ResolveTranslationPipe` is declared as a **pure pipe** (`pure: true`). In Angular, pure pipes only re-evaluate when their input reference changes. The input is a `TranslatedString` object (e.g., `attr.label`), which is the same object reference regardless of language change.

The pipe internally calls `resolveTranslation()` from `models/translated-string.ts`, which reads `localStorage.getItem('spark-lang')` to determine the current language. However, since the pipe is pure and the input object reference hasn't changed, **Angular never re-invokes the pipe** after a language switch.

**Affected templates** (25+ usages):
- `spark-po-form.component.html` — attribute labels, tab labels, group labels, lookup options
- `spark-po-detail.component.html` — attribute labels, tab labels, group labels, action names
- `spark-po-edit.component.html` — entity type description
- `spark-po-create.component.html` — entity type description
- `spark-query-list.component.html` — query description, column headers
- `spark-sub-query.component.html` — query description, column headers

### Issue 2: `SparkAuthTranslationService` reads localStorage, not signals (ng-spark-auth)

**File:** `node_packages/ng-spark-auth/src/lib/services/spark-auth-translation.service.ts`

The `SparkAuthTranslationService.t()` method reads the current language from `localStorage.getItem('spark-lang')` directly, rather than from a reactive signal. While the `TranslateKeyPipe` in ng-spark-auth is `pure: false` (impure), the `SparkAuthBarComponent` uses `ChangeDetectionStrategy.OnPush`.

With OnPush, Angular only runs change detection when:
- An `@Input()` changes
- An event originates from the component
- A signal read in the template changes
- `markForCheck()` is called

Since none of the signals read in the auth bar template (`authService.isAuthenticated()`, `authService.user()`) change on language switch, **change detection never runs** for this component, and the impure pipe never re-executes.

### Why it works after F5

On page reload, `SparkLanguageService` reads the persisted language from localStorage during initialization, and all pipes receive fresh inputs, so everything renders correctly in the selected language.

## Solution

### Approach: Global language signal

Instead of injecting `SparkLanguageService` into pipes and services, we export a **module-level signal** from ng-spark that carries the current language. This is singleton state (a JS module variable) — no DI needed. Both ng-spark and ng-spark-auth can import and read this signal directly.

### Fix 1: Export a global `currentLanguage` signal

**File:** `node_packages/ng-spark/src/lib/models/translated-string.ts`

Add an exported writable signal alongside the existing `TranslatedString` type:

```typescript
import { signal } from '@angular/core';

export type TranslatedString = Record<string, string>;

/** Global reactive language state — updated by SparkLanguageService.setLanguage() */
export const currentLanguage = signal('en');

export function resolveTranslation(ts: TranslatedString | undefined, lang?: string): string {
  if (!ts) return '';
  const language = lang ?? currentLanguage();
  return ts[language] ?? ts['en'] ?? Object.values(ts)[0] ?? '';
}
```

The `resolveTranslation()` function now reads from the `currentLanguage` signal instead of `localStorage`. The function signature and behavior are unchanged — callers can still pass an explicit `lang` override.

### Fix 2: Update `SparkLanguageService` to sync the global signal

**File:** `node_packages/ng-spark/src/lib/services/spark-language.service.ts`

Update `setLanguage()` and `loadCulture()` to write to the global `currentLanguage` signal alongside localStorage:

```typescript
import { currentLanguage } from '../models';

// In setLanguage():
setLanguage(lang: string) {
  this.currentLang.set(lang);
  currentLanguage.set(lang);
  localStorage.setItem('spark-lang', lang);
}

// In loadCulture():
private async loadCulture(): Promise<void> {
  const config = await firstValueFrom(this.http.get<CultureConfiguration>(`${this.baseUrl}/culture`));
  this.languages.set(config.languages);
  const saved = localStorage.getItem('spark-lang');
  const lang = saved ?? config.defaultLanguage;
  this.currentLang.set(lang);
  currentLanguage.set(lang);
}
```

### Fix 3: Make `ResolveTranslationPipe` impure

**File:** `node_packages/ng-spark/src/lib/pipes/resolve-translation.pipe.ts`

Change only `pure: true` → `pure: false`. The pipe body stays the same — `resolveTranslation()` now reads the global signal internally:

```typescript
@Pipe({ name: 'resolveTranslation', standalone: true, pure: false })
export class ResolveTranslationPipe implements PipeTransform {
  transform(value: TranslatedString | undefined, fallback?: string): string {
    return resolveTranslation(value) || fallback || '';
  }
}
```

**Why `pure: false` is required:** Angular's pure pipe optimization skips calling `transform` when input references haven't changed. Since `attr.label` is the same object before and after a language switch, a pure pipe would never re-invoke `transform`, and the signal inside `resolveTranslation()` would never be read. With `pure: false`, Angular calls `transform` on every change detection cycle, the signal is read, and Angular tracks it for future updates.

**Performance:** The transform is a simple dictionary lookup — negligible cost. The `TranslateKeyPipe` already uses `pure: false` with the same pattern.

### Fix 4: Update `SparkAuthTranslationService` to use the global signal

**File:** `node_packages/ng-spark-auth/src/lib/services/spark-auth-translation.service.ts`

Import the `currentLanguage` signal from `@mintplayer/ng-spark` and use it instead of reading localStorage:

```typescript
import { currentLanguage } from '@mintplayer/ng-spark';

@Injectable({ providedIn: 'root' })
export class SparkAuthTranslationService {
  private readonly http = inject(HttpClient);
  private readonly translationsMap = signal<Record<string, TranslatedString>>({});

  constructor() {
    this.loadTranslations();
  }

  private async loadTranslations(): Promise<void> {
    try {
      const t = await firstValueFrom(this.http.get<Record<string, TranslatedString>>('/spark/translations'));
      this.translationsMap.set(t);
    } catch {
      // Translations failed to load; keys will be shown as-is
    }
  }

  t(key: string): string {
    const ts = this.translationsMap()[key];
    if (!ts) return key;
    const lang = currentLanguage();
    return ts[lang] ?? ts['en'] ?? Object.values(ts)[0] ?? key;
  }
}
```

**Why this works:** The `TranslateKeyPipe` (already `pure: false`) calls `t()` during template evaluation. `t()` reads `currentLanguage()` — a signal. Angular tracks the signal for the `SparkAuthBarComponent`. When language changes, Angular schedules CD for the component, the impure pipe re-runs, and the login/logout text updates.

### Fix 5: Add `@mintplayer/ng-spark` as peer dependency of `ng-spark-auth`

**File:** `node_packages/ng-spark-auth/package.json`

Add `"@mintplayer/ng-spark": ">=0.0.6"` to `peerDependencies` so the auth translation service can import the `currentLanguage` signal.

### Fix 6: Export `currentLanguage` from ng-spark public API

**File:** `node_packages/ng-spark/src/public-api.ts`

Ensure `currentLanguage` is exported so ng-spark-auth can import it:

```typescript
export { TranslatedString, currentLanguage, resolveTranslation } from './lib/models/translated-string';
```

## Files to Modify

| File | Change |
|------|--------|
| `node_packages/ng-spark/src/lib/models/translated-string.ts` | Add `currentLanguage` signal, update `resolveTranslation()` to read it |
| `node_packages/ng-spark/src/lib/services/spark-language.service.ts` | Sync `currentLanguage` signal in `setLanguage()` and `loadCulture()` |
| `node_packages/ng-spark/src/lib/pipes/resolve-translation.pipe.ts` | Change `pure: true` → `pure: false` |
| `node_packages/ng-spark/src/public-api.ts` | Export `currentLanguage` |
| `node_packages/ng-spark-auth/src/lib/services/spark-auth-translation.service.ts` | Import `currentLanguage` signal, use instead of localStorage |
| `node_packages/ng-spark-auth/package.json` | Add `@mintplayer/ng-spark` peer dependency |

## Files NOT Modified (no template changes needed)

All 25+ template usages of `| resolveTranslation` continue to work unchanged — the pipe's external API (name, parameters) stays the same. Only the internal implementation changes.

## Testing

### Manual Test Plan
1. Start a demo app (e.g., Fleet) with multiple languages configured
2. Navigate to an entity detail page (e.g., a Car)
3. Verify attribute labels, tab labels, and group labels display in the default language
4. Switch language via the dropdown
5. **Verify:** All attribute labels, tab labels, and group labels update immediately (no F5)
6. **Verify:** Login/Logout button text in the auth bar updates immediately
7. **Verify:** Query list headers, sub-query headers, and column headers update
8. Switch back to the original language
9. **Verify:** All labels revert immediately

### Playwright E2E Test
Write an automated test that:
1. Navigates to a detail page
2. Captures the text of an attribute label
3. Switches language
4. Asserts the label text changed without page reload
5. Does the same for the auth bar button text

## Risk Assessment

- **Low risk**: The only behavioral change is making `ResolveTranslationPipe` impure. This is the same pattern already used by `TranslateKeyPipe` in both ng-spark and ng-spark-auth.
- **Performance**: Dictionary lookups in pipe transforms are negligible. The ng-spark `TranslateKeyPipe` has been impure since inception with no reported issues.
- **Breaking changes**: None. The pipe's external API is unchanged. The `resolveTranslation` standalone function remains available for programmatic use. The `currentLanguage` signal is a new export.
