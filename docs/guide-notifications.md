# Notifications: in-app, mail and Web Push from one call

The notifications package (`MintPlayer.Spark.Notifications`, alias `notifications`) tells a user something through
up to three channels at once, chosen by the user per organization:

- **in-app**: a `Notification` document, shown in the bell and the list. The type and its queries are
  `"live": true`, so an open page receives it over the one live socket (`/spark/live`, see
  [live updates](guide-live-updates.md)). There is no second channel and nothing to call;
- **mail**: one MailManager mail (`SparkNotifications/Notification`, en + nl);
- **Web Push**: one push per subscribed device, also when no tab is open (the PWA's service worker shows it).

The bell is also where an action's own message appears (`IClientAccessor.Notify`, the `notify` client operation): one
UI system for both, see [Angular](#angular-mintplayerng-sparknotifications). Anonymous visitors can be notified too
([below](#anonymous-visitors)).

Decisions and evidence: [seo_analyzer_PRD.md](seo_analyzer_PRD.md) X10, D20, D30, D38 and §12 "M6";
[notification_bell_PRD.md](notification_bell_PRD.md) (#710: the bell, visitors, the decoupling). Package
references: [notifications](../libs/notifications/MintPlayer.Spark.Notifications/README.md),
[Web Push](../libs/webpush/MintPlayer.Spark.WebPush/README.md).

## Setup

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>(); // optional: accounts (mail addresses, languages, push)
    spark.AddMessaging();          // every push is a durable outbox message
    spark.AddMailManager();        // optional: the mail channel
    spark.AddNotifications();      // options: Spark:Notifications, or AddNotifications(o => …)
});
```

The package does not reference Authorization (#710). It reads accounts through `ISparkAccountDirectory`
(`MintPlayer.Spark.Abstractions.Authentication`), which Authorization registers. Without a directory every id is taken
at its word and gets the in-app channel only; startup refuses Web Push configured without one.

```json
// App_Data/security.json — startup is refused without the binding
"bindings": { "notifications:users": [ "Users" ] }
```

Web Push needs a VAPID key pair. Make it once and keep the private key out of `appsettings.json`
(user secrets, environment variables, a vault):

```csharp
var keys = MintPlayer.Spark.WebPush.VapidDetails.Generate("mailto:ops@example.com");
// keys.PublicKey → Spark:Notifications:WebPush:PublicKey, keys.PrivateKey → …:PrivateKey, subject → …:Subject
```

Without the three values Web Push is off (`GET /spark/notifications/push/key` answers 404) and the other channels
work. With them, startup refuses keys that are malformed or not a pair, and a missing `AddMessaging()`.

## Notifying

```csharp
public sealed partial class AlertService
{
    [Inject] private readonly ISparkNotifier notifier;

    public Task ScoreDroppedAsync(Site site, string userId, int from, int to, CancellationToken ct)
        => notifier.NotifyAsync(new SparkNotification
        {
            UserIds = [userId],
            Title = $"Score dropped to {to}",
            Body = $"{site.Origin} went from {from} to {to}.",
            Url = $"/sites/{site.Id}",            // app-relative; mails and pushes get the public base URL in front
            OrganizationId = site.OrganizationId,  // picks each user's preference for it
            Category = "score-drop",               // a newer push of the same kind replaces an undelivered one
            Urgent = true,
        }, ct);
}
```

- The in-app documents and the push messages are written in **one** `SaveChanges` (the Messaging outbox), so a
  failure stores nothing. Mails are queued by MailManager right after.
- A mail goes only to a **confirmed** address (`EmailConfirmed`): an X or Entra sign-in without a verified email
  proves nothing about the address.
- Unknown user ids are skipped. Title ≤ 200, body ≤ 2 000 characters; a push carries the first 1 000.
- Links need a public base URL: `SparkNotificationsOptions.PublicBaseUrl` (`Spark:Notifications:PublicBaseUrl`), else
  `Spark:Auth:PublicBaseUrl` from configuration. A value set only in code on the Authorization options is no longer
  read (#710). Without one, links stay relative and a warning is logged.

## Anonymous visitors

A visitor who is not signed in can be notified too, for work that finishes after the request (an export, a report):

```csharp
[Inject] private readonly ISparkCurrentPrincipal principal;

var visitorId = principal.EnsureVisitorId();   // issues the visitor cookie when there is none; null for a signed-in caller
await messageBus.DelayBroadcastAsync(new ReportMessage(visitorId), TimeSpan.FromSeconds(5));
// … later, in the recipient:
await notifier.NotifyAsync(new SparkNotification { VisitorIds = [message.VisitorId], Title = "Your report is ready" });
```

- The visitor id lives in `spark.visitor` (HttpOnly, Data Protection, 24 h sliding: `Spark:Visitor:Lifetime`;
  `Spark:Visitor:SessionCookie` makes it a session cookie). `spark.visitor.present=1` tells the client one exists.
  Both are set only when `EnsureVisitorId()` runs, that is when the visitor asked for something that answers later,
  never on a plain visit: they are strictly necessary for a service the visitor requested. Say so in your cookie notice.
- A visitor gets the **in-app** channel only, and the document expires with the cookie (`Lifetime`), not after the
  model's 90 days. An id without the `visitor:` prefix addresses nobody, so a user id can never be passed as one.
- Only that browser reads its rows, over HTTP and `/spark/live`. Marking read needs the antiforgery token, as any
  cookie-authenticated POST does. Web Push and preferences stay for accounts.
- **Signing in claims them:** the visitor's notifications move to the account (with the model's expiry) and both cookies
  are cleared (`NotificationVisitorClaim`, an `ISparkSignedInHandler`). A job that finishes after the sign-in still
  addresses the old visitor id, which nobody reads any more.
- The slot must admit anonymous callers: `"notifications:users": [ "Anonymous visitors", "Signed-in users" ]` (group
  names from your `groups` block).
- Client: add `withSparkVisitor()` to `provideHttpClient(...)`. When a response sets the presence cookie it replaces the
  live socket, which still carries the handshake's cookies, so the notification reaches the open tab.

QnA's `RemindMe` action on a question is the working example.

## Channels per organization

A user picks channels in **Notifications → Preferences** (the `NotificationPreference` type: `OrganizationId`,
`InApp`, `Email`, `Push`, one row per organization, the caller's own rows only). For a notification about
organization O the channels are, in order:

1. the user's row for O;
2. else the user's row without an organization (their default);
3. else `Spark:Notifications:Defaults` (all on).

The package does not reference Organizations (an app without organizations keeps one default row per user), so
`OrganizationId` is plain text in its model. An app with organizations can make it a reference in its own model
layer.

## Web Push

The library is hand-rolled on .NET's own crypto (D38): RFC 8291 `aes128gcm` encryption and RFC 8292 VAPID,
checked byte for byte against the RFC 8291 Appendix A vectors.

- A browser subscribes through `POST /spark/notifications/push/subscribe` (`PushSubscription.toJSON()`); one
  document per device, `PushSubscriptions/{userId}/{hash of the endpoint}`, at most 20 per user (the oldest goes).
- **Only known push services** are accepted (Google `fcm.googleapis.com`, Mozilla `push.services.mozilla.com`,
  Apple `push.apple.com`, Microsoft `notify.windows.com`, and their subdomains). The endpoint comes from a browser:
  without the list a user could make the server POST to any public URL. Change it with
  `Spark:Notifications:WebPush:AllowedPushServices` (a configured list replaces the default).
- Pushes go out through the SSRF-safe client (X2, [`AddSafeHttp`](../libs/spark/MintPlayer.Spark/Http)), with no
  redirects and a 64 KiB answer cap (`Spark:Notifications:WebPush:Http`).
- The `spark-webpush` queue sends them: **404/410** deletes the device, **429/5xx** retries with backoff (up to
  8 attempts over about 10 hours), anything else (400/403: VAPID or keys; 413: too large) parks the message as
  failed. `TTL` is `Spark:Notifications:WebPush:TimeToLive` (1 day).
- The payload is Angular's service-worker format, so `withServiceWorker()`'s `ngsw-worker.js` shows it
  with no code of yours. A click uses `focusLastFocusedOrOpen`: an open tab of the app is focused and
  `SparkNotificationClicks` (ng-spark `pwa`) navigates it in-app to `data.url`; a closed app opens at that URL.
  `SparkNotificationsService` marks the clicked notification read (`data.notificationId`) when the click reaches
  an open page.
  (`navigateLastFocusedOrOpen` would reload the open tab first.)
- **Apple:** iOS/iPadOS 16.4+ deliver only to a web app added to the Home Screen; subscribe from a click; every
  push must show a notification (they always do here).

### Measured (spike S12, 2026-10-10)

Chrome 154 on Windows subscribed on `http://localhost` (an FCM endpoint); five pushes sent by the library
through the SSRF-safe client: **5 × 201**, 382 ms for the first (TLS to FCM) then 45–66 ms, and all five decrypted
by the browser's service worker. A forged endpoint path: **410** → `SubscriptionGone`. A private address: refused by
the SSRF guard before sending. Encryption: about 2.2 ms per message (a fresh P-256 key each time). Edge, Firefox
and Safari were not measured (PRD §11 Q-M6a).

## Rows and rights

- The package grants `QueryRead` + `Delete` on `Notification` and `PushSubscription`, and every verb on
  `NotificationPreference`, to `notifications:users`. Every row is its user's (or its visitor's) alone, on every path,
  the live socket included: anyone else gets the missing-row 404, or the uniform 401 when anonymous.
- Marking read is `POST /spark/notifications/read` with `{ ids }`, or no ids for all unread.
- Deleting an account deletes its notifications, devices and preferences; the personal-data export gains a
  `notifications` section (devices without their endpoints).
- `Notification` expires after 90 days (`expiresAfter`, X1); change it in your own model layer.

## Angular (`@mintplayer/ng-spark/notifications`)

```html
<spark-notification-bell />        <!-- unread badge, list, pop-out, mark all as read, push opt-in -->
<spark-push-toggle />              <!-- the opt-in alone, e.g. on an account page -->
```

The bell is the one place Spark shows notifications (#710; `<spark-toast-container>` is gone). Put it in the shell's
topbar of every app that uses `withClientOperations()`:

- **The list** (`<spark-notification-list>`) holds the stored notifications and this session's `notify` messages,
  newest first. Session entries are kept in memory (`SparkSessionNotifications`, at most 50) and can be closed; opening
  the menu marks them read. They are not stored, so a reload drops them, and the badge with them.
- **The pop-out** shows what arrives below the bell: at most 3, each for its `durationMs` or 6 s. An error, or a
  duration of 0, stays until closed; hovering pauses. Nothing pops out while the menu is open, for what the first
  result already held, on the server, or twice for one stored notification in a browser (`localStorage`).
- Without `withNotifications()` (no server package) the bell lists session messages only. In dev mode
  `SparkSessionNotifications` warns once when a message arrives and no bell is rendered.

- `SparkNotificationsService` (root): `unreadCount` (stored + session), `serverUnreadCount`, `recent` (live
  `MyNotifications`), `entries` (both, as `SparkBellEntry`), `arrived`, `following` (signed in, or an identified
  visitor), `choose(entry)`, `markAllRead()`, `dismiss(entry)`, `markRead(ids?)`, `open(notification)` (marks read,
  navigates in-app for same-origin URLs), and push: `pushSupported`, `pushState` (`unsupported` | `denied` | `off` |
  `on`), `pushConfigured`, `enablePush()` (asks permission inside the click, subscribes through `SwPush`, posts the
  subscription; a refused device is unsubscribed again), `disablePush()`. Both lists are subscriptions on the one live
  socket.
- `withNotifications({ recentCount, listLink })` turns the stored notifications on and changes the defaults (8,
  `['/query', 'notifications']`).
- Needs `@angular/service-worker` (for `SwPush`) and `withServiceWorker()`. Push is `unsupported` where
  there is no Notification API, `PushManager` or service worker (iOS outside an installed app).

## Testing

`tests/MintPlayer.Spark.Tests/Notifications/NotificationTests.cs` boots the organizations test host with the
package and Messaging, and swaps the network of the package's SSRF-safe client for a fake push service:
`spark.Services.AddHttpClient(SparkNotificationsExtensions.WebPushHttpClient).ConfigurePrimaryHttpMessageHandler(() => fake)`.
It decrypts what the fake received with the test's own browser key.
