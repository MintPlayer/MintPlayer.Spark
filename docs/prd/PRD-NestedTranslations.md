# PRD: Nested, Modular Translations

## 1. Context

Spark's translation surface is growing. Today a single flat `App_Data/translations.json` in the **host app** carries all keys — framework-level (`save`, `cancel`), auth (`authLogin`, `authEmail`), validation (`validationRequired`, `validationMinLength`), and app-domain strings — relying on implicit camelCase prefixes for grouping. With `@mintplayer/ng-spark-auth`, the upcoming identity provider, and per-app strings, the flat dictionary is hard to navigate and prone to collisions.

Two problems to solve in one PR:

1. **Nesting** — let authors write a hierarchical `translations.json` and address keys by dot-notation (`auth.login`, `validation.required`).
2. **Modularity** — let each Spark library (`MintPlayer.Spark`, `MintPlayer.Spark.Authorization`, `MintPlayer.Spark.IdentityProvider`, …) ship its own `translations.json` and have them aggregated automatically into the consuming app.

Backward compatibility is **not** a requirement — Spark is in preview.

## 2. Current State (findings)

### Backend (`MintPlayer.Spark`)
- `MintPlayer.Spark.Abstractions/TranslatedString.cs` — `TranslatedString` wraps `Dictionary<string, string>`; custom `System.Text.Json` converter reads flat `{"en": "...", "fr": "..."}`.
- `MintPlayer.Spark/Services/TranslationsLoader.cs` — singleton, `Lazy<Dictionary<string, TranslatedString>>`, deserialized from `App_Data/translations.json` at first access. **No file watcher.**
- `MintPlayer.Spark/Endpoints/Translations/Get.cs` — returns `Dictionary<string, TranslatedString>`.
- `MintPlayer.Spark/Services/Manager.cs::GetTranslatedMessage` — direct `translations[key]` lookup.
- `MintPlayer.Spark/Services/ValidationService.cs::FormatTranslatedMessage` — flat-key lookup.

### Frontend
- `node_packages/ng-spark/src/lib/models/translated-string.ts` — `type TranslatedString = Record<string, string>`.
- `SparkLanguageService.t()` and `SparkAuthTranslationService.t()` — single-level `map[key]` lookup. No dot-walking.

### Source generators (today)
- Spark uses **incremental generators only**; none read `AdditionalText` / `AdditionalFiles`. Discovery is syntax + symbol based.
- Cross-assembly discovery is **runtime**: `AppDomain.CurrentDomain.GetAssemblies()` + reflection (`ActionsResolver.cs:62-79`).
- `SparkFullGenerator` already uses `compilation.GetTypeByMetadataName(...)` to feature-flag on referenced packages — same technique is reusable here.
- Libraries reference `MintPlayer.Spark.SourceGenerators` as an Analyzer (`OutputItemType="Analyzer"`, `ReferenceOutputAssembly="false"`) or as a NuGet package. `MintPlayer.SourceGenerators` (external) ships `build/*.props` + `*.targets` with the analyzer under `analyzers/dotnet/cs/` — the packaging pattern we'll copy.
- **No library currently ships a `translations.json`.** There is no NuGet content-file convention for it today.

## 3. Goals

1. Authors can write `translations.json` as a nested JSON tree of arbitrary depth in **any Spark library project** or **host app project**.
2. Keys are addressed by dot-notation (`auth.login`, `validation.minLength`).
3. Aggregation is **compile-time**: libraries' translations flow into the host's assembly via source generators. Zero runtime file I/O, zero JSON parsing at runtime.
4. AOT / trimming friendly — generated data is rooted static.
5. `{{ 'auth.login' | t }}` works in all Spark Angular apps.

## 4. Non-Goals

- Hot-reload / file watcher — compile-time baking means translations are frozen per build. Dev-loop speed handled by Roslyn's incremental generator cache; a live-reload mode is a follow-up.
- Compile-time key catalog (`TranslationKeys` constants / IntelliSense) — follow-up PRD.
- Per-culture file splitting (e.g. `translations.en.json` + `translations.nl.json`).
- Plural / ICU message format.

## 5. Design

### 5.1 Authoring experience

Every project that contributes translations declares:

```xml
<ItemGroup>
  <AdditionalFiles Include="App_Data\translations.json" />
</ItemGroup>
```

File content is nested JSON. A JSON object is a **`TranslatedString` leaf** iff every property value is a JSON string; otherwise it's a **namespace**.

```json
{
  "common":     { "save": { "en": "Save", "nl": "Opslaan" } },
  "auth":       { "login": { "en": "Log in", "nl": "Aanmelden" } },
  "validation": {
    "required":  { "en": "{0} is required.", "nl": "{0} is verplicht." },
    "minLength": { "en": "{0} must be at least {1} characters.", "...": "..." }
  }
}
```

### 5.2 Per-library generator: `LibraryTranslationsGenerator`

Runs in **every project** that has a `translations.json` as an `AdditionalFile` (libraries and hosts alike).

Pipeline:

1. `AdditionalTextsProvider.Where(f => Path.GetFileName(f.Path) == "translations.json")`.
2. Project each matching file to an `IEquatable<T>` POCO `(path, contentHash, rawJson)`. **Crucial** — this keeps the generator incrementally cacheable. Never hold `AdditionalText` across stages.
3. Validate the tree using the leaf-detection rule (§5.5). On error, emit a diagnostic with precise JSON path.
4. Flatten the tree to a flat dotted-key map `Dictionary<string, TranslatedString>`.
5. Emit, in the library's assembly:

   ```csharp
   // Generated
   [assembly: MintPlayer.Spark.Abstractions.SparkTranslations(
       chunkIndex: 0, chunkCount: 1,
       json: """{"auth.login":{"en":"Log in","nl":"Aanmelden"},"validation.required":{"en":"{0} is required.","nl":"{0} is verplicht."}}""")]
   ```

   The attribute payload is the **flat dotted-key JSON** (not the nested tree). Flattening at library-build time means the aggregator never re-parses nested structure.

6. If the serialized JSON exceeds **60KB**, split into multiple attributes with `chunkIndex` 0..N-1. Safe ceiling chosen below the ~64KB threshold where some legacy tooling struggles with IL `#US`-heap strings.

The `[SparkTranslations]` attribute:

```csharp
// In MintPlayer.Spark.Abstractions
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class SparkTranslationsAttribute(int chunkIndex, int chunkCount, string json) : Attribute
{
    public int ChunkIndex { get; } = chunkIndex;
    public int ChunkCount { get; } = chunkCount;
    public string Json { get; } = json;
}
```

### 5.3 Host aggregator: `HostTranslationsAggregatorGenerator`

Runs only in **executable** projects — gated on `Compilation.Options.OutputKind` being `ConsoleApplication` or `WindowsApplication`. Libraries skip this generator entirely.

Pipeline:

1. `CompilationProvider` → project to `ImmutableArray<(string assembly, int idx, int count, string json)>` by walking `Compilation.SourceModule.ReferencedAssemblySymbols`, then each symbol's `GetAttributes()`, matching `SparkTranslations` by **fully qualified metadata name** (`attr.AttributeClass?.ToDisplayString() == "MintPlayer.Spark.Abstractions.SparkTranslationsAttribute"`). **Project immediately** — do not retain symbols downstream.
2. Also collect the current compilation's own `[assembly: SparkTranslations]` attributes (emitted by the in-project `LibraryTranslationsGenerator` from the host's `translations.json`).
3. Group entries by assembly, reassemble chunks in order, parse each to a flat dotted-key dict.
4. Merge with **host-wins** policy (§5.6).
5. Emit:

   ```csharp
   // <auto-generated />
   namespace MintPlayer.Spark.Generated;

   public static class SparkTranslationsRegistry
   {
       public static readonly IReadOnlyDictionary<string, TranslatedString> All =
           new Dictionary<string, TranslatedString>(StringComparer.Ordinal)
           {
               ["auth.login"]          = new() { Translations = { ["en"] = "Log in",   ["nl"] = "Aanmelden" } },
               ["validation.required"] = new() { Translations = { ["en"] = "{0} is required.", ["nl"] = "{0} is verplicht." } },
               // ...
           };
   }
   ```

### 5.4 Runtime path

`TranslationsLoader` becomes trivial:

```csharp
internal partial class TranslationsLoader : ITranslationsLoader
{
    public TranslatedString? Resolve(string dottedKey)
        => SparkTranslationsRegistry.All.TryGetValue(dottedKey, out var ts) ? ts : null;

    public IReadOnlyDictionary<string, TranslatedString> GetAll()
        => SparkTranslationsRegistry.All;
}
```

- No file reads. No JSON parsing. No `App_Data/` lookup.
- `/spark/translations` endpoint serves `SparkTranslationsRegistry.All` directly — same flat wire shape as today.
- `Manager.GetTranslatedMessage` and `ValidationService.FormatTranslatedMessage` switch from `translations[key]` to `loader.Resolve(key)`.

### 5.5 Leaf detection

> A JSON object is a **TranslatedString leaf** iff every property value is a JSON string. Otherwise it is a **namespace** — recurse into children.

Edge cases → **library-build-time** diagnostics, with JSON path in the message:
- Mixed object (some string values, some object values) → `SPARK_TRANS_002` (error).
- Empty object `{}` → `SPARK_TRANS_003` (warning, skipped).
- Arrays anywhere → `SPARK_TRANS_004` (error).
- A property name containing `.` → treated as a literal key at that level. The loader never splits property names on `.`; splitting happens only at JSON object boundaries. Document in the guide.

### 5.6 Conflict / merge policy

Deterministic, documented order — **last write wins**:

1. All library translations, merged in alphabetical order by assembly simple name (stable across builds).
2. Host translations last.

Rationale: host authors get the final word for overriding framework strings (e.g. rebranding `common.save`). Libraries should not shadow each other; when they do, the aggregator emits `SPARK_TRANS_005` (warning, with the losing assembly name + key). Escalating to an error is an opt-in via an MSBuild property (`<SparkTreatTranslationConflictsAsErrors>true</...>`).

### 5.7 HTTP endpoint

Unchanged wire contract: `GET /spark/translations` returns `Dictionary<string, TranslatedString>` with flat dotted keys. Frontend type stays `Record<string, TranslatedString>`; `t('auth.login')` keeps its O(1) lookup.

### 5.8 Frontend

No code change in `SparkLanguageService` or `SparkAuthTranslationService` — only template sweeps:

- `node_packages/ng-spark/**/*.html` — rewrite camelCase keys to dotted (`authLogin` → `auth.login`, `validationRequired` → `validation.required`, etc.).
- `node_packages/ng-spark-auth/**/*.html` — same.
- `Demo/*/ClientApp/**/*.html` — same for any keys they pass to `| t`.

## 6. Packaging & versioning

Follow the **Microsoft.Extensions.Logging.Abstractions** pattern.

- `MintPlayer.Spark.Abstractions` gets the `SparkTranslationsAttribute` type. No generator dependency — attribute is public, referenced by generator output in every library.
- `MintPlayer.Spark.SourceGenerators` gets both generators (`LibraryTranslationsGenerator`, `HostTranslationsAggregatorGenerator`) plus a `build/MintPlayer.Spark.SourceGenerators.props` that injects the analyzer transitively (`IncludeAssets="analyzers;build"`, `PrivateAssets="none"` on consumer-facing refs so the analyzer flows).
- Every Spark library already references `MintPlayer.Spark.Abstractions` + `MintPlayer.Spark.SourceGenerators` → no new explicit references needed.
- A consuming host app references `MintPlayer.Spark` (transitively brings everything). The aggregator activates purely from `OutputKind`.

Versioning: bump `MintPlayer.Spark.Abstractions`, `MintPlayer.Spark`, `MintPlayer.Spark.SourceGenerators`, `MintPlayer.Spark.Authorization`, `MintPlayer.Spark.IdentityProvider` (when it lands) — all preview minor. Frontend `@mintplayer/ng-spark` and `@mintplayer/ng-spark-auth` patch bump (template-only changes).

## 7. Diagnostics

Emitted by the relevant generator, attributed to the correct project:

| Code            | Severity | Origin     | Meaning                                                             |
|-----------------|----------|------------|---------------------------------------------------------------------|
| `SPARK_TRANS_001` | Error    | Library    | `translations.json` is not valid JSON.                             |
| `SPARK_TRANS_002` | Error    | Library    | Mixed leaf/namespace object at path `X`.                           |
| `SPARK_TRANS_003` | Warning  | Library    | Empty object at path `X`; skipped.                                 |
| `SPARK_TRANS_004` | Error    | Library    | Array not allowed at path `X`.                                     |
| `SPARK_TRANS_005` | Warning  | Host       | Key `X` defined by multiple assemblies; losing value from `Asm.Y`. |
| `SPARK_TRANS_006` | Info     | Library    | `translations.json` not found; skipping.                            |

Errors fail the library's build locally, before publication — the right place to catch them.

## 8. Incremental generator hygiene

Non-negotiable rules for both generators:

- Never retain `AdditionalText`, `Compilation`, or `ISymbol` beyond the first pipeline stage. Always project to `IEquatable<T>` records.
- For the aggregator, the input record is `ImmutableArray<(string AssemblyName, int ChunkIndex, int ChunkCount, string Json)>` with a custom comparer that treats the set as equal under any stable permutation — so unrelated source edits in the host don't invalidate the cache.
- Sort keys in the emitted dictionary to keep the output deterministic (required for reproducible builds and source-link).

## 9. Migration (this PR)

1. Rewrite `MintPlayer.Spark/App_Data/translations.json` (framework-level strings currently living in each host) — **no**, there isn't one. The framework's strings (`save`, `cancel`, `validation.*`) move into `MintPlayer.Spark/App_Data/translations.json` as **part of the library itself**. Same for `MintPlayer.Spark.Authorization` (auth strings) and `MintPlayer.Spark.IdentityProvider` when it lands.
2. Slim every demo app's `App_Data/translations.json` down to **app-specific** strings only (framework/auth strings come from libraries automatically).
3. Update `ValidationService` constants to dotted: `validationRequired` → `validation.required`, `validationMinLength` → `validation.minLength`, etc.
4. Sweep Angular templates in `node_packages/ng-spark`, `node_packages/ng-spark-auth`, `Demo/*/ClientApp` to dotted keys.
5. Delete the file-reading path in `TranslationsLoader`.

## 10. Testing

- **Unit tests** on `LibraryTranslationsGenerator`: valid nested JSON → correct flat attribute payload; every edge case from §5.5; chunking boundary at 60KB.
- **Unit tests** on `HostTranslationsAggregatorGenerator`: merge order determinism, host-wins, conflict-warning emission, chunk reassembly, multi-assembly scenarios using Roslyn test `Compilation` with synthetic referenced assemblies.
- **Incremental cache tests**: add assertions that editing an unrelated file in the host does not re-run the aggregator's later stages (Roslyn's `GeneratorDriverRunResult.Results[0].TrackedSteps`).
- **Integration test**: build a small host referencing `MintPlayer.Spark` + `MintPlayer.Spark.Authorization`, assert `SparkTranslationsRegistry.All["auth.login"]` resolves.
- **End-to-end smoke**: each demo app's Angular ClientApp renders translations correctly.

## 11. Risks & open questions

- **IL string size for assembly attribute**: safe under 60KB per chunk; chunking is implemented from day one so the ceiling isn't architectural. Translation files in this repo are all far smaller today; we're future-proofing.
- **Generator must be referenced by every library** — failing to reference `MintPlayer.Spark.SourceGenerators` in a new Spark library means its `translations.json` silently does nothing. Mitigation: document in the contribution guide; optionally add an analyzer that flags a `translations.json` AdditionalFile without the attribute being emitted.
- **AdditionalFile path convention** — we match on filename `translations.json`. If a project happens to have another file with that name used for some other purpose, it would be consumed. Acceptable risk; can be tightened to `App_Data/translations.json` exactly.
- **Rider / Omnisharp incremental responsiveness** — generators that read AdditionalFiles historically had refresh lag in non-VS IDEs. Flag for manual QA.
- **Debuggability** — generated `SparkTranslationsRegistry.cs` should be emittable to `obj/generated/` via `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>` so authors can inspect the merged dictionary. Recommend enabling in all Spark host csproj templates.
- **Dev-loop hot reload** — compile-time baking means translation edits require a rebuild. Roslyn's incremental generator cache makes this cheap, but it's a regression from the current runtime-file behavior. If this becomes a pain point, add a debug-only override: `TranslationsLoader` checks for `App_Data/translations.override.json` at startup and overlays it. Not in scope for this PR.

## 12. Acceptance criteria

- [ ] `translations.json` can be nested; the leaf rule is enforced with diagnostics.
- [ ] Every Spark library (`MintPlayer.Spark`, `MintPlayer.Spark.Authorization`) ships its own `App_Data/translations.json` as an `AdditionalFile`.
- [ ] `LibraryTranslationsGenerator` emits one or more `[assembly: SparkTranslations(...)]` attributes per library.
- [ ] `HostTranslationsAggregatorGenerator` runs only in `OutputKind == ConsoleApplication|WindowsApplication` and emits `SparkTranslationsRegistry.All`.
- [ ] Host-wins merge policy; `SPARK_TRANS_005` warns on cross-library conflicts.
- [ ] `TranslationsLoader` becomes a thin wrapper over `SparkTranslationsRegistry.All`; no file I/O.
- [ ] `/spark/translations` wire format unchanged (flat `Record<string, TranslatedString>`, dotted keys).
- [ ] `ValidationService` keys renamed to `validation.*`.
- [ ] All Angular templates use dotted keys.
- [ ] Demo apps' `translations.json` files contain only app-specific strings.
- [ ] Incremental-cache tests pass; unrelated host-source edits don't re-run the aggregator's expensive stages.
- [ ] `docs/guide-translated-strings.md` updated: nesting, leaf rule, modular contribution, diagnostic codes, merge policy, `EmitCompilerGeneratedFiles` tip.

## 13. Rollout

Single PR. Version bumps:
- `MintPlayer.Spark.Abstractions`, `MintPlayer.Spark`, `MintPlayer.Spark.SourceGenerators`, `MintPlayer.Spark.Authorization` → preview minor.
- `@mintplayer/ng-spark`, `@mintplayer/ng-spark-auth` → patch.

No migration guide required for downstream consumers (preview framework). Changelog documents:
- New `translations.json` authoring model (nested + per-library).
- `ValidationService` key rename.
- `TranslationsLoader` behavior change (no longer reads `App_Data/` at runtime).
