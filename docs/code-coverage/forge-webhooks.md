# Handling a webhook from any forge

**One interface, one method, one class. There is nothing else to write.**

```csharp
public partial class BranchCommitPushedRecipient
    : IRecipient<ForgeWebhookMessage<BranchCommitPushed>>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public async Task HandleAsync(
        ForgeWebhookMessage<BranchCommitPushed> message,
        CancellationToken cancellationToken = default)
    {
        var evt = message.Event;      // BranchCommitPushed — already normalised
        // … do the work. Nothing here knows which forge sent it.
    }
}
```

That class handles the event **from GitHub, GitLab, Bitbucket and every forge added later**. No
registration call, no switch on a provider, no `if (github)`. The source generator discovers the
interface; adding a forge adds a library and touches **zero** consumers.

This is D21, and the reason for it is that the alternative expands as M×N: with a message type per
forge, **M** recipients across **N** forges need M×N handlers, and every new forge edits M existing
consumer classes. Neutral messages need M, and N is absorbed once — inside the forge libraries,
where a new forge is a new library rather than an edit to every consumer.

## What arrives

| | |
|---|---|
| `message.Event` | the normalised event — the only thing most handlers read |
| `message.Provider` | `EForgeProvider`, for the rare case where it genuinely matters |
| `message.RawJson` | the original payload, as an escape hatch for something one forge alone sends |
| `message.DeliveryId` | the forge's delivery id when it has one. ⚠️ **Not a dedup key** — GitLab sends none |

## Handling several events

Several interfaces on the same class, one `HandleAsync` overload each:

```csharp
public partial class ForgeEventsRecipient :
    IRecipient<ForgeWebhookMessage<BranchCommitPushed>>,
    IRecipient<ForgeWebhookMessage<PullRequestUpdated>>,
    IRecipient<ForgeWebhookMessage<PullRequestMerged>>,
    IRecipient<ForgeWebhookMessage<OwnerRenamed>>,
    IRecipient<ForgeWebhookMessage<RepositoryRenamed>>,
    IRecipient<ForgeWebhookMessage<RepositoryConnectionChanged>>
{
    // …
}
```

⚠️ **Declare every interface on a single `partial` part.** The generator fires per class declaration
with a base list, so interfaces split across two parts register twice and **run each handler twice
per message**.

⚠️ **`ForgeWebhookMessage<T>` is constrained to `IForgeEvent`**, so `T` can only be one of the six
below. That is deliberate: the envelope is what makes a consumer forge-agnostic, so what may travel
in it is the one thing that must not be open-ended — a forge-specific payload inside a neutral
envelope would look neutral to every reader and every recipient signature, and be neutral to none of
them. A fact only one forge can state keeps a forge-specific message and an honestly forge-specific
recipient (D17).

## The vocabulary, and what raises each event

Six neutral events. A forge library normalises its own webhooks into them; the app writes nothing
but recipients.

✅ = implemented. The GitHub column is what the code does today. **The GitLab and Bitbucket columns
are the *intended* mapping and are not verified against those APIs** — neither library exists yet
(their projects contain a registration stub and nothing else), so treat them as the starting point
for a port, not as a specification.

| Neutral event | GitHub | GitLab *(intended)* | Bitbucket *(intended)* |
|---|---|---|---|
| **`BranchCommitPushed`** | ✅ `push` | `Push Hook` | `repo:push` |
| **`PullRequestUpdated`** | ✅ `pull_request` — `opened`, `reopened`, `synchronize` | `Merge Request Hook` — `open`, `reopen`, `update` | `pullrequest:created`, `pullrequest:updated` |
| **`PullRequestMerged`** | ✅ `pull_request` — `closed` **with `merged: true`** | `Merge Request Hook` — action `merge` | `pullrequest:fulfilled` |
| **`OwnerRenamed`** | ✅ `organization` / `installation_target` — `renamed` | `System Hook` — `group_rename`, `user_rename` | ⚠️ no known webhook |
| **`RepositoryRenamed`** | ✅ `repository` — `renamed`, `transferred` | `System Hook` — `project_rename`, `project_transfer` | `repo:transfer` |
| **`RepositoryConnectionChanged`** | ✅ `repository` `deleted`; `installation_repositories` `removed`; `installation` `deleted`/`suspend`/`unsuspend` | `System Hook` `project_destroy`; token revoked or 401 on access | app uninstalled; `repo:deleted` |

An event belongs in this vocabulary only if **all three forges can raise it**. Anything one forge
alone can say — GitHub's `check_run`, Projects V2 — keeps a forge-specific message and a recipient
that is honestly forge-specific. A neutral name over a single-forge concept is worse than an honest
one, because it invites an implementation that cannot exist.

## ⚠️ Per-event traps a port must not get wrong

Each is recorded on the record itself, in `CodeCoverage.Library/Forge/ForgeEvents.cs`.

- **`PullRequestMerged` is merged, not closed.** Retention *surrenders* a merged pull request's build
  data and *keeps* a closed-unmerged one, because that may reopen. GitLab's `close` and Bitbucket's
  `pullrequest:rejected` are declines. **A forge that cannot determine mergedness must raise
  nothing** — deleting builds on a guess is unrecoverable.
- **`BranchCommitPushed` carries no parent sha**, deliberately. GitHub's `before` is the previous ref
  tip, which is not the new commit's parent in three of the six push shapes: a push of five commits
  reports the tip five commits back, a branch creation reports the all-zero sha, and a force-push
  reports an abandoned tip that need not be an ancestor at all. Every forge's equivalent field has
  the same problem.
- **`RepositoryRenamed.PreviousFullName` must come from the producer.** By the time a consumer runs,
  a library that upserts metadata from the same payload has already overwritten the stored name, and
  the alias appended would be the name it already has. ⚠️ **Bitbucket slugs are renameable *and
  reusable*, so a name is never an identity.**
- **`RepositoryConnectionChanged.ReportedByAccountId`** settles an ordering hazard without needing an
  order. When a repository moves between two owners we both see, the loss and the gain are sent at
  the same instant and arrive in no guaranteed sequence — an unguarded loss disconnects a repository
  we can plainly still see.
- **Unknown reports `false`.** `PullRequestUpdated.IsFirstOpen`, `AuthorIsBot` and
  `PullRequestMerged.HeadIsFromSameRepository` all default to false when the forge cannot tell: a
  duplicate comment beats a missing one, and declining to delete a branch is recoverable where
  deleting someone else's is not.

## Writing the other side — a new forge

The mirror image: normalise the payload and publish it, and every existing recipient starts
receiving it.

```csharp
await messageBus.BroadcastAsync(
    new ForgeWebhookMessage<BranchCommitPushed>(EForgeProvider.GitLab, new BranchCommitPushed(…)), ct);
```

⚠️ A forge library reaches DI through **one explicit call** — `AddGitlabIntegration()` — because the
generated registrations are per-compilation and `internal`. Nothing scans assemblies, so an omitted
call is silent: the app boots, serves pages, and publishes nothing.

## Enforced, not just documented

`IForgeEvent` marks membership of the vocabulary and `ForgeWebhookMessage<T>` is constrained to it,
so a forge-specific payload cannot travel in a neutral envelope. `ForgeEventContractTests` asserts
that every event has a producer **and** a consumer, and that no consumer names a forge.

⚠️ That test exists because `RepositoryRenamed` and `RepositoryConnectionChanged` spent their first
weeks declared with **neither**. The facts were handled inline in the GitHub library instead — so the
contract *looked* complete, a second forge would have raised two of its six events and found them
silently dropped, and no test, analyzer or build step would have mentioned it. A declared event with
no implementation is worse than a missing one: a gap is obvious, a promise is believed.
