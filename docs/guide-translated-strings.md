# Translated Strings & Internationalization

Spark has built-in support for multilingual content using the `TranslatedString` class. Labels, descriptions, validation messages, menu items, UI strings, and the names of places can all be translated without any external i18n library.

## Overview

`TranslatedString` is Spark's **one name system** (commerce X17, reference data RD7). A value is an
ordered list of **entries**. Each entry is a text and an [RFC 5646](https://www.rfc-editor.org/rfc/rfc5646)
language tag. A name can add a kind, a validity window, a source and a romanization origin:

```json
{
  "Entries": [
    { "Tag": "en", "Value": "The Hague" },
    { "Tag": "nl", "Value": "'s-Gravenhage", "Kind": "official" },
    { "Tag": "nl", "Value": "Den Haag", "Kind": "common" },
    { "Tag": "zh-Hant", "Value": "海牙" }
  ]
}
```

The shape is the same wherever it appears:
- the HTTP API (System.Text.Json);
- the stored RavenDB document (Newtonsoft);
- the Angular client.

An app text is the simple case: one plain entry per language.

Any language works, `zh-Hant`, `sr-Latn` and `nl-BE` included. A tag is checked for well-formedness
(RFC 5646 §2.1) and stored in canonical case (`zh-Hant`, `nl-BE`).

## The TranslatedString Class (C#)

The C# class lives in the `MintPlayer.Spark.Model` package, in namespace `MintPlayer.Spark.Abstractions`,
so an entity library can use it without referencing ASP.NET Core (#388). The members you use:

```csharp
// App texts: one line, any language, each text tagged explicitly.
var save = TranslatedString.Of(("en", "Save"), ("fr", "Enregistrer"), ("nl", "Opslaan"));
var only = TranslatedString.Of("en", "Save");

// Read in a language, through the fallback chain below.
string text = save.GetValue("nl-BE");                 // "Opslaan"
string first = save.Resolve(["de-CH", "fr"]);         // several languages in priority order

// Edit a language's plain text in place; null removes it.
save["de"] = "Speichern";
save.Set("es", "Guardar");

// Names: entries with a kind and a validity window.
var hague = new TranslatedString { Entries =
[
    new("nl", "'s-Gravenhage") { Kind = TranslatedStringKind.Official },
    new("nl", "Den Haag") { Kind = TranslatedStringKind.Common },
    new("fr", "La Haye") { Kind = TranslatedStringKind.Exonym },
] };
```

Each text names its language, so the languages cannot silently swap. The old positional
`Create(en, fr, nl)` could swap them. Code-defined lookup labels carry no text at all: a
`[SparkLookup]` enum's labels are the keys `lookup.{Enum}.{Member}` in `translations.json` (see
[Reference attributes](guide-reference-attributes.md#transient-lookup-references-code-defined)).

| Member | What |
|---|---|
| `Entries` | `List<TranslatedStringEntry>`, in author order: within one tag the first current entry wins |
| `TranslatedStringEntry` | `Tag`, `Value`, and for names `Kind` (`Official`, `Common`, `Exonym`, `Historic`), `ValidFrom` (inclusive) / `ValidTo` (exclusive) as `DateOnly`, `Source`, `Romanization` (`Authoritative`, `Generated`) |
| `Of(tag, value)` / `Of((tag, value), …)` | A new app text |
| `this[tag]`, `Set(tag, value)` | A language's plain text (no fallback); `Set` replaces it in place |
| `GetValue(language)` / `Resolve(ranges, defaultLanguage = "en", asOf = today)` | The fallback chain |
| `Map((tag, text) => …)` | The same entries with every text transformed, e.g. formatted with arguments |
| `Equals` / `GetHashCode` | Value equality: the same key and the same entries in the same order |
| `Key` | A `translations.json` key in an App_Data file (see below); never stored |
| `LanguageTag.IsWellFormed`, `.Canonicalize`, `.LookupChain` | The tag rules the class uses |

### The fallback chain

Server and client resolve with one rule. The server is `TranslatedString.Resolve` and the client is
`resolveTranslation`. The shared fixture `libs/node_packages/ng-spark/models/src/translated-string.fixture.json`
drives the tests on both sides, so the two cannot drift apart.

1. For each requested language in order, then the default language (`en`), walk its RFC 4647 §3.4
   lookup chain: `zh-Hant-TW` → `zh-Hant` → `zh`. For each tag in the chain, take the first
   **current** entry with that tag. Tags compare case-insensitively.
2. Otherwise, take the first current entry.
3. Otherwise, take the first entry. If there is none, the result is `""`.

An entry is **current** when it is not `historic` and today (or `asOf`) falls in `[ValidFrom, ValidTo)`.
Lookup never widens. A request for `zh` does not match a `zh-Hant` entry; it falls through to the
default language.

The display rule for places is built on these entries and lives in the reference-data app; see
`docs/reference_data_PRD.md` §4.9. It covers the local official name, the name in the UI language,
the exonym and the Latin romanization.

## JSON serialization: one shape

`TranslatedStringJsonConverter` (System.Text.Json) and `TranslatedStringNewtonsoftJsonConverter`
(RavenDB, registered by `ApplySparkConventions`) share one field codec, `TranslatedStringJson`. They
write the same JSON: `{"Entries":[{"Tag":"en","Value":"…"}]}`.
- Optional members are omitted when null.
- `Kind` and `Romanization` are lowercase strings.
- Dates are `yyyy-MM-dd`.

**The member names are the C# names on purpose.** RavenDB's LINQ translator addresses stored JSON by
CLR member name. A generated index maps `x.Description.Entries.Where(e => e.Tag == "nl")`, and that
only matches when the stored names are exactly `Entries`, `Tag` and `Value`. A camel-cased store
would index every language field as null. It would do so silently: no deploy failure, no index error,
correct row counts, empty values. `TranslatedStringPersistedShapeTests` pins the stored shape, and
pins that it equals the API's.

The reader accepts two input-only shorthands. Neither is ever written:
- **A JSON string is a key.** App_Data files refer to `translations.json` by key (`"label": "common.name"`).
- **A flat object is plain entries.** `{"en": "Street", "nl": "Straat"}` is the leaf form of
  `translations.json`, and the form an API client may post.

It refuses:
- a member that is not a well-formed tag;
- a mix of both shapes;
- an unknown entry member.

## Where TranslatedString Is Used

Since #467 (D1), the `App_Data` configuration files carry **keys**, never text: model labels,
attribute help texts, validation messages, query labels, program units, action texts, security group
labels and language names all come from `translations.json` (see
[Application Translations](#application-translations-translationsjson) for the keys). The wire
carries the resolved `TranslatedString`.

`TranslatedString` is also the type for **data**. An entity property of type `TranslatedString` (a
product name in three languages, say) is stored, edited and indexed as one, as the sections below
describe. On a detail page, ng-spark shows it in the current language and lists every entry below
it (`<spark-translated-string>`). The edit form draws one input per language
(`<spark-translated-string-editor>`, from `@mintplayer/ng-spark/translated-string`):
- one row for each language in `culture.json`;
- one row for each other language the value already has;
- an "Add a language" field that accepts any RFC 5646 tag.

Names with a kind or a validity window are listed in the editor, and they survive every save. The
editor itself never changes them.

A save **merges per tag**. Each language the client sends replaces that language's entries. Languages
it does not send stay as they are. A language sent with an empty text is removed.

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

RavenDB cannot usefully sort or search a list of tagged entries, so a `TranslatedString` is not indexed
whole. A generated index fans it out into one field per configured language:

```csharp
[GenerateIndex]
public class Car
{
    [Search] public TranslatedString? Description { get; set; }
}
```

This emits `Description_en`, `Description_fr` and `Description_nl` on the index entity. Each one maps
the first entry of exactly that tag:
`car.Description!.Entries.Where(e => e.Tag == "<tag>").Select(e => e.Value).FirstOrDefault()`. With
`[Search]`, each language also gets a `Description_{lang}Search` companion.

The languages come from `App_Data/culture.json`. That file must be an `AdditionalFiles` item, because
a source generator has no DI and cannot ask `CultureLoader`.

**A hyphenated tag is sanitized in the field name.** `zh-Hant` gives `Description_zh_Hant`, a valid C#
identifier on the view class.
- The map compares the tag in canonical case, so `zh-hant` in `culture.json` still matches stored
  `zh-Hant`.
- The query search surface reverses the name. It matches the known attribute name as the prefix and
  turns the rest back into a tag. A tag never contains `_`, but a property name may.

The whole-object field is **replaced**, not kept alongside: a `string?` named `Description` next to the entity's
`TranslatedString Description` is a type mismatch the model merge rejects. Grids therefore bind a specific
language column rather than the whole value.

Two related notes:
- `GetValue("nl")` cannot be used in a map. The server has no `TranslatedString` type and would return
  null forever.
- A missing tag and a null property both index to `null` harmlessly.

### Migrating stored values (X17)

The pre-X17 store held `{"Translations":{"en":"…"},"Key":null}`. These migrations rewrite it:
- `M_202610191200_OidcResourceTextEntries` in the identity provider library handles `OidcResources`
  display names and descriptions, scopes included. The MCP seeding runs the same rewrite before it
  loads them, because its startup hook can run before the migrations.
- Fleet's `M_202610191200_TranslatedStringEntries` handles `Cars.Description` and the dynamic lookup
  values. A dynamic lookup value now stores its label as a `TranslatedString` (`Values`).

The old shape is not read (no backward compatibility).

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
        ["en"] = TranslatedString.Of("en", "English")
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
| Lookup value (`[SparkLookup]` enum member) | `lookup.{Enum}.{Member}` (resolved when `/spark/lookupref/{name}` serves it) |

**The key guard.** `tools/verify-translation-keys.test.mjs` (`npm run test:tools`, run by CI) fails when
the ng-spark library or an app's `ClientApp` asks for a literal key (`lang.t('common.save', …)`,
`'auth.logout' | t`, a component's prefixed `t('title')` helper) that no layer defines, when a
`[SparkLookup]` member has no label in its project's layer, and when a `lookup.*` key labels no member.
Keys built at runtime (`'x.' + name`) are not checked. A key a library's client code needs belongs in
that library's layer; an app's, in its own or a library's.

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

The Angular type is the wire shape (`@mintplayer/ng-spark/models`):

```typescript
export interface TranslatedString {
  Entries: TranslatedStringEntry[];
}

export interface TranslatedStringEntry {
  Tag: string;
  Value: string;
  Kind?: 'official' | 'common' | 'exonym' | 'historic';
  ValidFrom?: string; // yyyy-MM-dd, inclusive
  ValidTo?: string;   // yyyy-MM-dd, exclusive
  Source?: string;
  Romanization?: 'authoritative' | 'generated';
}
```

Build an app text in code with `translated`, the client's one-liner:

```typescript
const copy = translated({ en: 'Copy', fr: 'Copier', nl: 'Kopiëren' });
```

### resolveTranslation Function

`resolveTranslation` applies [the fallback chain](#the-fallback-chain), the same one as the server's
`TranslatedString.Resolve`:

```typescript
resolveTranslation(ts);                                // the current language (the global signal)
resolveTranslation(ts, 'nl-BE');                       // nl-BE → nl → en → first current entry
resolveTranslation(ts, ['de-CH', 'fr'], { defaultLanguage: null, asOf: '2025-01-01' });
```

`lookupChain(range)` and `isCurrentEntry(entry, day)` are exported as well. Use the
`resolveTranslation` pipe in templates:

```html
{{ attr.label | resolveTranslation }}
{{ entityType.description | resolveTranslation: entityType.name }}
```

### SparkLanguageService

The `SparkLanguageService` loads both the culture (`GET /spark/culture`) and the translations
(`GET /spark/translations`) from the server. Its members:
- `language`, a signal holding the current language;
- `languages`, a signal holding the app's languages and their names;
- `setLanguage(lang)`, which stores the choice in `localStorage` and in the `spark-lang` cookie;
- `resolve(ts)`, which is `resolveTranslation(ts, language())`;
- `t(key, fallback?)`, which looks up a key and returns the fallback, or the key itself, when nothing
  is found.

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

1. Add the code to `App_Data/culture.json`: `"languages": ["en", "de"]`. Any well-formed RFC 5646 tag works (`zh-Hant`, `nl-BE`); an ill-formed one is logged as a warning at startup.
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
- `libs/model/MintPlayer.Spark.Model/TranslatedString.cs` -- C# TranslatedString class with JSON converter (and `LanguageTag.cs`)
- `libs/node_packages/ng-spark/translated-string/` -- the detail view and editor for a TranslatedString attribute
