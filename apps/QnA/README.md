# QnA — the #460 demo

A small question-and-answer site that exercises every framework feature issue #460 added for the
MintPlayer migration, end to end: Moderation, SoftDelete, History, the row-policy and interceptor
seam, `OnDisableActionsAsync`, MailManager, the account pages, the timezone cookie, and core's Data
Protection and forwarded-headers defaults. Its E2E suite (`tests/MintPlayer.Spark.E2E.Tests/QnA/`)
is the executable spec of those features.

```
apps/QnA/
├── QnA.Library/      Question, Answer — IModeratable, ISoftDeletable, IAuditable
└── QnA/              the host: Program.cs, Actions, CustomActions, Interceptors, Security,
                      Services, Templates/Mail, App_Data, ClientApp (Angular)
```

## Running it

Start RavenDB on `http://localhost:8080` (see the root README), then:

```bash
cd apps/QnA/QnA
dotnet run --launch-profile https      # https://localhost:5009
```

`dotnet run` is the whole command: the host starts the Angular dev server itself
(`UseAngularCliServer`) — do not run `ng serve` next to it. Mail is not sent: every message is an
`.eml` file in `apps/QnA/QnA/mail-pickup/` (the confirmation link of a new account is in there).

A fresh database has no moderator. Register, confirm the address through the link in
`mail-pickup/`, then give the account the `Moderators` group — a `group` claim on the user document
(`SparkUsers/…`, `"Claims": [{ "ClaimType": "group", "ClaimValue": "Moderators" }]`) in RavenDB
Studio. Moderators hold everything a privilege cannot earn: lock, suspend, restore, purge, revert,
the audit log.

In Development, `appsettings.Development.json` lowers Moderation's fraud gates (no 48-hour crediting
delay, no voter age or reputation minimum) — an override of `App_Data/moderation.json`, which is the
lowest-precedence configuration source (D14) — and turns on the test seams, so a moderator can run
the crediting job at once: `POST /qna-test/moderation/credit` (antiforgery token required, like every
POST).

## What each feature looks like here

| #460 | Where | What to try |
|---|---|---|
| **M12 Moderation** | `AddModeration<SparkUser>()`, `App_Data/moderation.json`, the privilege groups in `App_Data/security.json` | Vote on a question (the `Votes` column is the `spark-vote` widget; its `rendererOptions.type` names the type, because a grid row carries none). Your reputation sits in the topbar with the not-yet-credited part beside it. Flag a post, then open **Review queue** as a moderator. Lock a post from the moderator panel under its detail page; the author can no longer save, delete or revert it (400). |
| **M6 SoftDelete** | `AddSoftDelete()`, `provideSparkSoftDelete()` | Delete an answer: it disappears from every list, sub-query and reference picker. A moderator's **Deleted** toggle shows the recycle bin; **Restore** / **Purge** on the row. |
| **M7 History** | `AddHistory()`, `"revisions": { "enabled": true }` in both model files, `provideSparkHistory()`, `QnAUserNames` | Edit a question twice: the History card lists the revisions with names (resolved at read time from ids), shows a diff, and a moderator can **Revert**. |
| **M2 row policy** | `Security/DraftQuestionPolicy.cs` | Tick **Draft**: the question is visible to you only, on every read path — lists, detail, vote and flag targets. |
| **M2 interceptors** | `Interceptors/QuestionTagsInterceptor.cs`, `Interceptors/ClosedQuestionInterceptor.cs` | Tags are normalised on every save (`C#, c# ,Spark` → `c#, spark`). A closed question refuses new answers (400), on every write path. |
| **M3 `OnDisableActionsAsync`** | `Actions/QuestionActions.cs` | **Close** / **Reopen** are offered only to the author or a moderator, and only the one that applies. The author cannot delete a question that has answers (Delete is hidden, and refused with 403 at submit). |
| **M8 MailManager** | `AddMailManager()` in pickup mode, `Templates/Mail/AccountDeleted*.mjml` | The confirmation mail on registration; the goodbye mail when an account is deleted (`QnAAccountDeletionHandler`), in English or Dutch per the account's mail language. |
| **M10 account pages** | `withAccount()` in `app.routes.ts` | **Account** in the topbar: profile (mail language), password, two-factor, personal data and account deletion. |
| **M9 timezone** | `withSparkTimezone()` in `app.config.ts` | The browser's zone travels as `X-Spark-Timezone` and is kept in the `spark-timezone` cookie. |
| **D5 / D15** | nothing in `Program.cs` | Data Protection keys go to a local folder in Development; forwarded headers trust loopback and private ranges. `appsettings.json` sets neither `Spark:DataProtection:Storage` nor `KeysPath` — by owner decision the base file never does; the E2E host sets `Storage=RavenDb` in its own settings. |

Who may change a post: everyone signed in holds `Edit`/`Delete` on both types, and the Actions
classes' row rule narrows it to the author — or a moderator, who is whoever holds `Lock/Question`
(`Services/QnAAccess.cs`). No earned privilege can grant `Lock`, so moderation cannot be farmed (D12).

## Privileges

| Privilege | Group | Rep | Account age | Active days | Grants |
|---|---|---|---|---|---|
| Upvote | Voters | 10 | 0 | 0 | `Vote/Question`, `Vote/Answer` |
| Flag | Flaggers | 15 | 1 | 1 | `Flag/…` |
| Downvote | Downvoters | 125 | 7 | 3 | `Downvote/…` |
| Review | Reviewers | 500 | 30 | 10 | `Review/Moderation` |

`dotnet run -- --spark-init-moderation` prints the rights these need; `App_Data/security.json` was
checked against its output.

## Model and security baselines

`App_Data/Model/*.json` and `modelHashes.json` are generated (`dotnet run -- --spark-synchronize-model`)
and then hand-edited (labels, read-only framework fields, renderers, `revisions`, the
`Question_Answers` sub-query); synchronization keeps the edits. CI runs `--spark-verify-model` and
`--spark-verify-security` for QnA like for the other apps. The anonymous surface
(`App_Data/securityPosture.txt`) is `Query`/`Read` on both types: anyone may read the site.

## Test seams

`Testing/QnATestSeams.cs` maps `POST /qna-test/moderation/{credit,detect,recompute}` when
`QnA:TestSeams:Enabled` is true (Development and the E2E host; refused at startup in Production).
They call `ISparkModerationJobs` — the Cron jobs' own code — and answer 404 to anyone but a moderator.
A test triggers crediting through them instead of waiting five minutes for the schedule.
