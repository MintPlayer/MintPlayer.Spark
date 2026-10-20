# Warehouse order picking (#521, D13): plan

This file is the order of work for [warehouse_picking_PRD.md](warehouse_picking_PRD.md).
- **Decisions** live in the PRD (§13); this file carries the checklists.
- **Evidence** lives in [warehouse_picking_research.md](warehouse_picking_research.md).
- **Scope:** these milestones are the content of commerce M20 ([commerce_plan.md](commerce_plan.md)) and land in the same PR.

## Rules for executing this plan

- **One PR** for everything: commerce, the reference-data app (K8), the warehouse plugin (D13), and every defect found along the way (commerce §6, including F12–F19).
- **Commit per milestone. Run no tests per milestone.**
  - Verify by reading code and type-checking.
  - The test sweep runs once, in WM13, inside commerce M22.
- **Logs go to the scratchpad raw** (`cmd > log 2>&1; echo EXIT: $?`), then are filtered. Never pipe the only copy.
- **Versions:**
  - bump the preview number of every touched `libs/` csproj;
  - new packages start at `11.0.0-preview.1`;
  - `@mintplayer/ng-spark` gets one minor bump for the whole PR;
  - majors never change (repo rule).
- **No backward compatibility** (K3): change core behaviour directly. Replication changes in place, and Fleet and HR are rewired without shims.
- **No string-spliced RQL.**
  - Values go through parameters, identifiers through `RqlIdentifier`.
  - SKUs, location codes and scanned barcodes are untrusted input.
- **Spikes record measured results in PRD §13** (numbers, file:line, red→green) before the milestone that depends on them starts.
- **Load spikes:**
  - run only in windows the owner chooses (commerce D24);
  - use RavenDB pinned to 3 cores, with no feature Community lacks;
  - no CPU burners while the owner works.
- **Device spikes** run on hardware the owner provides (Q-W21).
- **Licence realism.** Local runs and CI use the Developer licence, which has ETL and no index caps. So:
  - replication tests pin `Transport=Http` and run again with `Etl` (Q-W24);
  - index counts are asserted, never assumed (W19).
- **Ordering against commerce:**
  - WM1 lands before commerce M11's fulfilment work;
  - WM9's `scanning` and `offline` entry points land before commerce M15's scanner PWA.
- **Secrets.** The owner sets these in user-secrets; never print or paste them:
  - carrier sandbox keys;
  - the QZ Tray signing certificate;
  - print-agent credentials.
- **Short folder names** under `libs/` (D31). Aliases are permanent.
- **No personal data** in fixtures or seed data.
- **Angular component styles** go in a sibling `.scss` (`styleUrl`), never inline.

---

### WM0 — Spikes

Commerce S18's Community checklist gains the warehouse items (PRD §7). W19 and that checklist come first, because they constrain the index design.

**Stock and processes**
- [x] W1 stock engine invariants: 0 WI1–WI6 violations over ≥100 runs × 10k ops; p95 pick-confirm ≤150 ms; retry rates on hot balances and `StockLevel` recorded *(WM3 2026-10-20, at CI size per round 4: `tests/MintPlayer.Spark.Warehouse.Tests/Stock/StockInvariantGuardTests.cs`, seeded run of 6 concurrent workers × 120 commands on hot balances with 10 % re-sends; asserts WI1–WI4 and WI6 exactly and prints the retry rate. Written, not run yet: the result is recorded in PRD §13 at the WM13 sweep. ≥100 runs × 10k ops, the 3-node cluster and p95 ≤150 ms dropped, round 4)*
- [ ] W2 stock feed without oversell: 0 in ≥1,000 single-channel runs; the multi-channel bound recorded; red→green on refused cancellation and on refund of shipped units; ≤10 s p95 through the real HTTP transport
- [ ] W3 release, allocation and batching: 1,000 orders × 3 lines over 5,000 locations, with FEFO, in ≤30 s; 50-line route ≤50 ms
- [ ] W4 zone split and consolidation: 0 units lost or misplaced over ≥100 runs; no incomplete order released; no double-assigned slot

**Replication**
- [ ] W5 HTTP transport failure modes (consumer down, producer down, crash after commit, crash before 200, duplicates, reordering, poison, raw writes, exhausted budget): converges within one reconcile, 0 lost, 0 duplicated
- [ ] W6 ETL semantics on Developer, and WX13 licence detection on Developer and Community; round 3 adds: an ETL transform may set custom metadata (`Spark-Replica-Source-Change-Vector`, else the `SparkReplicaSource` body-field fallback, PRD §4.2.1); document expiration works on Community (tombstones); manifest projector = ETL output
- [ ] W7 F12–F19 red, then green; write-path refusal reaches the initiator
- [ ] W8 contract genericity: a Spark app without commerce packages; no `Commerce.Checkout` reference (covers commerce S20)

**Devices and scanning**
- [ ] W9 keyboard wedge: 100% correct over 1,000 scans per device; ≤150 ms p95; GS handled
- [ ] W10 AZERTY detection: 100% of 500 garbled scans flagged; 0 false flags on 500 correct scans and 200 typed entries
- [ ] W11 camera: ≥95% decoded within 1.5 s; 0 external requests; bundle impact recorded
- [ ] W12 GS1 corpus: ≥500 cases, C# = TypeScript = Syntax Engine
- [ ] W13 offline replay: exactly once; conflicts as exceptions; survives a restart; `persist()` granted; wipe on sign-out
- [ ] W14 live pick queue with 50 handhelds: costs recorded; assignment ≤1 s p95; 0 deliveries to the wrong user

**Printing and sign-in**
- [ ] W15 label printing paths: ≤3 s p95 via agent and bridges; printed codes scan back
- [ ] W16 agent exactly-once: one label per job id over 1,000 jobs with injected faults; PDF fallback within 10 s
- [ ] W17 PDF library, shared with commerce Q-O9; the result logged in both PRDs
- [ ] W18 shift sign-in: hybrid passkey sign-in succeeds ≥19/20 with median ≤20 s; idle sign-out within 5 s of the timeout

**Licence limits**
- [x] W19 index limits: read `/license/status` first (Community caps lifted 2024-05-22, #18564); budget checks (≤12 static, ≥2 left for the host, refusal behaviour, WX13 warning fixture) only if a cap still applies; always: 0 auto indexes from library pages *(WM3 2026-10-20: caps lifted per #18564, `/license/status` not read on a Community server here (Developer licence locally), so no budget checks; `Indexes/IndexBudgetTests.cs` asserts the core declares 4 indexes (Documents gets its maps from WM4–WM6) and that every library read path creates 0 auto indexes. Written, not run yet)*

### WM1 — Replication redesign (WX1–WX4, WX13; WD13–WD18; F12–F19)

Design: PRD §4.2 (rewritten 2026-10-20, owner round 3). Paths are under `libs/replication/MintPlayer.Spark.Replication/` unless stated otherwise. `@e0c542f9` marks a file taken from the last good commit. Tests are written in order with the code and **run once in WM13**; "red" below names what the test proves fails today (or on the WIP), "green" what makes it pass.

**Owner's final decisions (2026-10-20, PRD §11 and §13 round 3)** fold into the lines below: Q-W27 (own persistent object), Q-W28 (second edit refused locally), Q-W29 (last approved shape keeps flowing), `SourceModule` optional with `Spark:Replication:Sources` fallback, WD19 (per-property compare-and-set for write-back), WD20 (synchronous first, deferred outcomes durable + live + mail, never applied locally), WD21 (the "Replication requests" program unit, wired into HR and Fleet), the message queue for everything durable.
- [x] Core: `SparkConflictException` (public base of `SparkConcurrencyException`; endpoints catch it; 409 body gains `attributes`), `ISyncActionHandler` with `SyncEdit.BaseValues` and `ApplyDeleteAsync`.
- [x] ng-spark `@mintplayer/ng-spark/replication`: `provideSparkReplication()` (pending title badge), `<spark-pending-changes-link>` (live count of "My pending changes", red on a pushed failure), spec; wired into the HR and Fleet shells.

**0. Starting point (do first)**
- [x] Rewind `libs/replication/**` and `tests/MintPlayer.Spark.Tests/Replication/**` to e0c542f9 (allowed; HEAD does not compile there: `ReplicaCapture.cs` uses a missing `ReplicaPushMessage`; `SyncApply.cs`, `SyncActionSubscriptionWorker.cs`, both interceptors and `SparkReplicationExtensions.cs` still use deleted types). Then re-apply the WIP files marked **keep** below from b4c218eb (`git show b4c218eb:<path>`). *Done 2026-10-20 by rewriting the files (no git operation): the obsolete runtime and tests deleted, WIP keepers reworked in place.*
- [x] `stash@{0}` (30597360) holds the rest of the rejected half-refactor (SyncApply, extensions, worker, tests, HR/Fleet). Salvage nothing from it without reading it; the lead drops it after WM1 (agents don't touch git state). *Not applied; nothing salvaged.*
- [x] WIP triage (b4c218eb):

  | File | Verdict | Why / change |
  |---|---|---|
  | `libs/spark/MintPlayer.Spark.Abstractions/Licensing/ISparkLicenseCapabilities.cs`, `libs/spark/MintPlayer.Spark/Services/SparkLicenseCapabilities.cs` | keep | `/license/status` → `HasRavenEtl`, cached, unknown → null (`SparkLicenseCapabilities.cs:24-47,99,121`); replaces `RavenEtlLicence @e0c542f9` |
  | `Services/ReplicationTransportResolver.cs`, the `ReplicationTransport` enum in `Configuration/ReplicationTransportOptions.cs` | keep | Auto/Http/Etl over the licence (`:33-65`); drop its "every contract fits ETL" check (every generated manifest fits) |
  | `ReplicationTransportOptions` Consume/Registry/Push/Reconcile/WriteBack | rework | keep the push/write-back retry knobs (mapped onto the `spark-replication` queue) and Reconcile; replace Consume with `Sources:{TargetCollection}` |
  | `libs/spark/MintPlayer.Spark.Abstractions/ISparkReplicaTypes.cs`, `DatabaseAccess.EnsureNotReplica` | keep | fix the doc comment (`[Replica]` → `[Replicated]`) |
  | `libs/spark/MintPlayer.Spark.Abstractions/ISyncActionHandler.cs` (`ApplyEditAsync`, `SyncEdit`, `SyncEditOutcome`) | rework | add `BaseEtag` and a delete kind to `SyncEdit` |
  | `libs/spark/MintPlayer.Spark/Services/SyncActionHandler.cs` | rework | use `SyncEdit.BaseEtag`, not the stored vector (`:39-47,60`); keep `ReplacedSave` mapping and `element.Clone()` (`:287`); the working tree already removes HEAD's stray `}` |
  | Deletion of `ISyncActionInterceptor.cs`; `SparkSystemContext` module origin; `Services/AttributeRights.cs` (modules like people) | keep | F17 |
  | `Endpoints/Groups.cs` (`SparkReplicasGroup`, `ModuleCaller.AuthenticateAsync`) | keep | |
  | `Models/ReplicaApply.cs` | rework | items carry `sourceChangeVector` (drop `ReplicationVersion`), body carries `hash` |
  | `Models/ReplicaSubscription.cs`, `Endpoints/ReplicaSubscribe.cs`, `Services/ReplicaSubscriptions.cs` | rework | becomes register: items carry manifest/script/hash; the owner re-renders, stores `SparkReplicationSubscription`, gates approval |
  | `Models/ReplicationDocuments.cs` | rework | keep `ReplicationMetadata` (source change vector, source module), `ReplicaTombstone`, `ReplicaDirtyMarker`, `SyncInboxEntry`; drop `ReplicaVersion` and the contract-keyed `ReplicaSourceState` |
  | `ReplicaChanged.cs` (`IReplicaChanged<T>`, `IReplicaPublisher`, `ISyncActionRefusalObserver`) | keep | |
  | `Services/ReplicaCapture.cs` | rework | drop `ReplicationVersionStamp` (WD18); keep the after-commit interceptor, fan-out, dirty set; add `ReplicaPushMessage` + recipient |
  | `Services/ReplicaSourceReader.cs` | rework | project with the manifest projector, not a compiled lambda |
  | `Services/ReplicaJson.cs`, `Services/ModuleGrants.cs` | keep | grants check `Replicate/{SourceCollection}` (the WIP checked `Replicate/{contract}`, which no `security.json` grants) |
  | `Services/ReplicaCatalog.cs` | rework | read `[assembly: SparkReplicaTypes]` + the generated constants |
  | `Services/EtlTaskManager.cs` (`RemoveAsync`, licence via capabilities) | keep | deploy takes approved transforms only |
  | `Models/SyncAction.cs`, `Models/SparkSyncAction.cs`, `Models/ESyncActionStatus.cs` (`Refused`) | keep + `BaseEtag` | |
  | `Services/EtlTransformGenerator.cs` | drop | moves into the generator (identifier rules kept) |
  | `ReplicaAttribute.cs`, `ReplicationContractAttribute.cs`, `ReplicationContract.cs`, `Services/ReplicationContractCatalog.cs`, `ReplicaContractCollector.cs @e0c542f9`, `SparkReplicationOptions.Publish` | drop | WD13 |
  | `apps/HR/HR.Library/Contracts/*V1.cs`, `apps/Fleet/Fleet.Library/Contracts/CarV1.cs` | drop | WD13 |
  | `EtlDeploy.cs`, `EtlScriptDeployment*`, `RavenEtlLicence.cs` (all `@e0c542f9`) | drop | replaced by register/deploy; keep EtlDeploy's mTLS, grant and F19 code (`:63-79,101,115-129`) in the register endpoint |

**1. Declaration and generator (WD13, WD14)**
- [x] `ReplicatedAttribute`: `SourceCollection` (required), `SourceModule?`, `OriginalType?`, `WriteBack`; remove `Contract`/`Version`. `ReplicatedFromAttribute(path)`. `IReplicatedEntity` (static abstract `ReplicationSourceCollection`, `ReplicationTargetCollection`, `ReplicationManifest`, `ReplicationScript`, `ReplicationHash`). `SparkReplicaTypesAttribute` (assembly). *As built: also `TargetCollection?` (generated default, verified at startup); `IReplicatedEntity` is a marker, the runtime reads the generated literals.*
- [x] Shared renderer `ReplicaScriptRenderer.cs` (manifest → script, canonical manifest JSON, SHA-256 hash with own hex; netstandard2.0-safe), compiled into `Replication.Abstractions` and linked into the generator.
- [x] `ReplicatedGenerator` in `libs/source_generators/MintPlayer.Spark.LibraryGenerators/` (model/producer/reporter/rules split as `ValueObjectKeyGenerator`), emitting the constants and the assembly attribute.
- [x] Diagnostics SPARK065–SPARK068 + the `partial` code fix; rows in `docs/diagnostics.md` (also add the missing SPARK064 row).
- [x] Replica libraries reference `MintPlayer.Spark.LibraryGenerators` as an analyzer (`OutputItemType="Analyzer" ReferenceOutputAssembly="false"`); `Fulfilment.Contracts` too (WM2). *HR.Library and Fleet.Library already did; `Fulfilment.Contracts` is WM2.*

**2. Registration and the approval gate (WD15, WD16)**
- [x] Consumer: set hash per owner, `SparkReplicaRegistrations/{owner}` (last acknowledged hashes), `ReplicaRegistrationMessage` + recipient (retry vs non-retryable as `EtlScriptDeploymentRecipient.cs:43-48 @e0c542f9`). *`ReplicaRegistrationState`, `ReplicaRegistrationRecipient`.*
- [x] Owner: `POST /spark/replicas/register` — mTLS, `Replicate/{SourceCollection}`, attribute read rights per manifest field, re-render check, upsert, unchanged/pending/approved/refused per item, withdraw what's missing.
- [x] `SparkReplicationSubscription` persistent object (App_Data model layer, query, detail with pending-vs-approved diff), custom actions `ApproveReplication`/`RejectReplication` (+ `Suspend` on grant loss), rights in the library's security layer; auto-approve iff `IHostEnvironment.IsDevelopment()`. *As built (WD21): the persistent object is `SparkReplicationRequest` (program unit "Replication requests", slot `replication:approvers`), `SparkReplicationSubscription` is internal delivery state; suspension is automatic on grant loss (no `Suspend` action).*
- [x] `ReplicaDeployMessage` recipient: transport via resolver; ETL deploy/remove (skip when `(transport, destination, hash set)` unchanged; destination from the registry); HTTP activate + initial reconcile. `Transport=Etl` without licence refuses startup.

**3. HTTP path (WD17)**
- [x] Core `SparkChangeVector.Compare` (`libs/spark/MintPlayer.Spark.Abstractions`).
- [x] Owner: manifest projector over raw JSON; capture interceptor (`IAfterSaveCommitted`/`IAfterDeleteCommitted`) → `ReplicaPushMessage` per consumer on `spark-replication`; push recipient (load current, project, post to registry `AppUrl`; 5xx → `RetryAfterException`, 4xx → `NonRetryableException`); the queue's ladder 1 m/5 m/30 m/2 h with 48 h expiry as library default; `IReplicaPublisher.MarkChanged`; dirty-set flush job.
- [x] Consumer: `POST /spark/replicas/apply` (source-module check, hash = current or last approved, change-vector guard, tombstones with 7-day expiry, metadata stamp, durable `IReplicaChanged<T>`), `GET /spark/replicas/digest`.
- [x] Owner reconcile cron job (`Spark:Replication:Reconcile`, default hourly) merge-joining owner stream and consumer digest; collection name through `RqlIdentifier.Collection`. *As built: the consumer digest is held in memory, not merge-joined; source collection read through a collection query, never RQL text.*

**4. Write path (WD18, F12–F15, F17, F18)**
- [x] Consumer: `ISaveReplacement` + `IDeleteReplacement` for `WriteBack` replica types → `SparkSyncAction` (+ `BaseEtag` from the replica's source change vector, op id, initiator, structured values) + `SyncActionDispatchMessage` in the same session; refuse a second edit while one is pending (Q-W28); refuse edits of non-`WriteBack` replicas. *As built per WD19/WD20 (owner, 2026-10-20): per-property bases instead of `BaseEtag`; the save calls the owner synchronously (5 s) and queues only when it gets no answer.*
- [x] Owner `POST /spark/sync/apply`: one action per request, op-id replay from `SyncInboxEntry` (stored via `ReplacedSave.Alongside`), `ApplyEditAsync` with `BaseEtag` as `Module:{consumer}` with attribute rights; 200/409/403/422, never 207. *As built: `ApplyEditAsync` does the per-property compare-and-set (WD19).*
- [x] Dispatch recipient: applied → Completed; conflict/refused → Refused + `ISyncActionRefusalObserver` + `ISparkNotifier` when present; 5xx → retry ladder; dead-letter → Failed; `RetrySyncAction` custom action. *As built: statuses Completed/Conflict/Refused/GaveUp; `ISyncActionOutcomeObserver` replaces `ISyncActionRefusalObserver`; the initiator is told through the durable `SparkSyncAction` ("My pending changes"), the private live topic `replication:outcomes`, and mail on GaveUp (WD20).*
- [x] Delete `Workers/SyncActionSubscriptionWorker.cs`, `Services/SyncActionRetrySweeper.cs`, `Indexes/SparkSyncActions_ByStatus.cs`, `Services/ReplicationInterceptor.cs`, `Services/SyncActionInterceptor.cs`.

**5. Security (WX4)**
- [x] Grant/attribute-right re-check at approval, deploy and every reconcile; loss suspends and removes delivery.
- [x] Operator-managed read-only registry mode (`Services/ModuleRegistrationService.cs:33-35,58-68`; `Models/ModuleInformation.cs:18,30`).

**6. Migrate Fleet and HR**
- [x] `apps/HR/HR.Library/Replicated/Car.cs`, `apps/Fleet/Fleet.Library/Replicated/Person.cs`, `Company.cs`: `partial`, `[Replicated(SourceCollection = …, SourceModule = …, WriteBack = …)]`, `[ReplicatedFrom]` where a name differs; both libraries reference `LibraryGenerators`.
- [x] Delete the `Contracts/` folders; remove `Publish`/`AssembliesToScan` from `apps/HR/HR/Program.cs:86-92` and `apps/Fleet/Fleet/Program.cs:56-60`; `appsettings.json` `Replication` sections (`:19` in both) gain `Transport: Http` (Q-W24).
- [x] `security.json`: keep `Replicate/Cars` (`apps/Fleet/Fleet/App_Data/security.json:109`), `Replicate/Companies`/`Replicate/People` (`apps/HR/HR/App_Data/security.json:78,126`); add `Execute/ApproveReplication` for administrators. *As built: `bindings: { "replication:approvers": [ "Administrators" ] }` instead of an app-level right (the library ships the rights).*
- [x] Check `Fleet/Actions/CompanyActions.cs`, `FleetContext.cs`, `Entities/Car.cs:90`, `App_Data/Model/{Car,Company,Person}.json`; `Fleet.Tests/McpOutputBudgetTests.cs:138-140` stores replica types raw (now refused by `EnsureNotReplica` only through `DatabaseAccess`; keep the raw store).
- [ ] Regenerate `modelHashes.json`, `securityPosture.txt`, and the route snapshots (`apps/Fleet/Fleet.Tests/RouteTableSnapshotTests.cs`, `apps/HR/HR.Tests/RouteTableSnapshotTests.cs`) in WM13. *Partly 2026-10-20: HR and Fleet `modelHashes.json` and `securityPosture.txt` re-synchronized (`--spark-verify-model/-security/-contracts` exit 0); the route snapshots are left for WM13.*
- [x] Rewrite `docs/guide-cross-module-sync.md` (F18 drift) and `docs/guide-replication-mtls.md` (WM12 may finish the prose). *Rewritten 2026-10-20 for the as-built design; the mTLS guide got the endpoint list.*

**7. Tests**

Generator (`tests/MintPlayer.Spark.SourceGenerators.Tests`, `GeneratorHarness.Run(..., generatorAssemblyName: "MintPlayer.Spark.LibraryGenerators")`, pattern `Generators/ValueObjectKeyGeneratorTests.cs:27-68,158-195`):
- [x] `ReplicatedGeneratorTests.Emits_constants_for_partial_class` — red: nothing generated; green: the five constants and the assembly attribute, compiling without diagnostics.
- [x] `…Maps_ReplicatedFrom_paths_null_safely` — nested path renders `this.Owner == null ? null : this.Owner.Name`-style access; manifest lists `[OwnerName, Owner.Name]`.
- [x] `…Skips_Id_and_getter_only_members`.
- [x] `…Hash_is_stable_and_changes_with_any_field` — same input → same hash across runs; rename one property → different hash.
- [x] `…Hash_equals_runtime_renderer` — the generated `ReplicationHash` equals `ReplicaScriptRenderer.Hash(manifest)` at runtime (the owner's re-render check depends on it).
- [x] `…SPARK065_when_not_partial` (+ negative case) and the code fix test (`CodeFixHarness`).
- [x] `…SPARK066_invalid_identifier_or_missing_SourceCollection`, `…SPARK067_path_missing_on_OriginalType`, `…SPARK068_handwritten_member_collides`.
- [x] Snapshot `SourceGeneratorSnapshots.Replicated_car` (`Snapshots/SourceGeneratorSnapshots.cs:20-77`).

Runtime (`tests/MintPlayer.Spark.Tests/Replication`, `SparkTestDriver` + `SparkEndpointFactory` with `environment:` (`libs/testing/MintPlayer.Spark.Testing/SparkEndpointFactory.cs:121,165`); two hosts/databases for owner and consumer as in `EtlTaskManagerDeploymentTests @e0c542f9`):
- [x] `ReplicaRegisterEndpointTests` (reworked from `EtlDeployEndpointTests`): no cert → 401, wrong thumbprint → 403, no `Replicate/{SourceCollection}` → 403, field without attribute read right → refused by name, script ≠ re-render → 400 (F16: a posted `loadDocument` script is never stored), destination always the registry entry (F19 red at the pre-43d492df shape, kept green). *As built: `ReplicaEndpointGateTests` (401/403) + `ReplicationApprovalTests` (grant, read right by name, re-render 400, unknown collection 404); the destination is never in the request (`SparkReplicationExtensionsTests`).*
- [x] `ReplicationApprovalTests`: Development owner auto-approves; "Testing"/Production owner leaves Pending; `ApproveReplication` without the right → refused; approve → deploy message; same hash re-posted → `unchanged`, no second deploy (WD15; red: today every start redeploys); missing item → Withdrawn and transform removed (F18).
- [x] `ReplicaRegistrationRecipientTests` (reworked from `EtlScriptDeploymentRecipientTests`): unchanged acknowledged set hash → no post; owner unregistered → retry; 403 → non-retryable.
- [x] `EtlTaskManagerTests` / `EtlTaskManagerDeploymentTests` (kept, adapted): unlicensed → refusal before any ETL call; approved transform reaches the consumer database with `Spark-Replica-Source-Change-Vector` metadata (W6); `RemoveAsync` removes it; registry URL change redeploys. *Registry-URL redeploy: `ReplicaDeployRegistryTests` (ReplicationEndToEndTests.cs), through the deploy recipient's `(transport, destination, hash, scope)` comparison.*
- [x] `ReplicationTransportResolverTests`: Http pinned; Etl pinned without licence throws at startup; Auto with unknown licence → Http.
- [x] `SparkChangeVectorTests`: Older/Equal/Newer/Concurrent over single- and multi-entry vectors.
- [x] `ManifestProjectorEquivalenceTests`: the C# projector and the ETL script produce identical replicas for the same source documents (nulls, nested, arrays, `Color?`). *As built: `ManifestProjectorTests` (unit) + `EtlTaskManagerDeploymentTests.The_ETL_load_carries_the_source_stamp_and_equals_the_manifest_projection` (Developer licence, W6).*
- [x] `ReplicaApplyEndpointTests`: newer applies and stamps metadata; equal/older no-op 200; delete leaves a tombstone and an older push doesn't resurrect; foreign source module refused; hash neither current nor last approved → 409; `IReplicaChanged<T>` enqueued in the same commit. *As built: `ReplicaApplierTests`.*
- [x] `ReplicaPushTests`: owner save → after-commit work → push message per consumer → replica updated; consumer down → `RetryAfterException` and later delivery; 4xx → dead-letter; a burst of 5 saves → replica ends at the latest state. *As built: the push recipient (projection, tombstone, ladder, give-up, 4xx, dropped subscription); the interceptor → message hop is `ReplicaCaptureTests` (after-commit work stored in the write's commit, one push per HTTP consumer) and the 5-save burst is `ReplicaPushBurstTests` (owner and consumer databases, out-of-order delivery), both in ReplicationEndToEndTests.cs.*
- [x] `ReplicaReconcileTests`: a raw owner write (no interceptor) is repaired by one reconcile; an extra consumer replica gets a tombstone; initial load after approval fills an empty consumer.
- [x] `SyncActionCaptureTests` (reworked from `SyncActionInterceptorTests`): the edit stores the SyncAction **and** its outbox message in the same commit and leaves the replica untouched (F13; red: today `SyncActionInterceptor.cs:161` saves in a new session after the local save); `BaseEtag` = the replica's source change vector; initiator stamped (#271); nested values stay JSON (F18; red: `SyncActionInterceptor.cs:184`); second edit while pending refused (Q-W28); non-`WriteBack` replica edit refused. *As built: `ReplicaWriteBackTests` (synchronous applied/conflict/refused, queued in the same commit, Q-W28, nested field, no create, pending badge).*
- [x] `SyncApplyEndpointTests` (reworked): **stale edit** — owner changed after the replica was built → 409 `conflict(changed)` and the owner document unchanged (WD18; red: `SyncActionHandler.cs:39-47,60` overwrites); deleted at owner → `conflict(deleted)`; module without write right on an attribute → `refused(unwritable)` (F17; red: silently dropped); op-id replay returns the first outcome, no false conflict (F15); never 207 (F12). *As built: the compare-and-set red→green tests are `Services/SyncActionHandlerIntegrationTests`; the endpoint statuses and op-id replay are `Replication/SyncApplyEndpointTests`.*
- [x] `SyncActionDispatchTests` (replaces `SyncActionSubscriptionWorkerE2ETests` and `SyncActionRetrySweeperTests`): applied → Completed; conflict → Refused + observer + notifier called with the initiator (F12; red: 207 recorded Completed, `Workers/SyncActionSubscriptionWorker.cs:75-85`); owner down → retried past 8 minutes on the ladder (F14; red: `RetryNumerator` gives up after ~5–8 min); expired → Failed; `RetrySyncAction` re-drives.
- [x] `SparkReplicationExtensionsTests` / `SparkBuilderReplicationExtensionsTests` (reworked): no subscription worker, no sweeper registered; replication creates **0** RavenDB subscriptions (assert the database's subscription count with messaging + replication = 1). *As built: asserts no hosted service is registered; the database subscription count (messaging + replication = 1) is `ReplicationSubscriptionCountTests`.*
- [x] `ReplicaSourcesConfigurationTests`: a replica without `SourceModule` takes its owners from `Spark:Replication:Sources:{TargetCollection}` (bound through `AddReplication`), applies their pushes, refuses other modules, and is a startup problem when unbound; a declared `SourceModule` wins. *Added 2026-10-20 (ReplicationEndToEndTests.cs).*
- [x] Approvers told on arrival (found 2026-10-20: a pending request notified nobody): `IReplicationRequestObserver` (Replication.Abstractions, beside `ISyncActionOutcomeObserver`) is called once per newly pending request after its commit, with `ReplicationRequestArrived.ApproversSlot` = `replication:approvers`; a Development auto-approval tells nobody; a failing observer is logged. Tests in `ReplicationApprovalTests`. *Default delivery added 2026-10-20 (`ReplicationRequestNotifier`): the slot's members (users holding a bound group, via core `ISparkGroupMembers`, implemented by Authorization) get a private `replication:requests` frame and, with MailManager, the `SparkReplication/ReplicationRequested` mail; the pending request is the durable record on "Replication requests". The observer stays the extension point.*
- [x] Kept unchanged: `ModuleCertificateValidatorTests`, `ModuleRegistrationServiceTests`, `ReplicationHttpClientProviderTests`, `SparkReplicationOptionsBindingTests`. *`ModuleRegistrationServiceTests` gained the read-only registry test.*
- [x] Dropped: `EtlTransformGeneratorTests` (moved to the generator tests), `ReplicaContractCollectorTests`, `SyncActionGenericTests`.
- [ ] E2E `tests/MintPlayer.Spark.E2E.Tests/Security/CrossModuleSyncTests.cs`: replace the 207 expectation (`:116-134`) with Fleet ↔ HR end to end under `Transport=Http`: edit a replica in HR → Fleet applies → HR's replica updated by the push; stale edit → refusal shown to the initiator; once more under `Etl` on CI (Q-W24). Add the missing end-to-end mTLS test (`:24-31`).

### WM2 — Shared foundations

- [x] `MintPlayer.Spark.Fulfilment.Contracts` (`libs/fulfilment/contracts/`): v1 of `fulfilment-orders`, `catalog-items`, `expected-returns`, `fulfilment-status`, `shipments`, `stock-levels` and `return-receipts` (PRD §4.3); `expected-receipts` too if Q-W9 is accepted *As built 2026-10-20: one `[Replicated]` class per contract (8, `expected-receipts` included: Q-W9 accepted), stored by the owner and replicated to the host that binds `Spark:Replication:Sources:{collection}`; new `[assembly: SparkReplicaContracts]` makes unbound classes inert (PRD §4.3 "Contracts as replica classes"). Per-consumer row filter for a multi-shop warehouse: closed in WM3 (PRD §4.2.3 "Row filter").*
- [x] `MintPlayer.Spark.Gs1` (`libs/gs1/`):
  - check digits (GTIN, SSCC, location)
  - SSCC allocation (company prefix, extension digit, compare-exchange sequence) *(2026-10-20: core `ISparkSequences`, `libs/spark/MintPlayer.Spark/Sequences/SparkSequences.cs`, bound through `DelegateSsccSerialSource`)*
  - AI element strings and Digital Link parsing from GS1's AI dataset, kept as data (Q-W16)
  - the W12 corpus as tests
- [x] `MintPlayer.Spark.Warehouse.Abstractions` (`libs/warehouse/abstractions/`): `IAllocationRule`, `IPutawayRule`, `IPickStrategy`, `IPickRouter`, `ICartonizer`, `IBarcodeResolutionStep`, `ISiteRouter`
- [x] WX8 `Quantity`, on X4's value-type infrastructure (model types stop at `decimal`, `libs/spark/MintPlayer.Spark.Abstractions/Model/SparkModelShape.cs:191-216`) *(verified 2026-10-20: model shape, converters, mapper, sort/filter companions, `[GenerateIndex]` companions, renderers, guide)*
- [x] WX9 hierarchical reference picker: `loadChildren` (flat today, `libs/node_packages/ng-spark/po-form/src/reference-query-tree-select-provider.ts:35-38`), plus materialized-path helpers *(2026-10-20: also single references with a `referenceTree` pick from the lazy tree in `spark-po-form`)*
- [x] WX11 generic CSV import and export, shared with the commerce seat CSV (D32) *(reviewed 2026-10-20: guard round-trip fixed; open: request-size limit on import, no `docs/guide-csv.md`)*
- [x] WX14 zone-aware schedules in core cron, with DST gaps and folds defined (Q-W1). Today it is UTC only (`libs/cron/MintPlayer.Spark.Cron/SparkCronScheduler.cs:76,101-102,209`; `CronScheduleParser.cs:21`) *(reviewed 2026-10-20: gap/fold per Q-W1 in core; unknown `Spark:Cron:Jobs` keys now fail validation)*
- [x] WX16 composable multi-map indexes, if Q-W26 is accepted *(Q-W26 accepted; delivered by commerce X19, library-declared `[SparkIndexMap]` maps composed into one multi-map index by the app generator)*

### WM3 — Warehouse core

**Package and master data**
- [x] `libs/warehouse/core/`, alias `warehouse`: `spark.AddWarehouse()` binds `Spark:Warehouse`, with startup validation as in PRD §4.13 *(WM3: `MintPlayer.Spark.Warehouse` 11.0.0-preview.1, `SparkWarehouseOptionsValidator` + ValidateOnStart)*
- [x] Sites (IANA zone; address from reference data) and zones
- [x] Locations: separate documents, materialized path, walk sequence, check digits, types, geometry fields. Deactivated, never deleted *(`IWarehouseLocations`; delete refused once `HasHistory`)*

**Owners and items**
- [x] Owner links (Q-W23) *(`Owners/WarehouseOwnerLinks.cs`; also replication's row filter, PRD §4.2.3; an external owner id links to one module only)*
- [x] `IOrganizationOwned` on every owner-bearing document (`libs/organizations/MintPlayer.Spark.Organizations/IOrganizationOwned.cs:25-29`)
- [x] Explicit owner filters in every library query; raw session queries are unfiltered (`IOrganizationOwned.cs:21-22`) *(`IWarehouseOwnerScope`; staff through the `AllOrganizations/{Type}` right on the staff and supervisor slots, lead decision 2026-10-20, PRD §13 WM3)*
- [x] Items:
  - consume `catalog-items`
  - `WarehouseItem` extensions, with (owner, SKU) as a unique key
  - warehouse-owned items
  - pre-items and linking (Q-W12)
  - tombstoned replicas keep their stock
- [x] Barcode registry, with `barcodes/{code}/…` ids for prefix lookups, plus the resolution pipeline (`IBarcodeResolutionStep`)
  - several matches → the user chooses
  - unknown → the link-to-item right

**Stock engine**
- [x] Documents: `StockLot` (`DateOnly` expiry, `SparkModelShape.cs:206`), `StockBalance` per (location, item, lot), `StockMovement` keyed by op id
- [x] Moves and adjustments as custom actions in one session, with OCC retry (`libs/settings/MintPlayer.Spark.Settings/Quotas/SparkQuotas.cs:46-77`) *(`WarehouseMoveStock`/`WarehouseAdjustStock` on the never-stored `WarehouseStockCommand` form)*
- [x] Cluster-wide mode (`libs/authorization/MintPlayer.Spark.Authorization/Identity/UserStore.cs:203-209`) *(`Spark:Warehouse:Stock:ClusterWide`: a raw cluster-wide session, atomic guards)*
- [x] Atomic first-put-away create (commerce X3)
- [x] Reason codes; stock read-only in the UI
- [x] `StockLevel` per (owner, item), updated in the movement's session (WI6)
- [x] `stock-levels` publication: dirty-set flush; per-channel projection filtered by owner links *(per owner link as `FulfilmentStockLevel` in the movement's session; dirty set registered by the library, lead decision 2026-10-20)*
- [x] Min/max replenishment: daily sweep plus every short pick; fixed pick faces only; lots chosen by FEFO
- [x] Round 5 (commerce GA7): Recalled lots blocked at once (allocations re-allocated or short-picked); owner-scoped, audited **recall trace** report lot → movements → shipment lines → fulfilment orders (+ stock still on hand), CSV export; the shop resolves buyers and sends the notice; item + date-range trace for untracked items *(core side: block + trace + `ILotStatusObserver`; the re-allocation/short-pick is Outbound's, WM5)*
- [x] Round 5 (commerce GC9): dangerous-goods data on `WarehouseItem` (UN number, class, packing group, lithium-battery kind, limited quantity); zone mixing rules can segregate them *(`DangerousGoodsData`, `ZoneMixing.Allows`)*

**Work, indexes, guard**
- [x] Task envelope: `WarehouseTask` (one collection for every kind) and `WarehouseTaskGroup`; assignment, leases (commerce X12), op ids *(leases on the task document under OCC, not X12 claims; tote claims come with batches in WM5)*
- [x] Productivity events
- [x] Static indexes per the PRD §4.5 budget: `Warehouse_Stock`, `Warehouse_LedgerTotals`, `Warehouse_Work`, `Warehouse_Catalog`, `Warehouse_Documents` *(declared with `[SparkIndexMap]` (X19), not `AddIndexesFrom`; LedgerTotals is a map summed in code, X19 has no reduce)*
  - deployed through `AddIndexesFrom` (precedent: `libs/moderation/MintPlayer.Spark.Moderation/SparkModerationExtensions.cs:79`)
  - every library query bound to one of them
- [x] A scaled-down concurrency guard for WI1–WI6, kept in the suite (it runs in WM13) *(= the W1 test above)*

### WM4 — Inbound

- [ ] `libs/warehouse/inbound/`, alias `warehouse-inbound`
- [ ] Expected (ASN) and blind receipts:
  - GS1 lot, expiry and count capture
  - discrepancies
  - receipt sources per Q-W9
- [ ] Put-away:
  - rules in order: fixed → same lot → nearest empty (by walk sequence)
  - custom rules through `IPutawayRule`
  - Putaway tasks confirmed by scanning
- [ ] Returns:
  - consume `expected-returns`
  - receipt (scanned or blind) and inspection
  - disposition: Restock, Quarantine, Scrap or Return to vendor
  - round 5 (commerce GA8): the return reason per line (`Withdrawal`, `NonConformity`, `WrongItem`, `DamagedInTransit`, `SellerAllowed`, `Other`) sets the default disposition; inspection overrides it with a reason code
  - publish `return-receipts`
- [ ] Contribute this package's maps to `Warehouse_Documents` (Q-W26)

### WM5 — Outbound

**Intake and allocation**
- [ ] `libs/warehouse/outbound/`, alias `warehouse-outbound`
- [ ] Intake: `fulfilment-orders` → `OutboundOrder` (`outbound/{fulfilmentOrderId}`); accept or reject, with reasons; site routing (rules, else the owner's default site)
- [ ] Release:
  - immediate, by waves (cut-offs per site, WX14), or manual
  - the shop's holds respected; `ShipComplete` honoured
- [ ] Allocation:
  - FIFO, FEFO or closest, per owner and per item (`IAllocationRule`)
  - candidates from `Warehouse_Stock`; claims made on loaded balances

**Picking**
- [ ] Pick work:
  - discrete, batch, cluster (totes, unique through X12) and zone
  - waves as grouping rules
  - walk-sequence routing (`IPickRouter`)
- [ ] Scan-to-confirm:
  - order: location or check digits → item (expected one pre-selected) → lot check (Q-W6) → quantity → tote
  - batch leases, with supervisor takeover
- [ ] Short picks:
  - zero the remaining balance, with a reason
  - re-allocate
  - backorder per the shop's policy
  - optionally trigger a count and a replenishment
- [ ] Backordered lines re-allocated when stock arrives

**After picking**
- [ ] Cancellation handshake per state (PRD §4.3):
  - `Cancellation:AfterPick` (Q-W2)
  - put-back tasks
  - publish `fulfilment-status`
- [ ] Consolidation:
  - put wall and consolidation station
  - release to packing only when complete, unless partial shipment is allowed
  - the W4 bookkeeping
- [ ] Contribute this package's maps to `Warehouse_Documents` (Q-W26)

### WM6 — Printing

- [ ] `libs/printing/core/`, alias `printing`:
  - printers
  - jobs with idempotent ids and states
  - routing
  - delivery to the PDF print dialog
  - the agent endpoints (pull, ack, heartbeat)
  - the `Printing_Jobs` index
- [ ] WX6 rendering:
  - barcodes: Code 128, GS1-128, EAN-13, DataMatrix, QR. Today only QR exists (`libs/authorization/MintPlayer.Spark.Authorization/Identity/SparkQrCodeRenderer.cs:17-44`, registered by `Extensions/SparkAuthenticationExtensions.cs:91`)
  - ZPL templates (203 and 300 dpi)
  - the PDF seam, with the library chosen by W17 (commerce Q-O9)
  - the label set: locations, items and cases, totes, SSCC logistic labels, packing slips, pick lists
  - golden tests offline; no Labelary in CI
- [ ] `libs/printing/agent/`, the worker:
  - outbound connection to the app; TCP 9100 output
  - local journal; exactly-once acknowledgement
  - scale reading
  - heartbeat and update check
  - credential and distribution per Q-W14
- [ ] `libs/printing/qz-tray/`: the signing endpoint, with the host's certificate from user-secrets or configuration
- [ ] `libs/printing/zebra/`: the adapter; the host supplies the library (Q-W15)
- [ ] PDF fallback when the agent is down; a reprint is a new job

### WM7 — Packing

- [ ] `libs/warehouse/packing/`, alias `warehouse-packing`
- [ ] Pack station flow:
  - scan the tote or order; verify lines
  - cartonise, manually or by volume (`CartonType`)
  - weigh, manually or through the agent
  - capture serials
  - create the label through `Commerce.Shipping`; print the packing slip
  - round 5: only carrier services accepting the parcel's dangerous-goods classes, with label marks (commerce GC9); customs documents for non-EU destinations (GB6); optional return label in the parcel (GB5)
  - allocate an SSCC through `Gs1`
- [ ] Ship-confirm:
  - on hand −n and `ShippedTotal` +n in one session (WI6)
  - `WarehouseShipment` at `{fulfilmentOrderId}/shipments/{n}`, with the lots per line (round 5: feeds the GA7 recall trace)
  - publish `shipments`
  - handover per Q-W10
- [ ] Carriers:
  - an account per owner, or the site's default
  - tracking webhooks (commerce X5)
  - parcels keep reporting to the app that labelled them
  - the label-void capability
- [ ] Contribute this package's maps to `Warehouse_Documents` (Q-W26)

### WM8 — Counting

- [ ] `libs/warehouse/counting/`, alias `warehouse-counting`
- [ ] Count plans: ABC every 30/60/120 days, spread evenly; class assignment per Q-W8; spot counts
- [ ] Blind Count tasks:
  - each covers a whole location, held under a claim while counted (X12)
  - within tolerance → an adjustment; otherwise → supervisor review

### WM9 — Client entry points

- [ ] `libs/node_packages/ng-spark/scanning/` (WX5):
  - wedge capture: `inputmode="none"`, terminator plus timing, manual fallback, symbology identifiers, check digits
  - camera: native `BarcodeDetector`, or the same-shape `barcode-detector` ponyfill (WASM self-hosted, lazy-loaded)
  - GS1 parser (Q-W16)
  - AZERTY detection and `event.code` decoding
  - feedback and Screen Wake Lock
  - the browser-support matrix
- [ ] `libs/node_packages/ng-spark/offline/` (WX7):
  - an IndexedDB command queue (op ids, device time)
  - replay on `online`, on demand, and after successful requests
  - the exception list
  - `navigator.storage.persist()` and the offline warning
  - per-user partitions, wiped on sign-out (Q-W13)
  - keep `tools/verify-ngsw-config.test.mjs:90-96` green: no `/spark` data groups
- [ ] `libs/node_packages/ng-spark/printing/`: clients for the PDF print dialog, QZ Tray and Zebra Browser Print; job delivery through commerce X2's private stream
- [ ] `libs/node_packages/ng-spark/warehouse/` (+ `/inbound`, `/outbound`, `/packing`, `/counting`):
  - handheld task pages, the pack station, supervisor pages, the barcode-choice dialog
  - `provideSparkWarehouse(with…Routes())`, with routes replaceable one by one
  - theme variables, translations, WCAG
- [ ] WX15 idle sign-out or lock in `ng-spark/auth`, plus the server's session settings (Q-W13, Q-W25)
- [ ] BYOD data minimisation: no addresses in cached pick tasks. Hybrid passkey guidance added to `docs/guide-passkeys.md`

### WM10 — Shop side (commerce work, tracked in commerce M11–M12)

- [x] `Commerce.Fulfilment` works through `Fulfilment.Contracts` *(2026-10-20: `FulfilmentSync` publishes through `IReplicaPublisher`, consumes through `IReplicaChanged<T>` plus a reconcile job (the ETL path); the three published types are model types of the library, rights granted by the app: SPARK047)*:
  - publishes `fulfilment-orders`, `catalog-items` and `expected-returns`
  - consumes `fulfilment-status`, `shipments`, `stock-levels` and `return-receipts`
- [x] Fulfilment-order ids unique across shops; cancellation as a status; an edit = a cancellation plus a new order *(ids `FulfilmentOrders/{ShopName}/{orderNumber}/{seller}/{revision}`; one whole-order cancellation per revision, the replacement published after the warehouse accepted; `FulfilmentPlanner`)*
- [x] Capacity source from `stock-levels` (the PRD §4.4 formula) *(`FulfilmentStock`; rule 1 through Checkout's `ICheckoutStockFeed` + restock on confirmation; baseline `SetBaselineAsync`; bundles by construction (the feed sets component quotas); a capacity below sold + held closes the quota)*:
  - `sold` lowered only on a confirmed cancellation
  - an epoch baseline at onboarding
  - bundles through quota multipliers
  - the I4 exception
- [x] Returns gate: refund on the warehouse's receipt by default; the host can switch to refund on request (WD12); round 5 (commerce GA1): for withdrawals, also on the buyer's proof of dispatch, whichever is earlier *(`RefundTiming`; `GoodsBackKind.Released` added to Checkout; the proof of dispatch is registered in Checkout's aftercare, which pays on the first of the two)*
- [x] Round 5 (commerce GA8): `expected-returns` carries the return reason per line
- [x] Round 5 (commerce GA7): the shop resolves a recall trace's fulfilment orders to buyers and mails the recall notice *(`TraceRecallAsync` / `SendRecallNoticeAsync` over the shipments replicas; template `SparkFulfilment/RecallNotice` en/fr/nl)*
- [x] Sync the commerce text with WD1/WD2 (PRD §0): commerce PRD §4.9's transport paragraph, plus commerce plan M11 and S20 *(docs only; S20 synced in commerce round 5, 2026-10-19)*

### WM11 — Demo app and E2E

- [ ] `apps/Warehouse` (`Warehouse`, `Warehouse.Library`, `Warehouse.Tests`):
  - https 5019 / http 5020
  - database `SparkWarehouse`, cookie `.SparkAuth.Warehouse`, module name `Warehouse`
  - `Transport=Http` pinned (Q-W24)
  - test carrier and PDF printing
  - two seeded owners; no personal data
- [ ] New-app checklist:
  - workspaces and `MintPlayer.Spark.slnx`
  - the build and verify loops in `.github/workflows/pull-request.yml`
  - the app list in `tools/verify-ngsw-config.test.mjs:64-69`
  - `E2E_APPS` in `tools/test-local.mjs:33`
  - `regenerate.yml`, route snapshots, `modelHashes.json`, `securityPosture.txt`
  - the `test.yml` matrix
- [ ] Two-host fixture:
  - one shared `SparkModules` database (today one per host, `tests/MintPlayer.Spark.E2E.Tests/_Infrastructure/FleetTestHost.cs:43`)
  - the generalised host (multi-host plan H1, H2, R2: `docs/multi-host-e2e-plan.md:12-26`)
  - polling with point-loads under a failure bound
- [ ] The end-to-end mTLS test (`tests/MintPlayer.Spark.E2E.Tests/Security/CrossModuleSyncTests.cs:24-31`)
- [ ] The E2E flows of PRD §14; the replication E2E under `Http`, then again under `Etl` (Q-W24)

### WM12 — Documentation

- [ ] Rewrite `docs/guide-cross-module-sync.md` (contracts, transports, the write path), fixing `:117,124,243,245,270-272`
- [ ] Update `docs/guide-replication-mtls.md`: the registry mode, and destinations taken from the registry
- [ ] One `docs/guide-warehouse-*.md` per package
- [ ] `docs/guide-printing.md`: adapters, agent install, QZ signing
- [ ] `docs/guide-scanning.md`: the DataWedge profile, the AZERTY setup, the support matrix
- [ ] `docs/guide-offline.md`
- [ ] A README per package
- [ ] The index budget: counts per library, and how a host binds queries to static indexes

### WM13 — Sweep, verification, release (inside commerce M22)

- [ ] `npm run test:affected` with the Developer licence (raw log to the scratchpad); fix and re-run single failing classes only
- [ ] The replication E2E with `Transport=Http` and with `Etl`; the Community results of W5, W6 and W19 recorded
- [ ] Browser verification through the Playwright MCP tools: handheld pages at phone width, the pack station, the barcode dialog, the offline banner
- [ ] Device verification recorded: W9–W11, W15, W16, W18
- [ ] Version bumps: preview numbers; new packages at `11.0.0-preview.1`; one ng-spark minor, shared with commerce
- [ ] PRD §8: confirm no VPS change; list the conditional steps for the first production replica consumer
