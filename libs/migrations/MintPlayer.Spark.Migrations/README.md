# MintPlayer.Spark.Migrations

One-time database migrations for [MintPlayer.Spark](https://github.com/MintPlayer/MintPlayer.Spark).

Define a migration by implementing `ISparkMigration` on a non-abstract class named
`M_{yyyyMMddHHmm}_{Name}`. The timestamp in the name **is** the version (`SparkMigrationVersion`):
it sets the run order and the marker key, and registering a class not named that way (or naming a
minute that does not exist) refuses startup. Migrations run **once, in version order, at startup** —
after indexes are created and before the app serves requests.

```csharp
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents.Session;

public partial class M_202606081200_SeedDemo : ISparkMigration   // version 202606081200, from the name
{
    public static string? Description => "Seed demo data";

    [Inject] private readonly IAsyncDocumentSession session;   // full scoped DI, like a cron job

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        await session.StoreAsync(new Company { Name = "Acme" }, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }
}
```

Enable it — the source generator discovers every `ISparkMigration` and registers it in the generated `AddSparkDiscovered()`:

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<MyContext>();
    spark.AddSparkDiscovered(); // generated; auto-discovers all migrations in this project
});
```

Or register manually:

```csharp
spark.AddMigrations(m => m.AddMigration<M_202606081200_SeedDemo>());
```

## Guarantees

- **Run-once per database** — each applied version writes a `SparkMigrationRecords/{version}`
  marker document; a migration whose marker exists is skipped.
- **Ordered** — migrations run ascending by the version in their class names.
- **Multi-node safe** — a cluster-wide RavenDB compare-exchange lock (`spark/migrations/lock`,
  30-minute lease) ensures only one node applies migrations at a time.
- **Fail-fast** — a migration that throws aborts startup and is left unmarked, so it retries on the
  next start. Call `SaveChangesAsync` yourself; the version marker is written only after `UpAsync`
  returns successfully.

Forward-only (no `Down`) by design — migrations describe how the data moves forward.
