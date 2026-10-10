# MintPlayer.Spark.WebPush

Web Push for .NET on built-in crypto only (`docs/seo_analyzer_PRD.md` D38):

- **RFC 8291** message encryption with the RFC 8188 `aes128gcm` content coding (one record, a fresh salt and
  ephemeral P-256 key per message), on `ECDiffieHellman`, `HKDF` and `AesGcm`;
- **RFC 8292** VAPID: an ES256 JWT (`aud` = the push service origin, `exp` 12 h, `sub`) signed with `ECDsa`,
  reused per origin until an hour before it expires;
- an **RFC 8030** sender (`TTL`, `Urgency`, `Topic`) over any `HttpClient`, which maps every answer to what
  to do next.

No package dependencies. Verified byte for byte against the RFC 8291 Appendix A test vectors
(`tests/MintPlayer.Spark.Tests/WebPush/`). In a Spark app use it through `MintPlayer.Spark.Notifications`
(`spark.AddNotifications()`), which stores subscriptions, sends through the SSRF-safe client and deletes
expired subscriptions. Guide: `docs/guide-notifications.md`.

## Use

```csharp
// once: make the key pair and keep the private key in a secret store
var keys = VapidDetails.Generate("mailto:ops@example.com");

using var vapid = new VapidAuthorization(keys);            // validates the pair and the subject
var client = new WebPushClient(httpClient, vapid);          // hand it an SSRF-safe HttpClient

var result = await client.SendAsync(
    new WebPushSubscription(endpoint, p256dh, auth),         // PushSubscription.toJSON() from the browser
    new WebPushMessage
    {
        Payload = Encoding.UTF8.GetBytes(json),             // at most 3 993 bytes (4 096 encrypted)
        TimeToLive = TimeSpan.FromDays(1),
        Urgency = WebPushUrgency.High,
        Topic = "run-42",
    });
```

| `result.Outcome` | Status | Do |
|---|---|---|
| `Delivered` | 2xx | nothing |
| `SubscriptionGone` | 404, 410 | delete the subscription |
| `RetryLater` | 429, 5xx | retry after `result.RetryAfter` (when sent) |
| `Rejected` | anything else (400/403 = VAPID or configuration, 413 = too large) | log `result.Reason`; retrying will not help |

Only transport failures throw. A malformed subscription (not https, bad keys), a payload over the limit, a
negative TTL or a topic outside `[A-Za-z0-9_-]{1,32}` are `ArgumentException`s before anything is sent.

## Platform notes

- Every current browser (Chrome, Edge, Firefox, Safari) accepts `aes128gcm`. The legacy `aesgcm` draft is not
  implemented.
- Apple: iOS/iPadOS 16.4+ only for a web app added to the Home Screen; `subscribe()` from a user gesture; every
  push must show a notification (no silent pushes); `TTL` is required.
- The endpoint comes from a browser: send through an SSRF-safe client.
