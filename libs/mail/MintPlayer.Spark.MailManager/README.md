# MintPlayer.Spark.MailManager

Outgoing mail for MintPlayer.Spark, as building blocks configured through `IConfiguration`
(`Spark:Mail`). The framework does not decide for the app: the transport, the relay's security, the
sender, the culture default, the lanes' pacing, bounce handling — each is a setting or a replaceable
service.

- **Templates**: MJML + Scriban files in the app's repository, one file per culture, with a fallback
  chain. Embedded defaults (the account mails) can be overridden file by file.
- **Queued, never inline**: a mail is a Spark Messaging message on `mail-transactional` or `mail-bulk`,
  so a relay outage delays mail instead of failing the request, and retries render the same mail.
- **Transports**: SMTP (MailKit), an `.eml` pickup folder, or your own.
- **Safety**: token-carrying payloads encrypted with Data Protection and scrubbed once done;
  per-mail expiry; a suppression list checked at queue and send time; 5xx refusals logged loudly.
- **Bulk and bounces**: campaign fan-out (one mail per recipient), one-click `List-Unsubscribe`
  (RFC 8058), VERP + a bounce endpoint with a pluggable DSN parser.

## Setup

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddMessaging();     // required: mail is queued through Spark Messaging
    spark.AddMailManager();   // binds Spark:Mail
});
```

```jsonc
"Spark": {
  "Mail": {
    "From": { "Address": "noreply@app.example", "Name": "My App" },
    "ApplicationName": "My App",          // every template sees it as app_name
    "DefaultCulture": "en",
    "Smtp": { "Host": "smtp-relay", "Port": 587, "Security": "None" },   // or:
    // "PickupFolder": "mail-pickup",
    "PublicBaseUrl": "https://app.example" // unsubscribe links; falls back to Spark:Auth:PublicBaseUrl
  }
}
```

Secrets (`Spark:Mail:Smtp:Password`, `Spark:Mail:Bounces:Endpoint:Secret`) belong in environment
variables or user secrets (`Spark__Mail__Smtp__Password`), never in `appsettings.json`.

**Startup refuses**: no transport; both `Smtp:Host` and `PickupFolder`; no `From:Address`; no
`spark.AddMessaging()`; a template file that does not parse; the bounce endpoint enabled without a
secret of 32+ characters; and a template with no neutral file (a warning in Development).

### Transports

| Setting | Transport |
|---|---|
| `Spark:Mail:Smtp:Host` | SMTP through MailKit, one connection per mail. `Security`: `Auto` (default: TLS on 465, STARTTLS when offered), `None` (never TLS, even when the relay advertises STARTTLS — for a relay on a private network), `StartTls` (required), `SslOnConnect`. `UserName`/`Password` when the relay authenticates. |
| `Spark:Mail:PickupFolder` | Each mail written as `{deliveryId}.eml` (relative to the content root). Development and demo apps. |
| `spark.AddMailTransport<T>()` | Your `ISparkMailTransport` (an HTTP API provider, a test double). Replaces both. |

**Failure classification**: an SMTP 5xx reply (sender or recipient refused) and an authentication
failure throw `NonRetryableException` — dead-lettered at once and **logged as an error**, because a
relay refusing the sender (a `553` from Postfix `ALLOWED_SENDER_DOMAINS`) refuses every mail the same
way. 4xx replies, dropped connections and timeouts are retried with the lane's backoff.

### Development

`Spark:Mail:Development:RedirectTo` sends every mail to one address (the original recipient is in
`X-Spark-Original-To`; a warning is logged outside Development). Or use the pickup folder.

## Templates — multi-language, file based (owner decision, #460 M8)

Templates live in the app's repository, managed by developers, under `Spark:Mail:Templates:Path`
(default `Templates/Mail`, relative to the content root; copy them to the output/publish folder):

```
Templates/Mail/
  Welcome.mjml          ← neutral / default file: required
  Welcome.nl.mjml       ← language
  Welcome.nl-BE.mjml    ← culture
  Welcome.nl.txt        ← optional plain-text part (otherwise derived from the HTML)
  _Footer.mjml          ← a partial (mj-include), not a template
```

```xml
<ItemGroup>
  <Content Include="Templates\Mail\**\*" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
</ItemGroup>
```

**Resolution.** For a mail in `nl-BE`: `Welcome.nl-BE.mjml` → `Welcome.nl.mjml` → `Welcome.mjml`. The
same chain picks the `.txt` part and every `mj-include`. A missing localized variant falls back
silently (logged at Debug). A template **name** without its neutral file fails startup (warns in
Development): a recipient whose culture has no variant would otherwise get nothing.

**The subject** is the template's `<mj-title>`, rendered like the rest.

**Which culture.** Chosen per mail when it is queued, never from the ambient request culture (mail
renders on a messaging worker, where `CurrentUICulture` belongs to nobody):

1. `SparkMailRequest.Culture` — explicit;
2. the recipient's stored preference — every `ISparkMailRecipientCulture`, first answer wins (the
   Authorization package registers one reading `SparkUser.PreferredCulture`);
3. `Spark:Mail:DefaultCulture`.

The resolved culture is **stored on the queued message**, so a retry renders the same language even if
the preference changed meanwhile. Numbers and dates are formatted in it (`{{ amount | math.format 'N2' }}`
→ `1.234,50` in `nl-BE`; `{{ due | date.to_string '%d %B %Y' }}` → `05 januari 2026`).

**Layers.** The app's folder first, then stores added with `spark.AddMailTemplateStore(...)`, then
embedded defaults. Precedence is **per file**: an app's `SparkAuth/PasswordReset.mjml` replaces the
shipped neutral file, while the shipped `SparkAuth/PasswordReset.nl.mjml` still serves Dutch until the
app adds its own. A library ships defaults as embedded resources:

```xml
<EmbeddedResource Include="Mail\**\*.mjml" LogicalName="SparkMail/%(RecursiveDir)%(Filename)%(Extension)" WithCulture="false" />
```
```csharp
services.AddSparkMailTemplates(typeof(MyLibrary).Assembly, "SparkMail/");
```

⚠️ `WithCulture="false"` is load-bearing: without it MSBuild moves every `Name.{culture}.mjml` into a
satellite assembly under the neutral file's name (measured, spike S-M1). The resolver is
`ISparkMailTemplateResolver`; replace it for another scheme (a database, a CMS).

**Scriban rules.** Strict: an unknown variable **or member** fails the render (a typo never sends a
mail with a hole). Every string value is HTML-escaped before the template sees it — Scriban escapes
nothing itself — so write `{{ name }}`, never `{{ name | html.escape }}` (the `.txt` part gets raw
values). Data is any object that serializes to a JSON object; ISO date strings become dates. `app_name`
is always defined (`Spark:Mail:ApplicationName`, or null).

**Test every template** in your own test project:

```csharp
await SparkMailTemplateTester.RenderAllAsync(services, template => sampleData[template], "en", "nl");
```

It renders each template × culture through the app's real resolver and throws one
`AggregateException` naming every failure, MJML warnings included.

## Sending

```csharp
await mailer.SendAsync(new SparkMailRequest
{
    Template = "Welcome",
    To = user.Email,
    Data = new { name = user.DisplayName, link },
    Sensitive = true,                              // carries a token or a granting link
    ExpiresAtUtc = DateTime.UtcNow.AddHours(23),   // before the token dies
});
```

- `Sensitive`: the data is `sdp1:` + Data Protection ciphertext in the queued message, and the payload
  is scrubbed once the message completes or dead-letters (#460, T4). A scrubbed dead-letter cannot be
  replayed — re-trigger the mail instead.
- `ExpiresAtUtc`: a mail still queued then (a relay outage, a throttle) dead-letters as `Expired`
  instead of arriving dead.
- `Queue`: overrides `mail-transactional`; must be a declared queue.
- `DeduplicationKey`, `Delay`: as in Messaging.
- A suppressed recipient is not queued (`SparkMailReceipt.Suppressed`); the check runs again at send.

**Delivery ids.** Each mail gets a 32-hex delivery id: `Message-ID: <{id}@{From domain}>`, the VERP
address and `SparkMailDeliveries/{id}` (status `Sent`/`Suppressed`/`Failed`/`Bounced`, the address,
`@expires` after `DeliveryRetentionDays`, default 30). Delivery is at-least-once: a crash after the
relay accepted and before the message completed sends it again, with the **same** `Message-ID`, so a
duplicate is identifiable. That is documented, not "fixed".

### Lanes

| Queue | Default | For |
|---|---|---|
| `mail-transactional` | unthrottled, 10 attempts, backoff 30 s → 2 h (about 4 h in all) | account mail, notifications |
| `mail-bulk` | 20 per minute (GCRA), same retries | campaigns |

Declared with `Configure`, so `Spark:Messaging:Queues:mail-bulk:MaxPerInterval` (etc.) overrides them.
`MaxPerInterval` is a rate with a burst allowance: right after an idle period a window can hold ~3× the
figure (measured, S-M3); state the rate alone (1 per 3 s) for no burst.

### Campaigns

```csharp
await mailer.SendCampaignAsync(new SparkMailCampaign
{
    CampaignId = "newsletter-2026-10",
    Template = "Newsletter",
    Recipients = subscribers.Select(s => new SparkMailCampaignRecipient { Email = s.Email, Culture = s.Culture }).ToList(),
    Data = new { issue = 12 },
});
```

One message fans out into one `mail-bulk` mail per recipient, deduplicated on
`{campaignId}:{recipient}` (and a UUIDv5 delivery id), so re-queuing a campaign or a crash half way
through never mails anyone twice. Never one mail with many BCCs. The recipient list is scrubbed from the
campaign message once it is fanned out. `List-Unsubscribe` is on by default for bulk.

## Suppression and unsubscribe

`ISparkMailSuppressions` — `IsSuppressedAsync`, `SuppressAsync`, `RemoveAsync` — per stream or for
every stream. Stored as `SparkMailSuppressions/{sha256(normalized email)}`: the address itself is not
kept. Fed by permanent bounces (every stream), unsubscribes (one stream) and the app.

One-click unsubscribe (RFC 8058): with `ListUnsubscribe` (default on for bulk), a mail carries
`List-Unsubscribe: <{PublicBaseUrl}/spark/mail/unsubscribe?t=…>` and
`List-Unsubscribe-Post: List-Unsubscribe=One-Click`. The token is Data Protection, valid 90 days, and
names the address and stream (default: the template name). `POST /spark/mail/unsubscribe` is
**explicitly** exempt from antiforgery — mail providers post it without a browser session.

## Bounces (opt-in)

- `Spark:Mail:Bounces:VerpDomain` → the envelope sender of each mail is
  `bounces+{deliveryId}@{VerpDomain}`; `From:` is unchanged and no `Sender:` header is added
  (measured, S-M6). The relay must accept this sender domain (Postfix `ALLOWED_SENDER_DOMAINS`) and SPF
  must cover it.
- `Spark:Mail:Bounces:Endpoint:Enabled` + `Secret` (32+ chars) → `POST /spark/mail/bounces?recipient=…`
  with the raw report as the body and `Authorization: Bearer {secret}`. The secret is compared in
  constant time **before any parser sees the body**; then `ISparkMailBounceParser` (default: RFC 3464
  DSN via MimeKit; replace with `spark.AddMailBounceParser<T>()` for a provider's format). A permanent
  failure (`Action: failed`, `5.x.x`) marks the delivery `Bounced` and suppresses the address the
  delivery record names. 204 applied, 401 wrong secret, 413 too large (`MaxBodyBytes`, 1 MiB), 429
  over 120/min, 400 unparseable, 404 when disabled. Explicitly exempt from antiforgery.

The Postfix side (pipe → curl → this endpoint) is a documented recipe in
`docs/guide-outgoing-mail.md` §8.3 (spikes S-M5, S-M5b). The pipe maps this endpoint's answer to its
exit code, so Postfix retries only what can still succeed:

| Endpoint answer | Pipe exit | Postfix |
|---|---|---|
| 2xx | 0 | delivered |
| 400, 404, 413, 422 — the report can never be accepted | 0, with a `dropped` line | delivered (dropped), logged |
| 401, 403 — wrong secret, a misconfiguration | 75 | deferred, retried until fixed |
| 429, 5xx, no answer, anything else | 75 | deferred, retried |

⚠️ 404 is also what a **disabled** endpoint answers: enable it before routing bounces to it, or the
reports that arrive meanwhile are dropped. A deferred report lives until Postfix's queue lifetime (5
days by default); when bounces pile up, look for the 401 in the app log.

## With MintPlayer.Spark.Authorization

Nothing to wire: Authorization replaces Identity's no-op `IEmailSender<TUser>` with one that queues
through `ISparkMailer` (`SparkAuth/ConfirmEmail`, `SparkAuth/PasswordReset`) and registers the
external-login link-confirmation sender (`SparkAuth/LinkConfirmation`) while MailManager is present.
Each is `Sensitive`, expires before its token, and uses `SparkUser.PreferredCulture`. Links come from
`Spark:Auth:PublicBaseUrl` (required outside Development), never the request `Host`. Registration
(`LocalCredentials = Full`) without MailManager or a sender of your own refuses startup (#460, D6)
unless `Spark:Auth:AllowUnconfirmedRegistration=true`.

## Upgrading from hand-written SMTP

Delete the sender, move its settings to `Spark:Mail:*`, and turn the body into an MJML template (the
shipped defaults cover the account mails). A `System.Net.Mail.SmtpClient` with `EnableSsl = false` is
`Security = None`.
