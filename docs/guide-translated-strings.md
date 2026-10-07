# Translated Strings & Internationalization

Spark has built-in support for multilingual content using the `TranslatedString` class. Labels, descriptions, validation messages, menu items, and UI strings can all be translated without any external i18n library.

## Overview

A `TranslatedString` is a flat JSON object mapping language codes to their translated values:

```json
{"en": "First Name", "fr": "Prenom", "nl": "Voornaam"}
```

This format is used across the entire Spark data model: entity descriptions, attribute labels, validation rule messages, program unit names, security group names, query descriptions, and application-level UI translations.

## The TranslatedString Class (C#)

The C# class lives in the `MintPlayer.Spark.Model` package, in namespace `MintPlayer.Spark.Abstractions`,
so an entity library can use it without referencing ASP.NET Core (#388):

```csharp
[JsonConverter(typeof(TranslatedStringJsonConverter))]
public class TranslatedString
{
    public Dictionary<string, string> Translations { get; set; } = new();

    public string GetValue(string culture)
    {
        if (Translations.TryGetValue(culture, out var value))
            return value;

        // Fallback: try base culture (e.g., "en" from "en-US")
        var baseCulture = culture.Split('-')[0];
        if (Translations.TryGetValue(baseCulture, out value))
            return value;

        // Fallback: return first available or empty
        return Translations.Values.FirstOrDefault() ?? string.Empty;
    }

    public static TranslatedString Create(string en, string? fr = null, string? nl = null)
    {
        var ts = new TranslatedString();
        ts.Translations["en"] = en;
        if (fr != null) ts.Translations["fr"] = fr;
        if (nl != null) ts.Translations["nl"] = nl;
        return ts;
    }
}
```

Key behaviors:
- `GetValue(culture)` returns the best match using a fallback chain: exact match, base culture (e.g. `"en"` from `"en-US"`), then first available value.
- `GetDefaultValue()` returns the first available translation (useful when no specific culture is needed).
- `Create()` is a convenience factory for building instances in code.

## Custom JSON Serialization

The `TranslatedStringJsonConverter` serializes `TranslatedString` as a **flat JSON object** rather than a wrapper with a `Translations` property. This means the wire format is compact and human-readable:

```json
{"en": "Street", "fr": "Rue", "nl": "Straat"}
```

Not:

```json
{"translations": {"en": "Street", "fr": "Rue", "nl": "Straat"}}
```

The converter handles both reading and writing. On the read side, it iterates property names as language codes and property values as translated text. On the write side, it writes a flat object with each `Translations` entry as a property.

## Where TranslatedString Is Used

Since #467 (D1), the `App_Data` configuration files carry **keys**, never text: model labels,
attribute help texts, validation messages, query labels, program units, action texts, security group
labels and language names all come from `translations.json` (see
[Application Translations](#application-translations-translationsjson) for the keys). The wire still
carries a resolved `TranslatedString`, so a client sees the same shape as before.

`TranslatedString` remains the type for **data**: an entity property of type `TranslatedString`
(a product name in three languages, say) is stored, edited and indexed as one, as the sections below
describe.

### Labels and help texts are yours to edit

- A label no layer translates shows the element's humanized name (`FirstName` → `First Name`).
  Camel-case splitting cannot know that `GitHubId` is one word, so write the label you want.
- An attribute's help text (`model.{Entity}.attributes.{Attribute}.description`) is seeded in `en`
  from the property's `///` summary on synchronize. The other languages are yours to write. See
  [Attribute descriptions](guide-attribute-descriptions.md).
- **Edit all languages together.** Nothing correlates them, so shortening `en` and leaving `nl`
  alone is silent: an English reviewer sees a tidy grid while a Dutch user still gets the old wording.
- Labels are presentational: editing one never changes the model hash, so it cannot refuse startup.

## Indexing and sorting a TranslatedString

RavenDB cannot usefully sort or search a dictionary, so a `TranslatedString` is not indexed whole. A generated
index fans it out into one field per configured language:

```csharp
[GenerateIndex]
public class Car
{
    [Search] public TranslatedString? Description { get; set; }
}
```

emits `Description_en`, `Description_fr`, `Description_nl` on the index entity — each mapped from
`car.Description!.Translations["<lang>"]` — plus a `Description_{lang}Sort` companion per language when
`[Search]` is present. The languages come from `App_Data/culture.json`, which must be an `AdditionalFiles` item
because a source generator has no DI and cannot ask `CultureLoader`.

The whole-object field is **replaced**, not kept alongside: a `string?` named `Description` next to the entity's
`TranslatedString Description` is a type mismatch the model merge rejects. Grids therefore bind a specific
language column rather than the dictionary.

### The mapping depends on the persisted shape, which is not the wire shape

This is the one trap worth internalising. `TranslatedStringJsonConverter` writes the flat form
`{"en":"…","nl":"…"}`, but it is a **System.Text.Json** converter and applies only at the HTTP /
`PersistentObject` boundary. RavenDB persists through **Newtonsoft**, where no converter is registered for the
type, so the stored document is nested:

```json
{ "Description": { "Translations": { "en": "…", "nl": "…" } } }
```

which is exactly why the index maps `Description.Translations["nl"]`.

**Do not add a Newtonsoft converter to make persistence "consistent with the API".** Every generated
per-language index field would silently become null: no deploy failure, no index error, index state healthy,
correct row counts, empty values. The model hash would not move and `--spark-verify-model` would still pass.
A test asserts the stored JSON is nested precisely so that change fails loudly instead.

Two related notes: `GetValue("nl")` cannot be used in a map — the server has no `TranslatedString` type and
would return null forever — and a missing language key or a null property both index to `null` harmlessly.

## Culture Configuration

Create `App_Data/culture.json` to define the supported languages and default language:

```json
{
  "languages": ["en", "fr", "nl"],
  "defaultLanguage": "en"
}
```

The file lists language **codes** (#467, D25). Each name comes from the key `culture.languages.{code}`,
which the core `translations.json` ships for en, fr, nl, de and es; a code no layer translates is shown
as-is. The object form with inline names is refused.

The loader resolves the names, so the endpoint still serves this C# model:

```csharp
public sealed class CultureConfiguration
{
    public Dictionary<string, TranslatedString> Languages { get; set; } = new()
    {
        ["en"] = TranslatedString.Create("English")
    };
    public string DefaultLanguage { get; set; } = "en";
}
```

If `culture.json` does not exist, a default configuration with English only is used.

### Culture Endpoint

Spark exposes `GET /spark/culture` which returns the culture configuration. The Angular app uses this to build a language picker and know which languages are available.

## Application Translations (translations.json)

**Every localized string lives in `translations.json`** (#467, D1). No other `App_Data` file embeds
text: model labels, actions, queries, program units, security group names and culture names are all
looked up by **key**. A loader that finds inline text (`{ "en": "…" }` in a model file, a
`programUnits.json` name that is not a key, …) refuses to start and names the key it belongs under.

The file nests; a key is the dotted path to a `TranslatedString`:

```json
{
  "model": {
    "Car": {
      "label": { "en": "Car", "nl": "Wagen" },
      "attributes": {
        "LicensePlate": {
          "label": { "en": "License plate", "nl": "Nummerplaat" },
          "rules": { "regex": { "en": "Use the format 1-ABC-123" } }
        }
      }
    }
  },
  "queries": { "Recent_Cars": { "label": { "en": "Recent cars" } } },
  "actions": {
    "CarCopy": {
      "label": { "en": "Copy" },
      "confirmation": { "en": "Copy {count} car(s)?" }
    }
  }
}
```

### Keys by convention

A model element finds its text without naming a key (D4):

| Element | Key |
|---|---|
| Entity label | `model.{Entity}.label` |
| Attribute label / help text | `model.{Entity}.attributes.{Attribute}.label` / `.description` |
| Validation message | `model.{Entity}.attributes.{Attribute}.rules.{type}` (an explicit key in the rule's `message`) |
| Tab / group label | `model.{Entity}.tabs.{Tab}.label` / `model.{Entity}.groups.{Group}.label` |
| Query label | `queries.{Query}.label` |
| Action label / confirmation / description | `actions.{Name}.label` / `.confirmation` / `.description` |
| Security group | `security.groups.{name}.label` (the group's untranslated name stays its identity for claims, D24) |
| Language name | `culture.languages.{code}` |

A model file may instead name a key to reuse one (`"label": "common.name"`). An element whose key no
layer translates shows its humanized name (`CreatedBy` → `Created By`); a help text shows nothing.
`programUnits.json` names are always explicit keys (`"programUnits.cars"`). Keys are resolved on the
server when the model loads, so the client and server-side text (validation messages, breadcrumbs)
receive the resolved `TranslatedString`.

### Composition: libraries, then the app, per key and language (D2, D3, D23)

Every library may ship a `translations.json` (the core one carries `common.*`, `validation.*`,
`culture.languages.*` and the built-in actions). They are layered in a stable order, the app's file
last, and they merge **per (key, language)**:

- An app that adds `es` to a library key keeps the library's `en`/`fr`/`nl`.
- An app that overrides `nl` for a key keeps the other languages.
- An app's `""` counts as **not defined**: it never blanks a library value.
- Only two *libraries* giving the same (key, language) different values is warned about, because
  their order is an arbitrary tiebreak. An app overriding a library is silent.
- Each layer is flattened first, so `"auth.passkey.title"` and `{ "auth": { "passkey": { "title": … } } }`
  are the same key.
- **`"ns": null` removes a namespace**: the key `ns` and every key below it, as far as the layers
  below state them. The removal applies before the layer's own keys, so the same file may remove a
  namespace and restate part of it:

  ```json
  {
    "auth.accountError": null,
    "auth.accountError.notFound": { "en": "No such account." }
  }
  ```

  A single language cannot be removed; override it instead.

The composition happens **at run time**, in the host, from the layers compiled into each referenced
library and your file on disk; `GET /spark/translations` serves the result. Nothing is copied into
your `App_Data`. `dotnet run -- --spark-describe translations [prefix] --layers` prints each key and
the layer it came from (see [Library layers](guide-library-layers.md)).

Your `translations.json` **reloads on save** (debounced, swapped atomically); a file that no longer
composes keeps the previous texts and logs why. Labels in the model follow the reload. The
libraries' layers change only with a rebuild.

### Seeding and the missing-keys report

`--spark-synchronize-model` never writes `translations.json`, with one exception (D5): an attribute
description no layer defines is seeded as `en` from the property's `///` summary or `[Description]`
into the **app's** file. It only adds; blanking the value asks for the seed again. Sync also prints
an **Info** list of label keys that lack a translation in a language `culture.json` declares.

### Translations Endpoint

Spark exposes `GET /spark/translations` which returns the full translations dictionary. The Angular `TranslationsService` loads this on startup.

## Angular Integration

### TranslatedString Type

On the Angular side, `TranslatedString` is a simple type alias:

```typescript
export type TranslatedString = Record<string, string>;
```

### resolveTranslation Function

The `resolveTranslation` helper picks the right translation for the current user:

```typescript
export function resolveTranslation(ts: TranslatedString | undefined, lang?: string): string {
  if (!ts) return '';
  const language = lang ?? localStorage.getItem('spark-lang') ?? navigator.language?.split('-')[0] ?? 'en';
  return ts[language] ?? ts['en'] ?? Object.values(ts)[0] ?? '';
}
```

Resolution order:
1. Explicit `lang` parameter
2. `spark-lang` value from `localStorage` (set by a language picker)
3. Browser language (`navigator.language`, base code only)
4. English (`en`) as final fallback
5. First available value if none of the above match

Use it in components to display attribute labels, entity descriptions, and other translated model data:

```typescript
// In a component class
import { resolveTranslation } from '../core/models';

resolveTranslation = resolveTranslation;

// In the template
{{ resolveTranslation(attr.label) || attr.name }}
{{ resolveTranslation(entityType.description) || entityType.name }}
```

### SparkLanguageService

The `SparkLanguageService` loads both `culture.json` and `translations.json` from the server. It provides a `t(key)` method for keyed lookups and a `resolve(ts)` method for inline `TranslatedString` values:

```typescript
@Injectable({ providedIn: 'root' })
export class SparkLanguageService {
  private readonly http = inject(HttpClient);
  private readonly currentLang = signal('en');
  private readonly translationsMap = signal<Record<string, TranslatedString>>({});

  readonly language = this.currentLang.asReadonly();
  readonly languages = signal<Record<string, TranslatedString>>({});

  constructor() {
    this.loadCulture();
    this.loadTranslations();
  }

  setLanguage(lang: string) {
    this.currentLang.set(lang);
    localStorage.setItem('spark-lang', lang);
  }

  resolve(ts: TranslatedString | undefined): string {
    if (!ts) return '';
    const lang = this.currentLang();
    return ts[lang] ?? ts['en'] ?? Object.values(ts)[0] ?? '';
  }

  t(key: string): string {
    const ts = this.translationsMap()[key];
    return this.resolve(ts) || key;
  }
}
```

If a key is not found, the key itself is returned as a fallback. The service also persists the user's language choice in `localStorage`.

### TranslateKeyPipe

The `TranslateKeyPipe` (`t` pipe) makes keyed translations easy to use in templates:

```typescript
@Pipe({ name: 't', pure: false, standalone: true })
export class TranslateKeyPipe implements PipeTransform {
  private readonly lang = inject(SparkLanguageService);

  transform(key: string): string {
    return this.lang.t(key);
  }
}
```

Usage in templates:

```html
<button (click)="onSave()">{{ 'save' | t }}</button>
<button (click)="onCancel()">{{ 'cancel' | t }}</button>
<input [placeholder]="'search' | t" [(ngModel)]="searchTerm">
```

Import `TranslateKeyPipe` in each component that uses it:

```typescript
@Component({
  imports: [TranslateKeyPipe],
  // ...
})
```

## Two Translation Mechanisms

Spark uses two complementary approaches:

| Mechanism | Source | Used for | Angular API |
|---|---|---|---|
| `resolveTranslation()` | `TranslatedString` values on the wire: model labels, help texts, validation messages, menu items (resolved from `translations.json` keys on the server) and translated data | Labels, descriptions, validation messages, menu items, `TranslatedString` properties | `resolveTranslation(ts)` function |
| `SparkLanguageService.t()` | `App_Data/translations.json` via `/spark/translations` | Button text, placeholders, confirmation dialogs, status messages | `'key' \| t` pipe or `langService.t('key')` |

Both read the same `translations.json` layers; the first receives the text already resolved by the
server, the second looks a key up in the browser.

## Adding a New Language

1. Add the code to `App_Data/culture.json`: `"languages": ["en", "de"]`.
2. Add the `de` value to the keys in the app's `App_Data/translations.json`. Composition is per
   language, so adding `de` to a library key keeps the library's other languages.
3. Run synchronize: its Info list names the label keys still missing `de`.

No code changes or recompilation are required: the app's translations are loaded from JSON at runtime.

## Complete Example

See the demo apps for working examples:
- `apps/Fleet/Fleet/App_Data/culture.json` -- culture configuration with en/fr/nl
- `apps/Fleet/Fleet/App_Data/translations.json` -- model, query, action and program-unit texts
- `libs/spark/MintPlayer.Spark/App_Data/translations.json` -- the core layer (`common.*`, `validation.*`, built-in actions)
- `libs/node_packages/ng-spark/models/src/translated-string.ts` -- Angular type and resolver
- `libs/node_packages/ng-spark/services/src/spark-language.service.ts` -- SparkLanguageService
- `libs/node_packages/ng-spark/pipes/src/translate-key.pipe.ts` -- TranslateKeyPipe
- `libs/model/MintPlayer.Spark.Model/TranslatedString.cs` -- C# TranslatedString class with JSON converter
