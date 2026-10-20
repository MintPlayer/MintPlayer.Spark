# Guide: community moderation

`MintPlayer.Spark.Moderation` adds votes, reputation, earned privileges, flags, a review queue,
locks and suspensions to any entity that implements `IModeratable`. The package README
(`libs/moderation/MintPlayer.Spark.Moderation/README.md`) is the reference; this guide is the
adoption path.

## 1. Server

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<QnAContext>();
    spark.AddAuthentication<SparkUser>();
    spark.AddSoftDelete();                 // moderators restore / purge
    spark.AddHistory();                    // moderators revert
    spark.AddModeration<SparkUser>();
});

var app = builder.Build();
```

`if (builder.InitializeSparkModerationIfRequested(args)) return;` before `Build()` enables
`dotnet run -- --spark-init-moderation`.

Domain types implement `IModeratable` (reference `MintPlayer.Spark.Moderation.Abstractions`):

```csharp
public class Answer : IModeratable, ISoftDeletable
{
    public string? Id { get; set; }
    public string QuestionId { get; set; } = "";
    public string Body { get; set; } = "";
    public string? AuthorId { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    // ISoftDeletable members …
}
```

Moderation's documents (votes, reputation events, flags, review cases, locks, the audit log) store the
target's type as its persistent-object **id** in `TargetType`, never its name: an application's
`[assembly: PersistentObjectName]` rename (D28) changes a name, never an id. Rights are still checked by the
type's current name (`Lock/Answer`). Documents written by name before D28 are moved by the migration
`M_202610191545_ModerationTargetTypesById`; a name the model no longer knows is left as it is and logged.

## 2. Privileges are groups

The library ships its own `App_Data/moderation.json` as a [library layer](guide-library-layers.md):
the reputation table and four privileges, `Upvote`, `Flag`, `Downvote` and `Review`. Each privilege
confers a **slot** (`"Group": "moderation:voters"`), never a group id, so the application decides who
holds it:

1. Add one group per privilege to `App_Data/security.json` (`"groups": { "<guid>": "Voters" }`).
2. Bind each slot to it, by name or id, in the same file:

   ```json
   "bindings": {
     "moderation:voters": ["Voters"], "moderation:flaggers": ["Flaggers"],
     "moderation:downvoters": ["Downvoters"], "moderation:reviewers": ["Reviewers"],
     "moderation:moderators": ["Moderators"]
   }
   ```

   An unbound slot refuses startup (SPARK048 at build time). A privilege's slot must bind exactly
   one group, because the privilege confers that group; `moderation:moderators` confers nothing and
   may bind several.
3. Run `dotnet run -- --spark-init-moderation`, review the printed rights and add them to
   `security.json` (`Vote/Answer`, `Vote/Question`, …), plus the moderators' `Lock/T`, `Restore/T`,
   `Purge/T`, `ViewDeleted/T`, `Revert/T`. These are per type, on types your application owns, so a
   library cannot ship them. `Review/Moderation`, `Suspend/Moderation` and `Audit/Moderation` are on
   the library's own `Moderation` pseudo-type, so they ship with it, granted to the slots; the report
   leaves them out.
4. Your `App_Data/moderation.json` holds only what differs. A privilege or reputation event composes
   per key, `"Review": null` removes a privilege, and arrays are replaced whole. QnA's file is one
   line, `"DownvoteCastTypes": [ "Answer" ]`. `--spark-describe moderation --layers` shows where each
   value came from.

`"libraries": { "moderation": false }` in `security.json` switches the shipped rights off. They then
show as inert in `securityPosture.txt`, and `--spark-init-moderation` prints them for you to grant.
Setting `GroupId` in code (`AddModeration(o => …)`) still works and needs no slot.

Startup refuses a privilege whose group is missing, well-known, holds a right that is not
earnable (`Delete`, custom actions — unless listed under `Earnable`), or has no grant. `Lock`,
`Suspend`, `Audit`, `Purge`, `Restore`, `Revert`, `ViewDeleted` can never be earned.

## 3. Operators override thresholds

`moderation.json` is the lowest-precedence configuration source. On a server, raise or lower any
threshold without a redeploy of the file:

```
Spark__Moderation__Fraud__MaxVotesCastPerDay=20
Spark__Moderation__Fraud__CreditDelayHours=24
Spark__Moderation__NewAccounts__MaxPostsPerDay=3
```

**A review case is decided once.** `/spark/moderation/case/decide` validates the decision, then claims
the case: the open → decided transition is one optimistic-concurrency write. Of several moderators
deciding at once, exactly one wins and runs the side effects (settle flags, reverse votes, merge,
suspend); the others get a 400 "already decided" (commerce F2). If a side effect fails, the case is
reopened so the decision can be retried.

## 4. Client

```ts
import { provideSparkModeration, sparkModerationRenderers, sparkModerationRoutes } from '@mintplayer/ng-spark/moderation';

providers: [
  provideSpark(...),
  provideSparkModeration(),
  provideSparkAttributeRenderers([...sparkModerationRenderers]),
],
routes: [...sparkModerationRoutes(), ...sparkRoutes()],
```

Give the entity a display attribute with `"renderer": "spark-vote"` (in a query column also
`"rendererOptions": { "type": "Answer" }`). Flag and the moderator panel appear on every detail
page; `<spark-reputation-badge [userId]="…" />` shows a user's reputation.

## 5. What it will not stop

A patient attacker who ages accounts and votes slowly across many authors still farms slowly: the
thresholds are visible configuration. The design bounds the damage instead — only reversible
privileges can be earned, every vote is reversible, and reviewers see the evidence (matrix, timeline,
ages, shared networks) for the cases the detector does not reverse on its own. See the README for the
legitimate-interest assessment and privacy-notice text for the network observations.
