# Settings: declared in code, changed by an administrator

Feature switches and limits that an administrator changes at runtime, without a deploy, belong in the
settings package (`MintPlayer.Spark.Settings`, alias `settings`). A setting is declared in code with its
type and default; the administrator overrides it on a generic Spark screen; code reads it through one
service that coalesces the stored value with `IConfiguration` and the default.

Decisions and evidence: [seo_analyzer_PRD.md](seo_analyzer_PRD.md) X9, D11 and §12 "Spike S11".
Package reference: [README](../libs/settings/MintPlayer.Spark.Settings/README.md).

## Declaring settings

```csharp
public static class SeoSettings
{
    public static readonly SparkSetting<bool> IntrusiveProbes =
        new("Seo:Features:IntrusiveProbes", false, "Run probes that send unusual requests.");
    public static readonly SparkSetting<int> MaxPages =
        new("Seo:Limits:MaxPages", 100, "Pages crawled per audit.");
    public static readonly SparkSetting<TimeSpan> CoolDown =
        new("Seo:Limits:CoolDown", TimeSpan.FromHours(6), "Wait between two audits of one site.");
}

builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>();
    spark.AddSettings(typeof(SeoSettings));            // every public static SparkSetting<T> of the type
});
```

- **The key is the configuration key** (`Seo:Limits:MaxPages`), so `appsettings.json`, environment
  variables (`Seo__Limits__MaxPages`) and user secrets keep working as the layer beneath the stored value.
  Keys match `^[A-Za-z0-9][A-Za-z0-9:._-]*$` because they become the document id `Settings/{Key}`.
- **An administrator never types a key.** A malformed key, or one key declared twice with a different
  type or default, refuses registration. `AddSettings` may be called again by another library or module
  to declare more.
- **No secrets.** Values are stored in plaintext and shown to every settings administrator. Connection
  strings, API keys and OAuth secrets stay in configuration.

## Granting who may change them

The package ships its rights as a [library layer](guide-library-layers.md): `QueryReadEdit/Setting`
granted to the slot `settings:administrators`. The application binds the slot in its own
`App_Data/security.json`, or **startup is refused** (the exception names the slot):

```json
"bindings": { "settings:administrators": [ "Administrators" ] }
```

There is no `New` or `Delete`: settings are declared in code and seeded. Clearing `Value` is how an
administrator falls back to configuration. Everyone outside the bound group gets the missing-row **404**
on the list and on a single setting, the same answer as for a row that does not exist (#453).

The menu gains a **Settings** unit (alias `settings`, order 95), visible only to the bound group. Remove
it with `{ "id": "e95310bb-0014-5185-a013-0c9029c201bc", "$remove": true }` in the application's
`programUnits.json`.

## Reading a setting

```csharp
public sealed partial class AuditService
{
    [Inject] private readonly ISparkSettings settings;

    public async Task RunAsync(CancellationToken ct)
    {
        var maxPages = await settings.GetAsync(SeoSettings.MaxPages, ct);
        // …
    }
}
```

`GetAsync` returns, in order:

1. the **stored value** of `Settings/{Key}`, when it is not empty;
2. else **`IConfiguration[Key]`**;
3. else the **code default**.

Stored and configured strings are parsed the same way, with the type's `TypeConverter` in the invariant
culture (what `ConfigurationBinder` uses), so `25`, `true`, `True` and `00:05:00` mean the same from either
source.

**A read never throws for bad data.** A stored or configured value that no longer parses (the type
changed in code) is skipped with one warning and the next source applies. Passing a `SparkSetting<T>` that
was never declared through `AddSettings` is a programming error and throws.

## Seeding

At startup (`IHostedLifecycleService.StartedAsync`, after `UseSpark()` built the pipeline) one
`Settings/{Key}` document is written per declared key. Its `Type`, `Default` and `Description` follow the
code; **its `Value` is never touched**. The seeder writes optimistically and retries on a concurrency
conflict, so a concurrent administrator save is never overwritten. Documents of keys no longer declared
are left alone: reads ignore them and a save of one is refused.

## Validation on save

A before-save interceptor (see [interceptors](guide-interceptors.md)) enforces:

- an edit keeps `Key`, `Type`, `Default` and `Description` from the stored document, whatever the client
  posted (they are also read-only in the model);
- the key must be declared in code, else **400**;
- whitespace is stored as `null`, meaning "not overridden";
- any other value must parse as the declared type, else **400** naming the type (`'lots' is not a valid
  Int32.`).

## Caching

The stored values are read as one snapshot of the `Settings` collection by id prefix (never a query, so a
value saved a moment ago is not missed behind a stale index), kept for `Spark:Settings:CacheDuration`:

| Key | Default | Meaning |
|---|---|---|
| `Spark:Settings:CacheDuration` | `00:00:30` | How long a snapshot is used. The longest another instance shows an old value. |

The instance that saves or deletes a setting drops its snapshot at once (after-save and after-delete
interceptors call `ISparkSettings.Invalidate()`); another instance sees the change within the cache
duration. Options can also be set in code: `spark.AddSettings(o => o.CacheDuration = …, typeof(SeoSettings))`.

## Testing

`ISparkSettings` runs against the test store like any service; inject a fake `TimeProvider` to step past
the cache duration. Endpoint tests bind `settings:administrators` to a test group through the
application's security JSON. Examples: `tests/MintPlayer.Spark.Tests/Settings/SparkSettingsTests.cs` and
`SettingsEndpointTests.cs`.
