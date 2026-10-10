# Quotas: per-user limits on custom actions

"Five audits a day per user" is a quota, not a rate limit. The ASP.NET rate limiter (`AddRateLimiter`) counts
requests per address before authentication; a quota counts uses of one action per signed-in user, after every
authorization check, with limits an administrator changes at runtime. Quotas live in the
[settings package](guide-settings.md), because their limits are settings.

Decisions and evidence: [seo_analyzer_PRD.md](seo_analyzer_PRD.md) X3, D10, D14 and §12 "M6".

## Declaring

```csharp
public static class SeoQuotas
{
    public static readonly SparkQuota Audits = new("audits",
        new SparkSetting<int>("Seo:Quotas:Audits:Limit", 5, "Audits a user may start per window."),
        new SparkSetting<TimeSpan>("Seo:Quotas:Audits:Window", TimeSpan.FromDays(1), "The audits window."));

    public static readonly SparkQuota FullCrawls = new("full-crawls",
        new SparkSetting<int>("Seo:Quotas:FullCrawls:Limit", 1, "Full-site crawls per user per window (D14)."),
        new SparkSetting<TimeSpan>("Seo:Quotas:FullCrawls:Window", TimeSpan.FromDays(7), "The full-crawl window."));
}

builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>();
    spark.AddQuotas(typeof(SeoQuotas));      // declares the quotas and their four settings
});
```

`AddQuotas` declares each quota's two settings with `AddSettings`, so they appear on the **Settings** screen and
follow its rule: the stored value, else `IConfiguration`, else the default. Bind `settings:administrators` as for
any setting.

## On a custom action

```csharp
[SparkQuota("audits")]
public sealed partial class StartAuditAction : ICustomAction { … }
```

- Checked by `/spark/actions/execute` **after** authentication, the type right, the catalogue, the row gates and
  the disabled-action gate, right before the action runs. A refused run never reaches the action and does not
  count; the caller gets **429** with `Retry-After` (the moment the oldest counted use leaves the window) and
  `{ result: { error } }` naming the quota.
- An admitted run counts even when the action then fails, so a failing action cannot be retried for free.
- The key is the signed-in user's id, else `ip:{address}` for an anonymous caller.
- Startup refuses a `[SparkQuota]` that names a quota no `AddQuotas` declared.

## In code

When what counts is decided inside the action ("starting an audit counts, joining a running one does not",
"unverified sites have their own limit"), leave the attribute off and call the service:

```csharp
[Inject] private readonly ISparkQuotas quotas;

var decision = await quotas.TryConsumeAsync(site.IsVerified ? SeoQuotas.VerifiedAudits : SeoQuotas.Audits, userId, ct);
if (!decision.Allowed)
    throw new SparkThrottledException("You started 5 audits today.", decision.RetryAfter);
// or: await quotas.ConsumeAsync(SeoQuotas.Audits, ISparkQuotas.KeyFor(httpContext), ct);  (throws the 429 itself)
```

`GetUsedAsync` reads the current count without counting.

## Semantics

- **Sliding window:** at most `Limit` uses in any span of `Window`. A use leaves the window exactly one window
  after it was made.
- `Limit` **0** refuses every use; a **negative** limit is unlimited (nothing is stored).
- Uses are stored at `SparkQuotaUsages/{quota}/{key}` as a short list of times, pruned on every write, written
  under optimistic concurrency: 20 concurrent uses against a limit of 5 admit exactly 5 (tested).
- The document is `IExpiring`: it expires with its last use, so idle users leave nothing behind (X1).

## Testing

`tests/MintPlayer.Spark.Tests/Settings/SparkQuotaTests.cs`: a host with a stubbed action catalogue and resolver,
a manual `TimeProvider` to step through windows, and settings changed by writing the `Settings/{Key}` document and
calling `ISparkSettings.Invalidate()`.
