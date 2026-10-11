# MintPlayer.Spark.Notifications

Notifications for MintPlayer.Spark (`docs/seo_analyzer_PRD.md` X10, D20, D30): one call reaches a user

- **in-app**: a `Notification` document. The type and its queries are `"live": true`, so an open UI gets it over
  the one live socket (`/spark/live`, X4); there is no second channel and no notifier call to write;
- **by email**: one mail through MailManager (`SparkNotifications/Notification`, en + nl), to a confirmed address;
- **by Web Push**: one durable message per subscribed device, encrypted with the hand-rolled
  [MintPlayer.Spark.WebPush](../../webpush/MintPlayer.Spark.WebPush/README.md) (RFC 8291/8292) and sent through
  the SSRF-safe client (X2).

Each user picks the channels **per organization** (`NotificationPreference`), with a default row and configured
defaults beneath it. Guide: [docs/guide-notifications.md](../../../docs/guide-notifications.md).

## Setup

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>();
    spark.AddMessaging();          // Web Push deliveries are outbox messages
    spark.AddMailManager();        // optional: the mail channel
    spark.AddNotifications();      // Spark:Notifications
});
```

```json
// App_Data/security.json — startup is refused without the binding
"bindings": { "notifications:users": [ "Users" ] }
```

```json
// configuration (the private key in user secrets / environment, never appsettings.json)
"Spark": { "Notifications": { "WebPush": {
  "PublicKey": "BOr…", "PrivateKey": "…", "Subject": "mailto:ops@example.com" } } }
```

Make the keys once with `VapidDetails.Generate("mailto:ops@example.com")`. Without them Web Push is off and the
other channels work. With them, startup refuses bad keys and a missing `AddMessaging()`.

## Sending

```csharp
await notifier.NotifyAsync(new SparkNotification
{
    UserIds = [userId],
    Title = "Score dropped to 74",
    Body = "seotester.example lost 17 points since the last run.",
    Url = "/runs/42",                      // app-relative; mails and pushes make it absolute
    OrganizationId = site.OrganizationId,  // picks the users' preference for it
    Category = "score-drop",               // also the push Topic
    Urgent = true,                         // Urgency: high
});
```

## Endpoints (`/spark/notifications`, signed in, antiforgery on POST)

| Endpoint | Does |
|---|---|
| `POST /read` `{ ids? }` | marks those (or all unread) of the caller read |
| `GET /push/key` | `{ publicKey }`; 404 while Web Push is off |
| `POST /push/subscribe` | `PushSubscription.toJSON()`; 400 for bad keys or a push service outside `AllowedPushServices` |
| `POST /push/unsubscribe` `{ endpoint }` | forgets that device |

## Options (`Spark:Notifications`)

| Key | Default | Meaning |
|---|---|---|
| `WebPush:PublicKey`, `PrivateKey`, `Subject` | — | VAPID; all three turn Web Push on |
| `WebPush:TimeToLive` | `1.00:00:00` | how long a push service keeps an undelivered message (≤ 28 days) |
| `WebPush:AllowedPushServices` | Google, Mozilla, Apple, Microsoft | host names (and their subdomains) a subscription may point at; a configured list replaces the default |
| `WebPush:MaxDevicesPerUser` | `20` | the oldest device goes when one more subscribes |
| `WebPush:Http` | no redirects, 64 KiB, 30 s | the SSRF-safe client's limits (`SafeHttpOptions`) |
| `Defaults:InApp`, `Email`, `Push` | `true` | channels of a user without a preference |
| `MailTemplate` | `SparkNotifications/Notification` | overridable by file name in `Templates/Mail` |

The menu gains **Notifications** (the list, preferences and push devices). Deleting an account deletes its
notifications, devices and preferences; the personal-data export gains a `notifications` section.
