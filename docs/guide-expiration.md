# Document expiration: declare it, Spark stamps and hides

A document that should disappear after a while (a crawl's per-page results, a cool-down marker, an
invitation) implements `IExpiring`. Spark writes RavenDB's `@expires` for it on every store, hides it
from every Spark read once its time has passed, and enables expiration on the database with the one
frequency the Community licence allows.

Decisions and evidence: [seo_analyzer_PRD.md](seo_analyzer_PRD.md) X1 and §12 "Spike S6".

## Declaring an expiring type

```csharp
public class PageResult : IExpiring                 // MintPlayer.Spark.Abstractions
{
    public string? Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public DateTime? ExpiresAtUtc { get; set; }     // public, never an explicit implementation
}
```

```jsonc
// App_Data/Model/PageResult.json, hand-written and kept by model synchronization
"persistentObject": { "name": "PageResult", "clrType": "…PageResult", "expiresAfter": "7.00:00:00", … }
```

- **`expiresAfter`** (optional) gives a **new** document stored without an `ExpiresAtUtc`
  `now + expiresAfter`. Without it, a document expires only when code sets `ExpiresAtUtc`.
- **`ExpiresAtUtc` is the source of truth.** Set it before the first store to override the default,
  change it later to extend or shorten, set it to `null` to keep the document for good. `@expires`
  follows it on the next store; a `Local` time is converted to UTC.
- Nothing to register: stamping and hiding are always on for `IExpiring` types. `spark.AddExpiration(o
  => …)` is only for options in code and for an app that writes `@expires` by hand.
- Startup refuses an `ExpiresAtUtc` that is not a public `DateTime?` property, an `expiresAfter` on a type
  that is not `IExpiring`, and an `expiresAfter` of zero or less.
- `ExpiresAtUtc` becomes a model attribute like any public property. If users must not change it, mark
  the attribute read-only or the property `[IgnoreProperty]`.

## Where `@expires` is written

A store-level `OnBeforeStore` (`libs/spark/MintPlayer.Spark/Expiration/ExpirationStamper.cs`), so it
covers the persistent-object pipeline **and** raw sessions: a message handler, a cron job, a migration.
A persistent-object interceptor could not: on a create it has no id and no metadata yet.

Not covered, because RavenDB raises no event for them: **patches, bulk inserts and ETL**. Code using
those writes `@expires` itself (ISO 8601 UTC).

## Hiding expired documents

RavenDB deletes an expired document at its next sweep, and on the **Community licence the sweep runs at
most every 36 hours**. A document can therefore linger up to 36 h past its time. The guarantee is the read
filter, not the sweep:

- A row policy (`ExpirationRowPolicy`) adds `!(ExpiresAtUtc <= now)` to every path that asks row security:
  lists, custom queries and sub-queries, distinct values, streams, breadcrumbs, reference pickers, the
  edit/delete gates, custom-action selections. A load of an expired document is the missing-row **404**
  (#453: missing ≡ no access).
- It applies in the system context too: background work through `IDatabaseAccess` does not see them.
- ⚠️ Raw session queries are not filtered. Write the same predicate, `!(x.ExpiresAtUtc <= now)`. Never
  `x.ExpiresAtUtc == null || x.ExpiresAtUtc > now`: an absent field matches no `== null`, so documents
  stored before the type became `IExpiring` would vanish (measured in `ExpirationTests`).
- An index projection over an expiring collection should carry `ExpiresAtUtc` in its Map and projection;
  without it the filter runs after materialization and pages come back short (startup warns).

## The database and the 36-hour floor

`UseSpark()` enables expiration once, when anything needs it: a model type implementing `IExpiring`,
`AddExpiration()`, or a library that declared `Registry.RequireDocumentExpiration(...)` (Messaging,
MailManager, Moderation and the identity provider do; they write `@expires` themselves).

| Key | Default | Meaning |
|---|---|---|
| `Spark:Expiration:DeleteFrequency` | `1.12:00:00` (36 h) | How often RavenDB sweeps. Startup **refuses anything shorter, on every licence**. |
| `Spark:Expiration:ConfigureDatabase` | `true` | `false` leaves the database record to the operator. Stamping and hiding still work. |

⚠️ The floor is checked in code because no test can catch it otherwise: Community refuses a shorter
frequency and **the RavenDB process dies** on the configuration change, while the Developer licence that
CI and local runs use accepts it. See `SparkExpiration.MinimumDeleteFrequency`.

## Storage

Measured for a SeoTester `PageResult` (20 findings, title/description/h1 snapshot): about 2 KB per
document, so a 100-page run holds ~0.2 MB and a 2 000-page run ~4 MB until the sweep after its seven days.
Details in the PRD (§12 "Spike S6").
