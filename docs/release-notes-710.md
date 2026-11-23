# The notification bell and visitor notifications (#710)

**Packages:**
- NuGet `MintPlayer.Spark`, `MintPlayer.Spark.Abstractions`, `MintPlayer.Spark.Authorization` and
  `MintPlayer.Spark.Notifications` get a minor with breaking changes (owner: "For once, you are allowed to introduce
  breaking changes"). The packages that only follow the renames get a minor: `.Boards`, `.Contributions`, `.Files`,
  `.History`, `.Mcp`, `.Moderation`, `.Organizations`, `.Replication`, `.Replication.Abstractions` and `.SoftDelete`.
- npm `@mintplayer/ng-spark` gets a minor with breaking changes (the `/client-operations`, `/notifications` and
  `/services` entry points).

The versioning engine computes the numbers from the tags. Everything ships once the merged pull request is backported to
`release/dotnet-11.x` and `release/angular-22.x`.

Guide: [guide-notifications.md](guide-notifications.md). Decisions and evidence:
[notification_bell_PRD.md](notification_bell_PRD.md), plan: [notification_bell_plan.md](notification_bell_plan.md).

---

## New

1. **One UI system for notifications.** `<spark-notification-bell>` lists the stored notifications and this session's
   `notify` messages together, and pops out what arrives below the bell (at most 3, 6 s, errors stay, hover pauses).
   The list is its own component, `<spark-notification-list>`.
2. **The bell works without the Notifications package.** Without `withNotifications()` it lists session messages only;
   every app that uses `withClientOperations()` can put it in its topbar.
3. **Anonymous visitors.** `ISparkCurrentPrincipal.EnsureVisitorId()` issues an HttpOnly, Data-Protection-protected
   visitor cookie (`spark.visitor`, 24 h sliding by default; `Spark:Visitor:SessionCookie` makes it a session cookie)
   and `SparkNotification.VisitorIds` addresses it. A visitor gets the in-app channel only, the row expires with the
   cookie, and the visitor's notifications move to the account at sign-in.
4. **`withSparkVisitor()`** (ng-spark `/services`, for `provideHttpClient`) replaces the live socket when a response
   identifies the visitor, so a notification addressed to the new visitor reaches the open tab.
   `SparkLiveService.reconnect()` is public. `sparkSignedIn(state)` (`@mintplayer/ng-spark`) reads `SPARK_AUTH_STATE`:
   a falsy value or `{ isAuthenticated: false }` is signed out.
5. **Account seams in Abstractions:** `ISparkAccount`, `ISparkAccountDirectory`, and the hooks
   `ISparkSignedInHandler`, `ISparkAccountDeletionHandler`, `ISparkAccountDeletedHandler`,
   `ISparkPersonalDataContributor`. A package can now read accounts and react to sign-ins without referencing
   Authorization.
6. **QnA demo:** the `RemindMe` action on a question notifies its caller 5 s later, signed in or not.

## Breaking changes

7. **`ISparkCurrentUser` is now `ISparkCurrentPrincipal`** (`MintPlayer.Spark.Abstractions.Authentication`), with
   `VisitorId`, `EnsureVisitorId()` and `ForgetVisitor()`. Replace the type name; the members it had keep their
   meaning.
8. **The account hooks are no longer generic and moved to Abstractions.** `ISparkSignedInHandler<TUser>`,
   `ISparkAccountDeletionHandler<TUser>`, `ISparkAccountDeletedHandler<TUser>` and
   `ISparkPersonalDataContributor<TUser>` are now the non-generic interfaces in `MintPlayer.Spark.Abstractions.Authentication`
   and receive an `ISparkAccount`. Authorization adapts its users through `SparkUserAccount`; a handler that needs the
   application's own user type loads it by `account.Id`. Register them without the type argument.
9. **The Notifications package no longer references Authorization.** It reads accounts through `ISparkAccountDirectory`.
   Its public base URL comes from `Spark:Notifications:PublicBaseUrl`, else the configuration key
   `Spark:Auth:PublicBaseUrl`; a value set only in code on the Authorization options is no longer read.
10. **`/spark/notifications` no longer requires authentication as a group.** `POST /read` serves an identified visitor
    too (401 for neither); the push endpoints still require a signed-in user. An app that relied on the group's own
    challenge gets the same answers.
11. **`<spark-toast-container>` (`SparkToastContainerComponent`) and `SparkNotificationService` are removed** from
    `@mintplayer/ng-spark/client-operations`. Replace the container with `<spark-notification-bell>` in the topbar;
    `SparkSessionNotifications` holds the session messages (`notify` client operations) the old service held.
12. **`withNotifications()` now turns the stored notifications on** (`SparkNotificationsOptions.server`). Without it the
    bell follows nothing on the server.
13. **The bell's `unreadCount` counts session messages too.** `serverUnreadCount` is the stored notifications alone.

## Notes

14. **Admitting visitors is a binding.** Bind the slot to the anonymous group by its name, e.g.
    `"notifications:users": [ "Anonymous visitors", "Signed-in users" ]` (`@anonymous` is not accepted there). An
    anonymous caller denied a row gets the uniform 401; a signed-in one gets 404, as before.
