# MintPlayer.Spark.Migrations

One-time database migrations for [MintPlayer.Spark](https://github.com/MintPlayer/MintPlayer.Spark).

Define a migration by implementing `ISparkMigration` on a non-abstract class and giving it a
`static abstract long Version`. Migrations run **once, in version order, at startup** — after
indexes are created and before the app serves requests.

```csharp
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents.Session;

public partial class M_202606081200_SeedDemo : ISparkMigration
{
    public static long Version => 202606081200;          // sortable timestamp; must be unique
    public static string? Description => "Seed demo data";

    [Inject] private readonly IAsyncDocumentSession session;   // full scoped DI, like a cron job

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        await session.StoreAsync(new Company { Name = "Acme" }, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }
}
```

Enable it — the source generator discovers every `ISparkMigration` and emits `AddMigrations()`:

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<MyContext>();
    spark.AddMigrations();   // generated; auto-discovers all migrations in this project
});
```

Or register manually:

```csharp
spark.AddMigrations(m => m.AddMigration<M_202606081200_SeedDemo>());
```

## Guarantees

- **Run-once per database** — each applied version writes a `SparkMigrationRecords/{version}`
  marker document; a migration whose marker exists is skipped.
- **Ordered** — migrations run ascending by `Version`.
- **Multi-node safe** — a cluster-wide RavenDB compare-exchange lock (`spark/migrations/lock`,
  30-minute lease) ensures only one node applies migrations at a time.
- **Fail-fast** — a migration that throws aborts startup and is left unmarked, so it retries on the
  next start. Call `SaveChangesAsync` yourself; the version marker is written only after `UpAsync`
  returns successfully.

Forward-only (no `Down`) by design — migrations describe how the data moves forward.
