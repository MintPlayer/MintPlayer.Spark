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

## 2. Privileges are groups

1. Add one group per privilege to `App_Data/security.json` (`"groups": { "<guid>": "Voters" }`).
2. Reference it by id in `App_Data/moderation.json`, with its gates and the actions it grants:

   ```json
   { "Privileges": { "Upvote": { "GroupId": "<guid>", "Rep": 15, "MinAccountAgeDays": 0, "MinActiveDays": 1, "Grants": [ "Vote" ] } } }
   ```

3. Run `dotnet run -- --spark-init-moderation`, review the printed rights and add them to
   `security.json` (`Vote/Answer`, `Vote/Question`, …), plus the moderators group's
   `Lock/T`, `Restore/T`, `Purge/T`, `ViewDeleted/T`, `Revert/T`. `Review/Moderation`,
   `Suspend/Moderation` and `Audit/Moderation` ship with the library, granted to its slots: bind
   them, `"bindings": { "moderation:moderators": ["Moderators"], "moderation:reviewers": ["Reviewers"] }`.

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
