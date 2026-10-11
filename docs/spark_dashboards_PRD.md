# PRD — Spark dashboards: administrator dashboards as live virtual PersistentObjects

**Status:** Proposed — spikes S10, S11 not yet run · **Date:** 2026-10-11 ·
**Origin:** split out of [coverage_commit_graph_PRD.md](coverage_commit_graph_PRD.md) §9, where it began as
"how do we watch the mirrors' disk usage". **Decisions:** D9 (superseded), D11–D14, D24 and D29 (S11 result: single-valued series) in that PRD's §0 table
are the decision log for this package; they are not repeated here. **Plan:** milestones M5a (package +
SeoTester migration) and M5b (CodeCoverage host metrics) in [coverage_commit_graph_plan.md](coverage_commit_graph_plan.md).
Ships in the **same pull request** as the commit graph (one-PR rule).

**Packages:** NuGet `MintPlayer.Spark.Dashboards` `11.0.0-preview.*`; npm `@mintplayer/ng-spark` new sub-entry
points in a 22.x **minor**. **Consumers in this PR:** CodeCoverage (host metrics, mirror totals) and SeoTester
(its operator dashboard migrates, D13).

> Sections below were written as §9 of the commit-graph PRD and keep their original numbering for
> cross-references (§9.1–§9.4).

## 9. Host metrics for administrators (D8, D9)

The mirrors add a disk cost that someone must be able to see. The owner asked two things: whether
the **host's** disk can be measured from inside a container, and whether Spark should ship a package
for administrator dashboards. Two investigators answered on 2026-10-11.

### 9.1 No dashboard package. A live virtual PersistentObject instead.

Spark already has every piece **[verified]**:
- **Virtual POs:** SeoTester's `OperatorDashboard` has no `clrType` and no documents. It is filled in
  `OnLoadAsync` (`apps/SeoTester/SeoTester/Admin/OperatorDashboard.cs:21-33`).
- **Push:** `/spark/live` is a multiplexed WebSocket.
  - It accepts PO subscriptions only for types with `"live": true` (`libs/spark/MintPlayer.Spark/Endpoints/Live/LiveConnection.cs:243-251`).
  - It re-evaluates through the normal read path on Raven changes, on `ISparkLiveNotifier.Changed(id)`
    (`MintPlayer.Spark.Abstractions/Live/ISparkLiveNotifier.cs:19`), or every 1 min by default
    (`SparkLiveOptions.cs:60`).
  - `spark-po-detail` follows live types automatically (`ng-spark/models/src/entity-type.ts:126-130`).
- **Renderers:** `spark-gauge`, `spark-threshold-bar`, `spark-trend-sparkline`
  (`ng-spark` `score-renderers/src/index.ts`).
- **Rights:**
  - A `Read/HostMetrics` right granted to a group in `security.json` hides the menu entry.
  - The live subscription refuses callers without that right, giving one answer for unknown,
    forbidden and not-live (`LiveConnection.cs:229`).

There is **no second consumer**. SeoTester's dashboard is deliberately app code
(`docs/seo_analyzer_PRD.md:676-682`, D28), SparkId has no admin dashboard, and the demos don't drive
framework design. A widget package would add an API with one user. That goes against the owner's
standing preferences (compose existing blocks, a lean API). **Revisit when a second app needs the
same metrics.** At that point, extract the parts both apps share (e.g. `formatBytes` into
`ng-spark/pipes`, and a library virtual PO under an `xyz:administrators` slot).

> **[superseded by D11–D14]** The block below described app code. HostMetrics is now a *provider* on
> the Spark dashboard package (plan M5a/M5b). The package owns the notifier, the read cache and the
> slot. The attributes and sources in §9.2 are unchanged.

**The design (app code in CodeCoverage):**
- `App_Data/Model/HostMetrics.json`: `"live": true`, no `clrType`, object id `hostmetrics/main`.
  Attributes are rendered with `spark-gauge` / `spark-threshold-bar`.
- `HostMetricsActions.OnLoadAsync` reads the sources in §9.2.
- A `BackgroundService` calls `ISparkLiveNotifier.Changed("hostmetrics/main")` every 15 s while
  anyone is subscribed. Each read is cached for 5 s, so that concurrent viewers trigger one statfs
  between them.
- A query over `RepositoryGraphState` with a size column answers "which repositories use the disk",
  with no new widget.
- **An `Administrators` group in CodeCoverage's `security.json`.** Today it has only "Anonymous
  visitors" and "Signed-in users" (`apps/CodeCoverage/.../App_Data/security.json:7-10`)
  **[verified]**. This is a production authorization change. The security posture must be
  regenerated (`--spark-synchronize-security`).

**Untested, so S10 covers it:** `"live": true` on a type with no `clrType`, and a notifier id with no
backing document.

### 9.2 Which numbers come from where

Owner, 2026-10-11: *"Some of the measurements can probably be the container measurements. But the
disk usage should probably be from the host."*

| Attribute | Source | Measures |
|---|---|---|
| Host disk: total / free / used % | `DriveInfo("/")` in the app container | **The host root filesystem** (`/dev/sdb1`, 150 GB). overlay2 reports its backing filesystem, `/var/lib/docker` on the host root **[cited]**. Exception: overlay2 on XFS with `pquota`, which this host does not appear to use **[inferred → S10]**. |
| Mirror count, total mirror size vs budget, largest mirrors, `DiskGuard` refusals | `RepositoryGraphState` documents (size measured per repository directory by the sidecar after each fetch, D8) | App data. The total is exact as of each repository's last fetch. |
| App CPU % | `System.Runtime` meter (`process.cpu.time`) over the sampling interval, or ResourceMonitoring `container.cpu.limit.utilization` | **The container** |
| App memory | cgroup v2 `memory.current` / `memory.max`; GC heap from `GC.GetGCMemoryInfo` | **The container** (`memory.current` includes page cache) |
| Host RAM / CPU | **Not shown** | A container sees only host totals in `/proc` without its own share. True host-wide figures need the host's `/proc` mounted in, a security cost not worth paying when Hetzner's console already graphs them. |

No `docker.sock`, no host `/proc`, and no writable host mount in the app.

### 9.3 Security

- The PO is readable by Administrators only.
- Without the right, the live subscription and the load both give Spark's single denial answer, so
  the type's existence doesn't leak.
- No mount paths, device names, exception text or environment values go into attribute values.
  Labels are fixed strings ("Host disk", "Mirror volume").
- The 5 s read cache plus the live hub's 500 ms throttle bound the statfs cost per viewer.

### 9.4 Spikes S10–S11

S11 was added during the grill.

| # | Question | Method | Pass |
|---|---|---|---|
| **S11** | Does dashboard history work on RavenDB time series (D14)? | One value sampled every 60 s on the embedded test server, keeping raw points for 1 day and hourly rollups for 30 days. Simulate 2 days of points through timestamps. Two sampler hosts run concurrently. | Raw points older than 1 day are gone and rollups exist. A 7-day history read returns **≤ 300** points in **< 100 ms**. **0** duplicate points from the two hosts (lease holds). **0** revisions created on the backing collection. |

### S10

| # | Question | Method | Pass |
|---|---|---|---|
| **S10** | Do the metrics measure what §9.2 claims, and does a live virtual PO stream them? | On the VPS: log `DriveInfo` for `/` from inside the app container and for the named volume from inside the sidecar, then compare both with `df -B1 /` on the host. Compare the sidecar's per-repository size with `du -sb` of that directory. Locally: a virtual `"live": true` PO with no `clrType`, notified every 15 s. | Totals and free bytes within **1%** of the host's `df` for both, and no 0 values. Per-repository size within **1%** of `du`. The detail page updates **≥ 3** times in 60 s without a reload. An anonymous user and a signed-in non-admin get Spark's standard denial on both load and live subscribe. |
