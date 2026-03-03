# Translated Strings & Internationalization

Spark has built-in support for multilingual content using the `TranslatedString` class. Labels, descriptions, validation messages, menu items, and UI strings can all be translated without any external i18n library.

## Overview

A `TranslatedString` is a flat JSON object mapping language codes to their translated values:

```json
{"en": "First Name", "fr": "Prenom", "nl": "Voornaam"}
```

This format is used across the entire Spark data model: entity descriptions, attribute labels, validation rule messages, program unit names, security group names, query descriptions, and application-level UI translations.

## The TranslatedString Class (C#)

The C# class lives in `MintPlayer.Spark.Abstractions`:

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

### Entity Descriptions

Each entity type can have a `description` field used as a human-readable label for create/edit page headings:

```json
{
  "name": "Person",
  "description": {"en": "Person", "fr": "Personne", "nl": "Persoon"},
  "clrType": "DemoApp.Library.Entities.Person"
}
```

### Attribute Labels

Every attribute's `label` field is a `TranslatedString`:

```json
{
  "name": "FirstName",
  "label": {"en": "First Name", "fr": "Prenom", "nl": "Voornaam"},
  "dataType": "string"
}
```

### Validation Messages

Validation rule messages support translations:

```json
{
  "type": "minLength",
  "value": 2,
  "message": {
    "en": "First Name must be at least 2 characters.",
    "fr": "Le prenom doit contenir au moins 2 caracteres.",
    "nl": "Voornaam moet minstens 2 tekens bevatten."
  }
}
```

### Program Units (Navigation Menu)

Program unit groups and items use `TranslatedString` for their names:

```json
{
  "programUnitGroups": [
    {
      "name": {"en": "Fleet Management", "fr": "Gestion de flotte", "nl": "Wagenparkbeheer"},
      "programUnits": [
        {
          "name": {"en": "Cars", "fr": "Voitures", "nl": "Auto's"},
          "type": "query",
          "queryId": "a20e8400-..."
        }
      ]
    }
  ]
}
```

### Query Descriptions

Queries use `description` for translated page titles:

```json
{
  "name": "GetCars",
  "description": {"en": "Cars", "fr": "Voitures", "nl": "Auto's"},
  "contextProperty": "Cars"
}
```

### Security Group Names

Group names in `security.json` support translations:

```json
{
  "groups": {
    "a1b2c3d4-...": {"en": "Administrators", "fr": "Administrateurs", "nl": "Beheerders"}
  }
}
```

## Culture Configuration

Create `App_Data/culture.json` to define the supported languages and default language:

```json
{
  "languages": {
    "en": { "en": "English", "fr": "Anglais", "nl": "Engels" },
    "fr": { "en": "French", "fr": "Francais", "nl": "Frans" },
    "nl": { "en": "Dutch", "fr": "Neerlandais", "nl": "Nederlands" }
  },
  "defaultLanguage": "en"
}
```

Each language entry is itself a `TranslatedString` -- its keys are language codes and its values are what to display when the UI is in that language. For example, when the UI is in French, `"nl"` displays as `"Neerlandais"`.

The C# model:

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

For UI strings that are not part of the data model (button labels, placeholder text, confirmation dialogs, etc.), create `App_Data/translations.json`:

```json
{
  "save": {
    "en": "Save",
    "fr": "Enregistrer",
    "nl": "Opslaan"
  },
  "cancel": {
    "en": "Cancel",
    "fr": "Annuler",
    "nl": "Annuleren"
  },
  "confirmDelete": {
    "en": "Are you sure you want to delete this item?",
    "fr": "Etes-vous sur de vouloir supprimer cet element ?",
    "nl": "Weet u zeker dat u dit item wilt verwijderen?"
  }
}
```

Each key is a translation key used in the Angular app. The value is a `TranslatedString`.

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
| `resolveTranslation()` | Inline `TranslatedString` values from model JSON | Labels, descriptions, validation messages, menu items | `resolveTranslation(ts)` function |
| `SparkLanguageService.t()` | `App_Data/translations.json` via `/spark/translations` | Button text, placeholders, confirmation dialogs, status messages | `'key' \| t` pipe or `langService.t('key')` |

Use inline `TranslatedString` for data-model content that varies per entity. Use `translations.json` for shared UI strings that appear across the application.

## Adding a New Language

1. Add the language to `App_Data/culture.json`:

```json
{
  "languages": {
    "en": { "en": "English", "de": "Englisch" },
    "de": { "en": "German", "de": "Deutsch" }
  },
  "defaultLanguage": "en"
}
```

2. Add translations to each model JSON file (entity descriptions, attribute labels, validation messages).

3. Add translations to `App_Data/translations.json` for UI strings.

4. Add translations to `App_Data/programUnits.json` for navigation menu items.

No code changes or recompilation are required -- all translations are loaded from JSON files at runtime.

## Complete Example

See the demo apps for working examples:
- `Demo/Fleet/Fleet/App_Data/culture.json` -- culture configuration with en/fr/nl
- `Demo/HR/HR/App_Data/culture.json` -- culture configuration with en/fr/nl
- `Demo/DemoApp/DemoApp/App_Data/translations.json` -- application translations
- `Demo/DemoApp/DemoApp/App_Data/Model/Person.json` -- model with translated labels
- `node_packages/ng-spark/src/lib/models/translated-string.ts` -- Angular type and resolver
- `node_packages/ng-spark/src/lib/services/spark-language.service.ts` -- SparkLanguageService
- `node_packages/ng-spark/src/lib/pipes/translate-key.pipe.ts` -- TranslateKeyPipe
- `MintPlayer.Spark.Abstractions/TranslatedString.cs` -- C# TranslatedString class with JSON converter
