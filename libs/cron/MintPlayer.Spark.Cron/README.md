# MintPlayer.Spark.Cron

Cron-scheduled background jobs for [MintPlayer.Spark](https://github.com/MintPlayer/MintPlayer.Spark).

Define a job by implementing `ISparkCronJob` and declaring its schedule with a `static abstract`
member — the schedule lives with the job, not at the registration call site. Each job runs in its
own loop as part of an ASP.NET Core `BackgroundService`. The scheduler runs each job on its cron
schedule, and is safe to run on multiple nodes: a RavenDB compare-exchange claim ensures every
occurrence fires exactly once across the cluster. It follows the same conventions as the rest of
Spark (`[Inject]` dependencies, source-generated registration on `ISparkBuilder`).

## Installation

```bash
dotnet add package MintPlayer.Spark.Cron
```

If you also use the Spark source generators (for auto-registration), ensure the
`MintPlayer.Spark.SourceGenerators` package is referenced. Apps using the
`MintPlayer.Spark.AllFeatures` meta-package already have both.

## Overview

A cron job is a unit of recurring work scheduled by a cron expression. The scheduler:

- Runs one independent loop per registered job.
- Computes the next occurrence (in **UTC**) using [NCrontab](https://github.com/atifaziz/NCrontab).
- Resolves a fresh instance of the job from a DI scope for every run, so `[Inject]` dependencies work like any scoped service.
- Claims each occurrence cluster-wide via a RavenDB compare-exchange key, so the job runs on exactly one node.

> **Time zone:** schedules are always interpreted in UTC. NCrontab is not DST-aware, so there is intentionally no per-job time zone. To run at a local wall-clock time, write the UTC equivalent in the expression.

## Defining a job

Implement `ISparkCronJob`. Declare the schedule with the `static abstract` `CronSchedule` member and
put the work in `RunAsync`:

```csharp
using MintPlayer.Spark.Cron;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents.Session;

public partial class NightlyCleanup : ISparkCronJob
{
    public static string CronSchedule => "0 0 * * *"; // 00:00 UTC daily

    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ILogger<NightlyCleanup> logger;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Running nightly cleanup...");
        // ... use session ...
        await session.SaveChangesAsync(cancellationToken);
    }
}
```

The class must be `partial` for the `[Inject]` source generator to emit the constructor. The job is
resolved from a fresh DI scope for every run, so `[Inject]` dependencies behave like any scoped
service. The `CronSchedule` value is read once, at registration time — no instance is created to
read it.

### Cron Expression Format

`CronSchedule` is an NCrontab expression, always interpreted in **UTC**. The number of fields is
auto-detected:

- **Five fields** (`m h dom mon dow`) — minute precision, e.g. `0 0 * * *` (daily at midnight), `*/15 * * * *` (every 15 minutes).
- **Six fields** (`s m h dom mon dow`) — second precision, e.g. `*/30 * * * * *` (every 30 seconds).

For a job that should run at a local wall-clock time, write the UTC equivalent. (UTC schedules do
not auto-shift across daylight-saving transitions.)

### Concurrency

By default a job is non-concurrent: if a run overruns its interval, the loop waits for it to finish
and intervening occurrences are skipped. Opt into overlapping runs by overriding
`AllowConcurrentRuns`:

```csharp
public partial class ReportJob : ISparkCronJob
{
    public static string CronSchedule => "*/5 * * * *";
    public static bool AllowConcurrentRuns => true;   // occurrences may overlap

    public Task RunAsync(CancellationToken cancellationToken) => /* ... */;
}
```

Concurrent runs are bounded: at most **10** occurrences of a single job may run simultaneously. If a
concurrent job consistently overruns its interval and that cap is reached, further occurrences are
*shed* (logged at warning level and skipped) rather than piling up and exhausting the host. Keep
concurrent jobs short, or make `RunAsync` idempotent and rely on the cluster claim for exactly-once
semantics.

## Registration

### Option A: Source-Generated Registration (Recommended)

If your project references `MintPlayer.Spark.SourceGenerators`, a source generator discovers all
`ISparkCronJob` implementers and generates an `AddCronJobs()` extension on `ISparkBuilder`:

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<MyContext>();
    spark.AddCronJobs(); // source-generated — registers every discovered job with its default schedule
});
```

The generated code registers the scheduler and calls `AddJob<T>()` for each job found, using each
job's shipped `CronSchedule`.

### Option B: Manual Registration

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<MyContext>();
    spark.AddCron(cron => cron.AddJob<NightlyCleanup>());
});
```

`AddCron` and `AddCronJobs` both register the scheduler exactly once, so they compose freely — call
both if you want auto-discovery plus an extra manual registration.

### Overriding the Schedule

`CronSchedule` is the job author's *default*. A consumer can override it at registration — useful
when a job ships in a reusable package and you want it on your own cadence:

```csharp
spark.AddCron(cron => cron.AddJob<HeartbeatJob>("*/5 * * * *")); // every 5 minutes instead
```

To run the **same job type on several schedules**, give each registration a distinct `name`. The
name defaults to the job type name and also serves as the cluster-wide lock key, so it must be
unique across all registered jobs:

```csharp
spark.AddCron(cron => cron
    .AddJob<ReportJob>("0 8 * * *", "report-am")
    .AddJob<ReportJob>("0 18 * * *", "report-pm"));
```

Registering the same type twice without a distinct name throws an `InvalidOperationException`.

### AllFeatures

Apps that use `AddSparkFull` (the `MintPlayer.Spark.AllFeatures` package) get cron jobs wired
automatically — the AllFeatures generator detects `ISparkCronJob` implementers and emits the
`AddCronJobs()` call into `AddSparkFull`. No explicit registration is needed.

## How Scheduling Works

Each registered job runs its own loop inside the `SparkCronScheduler` background service:

1. Parse the cron expression once. A malformed expression is rejected at **registration** (`AddJob` throws), so it cannot reach the scheduler. A syntactically-valid but never-occurring expression (e.g. `0 0 30 2 *` — 30 February) is detected here, logged at warning level, and only *that* job's loop is disabled — other jobs are unaffected. The resolved precision (minute vs. second) and the first computed run time are logged at startup so a precision misread (a five- vs. six-field mix-up) is visible.
2. Compute the next occurrence after `DateTime.UtcNow`.
3. Wait until that time. Waits are capped at one hour and re-evaluated, which absorbs long delays and host clock corrections.
4. Attempt to **claim** the occurrence cluster-wide (see below).
5. If claimed, resolve the job from a fresh DI scope and run it. Non-concurrent jobs are awaited before the loop computes the next occurrence; concurrent jobs are dispatched fire-and-forget (bounded — see [Concurrency](#concurrency)).

## Multi-Node Execution

When the app is deployed to multiple instances, every node runs the same loops — but only one may
execute each occurrence. Coordination uses a single RavenDB compare-exchange key per job:

- Key: `cron/{jobName}` (where `jobName` is the registration name).
- Value: the latest claimed occurrence as an ISO-8601 UTC timestamp.

Before running, a node reads the key and compares (ordinal) the stored occurrence against the one it
is about to run. If the stored value is greater than or equal, the occurrence is already claimed and
the node skips it. Otherwise it attempts an atomic compare-and-swap with the read index; only the
node whose swap succeeds runs the job. Because compare-exchange is cluster-wide and strongly
consistent (Raft), exactly one node wins per occurrence. Using a single key per job (updated in
place) keeps the number of compare-exchange values bounded.

The stored value is treated as **untrusted**. A value that does not parse as a UTC timestamp, or
that is implausibly far in the future (more than a day ahead of the occurrence being claimed — a
legitimate node never claims an occurrence before its own clock reaches it), is logged at warning
level and reclaimed rather than honored. This prevents a corrupted or maliciously-written
`cron/{jobName}` value (e.g. a far-future timestamp) from silently and permanently suppressing the
job across the cluster.

## Error Handling

- A job that throws is logged and does **not** stop its schedule — the next occurrence is computed as normal.
- `OperationCanceledException` during shutdown is treated as a graceful stop, not an error.
- A failure to reach RavenDB while claiming an occurrence is logged and that single run is skipped; the loop continues.

## Security & Trust Model

Cron jobs run **outside the request authorization pipeline**. A job is resolved from a fresh DI
scope inside the background service, with:

- **No `ClaimsPrincipal` / `HttpContext`** — there is no "current user". An `IAccessControl` / `IPermissionService` check inside a job would evaluate against an empty (anonymous) principal, not a real user.
- **No tenant context** — the job sees whatever an `IAsyncDocumentSession` / `IDatabaseAccess` resolved from the root scope can see.

In other words, a cron job runs with full trust over the data store, by design — it is the right
place for privileged maintenance (cleanup, retention, aggregation), not for work that must be scoped
to an end user. **Treat registering a job — including jobs that ship inside referenced packages — as
a privileged operation**, the same way you would treat any code that runs with database-owner rights
on every node. If you need per-tenant or per-user behavior, pass the identity explicitly (e.g.
iterate tenants inside `RunAsync`) rather than relying on an ambient principal.

## No Catch-Up

Scheduling is computed in memory; there is no persisted run history. If every node is down across an
occurrence, that occurrence is simply skipped — there is no missed-run replay. (The compare-exchange
value records the last claimed occurrence, so adding catch-up later would be an additive change.)

## Requirements

- .NET 10.0+
- An `IDocumentStore` registered in the DI container (provided by `AddSpark()`).
- For source-generated registration: `MintPlayer.Spark.SourceGenerators`.

## Complete Example

See the following files for working implementations:

- `ISparkCronJob.cs` — the job contract (`CronSchedule`, `AllowConcurrentRuns`, `RunAsync`)
- `SparkCronScheduler.cs` — the background service: per-job loop + compare-exchange claim
- `ISparkCronBuilder.cs` — `AddJob` registration (default, override, multi-schedule)
- `SparkCronExtensions.cs` — `AddCron` DI registration
- `../MintPlayer.Spark.SourceGenerators/Generators/CronJobRegistrationGenerator.cs` — source generator for auto-registration
- `../Demo/DemoApp/DemoApp/Jobs/HeartbeatJob.cs` — explicit `AddCronJobs()` registration
- `../Demo/Fleet/Fleet/Jobs/FleetHeartbeatJob.cs` — zero-config registration via `AddSparkFull`
