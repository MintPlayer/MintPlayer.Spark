# Spark 11.0.0-preview.97 — `isVisible` is gone: rights decide who sees, `showedOn` decides where (#264)

**Packages:** `MintPlayer.Spark`, `MintPlayer.Spark.Abstractions`, `MintPlayer.Spark.Model`,
`MintPlayer.Spark.SourceGenerators`, `MintPlayer.Spark.Contributions`,
`MintPlayer.Spark.Contributions.Abstractions`, `MintPlayer.Spark.Authorization`,
`MintPlayer.Spark.Client` and `MintPlayer.Spark.Client.Authorization` → `11.0.0-preview.97`. npm:
`@mintplayer/ng-spark` → `22.28.0`, `@mintplayer/ng-spark-auth` → `22.19.0`. No other package changed. The majors do not move: the packages still target .NET 11 and
Angular 22, and the breaking changes below ship as a minor.

`isVisible` fused two things. It was layout (draw this attribute nowhere) **and** a write gate (refuse
a posted value for it), and that fusion caused two live bugs: a stolen car's police report number,
revealed by a refresh hook, was silently dropped on save; and HR's required `LastName`, hidden from
the form, made a person impossible to create. Spark now follows Vidyano's split:

- **who** may see or write a value is `security.json` (attribute rights) and `isReadOnly`;
- **where** a value is drawn is `showedOn` in the model, and the runtime `ShowedOn` an action sets
  per object.

The decisions and their evidence are in `docs/remove_isvisible_PRD.md` (§0 and the `G-Q*` rows of §7)
and `docs/remove_isvisible_plan.md`.

---

## ⚠️ Breaking changes

No backward compatibility (preview). Each item says what to change.

### 1. `IsVisible` is removed everywhere

Removed: `EntityAttributeDefinition.IsVisible` (the model), `PersistentObjectAttribute.IsVisible`
(the wire), `QueryColumn.IsVisible`, and `isVisible` on ng-spark's `EntityAttributeDefinition`,
`PersistentObjectAttribute`, `QueryColumn` and the refresh `AttributeOverlay`. ng-spark's unused
`visibleGridAttributes()` is deleted. `isVisible` is no longer part of the model hash.

**A model file that still says `"isVisible": false` refuses startup** (and `--spark-synchronize-model`
refuses it too). The message names each `Type.Attribute` and the replacement. Silently ignoring the
flag would have made every hidden field writable. `"isVisible": true` (the old default) loads and is
dropped by the next synchronize.

**Migration** — pick by what the flag was doing:

| `isVisible: false` was used to… | Now |
|---|---|
| hide a value from a group | an attribute deny in `security.json`: `{ "resource": "QueryRead/Employee/Salary", "groupId": "…", "isDenied": true }` |
| hide a value from everyone | the same deny on **both** `wellKnown` groups (`anonymous` and `authenticated`); there is no "everyone" group |
| keep an internal value nobody reads off the wire | `[IgnoreProperty]` on the property, or delete the attribute (⚠️ `[IgnoreProperty]` also drops it from a `[GenerateIndex]` index) |
| ship a value the client code needs, drawn nowhere | `"showedOn": "None"` **plus** `"isReadOnly": true` (or an `Edit`/`New` deny) — `None` is layout, not protection |
| keep a column off the grid but on the form | `"showedOn": "PersistentObject"` |
| show an attribute only in some states (`IsVisible = …` in `OnRefreshAsync`) | the model says `"showedOn": "None"`; set the runtime `attr.ShowedOn` in `OnLoadAsync`, `OnNewAsync` **and** `OnRefreshAsync` (one shared helper) |
| ship a grid value for a renderer without a column (`"showedOn": "Query", "isVisible": false`) | make it its own narrow column with a renderer, or fold it server-side into a column that is drawn — a grid row carries only drawn columns |

### 2. Visibility is no longer a write gate

`EntityMapper` no longer refuses a posted value because the attribute is hidden. The only
model-level write gate is `isReadOnly`, plus `Edit`/`New` attribute rights and presence in the model.
**An attribute you hid with `isVisible: false` and did not also mark `isReadOnly` becomes writable**
once you migrate it to `showedOn: "None"` — add `isReadOnly: true` or a deny (the startup error in
§1 makes you look at each one).

### 3. `showedOn: "None"`

`EShowedOn` (and ng-spark's `ShowedOn`) gain `None = 0`: shipped on the persistent object, drawn on no
page (Vidyano's `Never`). Synchronize keeps an explicit `None`; only an absent `showedOn` is derived.
There is no `QueryValue`: Spark ships no values for grid columns it does not draw.

### 4. The new-attribute seed API is removed

`SparkNewAttributeSeed` and `SparkModelSatellites.SeedNewAttribute` / `NewAttributeSeedFor` are gone,
and with them the Contributions seeds that wrote `ContributorId` and the row `Key` as
`showedOn: PersistentObject` + `isVisible: false`. Libraries no longer decide visibility: the
**application's** model file decides how `ContributorId` and `Key` are shown (QnA: `showedOn: "None"`
+ `isReadOnly: true`, and the resolved `ContributorName` is shown).

### 5. `SparkSelectionResolver` takes one more constructor parameter

Its batched-load fallback now builds a selected row's columns from the caller's query surface, so it
takes `IAttributeRightsEnforcement` as a new last constructor parameter (generated by `[Inject]`).
The class is internal; only code that constructs it directly (tests) needs the extra argument.

### 6. A user name is a public handle: registration asks for one, and it never contains `@`

Registration used to set `UserName` to the email address, and the user name is what **other** users
see (a `{UserName}` reference label, a history author, a contributor). QnA showed authors' email
addresses that way. Email addresses are never shown to another user now (G-Q22):

- **`POST /spark/auth/register` requires `userName`** next to `email` and `password`; missing or blank
  is a 400. ng-spark-auth's register page has a "User name" field. `SparkClient.RegisterAsync` takes it
  as a new third parameter: `RegisterAsync(email, password, userName)`.
- **A user name may never contain `@`.** `SparkUserNameValidator<TUser>` refuses it on every save, so
  register, external sign-up, the profile page and an application's own `UserManager` calls are all
  covered. Its error code is now `UserNameContainsAt` (was `UserNameMustMatchEmail`, which allowed the
  account's own email).
- **An email change no longer touches the user name.** `SparkUserManager<TUser>` existed only to move
  an email-shaped user name along and is removed; Identity's `UserManager<TUser>` is used.
- **External sign-up** never uses a name that is an email address (it generates `user-` + six hex
  digits instead), and a `ProviderHandle` that is already taken is now suffixed (`-2`, …) rather than
  failing the sign-up.
- **Migration:** `M_202610051200_UserNamesAreNotEmails` (shipped by `MintPlayer.Spark.Authorization`,
  registered by the generated `AddMigrations()`) gives every stored user whose user name contains
  `@` a generated `user-xxxxxx` handle, `NormalizedUserName` included. Sign-in by email keeps
  working, and the user can pick a better handle on the account page.
- **Sign-in no longer falls back from an email to a user name.** An identifier containing `@` is
  looked up by email only; without `@`, by user name only. The fallback existed for `@`-shaped user
  names, which the migration above removes — a leftover one cannot sign in by that name.
- **New: `SparkAuthenticationOptions.SignInIdentifiers`** (`[Flags] SparkSignInIdentifiers`: `Email`,
  `UserName`; default both, today's behaviour) lets an application accept only the email or only the
  user name. A disallowed kind gets the same 401 as an unknown account. `/spark/auth/capabilities`
  reports it as `signInIdentifiers`, and ng-spark-auth's login page and the OIDC `/connect/login`
  page label their field from it ("Email", "User name" or "Email or user name"). A value allowing
  neither is refused at startup unless `LocalCredentials` is `Disabled`. Password reset stays
  email-based.

**To do:** a client that posts to `/register` itself must send `userName`; code that set a user name
to an email must pick a handle; a test that asserted a label equal to the email asserts the user name.

---

## Runtime `ShowedOn`: state-dependent layout (Vidyano's `Visibility` pattern)

```csharp
public override async Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)
{
    var po = await base.OnLoadAsync(id, parent);
    if (po is not null) ShapeForStatus(po, refresh: false);
    return po;
}
// … and the same helper from OnNewAsync and OnRefreshAsync

private static void ShapeForStatus(PersistentObject obj, bool refresh)
{
    var stolen = obj[nameof(Car.Status)].Value?.ToString() == CarStatus.Stolen;
    obj[nameof(Car.PoliceReportNumber)].ShowedOn = stolen ? EShowedOn.PersistentObject : EShowedOn.None;
    obj[nameof(Car.PoliceReportNumber)].IsRequired = stolen;
}
```

**ng-spark applies the runtime `ShowedOn`, `IsRequired` and `IsReadOnly` from the first render** of a
loaded or new object (`po-detail`, `po-edit`, `po-create`), not only after a refresh. Overriding
`OnLoadAsync` opts the type out of batched selection loads, which is accepted for types shaped this
way. See `docs/guide-triggers-refresh.md`.

---

## Attribute-rights gaps closed along the way

- **`GET /spark/types/{id}?for=new|edit|read`.** The create form asks `for=new`, which leaves out
  `New`-denied attributes (they used to be drawn, then refused on save) and does not apply an
  `Edit`-only deny. Every purpose still removes `Read`-denied attributes; absent or unknown is the
  edit shape, as before. ng-spark: `SparkService.getEntityType(id, purpose?)`.
- **Custom-action selections** no longer carry a `Query`-denied attribute's value in the batched-load
  fallback (§5 above).
- **A validation error on an attribute the form does not draw** is shown as a form-level error on the
  create and edit pages instead of being swallowed (Vidyano promotes it to a notification).
- **SPARK024** now lists only attributes the model declares, as the runtime posture report always did.
  A hand-authored model over a library type (`SparkUser.json` with 4 of ~24 properties) no longer
  reports `PasswordHash` and the rest as "still readable".
- **SPARK024 is silent for a deny both `wellKnown` groups share** ("hide from everyone", G-Q23); the
  runtime posture note agrees. A deny on one of the two groups only still warns.

---

## Moderation: an anonymous visitor is no longer sent to the sign-in page

`POST /spark/moderation/reputation` answered an anonymous caller 401. The reputation badge sits in
every author cell of pages anyone may read (QnA's question list), and ng-spark-auth's interceptor
navigates to the sign-in page on any 401 outside `/spark/auth`, so an anonymous visitor opening
`/query/questions` landed on `/login`. The endpoint now answers an anonymous caller **200 with no
result** (`result: null`): no reputation of their own, and still nobody else's, so nothing is
disclosed that the 401 withheld. `/reputation/history` and the review-queue endpoints still answer
401; they back pages where signing in is the answer. ng-spark: `SparkModerationService.reputation()`
and `ownReputation()` now resolve to `ModerationReputation | null`; the badge and the review-queue
link render nothing for `null`. MintPlayer.Spark.Moderation `11.0.0-preview.97`.

---

## JSON schemas for the App_Data files, and `$schema`

Spark now publishes strict JSON schemas for the six hand-edited files — `Model/*.json`,
`security.json`, `programUnits.json`, `translations.json`, `culture.json`, `actions.json` — at

```
https://schemas.spark.mintplayer.com/v{n}/<file>.schema.json
```

`v{n}` is one global revision, tagged `schemas/v{n}` and released on GitHub whenever any generated
schema changes. **`--spark-synchronize-model` now adds and maintains `"$schema"`** in those files: it
adds the URL of the revision the installed package was built against, rewrites the `v{n}` segment of
a hosted URL after an upgrade, and leaves any other value alone, touching only that one line. Editors
then flag typos, unknown properties and a leftover `isVisible` while you type. Properties starting
with `_` stay allowed as comments (a string in `translations.json`). `$schema` and `_` properties do
not affect the model hash or what the loaders read. See `docs/guide-json-schemas.md`.

---

## A request's own write is in its next query: writes wait for their indexes, after the commit

**Behaviour change.** Every write `IDatabaseAccess` makes while serving an HTTP request — persistent
object create, update and delete, the bulk delete, AsDetail rows, retries, `SaveDocumentUncheckedAsync`
/ `DeleteDocumentUncheckedAsync`, and so every custom action that writes through it — now **commits
first, then waits** until every index (static or auto) over a collection the commit wrote has
processed it. Before, the query right after the user's own write could read a stale index under load:
QnA's Answers card re-fetched after the row menu's Duplicate and showed the old row count, and a bulk
delete's sub-query membership check missed a just-created answer and answered 404 instead of 403.

- **After the commit, never failing it.** The commit's outcome is final and is what the request
  answers. A wait that times out, or whose index is disposed under it (an auto-index merge, a
  side-by-side swap, a reset, a delete), is logged as a warning and swallowed. (Raven's
  `WaitForIndexesAfterSaveChanges` was rejected for this: it waits inside the commit request and
  answered committed writes with HTTP 500 when an index was disposed during the wait.)
- **Bounded at 15 s**, and normally milliseconds: one index-statistics request, and a waiting query
  only for an index that is actually stale. A paused index costs each write to its collections the
  full 15 s; the warning names it.
- **Requests only.** Scopes without an `HttpContext` — message handlers and durable interceptors, cron
  jobs, migrations, replication, hosted services — do not wait.
- Code that calls `SaveChangesAsync` on a session itself does not wait; write through
  `IDatabaseAccess` to get it (`docs/guide-custom-actions.md`). No new API.

---

## Documentation

- `docs/guide-authorization.md` — "Hide an attribute"; groups match the untranslated name only.
- `docs/guide-triggers-refresh.md` — the runtime `ShowedOn` pattern.
- `docs/guide-queries-and-sorting.md` — every shipped column is drawn; sort only on a column that is
  shown and indexed.
- `docs/guide-json-schemas.md` — new.
- `libs/authorization/MintPlayer.Spark.Authorization/README.md` — "The user name is a public handle".
