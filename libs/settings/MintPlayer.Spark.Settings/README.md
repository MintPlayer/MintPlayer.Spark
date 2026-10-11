# MintPlayer.Spark.Settings

Admin-changeable settings for MintPlayer.Spark (`docs/seo_analyzer_PRD.md` X9): feature switches and
limits an administrator changes at runtime, without a deploy.

- Settings are **declared in code** as `SparkSetting<T>(Key, Default, Description)`. An administrator never
  types a key.
- `ISparkSettings.GetAsync(setting)` returns the **stored value**, else **`IConfiguration[Key]`**, else the
  **code default**. Stored and configured strings parse identically (the configuration binder's
  `TypeConverter`, invariant culture), so `25`, `true` and `00:05:00` mean the same from either source.
- At startup one `Settings/{Key}` document is written per declared key. Its `Type`, `Default` and
  `Description` follow the code; its `Value` is never touched.
- Who may change settings is a `security.json` slot: the package grants `QueryReadEdit/Setting` to
  `settings:administrators`, and the application binds it. There is no New or Delete; clearing `Value`
  falls back to configuration.
- **No secrets.** Values are stored in plaintext and shown to every settings administrator.

## Setup

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
    spark.AddSettings(typeof(SeoSettings));
});
```

`App_Data/security.json` of the application (startup is refused without it):

```json
"bindings": { "settings:administrators": [ "Administrators" ] }
```

Reading:

```csharp
public sealed partial class AuditService
{
    [Inject] private readonly ISparkSettings settings;

    public async Task RunAsync(CancellationToken ct)
    {
        var maxPages = await settings.GetAsync(SeoSettings.MaxPages, ct);
        …
    }
}
```

The menu gets a **Settings** unit (alias `settings`), visible only to the bound group. Remove it with
`{ "id": "e95310bb-0014-5185-a013-0c9029c201bc", "$remove": true }` in the application's `programUnits.json`.

## Caching

The stored values are read as one snapshot of the `Settings` collection, kept for
`Spark:Settings:CacheDuration` (default 30 s). The instance that saves a change drops its snapshot at
once; another instance sees the change within the cache duration.

## Validation

A save is refused (400) when the key is not declared in code, or when `Value` does not parse as the
declared type. An empty `Value` is stored as "not overridden". A stored value that stops parsing later
(the type changed in code) is skipped with a warning, never thrown: configuration or the default applies.

Guide: [docs/guide-settings.md](../../../docs/guide-settings.md).

## Quotas

Per-user limits on custom actions (`docs/seo_analyzer_PRD.md` X3), with their limits as settings:

```csharp
public static class SeoQuotas
{
    public static readonly SparkQuota Audits = new("audits",
        new SparkSetting<int>("Seo:Quotas:Audits:Limit", 5, "Audits per window."),
        new SparkSetting<TimeSpan>("Seo:Quotas:Audits:Window", TimeSpan.FromDays(1), "The audits window."));
}
spark.AddQuotas(typeof(SeoQuotas));

[SparkQuota("audits")] public sealed partial class StartAuditAction : ICustomAction { … }   // 429 + Retry-After past the limit
await quotas.TryConsumeAsync(SeoQuotas.Audits, userId);                                 // ISparkQuotas, where code decides
```

A sliding window per user (else per address), checked after every authorization gate; a limit of 0 refuses,
a negative limit is unlimited. Guide: [docs/guide-quotas.md](../../../docs/guide-quotas.md).
