# Declaring lanes with attributes

**Status:** design, not implemented. Depends on #363 (one subscription, partitioned ordering) being merged.

## Why

Lane declaration is currently a delegate in `Program.cs`. CodeCoverage's is 44 lines
(`apps/CodeCoverage/CodeCoverage/Program.cs:160-203`) and repeats, for every message type, a name the
message record already carries in its own `[MessageQueue]` attribute. The partition selector — the
single most consequential value in the whole block, because a wrong one silently breaks ordering or
serializes a lane — is a lambda checked only at startup, by a `LaneRegistry.Validate` that throws.

The goal is to move what is *static* about a lane next to the type it describes, and to turn the
startup throw into a compile error. The goal is **not** to replace the delegate: one production lane
cannot be expressed as attributes at all, and that is by design rather than an oversight (see
Constraints).

## Shape

```csharp
[Lane(Ordered = true, MaxPartitionsInFlight = 2, Name = "coverage-parse-session")]
public record CoverageParseLane;

[Lane<CoverageParseLane>] public record ParseSessionMessage   { [PartitionedBy] public required string BuildId  { get; init; } }
[Lane<CoverageParseLane>] public record FinalizeBuildMessage  { [PartitionedBy] public required string BuildId  { get; init; } }
[Lane<CoverageParseLane>] public record AssembleCommitMessage { [PartitionedBy] public required string CommitId { get; init; } }

[Lane(Concurrent = 4)]
public record CoveragePublishFeedbackLane;
```

The generator emits one `ILaneConfigurator` plus a `messaging.AddCodeCoverageLanes()` shorthand, into
the consumer's root namespace, following the existing `Add*()` convention
(`RecipientRegistrationGenerator`).

### Why the lane is a type, not attributes on the message

A lane carries **several message types** — that is the entire point of it. `coverage-parse-session`
holds three, because `FinalizeBuildMessage` must not overtake the `ParseSessionMessage`s of its own
build. If each record declared `[Ordered]` for itself, they would generate three separate lanes and
finalize could overtake again — reintroducing the production bug #363 exists to fix, silently, with
attributes that look correct. A lane type is the only shape in which "these three share an ordering
domain" is expressible.

### Why `Name` stays, and stays optional

The lane name defaults to the record's name. `Name` pins a wire name against renames, and
`coverage-parse-session` **needs** it: production holds live `SparkMessage` documents whose
`QueueName` is that string. A rename strands them. `Name` is therefore a compatibility anchor, not
the binding mechanism — messages bind by *type*, so the string exists once instead of four times.

## Constraints — what the generator must not try to absorb

Established by inspection of every lane declaration in the repo.

| Constraint | Evidence | Consequence |
|---|---|---|
| **A lane may be declared for a message type the app does not own** | CodeCoverage declares `spark-github-all` for `GitHubWebhookMessage`, owned by `libs/webhooks` (`Program.cs:195`) | Cannot be an attribute on the message. The lane record adopts it: `[LaneMessage<GitHubWebhookMessage>(PartitionBy = nameof(GitHubWebhookMessage.RepositoryFullName))]` |
| **A lane may be configured from DI** | `SyncActionLaneConfigurator` reads `MaxPartitionsInFlight`, the retry ladder and the block budget from `IOptions<SparkReplicationOptions>` (`SyncActionLaneExtensions.cs:49-62`) | Attributes are compile-time constants. Stays a hand-written `ILaneConfigurator`, forever |
| **A partition selector may be conditional** | replication: `m => string.IsNullOrEmpty(m.DocumentId) ? m.Collection : m.DocumentId` | Not attributable. Same escape hatch |
| **A partition key may be composite** | `coverage-delete-pr-builds`: `$"{m.RepositoryGitHubId}/{m.PullRequestNumber}"` | Generator must support ordered multi-property keys: `[PartitionedBy(Order = 1)]` |

Both forms compose without ordering hazards: all three `AddSparkLane` overloads funnel to an
**enumerable** `AddSingleton<ILaneConfigurator>` — never `TryAdd` — so a generated configurator and a
hand-written one coexist regardless of registration order.

## Diagnostics

The point of the exercise: `LaneRegistry.Validate` throws at startup today, per
`LaneRegistry.cs:126-157`. Next free ID is **SPARK011** (`SPARK003` is also unused).

- ordered lane has a bound message type with no `[PartitionedBy]` — today a startup throw
- `[PartitionedBy]` on a non-string property, or on a message not bound to an ordered lane
- `[Lane]` declaring both `Ordered` and `Concurrent`
- `MaxPartitionsInFlight`/`Concurrent` below 1; invalid lane `Name` (`QueueNames.IsValid`)
- two lane records resolving to the same name — today a throw at `LaneRegistry.cs:184`

Not statically checkable, stays a runtime throw: a selector returning an empty key.

## Test plan

`Expression<Func<T,string>>` **does not survive declaration** — `LaneDeclaration.PartitionBy`
(`LaneRegistry.cs:231-237`) compiles it immediately and stores `Func<object,string>`. There is no
expression tree to compare, so a generated selector can only be checked by **invoking it on sample
instances**. That is the stronger assertion regardless: it tests the key, not the syntax that
produced it.

| Class | Proves | Value |
|---|---|---|
| `LaneConfiguratorEquivalenceTests` | The generated configurator resolves to the same `DeclaredLanes`, `LanePlan` and partition keys as a hand-written baseline | Highest — the whole point |
| `CoverageLaneParityTests` (in `CodeCoverage.Tests`) | The real generated configurator matches a snapshot of today's `Program.cs:160-203` | Highest — regression fence on production |
| `LaneGeneratorDiagnosticsTests` | Each diagnostic above fires, and does not fire on valid input | High |
| `LaneGeneratorEdgeCaseTests` | No `[Lane]` types → no file emitted; generic/nested/global-namespace message types; name derived vs pinned | High |
| `LaneGeneratorSnapshotTests` | One golden file, as a diff aid — **not** the correctness oracle | Medium |
| Incrementality assertions | — | Ceremony: the vendored harness has no `RunGeneratorTwice`, inputs are a handful of types |
| `.Should().Contain("Ordered()")` text assertions | — | Actively discouraged; superseded by equivalence |

The retry schedule must be **probed** (`Enumerable.Range(1, n).Select(a => plan.Retry.Next(a))`) —
`IRetrySchedule` instances are reference-unequal. `MaxInFlight` after a bare `.Ordered()` is
`Clamp(ProcessorCount/2, 1, 4)`, machine-dependent but identical on both sides of one comparison.

Prerequisites: `InternalsVisibleTo("MintPlayer.Spark.SourceGenerators.Tests")` next to the existing
one (`MintPlayer.Spark.Messaging.csproj:29`), a `ProjectReference` to `MintPlayer.Spark.Messaging`
from the SG test project (only `.Abstractions` is referenced today), and extracting
`Program.cs:160-203` into a named `ILaneConfigurator` so a baseline is addressable.

## Milestones

1. **Delete `SubscriptionWorkerRegistrationGenerator`** and its test. Nothing calls it; see below.
2. Attributes in `MintPlayer.Spark.Messaging.Abstractions`: `LaneAttribute`, `LaneAttribute<TLane>`,
   `LaneMessageAttribute<TMessage>`, `PartitionedByAttribute`.
3. `LaneRegistrationGenerator` + `.Producer.cs` + `Models/LaneClassInfo.cs` (`[AutoValueComparer]`),
   inside the existing `MintPlayer.Spark.SourceGenerators` assembly — **zero packaging work**, the
   Tools props file already packs it.
4. Diagnostics (SPARK011+) and their tests.
5. Equivalence + parity tests.
6. **Wire `AddLanes()` into `SparkFullGenerator.Producer.cs`** — see the warning below.
7. Migrate CodeCoverage's `Program.cs` to attributes; keep replication hand-written.
8. Docs: the messaging README's configuration section.

### The wiring step is not optional

`AddSubscriptionWorkers()` is dead code for exactly one reason: the generator emits it correctly and
**no branch was ever added to `SparkFullGenerator.Producer.cs`** to call it. A generated method in a
namespace nobody calls is indistinguishable from a working feature until someone checks. `AddLanes()`
fails the same way unless M6 lands with the rest.

## Out of scope

- Replacing `AddLane` / `ILaneConfigurator`. It is the escape hatch three production requirements
  depend on.
- Attribute-driven retry ladders beyond a literal string. Config-derived ladders stay in code.
- `[MessageQueue]` → `[MessageLane]` rename. Worth doing while in preview — "lane" and "queue" are
  one concept under two names — but it is a mechanical rename touching every app, and mixing it into
  this change would bury the design in churn.
