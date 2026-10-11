# Spark Settings (X9 / spike S11) — read-only investigation, 2026-10-10

Scope fixed by the owner (docs/seo_analyzer_PRD.md:376-381, X9 row :201, S11 row :266): a `Setting`
entity + PO, edit rights from the Authorization package (security.json), and a service + interface that
coalesces stored value > IConfiguration > code default. No new authorization design.

Evidence base: master @ 761a9f2b (working tree, read-only).

---

## 1. Which library owns it, and how library-owned entities reach an app's model

### Recommendation: a new small opt-in package `libs/settings/MintPlayer.Spark.Settings` (alias `settings`), NOT core.

Reasons:
- Core `MintPlayer.Spark` ships only `App_Data/actions.json` + `translations.json` (no `Model/`), see
  `find libs -path "*/App_Data/*"`. A `Setting` type in core would land in **every** app's composed
  model, menu and security surface, including DemoApp (the "core only" app, repo CLAUDE.md).
- A library that ships rights to a **slot** forces the app to bind the slot or startup is refused
  (`libs/spark/Shared/Layering/SparkSecurityLayers.cs:298`: "names the slot '…', which security.json
  does not bind"). That is exactly the owner's "who may change settings comes from security.json", but
  it must stay opt-in, i.e. a package.
- It depends on `MintPlayer.Spark` + `MintPlayer.Spark.Authorization` only, like Moderation/IdentityProvider
  (`libs/moderation/MintPlayer.Spark.Moderation/MintPlayer.Spark.Moderation.csproj` ProjectReferences).
  One project is enough (History keeps `ISparkHistory` in its main project: `libs/history/MintPlayer.Spark.History/ISparkHistory.cs`);
  no `.Abstractions` split needed for a lean feature.
- Do not add it to `MintPlayer.Spark.AllFeatures` (that csproj, lines 30-35, references only
  Authorization/Messaging/Replication/Cron/Migrations; IdentityProvider/Moderation/History are not in it either).

### How a library ships entities / POs / rights / menu today (composition redesign, docs/spark_composition_PRD.md)

The template to copy is **IdentityProvider** (ships 15 POs, rights, menu, translations):

| Piece | Where | Mechanism |
|---|---|---|
| Alias | `<SparkLibraryAlias>identity-provider</SparkLibraryAlias>` in the csproj (IdP csproj:12; Moderation csproj:9) | Names the library's layers; required when a library ships App_Data (SPARK041, `libs/source_generators/MintPlayer.Spark.SourceGenerators/build/MintPlayer.Spark.SourceGenerators.targets:18`) |
| Transport | `App_Data/**` compiled into `[assembly: SparkLayer(alias, kind, path, json)]` by `LibraryLayersGenerator` (`libs/source_generators/.../Generators/LibraryLayersGenerator.cs`) | PRD D1 "Decided: C" (spark_composition_PRD.md:113), as built M3 (:119-135). The app records `SparkLayerAssemblies` and the runtime Assembly.Loads them (trimming fix, S1). |
| Model | `App_Data/Model/<Type>.json` with `clrType` = the library's class, e.g. `libs/identity_provider/MintPlayer.Spark.IdentityProvider/App_Data/Model/OidcApiScope.json` | PRD D5 (:333): ids are deterministic UUIDv5 over `{alias}:{Type}...`, **written into the file** with `npm run stamp:library-model-ids -- <lib folder>` (`package.json:10`, `tools/stamp-library-model-ids.mjs`) and verified by SPARK045/046. D6 (:361): the app synchronizer writes only the app's delta; it never writes library types. |
| Queries | Root `queries` in the model file with `"source": "Custom.<Method>"` (every library query uses Custom.*, e.g. OidcResource.json:174) | The method lives on the library's Actions class: `OidcResourceActions.Resources() => session.Query<OidcResource>()` (`.../Actions/OidcResourceActions.cs:25`). A library cannot use `Database.X` because that needs a property on the **app's** SparkContext. Actions classes are found by AppDomain scan (`libs/spark/MintPlayer.Spark/Services/ActionsResolver.cs:131`), no registration. |
| Rights | `App_Data/security.json` with `reservedTargets` and `rights` granted to **slots** (`identity-provider:administrators`), e.g. `.../IdentityProvider/App_Data/security.json` | PRD D4. App binds the slot: `apps/SparkId/SparkId/App_Data/security.json:13-16` (`"bindings": { "identity-provider:administrators": ["Administrators"] }`), QnA binds moderation slots at :16-20. Unbound slot = startup refused. App can drop a grant (`$remove`) or the whole library (`"libraries": { "<alias>": false }`). |
| Menu | `App_Data/programUnits.json` fragment, group + units of `type: "query"` with `queryId`/`alias` (`.../IdentityProvider/App_Data/programUnits.json`) | Units show only to callers holding Query on the type; app removes a unit with `{ "id": …, "$remove": true }`. |
| Translations | `App_Data/translations.json` (`model.<Type>…`, `programUnits.…`) | Composed at runtime (D10). |
| DI | `ISparkBuilder` extension, e.g. `AddIdentityProvider` (`.../Extensions/SparkIdentityProviderExtensions.cs:40`), `AddHistory` (`libs/history/.../SparkHistoryExtensions.cs:34`), `AddContributions(params Assembly[])` (`libs/contributions/.../SparkContributionsExtensions.cs:38`) | Validation as interceptors (`builder.AddInterceptor<T>()`, `libs/spark/MintPlayer.Spark.Abstractions/Interceptors/SparkBuilderInterceptorExtensions.cs:22`; IdP uses it at Extensions:76-77), per #482. |

Moderation is the precedent for a library **layering into IConfiguration** (file-based, at build time):
`libs/moderation/MintPlayer.Spark.Moderation/ModerationConfiguration.cs:41-56` inserts a composed source
at index 0 (lowest precedence). Different problem (static file), but shows the config conventions.

No existing generic settings feature (seo PRD:201, grep confirmed). CodeCoverage's `GateSettings`
(`apps/CodeCoverage/CodeCoverage.Library/Entities/GateSettings.cs`) is a typed per-repository embedded
object, not a key/value store; not reusable.

---

## 2. Plain service vs RavenDB-backed IConfigurationProvider; multi-instance invalidation

### Feasibility of an IConfigurationProvider
Technically possible, but poor fit:
- The store is a DI singleton built **after** configuration (`libs/spark/MintPlayer.Spark/SparkMiddleware.cs:158-188`,
  and its own URLs come from `options.RavenDb`, i.e. from IConfiguration). A provider would have to be
  added empty at config-build time and filled later by a hosted service that hands it the store, then
  `OnReload()`. Until then every reader sees config-only values: "stored wins" silently does not hold
  during startup, and any `IOptions<T>` (not Monitor) resolved early is frozen with the wrong value forever.
- To "win", the provider must be the **last** source, above env vars and user-secrets. Then a holder of
  `Edit/Setting` could override **any** key: `Spark:RavenDb:Urls`, OAuth client secrets, connection strings.
  That turns a settings admin into a configuration admin: a privilege escalation unless an allow-list
  is added, and the allow-list is exactly the "declared keys" a plain service needs anyway.
- IConfiguration is flat strings, so per-key typing and validation are lost; values also leak through
  `IConfiguration` debug views.
- IOptionsMonitor reload would work (provider `OnReload()` fires the change token), but only for code
  written against IOptionsMonitor; the SEO features read a handful of gates at request time, where a
  direct async read is simpler.

### Invalidation across instances
- **Changes API** (`store.Changes().ForDocumentsInCollection<Setting>()`): not used anywhere in the repo
  today (`grep "\.Changes("` libs/apps = 0 hits). To my knowledge it is not licence-gated (unlike
  subscriptions, which prod caps at 3 per database: memory `reference_raven_subscription_cap.md`), but
  **unverified here**: confirm against the prod `/license/status` before relying on it. It is
  at-most-once: a websocket reconnect loses notifications, so a TTL is still needed as the safety net.
- **Short TTL snapshot**: load all `Setting` docs (a few dozen) into one in-memory snapshot, refresh
  after TTL (default 30 s, configurable), and invalidate locally right after a save/delete on the same
  instance. Worst case another instance sees the change TTL later. One query per TTL per instance.

### Recommendation
**Plain service `ISparkSettings` with a TTL snapshot plus local invalidation on save.** Do not build the
IConfigurationProvider. Changes API can be added later as a pure latency optimisation inside the same
service (no API change) if a 30 s lag ever matters; the admin toggles here (probes, limits) do not need it.
prod is a single instance today, so local invalidation alone gives immediate effect there.

Sketch:
```csharp
public sealed record SparkSetting<T>(string Key, T Default, string Description);   // declared by code
public interface ISparkSettings
{
    ValueTask<T> GetAsync<T>(SparkSetting<T> setting, CancellationToken ct = default);
    void Invalidate();                       // called by the save/delete interceptor
}
// stored (non-null Value) > configuration[Key] > setting.Default
```

---

## 3. Value typing, validation, key selection

- **Typing:** declared in code by `SparkSetting<T>`, T in {bool, int, long, double, decimal, TimeSpan, string, enum}.
  Store `Value` as a **string** (invariant culture) on the document; parse both the stored string and the
  configuration string with the same `TypeDescriptor.GetConverter(typeof(T)).ConvertFromInvariantString`
  that `ConfigurationBinder` uses, so `"00:05:00"`, `"true"`, `"200"` behave identically from either source.
- **Validation** (interceptor on save, per #482): key must be declared; `Value` null/empty = "not
  overridden"; otherwise it must parse as the declared T (and optional Min/Max if declared). At read
  time an unparsable stored value (e.g. code changed T) falls back to config/default and logs a warning,
  never throws on a hot path.
- **Key selection: declared by code, never free text.** The admin never types a key:
  - The app declares keys as static fields, e.g. `SeoSettings.IntrusiveProbes = new("Seo:Features:IntrusiveProbes", false, "...")`,
    registered with `spark.AddSettings(typeof(SeoSettings))` (static-field scan, like `AddContributions(params Assembly[])`).
  - At startup a hosted step **upserts one `Setting` document per declared key** with a natural id
    `Settings/{Key}`, refreshing `Description`/`Type`/`Default` and **never touching `Value`**. Idempotent,
    safe on several instances.
  - Why seeding instead of a dropdown on New: transient lookups are static types discovered by reflection
    (`libs/model/MintPlayer.Spark.Model/TransientLookupReference.cs`, `LookupReferenceDiscoveryService.cs:61-64`)
    and dynamic ones are Raven documents (`DynamicLookupReference.cs`); neither can list keys declared via
    DI from a library-owned entity. A custom query over declared keys could merge rows in memory
    (`QueryExecutor.cs:678` supports IEnumerable), but a row with no document cannot be opened/edited.
  - Rights: library grants `QueryReadEdit/Setting` only (no New/Delete), so the UI has no free-text path.
    "Fall back" from the UI = clear `Value`. The service still treats a missing document as "not
    overridden" (needed for the red→green delete test, and for the window before seeding).
- Optional, not in the lean cut: `PersistentObjectAttribute.DataType` is settable per instance at runtime
  (`libs/spark/MintPlayer.Spark.Abstractions/PersistentObject.cs:227`), so `OnLoad` could render a bool
  setting as a checkbox. Skip: the wire value would become typed while the entity field is a string
  (EntityMapper conversion risk). A string field + `Type` column + validation is enough.
- **Never put secrets in settings.** Stored plaintext, visible to every settings admin. If an
  `EffectiveValue`/`Source` column is shown, it echoes configuration values, so only declared, non-secret
  keys may exist (declaration is code-reviewed).

---

## 4. Admin page in ng-spark

- **Zero client code.** The generic query page + PO detail/edit render any type. There is **no inline
  grid editing** in ng-spark today (`grep -ri inline query-list grid po-edit` = no hits); the flow is
  list -> row -> Edit -> Save, plus the list-row Edit action from #467/#483. That is enough for ~20 rows.
- Model `Setting.json` (library layer): attributes `Key` (read-only), `Value` (string, not required),
  `Type` (read-only), `Default` (read-only), `Description` (read-only, PO only or both); query
  `Settings`, `source: "Custom.Settings"`, `entityType: "Setting"`, sort by `Key`, alias `settings`;
  breadcrumb `{Key}`. `SettingActions.Settings() => session.Query<Setting>()` (same shape as
  `OidcResourceActions.cs:25`).
- **programUnits.json** in the library: one group (or a unit an app can move) with a single `type: "query"`
  unit, `alias: "settings"`, icon `bi-sliders`, high `order` (IdP uses 90) so it sits at the bottom
  "admin" area. It is visible only to holders of `Query/Setting`, i.e. the bound `settings:administrators`
  group; an app removes it with `$remove`. The SEO app needs nothing in its own programUnits.json.
- security.json (library): `reservedTargets` not needed (Setting is a real type); rights
  `{ "key": "administrators", "resource": "QueryReadEdit/Setting", "groupId": "settings:administrators" }`.
  The app adds `"bindings": { "settings:administrators": ["Administrators"] }`.

---

## 5. Minimal file list and tests

### New: `libs/settings/MintPlayer.Spark.Settings/`
- `MintPlayer.Spark.Settings.csproj` (net11.0, `<SparkLibraryAlias>settings</SparkLibraryAlias>`, version
  `11.0.0-preview.N` per the platform-major rule, refs MintPlayer.Spark + Authorization + SourceGenerators analyzer)
- `README.md` (packed, like the other packages)
- `Models/Setting.cs`: `Id`, `Key`, `Value` (string?), `Type`, `Default`, `Description`
- `SparkSetting.cs`: `SparkSetting<T>` declaration record
- `ISparkSettings.cs` + `Services/SparkSettings.cs`: coalescing + TTL snapshot (TimeProvider-injectable)
- `Services/SettingsSeeder.cs`: hosted startup upsert of declared keys (natural ids)
- `Actions/SettingActions.cs`: `Custom.Settings` query
- `Interceptors/SettingInterceptors.cs`: validate on save; `Invalidate()` after save/delete
- `SparkSettingsExtensions.cs`: `AddSettings(this ISparkBuilder, params Type[] declaringTypes)` + `SparkSettingsOptions { CacheDuration }`
- `App_Data/Model/Setting.json` (ids stamped via `npm run stamp:library-model-ids -- libs/settings/MintPlayer.Spark.Settings`)
- `App_Data/security.json`, `App_Data/programUnits.json`, `App_Data/translations.json`

### Changed
- Solution file / Nx `project.json` for the new project (follow History's)
- `tests/MintPlayer.Spark.Tests/MintPlayer.Spark.Tests.csproj`: ProjectReference (list at :58-81)
- The SEO app (when it exists): `spark.AddSettings(typeof(SeoSettings))`, a `SeoSettings` declaration class,
  `bindings` in its security.json, and its features call `ISparkSettings.GetAsync(SeoSettings.X)`
- Docs: a guide page + the SEO PRD S11 result

### Tests (`tests/MintPlayer.Spark.Tests/Settings/`, SparkTestDriver, in-memory IConfiguration)
Red->green core pair the owner asked for:
1. `StoredValue_OverridesConfiguration`: config `Seo:Limits:Unverified:MaxPages=10`, stored `"25"` -> 25.
2. `DeletingStoredValue_FallsBackToConfiguration`: then delete the doc (and separately: set Value=null) -> 10.
Plus:
3. no doc, no config -> code default; 4. config only -> config (TimeSpan and bool parse identically from both sources).
5. Save with unparsable value / undeclared key -> refused by the interceptor (400-class), document unchanged.
6. Stored value made unparsable behind the API (type changed in code) -> falls back + warning, no throw.
7. Seeder: idempotent, refreshes Description/Default, never overwrites Value.
8. Cache: change via instance B is seen by instance A after TTL (FakeTimeProvider or TTL=0), and immediately on the saving instance.
9. Authorization through the real route table (HistoryTests style): Edit/Setting allowed for the bound
   group, 403 for a signed-in non-member, New/Delete refused for everyone; app without the
   `settings:administrators` binding refuses startup (existing SparkSecurityLayers check, one assertion).
