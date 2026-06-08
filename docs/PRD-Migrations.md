# PRD — MintPlayer.Spark.Migrations

**Status:** Implemented (branch `feat/recursive-breadcrumbs`)
**Date:** 2026-06-08
**Scope:** New optional library `MintPlayer.Spark.Migrations` (+ source generator in the shared `MintPlayer.Spark.SourceGenerators`, + AllFeatures wiring)
**Backward compatibility:** Not required — preview.

---

## 1. Problem

Spark apps need a first-class way to **seed and migrate RavenDB data** at startup — create reference/lookup data, backfill or reshape documents after a schema change, etc. Today there is no mechanism: demos have no seeding, and ad-hoc seeders aren't ordered, idempotent, or cluster-safe. (This surfaced concretely when seeding the recursive-breadcrumb demo: `Person → Company → Profession`.)

The team uses the **RavenMigrations** library pattern in other (Vidyano/Cronos) projects and asked whether to build the capability into Spark — in core or a dedicated library — within this PR.

---

## 2. Investigation (team findings)

- **`MintPlayer.Spark.Cron` is an exact blueprint.** A dedicated optional package: an interface (`ISparkCronJob`) → a source generator in the shared `MintPlayer.Spark.SourceGenerators` that discovers implementers and generates `AddCronJobs()` → a runner that uses **RavenDB compare-exchange locking** for cluster-once semantics. Migrations map onto this almost 1:1.
- **Startup hook exists and is proven.** Index creation already runs synchronously during `UseSpark()` (after the store/db are ready, before requests); optional features register startup work via `ISparkBuilder.Registry.AddMiddleware(...)`. A migration runner slots in right after index creation.
- **`RavenMigrations` NuGet — do *not* depend on it.** It is MIT and well-designed, and its concurrency model (cluster-wide compare-exchange lock + per-version marker doc) is exactly what we'd build. But it is **stale**: latest `6.0.2` (Aug 2024), targets **net8 only**, pins **`RavenDB.Client 6.0.105`**, no committed net10/Raven7 support. Spark tracks **net10 + RavenDB 7.2** closely and **already owns the compare-exchange locking primitive** (Cron). Taking it as a hard dependency drags a stale Raven-6 client constraint into the framework. **Verdict: hand-roll**, using RavenMigrations' design as the template, not as a dependency. (Sources reviewed: migrating-ravens/RavenMigrations source, NuGet, RavenDB 7.0 client breaking-changes.)
- **Home: dedicated library, not core.** Every other optional feature (Cron, Messaging, Replication, Authorization) is its own package; core stays lean (only `RavenDB.Client` + endpoints). Migrations is optional, so it's a separate package.

---

## 3. Design (implemented)

Library **`libs/migrations/MintPlayer.Spark.Migrations`** (net10, MIT), modeled on Cron.

### Authoring — discovery by interface, no attribute
```csharp
public partial class M_202606081200_SeedDemo : ISparkMigration
{
    public static long Version => 202606081200;          // sortable; unique; ordering + marker key
    public static string? Description => "Seed demo data";
    [Inject] private readonly IAsyncDocumentSession session;   // full scoped DI, like a cron job
    public async Task UpAsync(CancellationToken ct)
    {
        await session.StoreAsync(new Company { Name = "Acme" }, ct);
        await session.SaveChangesAsync(ct);
    }
}
```
- **Discovery is by interface** — non-abstract `ISparkMigration` implementers. No attribute, no base class (deliberately: the version travels with the type via a compiler-enforced `static abstract` member, mirroring `ISparkCronJob.CronSchedule`).
- Migrations resolve from a **DI scope**, so `[Inject]` works (session, stores, mail facades, …).
- **Forward-only** (no `Down`) by design — migrations describe how data moves forward.

### Registration
- **Generated `spark.AddMigrations()`** (no argument) — the ergonomic entry point. A new generator (`MigrationRegistrationGenerator`, alongside the cron one) discovers every `ISparkMigration` and emits it, exactly like `AddCronJobs()`.
- Manual escape hatch: `spark.AddMigrations(m => m.AddMigration<T>())`.
- **AllFeatures parity:** `SparkFullGenerator` detects `ISparkMigration` and `AddSparkFull()` calls `AddMigrations(spark)`; the AllFeatures package references the migrations lib.

### Runner (`SparkMigrationRunner`)
- Runs **once at startup**, during `UseSpark()` — **after** index creation, **before** the app serves requests (registered via `Registry.AddMiddleware`). Blocking, so traffic is never served against an un-migrated database.
- **Ordered** ascending by `Version`.
- **Run-once per database** — each applied version writes a `SparkMigrationRecords/{version}` marker; a migration whose marker exists is skipped.
- **Cluster-safe** — a compare-exchange lock (`spark/migrations/lock`, 30-minute lease, take-over on expiry) ensures one node at a time; the same primitive Cron uses.
- **Fail-fast** — a throwing migration aborts startup and stays unmarked, so it retries next start. The marker is written only after `UpAsync` returns successfully.

---

## 4. Alternatives considered

| Option | Verdict |
|---|---|
| Depend on `RavenMigrations` NuGet | Rejected — stale net8/Raven6, pins RavenDB.Client 6.0.105, no net10/Raven7 commitment. |
| Put migrations in `MintPlayer.Spark` core | Rejected — breaks the "core stays lean, features are separate packages" convention. |
| `[SparkMigration(version)]` attribute | Rejected — discovery by interface + `static abstract Version` is more consistent with Cron and needs no attribute import. |
| **Dedicated hand-rolled `MintPlayer.Spark.Migrations`** | **Chosen.** |

---

## 5. Verification

- **Unit/integration (embedded Raven):** `SparkMigrationRunnerTests` — ordered run + markers, idempotent re-run, fail-fast halts and leaves the failed version unmarked.
- **Live:** the demo migration (`M_202606081200_BreadcrumbDemo`) ran at HR startup, seeded `Ada Lovelace → Acme Corp → Software Engineering`, and **skipped on restart** (idempotent) — observed against the live `SparkHR` database.

---

## 6. Risks

| Risk | Mitigation |
|---|---|
| Two instances start together | Compare-exchange lock; the loser skips and serves once the winner releases. |
| A node crashes mid-run holding the lock | 30-minute lease; expired lock is taken over. |
| Long-running migration blocks startup | By design — correctness over availability; keep migrations bounded, or move heavy backfills to a cron/subscription worker. |
| Version collision | Registry throws at registration if two migrations share a `Version`. |
