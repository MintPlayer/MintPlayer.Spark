# PRD: Parameterized Translation Messages & Translated Validation Errors

## Problem Statement

The `App_Data/translations.json` file currently only supports static strings (e.g. `"save": {"en": "Save", ...}`). There is no way to include dynamic values via placeholders like `{0}`, `{1}`, etc.

Additionally, the `ValidationService` returns hardcoded English error messages (e.g. `"First Name must be at least 2 characters."`) regardless of the user's language. Validation messages need to be translated using the same translations system, with placeholder support for dynamic values like field names and constraint values.

## Goals

1. **Placeholder support in translations.json** — Allow `string.Format`-style placeholders (`{0}`, `{1}`, etc.) in translation values.
2. **New `IManager` methods** — Expose `GetTranslatedMessage` and `GetMessage` on `IManager` so Actions classes can resolve translated messages with parameters.
3. **Translated validation errors** — The `ValidationService` produces language-aware error messages using translation keys instead of hardcoded English strings.
4. **Backward compatible** — Existing translations without placeholders continue to work unchanged. Existing custom `ValidationRule.Message` values continue to work.

## Design

### 1. Placeholder Support in translations.json

Translation values can contain `string.Format` placeholders:

```json
{
  "validationRequired": {
    "en": "{0} is required.",
    "fr": "{0} est obligatoire.",
    "nl": "{0} is verplicht."
  },
  "validationMinLength": {
    "en": "{0} must be at least {1} characters.",
    "fr": "{0} doit contenir au moins {1} caractères.",
    "nl": "{0} moet minstens {1} tekens bevatten."
  },
  "validationMaxLength": {
    "en": "{0} must be at most {1} characters.",
    "fr": "{0} doit contenir au plus {1} caractères.",
    "nl": "{0} mag maximaal {1} tekens bevatten."
  },
  "validationRangeMin": {
    "en": "{0} must be at least {1}.",
    "fr": "{0} doit être au moins {1}.",
    "nl": "{0} moet minstens {1} zijn."
  },
  "validationRangeMax": {
    "en": "{0} must be at most {1}.",
    "fr": "{0} doit être au plus {1}.",
    "nl": "{0} mag maximaal {1} zijn."
  },
  "validationInvalidFormat": {
    "en": "{0} has an invalid format.",
    "fr": "{0} a un format invalide.",
    "nl": "{0} heeft een ongeldig formaat."
  },
  "validationInvalidEmail": {
    "en": "{0} must be a valid email address.",
    "fr": "{0} doit être une adresse e-mail valide.",
    "nl": "{0} moet een geldig e-mailadres zijn."
  },
  "validationInvalidUrl": {
    "en": "{0} must be a valid URL.",
    "fr": "{0} doit être une URL valide.",
    "nl": "{0} moet een geldige URL zijn."
  }
}
```

No changes to `TranslatedString` or `TranslatedStringJsonConverter` are needed — they already store plain strings. The `string.Format` call happens at resolve time.

### 2. New Methods on IManager

Add two methods to `IManager` (in `MintPlayer.Spark.Abstractions`):

```csharp
public interface IManager
{
    // ... existing methods ...

    /// <summary>
    /// Gets a translated message for the current request culture, with placeholder substitution.
    /// </summary>
    string GetTranslatedMessage(string key, params object[] parameters);

    /// <summary>
    /// Gets a translated message for a specific language, with placeholder substitution.
    /// </summary>
    string GetMessage(string key, string language, params object[] parameters);
}
```

**Behavior:**
- Looks up `key` in the `translations.json` dictionary (via `ITranslationsLoader`).
- `GetTranslatedMessage` resolves the current language from the HTTP request's `Accept-Language` header (falling back to the default culture from `culture.json`).
- `GetMessage` uses the explicitly provided `language` parameter.
- Calls `string.Format(template, parameters)` on the resolved template string.
- If the key is not found, returns the key itself (e.g. `"validationRequired"`) as a fallback, so missing translations are visible during development.

### 3. Request Culture Resolution

Add a small `IRequestCultureResolver` service (scoped) that determines the language for the current HTTP request:

```csharp
public interface IRequestCultureResolver
{
    string GetCurrentCulture();
}
```

**Resolution order:**
1. `Accept-Language` header (first quality-weighted value that exists in the culture config's `supportedCultures`).
2. Default culture from `culture.json` (`defaultCulture` field).
3. `"en"` as ultimate fallback.

This service is internal to `MintPlayer.Spark` and injected into `Manager`.

### 4. Manager Implementation

The `Manager` class gets `ITranslationsLoader` and `IRequestCultureResolver` injected:

```csharp
[Register(typeof(IManager), ServiceLifetime.Scoped)]
internal sealed partial class Manager : IManager
{
    [Inject] private readonly IRetryAccessor retry;
    [Inject] private readonly ITranslationsLoader translationsLoader;
    [Inject] private readonly IRequestCultureResolver requestCultureResolver;

    public string GetTranslatedMessage(string key, params object[] parameters)
    {
        var culture = requestCultureResolver.GetCurrentCulture();
        return GetMessage(key, culture, parameters);
    }

    public string GetMessage(string key, string language, params object[] parameters)
    {
        var translations = translationsLoader.GetTranslations();
        if (!translations.TryGetValue(key, out var translatedString))
            return key; // Fallback: return the key itself

        var template = translatedString.GetValue(language);
        if (string.IsNullOrEmpty(template))
            return key;

        return parameters.Length > 0
            ? string.Format(template, parameters)
            : template;
    }

    // ... existing methods unchanged ...
}
```

### 5. Translated Validation Errors

Change `ValidationError.ErrorMessage` from `string` to `TranslatedString`:

```csharp
public sealed class ValidationError
{
    public required string AttributeName { get; set; }
    public required TranslatedString ErrorMessage { get; set; }
    public required string RuleType { get; set; }
}
```

Update `ValidationService` to:
- Accept `ITranslationsLoader` as a dependency.
- Build `TranslatedString` error messages by looking up keys like `validationRequired`, `validationMinLength`, etc. from translations.json.
- For each supported language in the translation entry, call `string.Format` with the attribute label (in that language) and the constraint value.
- If the `ValidationRule` has a custom `Message` set, use that `TranslatedString` directly (preserving current behavior for per-rule overrides).
- Fallback: if a translation key is missing from translations.json, produce the current hardcoded English string wrapped in a `TranslatedString`.

**Example flow for `minLength` validation on `FirstName` (minLength=2):**

1. Look up `"validationMinLength"` in translations → `{"en": "{0} must be at least {1} characters.", "fr": "...", "nl": "..."}`
2. Look up the attribute label → `{"en": "First Name", "fr": "Prénom", "nl": "Voornaam"}`
3. For each language, format: `string.Format(template[lang], label[lang], 2)`
4. Result: `{"en": "First Name must be at least 2 characters.", "fr": "Le prénom doit contenir au moins 2 caractères.", "nl": "Voornaam moet minstens 2 tekens bevatten."}`

### 6. Translation Key Conventions for Validation

| Rule Type | Translation Key | Placeholders |
|-----------|----------------|--------------|
| required | `validationRequired` | `{0}` = field label |
| minLength | `validationMinLength` | `{0}` = field label, `{1}` = min value |
| maxLength | `validationMaxLength` | `{0}` = field label, `{1}` = max value |
| range (min) | `validationRangeMin` | `{0}` = field label, `{1}` = min value |
| range (max) | `validationRangeMax` | `{0}` = field label, `{1}` = max value |
| regex | `validationInvalidFormat` | `{0}` = field label |
| email | `validationInvalidEmail` | `{0}` = field label |
| url | `validationInvalidUrl` | `{0}` = field label |

## Files to Change

| File | Change |
|------|--------|
| `MintPlayer.Spark.Abstractions/IManager.cs` | Add `GetTranslatedMessage` and `GetMessage` methods |
| `MintPlayer.Spark.Abstractions/ValidationError.cs` | Change `ErrorMessage` from `string` to `TranslatedString` |
| `MintPlayer.Spark/Services/Manager.cs` | Implement `GetTranslatedMessage` and `GetMessage` |
| `MintPlayer.Spark/Services/RequestCultureResolver.cs` | New file — `IRequestCultureResolver` + implementation |
| `MintPlayer.Spark/Services/ValidationService.cs` | Produce `TranslatedString` error messages using translation keys |
| `Demo/DemoApp/DemoApp/App_Data/translations.json` | Add `validation*` keys with placeholders |
| `Demo/HR/HR/App_Data/translations.json` | Add `validation*` keys with placeholders |
| `Demo/Fleet/Fleet/App_Data/translations.json` | Add `validation*` keys with placeholders |

## Non-Goals

- Pluralization support (e.g. "1 character" vs "2 characters") — can be added later if needed.
- Client-side validation message translation — the Angular client already receives `TranslatedString` objects and can pick the right language. This PRD only covers the server-side changes.
- Changing existing `TranslatedString` or `TranslatedStringJsonConverter` — no changes needed there.

## Breaking Changes

This is a preview release — no backward compatibility guarantees.

- `ValidationError.ErrorMessage` changes from `string` to `TranslatedString`.
- Validation error JSON output changes from a plain string to a `{"en": "...", "fr": "...", "nl": "..."}` object.
