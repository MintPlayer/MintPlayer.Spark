# MintPlayer.Spark.MailManager

Outgoing mail for MintPlayer.Spark, as building blocks configured through `IConfiguration`
(`Spark:Mail`). The framework does not decide for the app: the transport, the relay's security, the
sender, the culture default, the lanes' pacing, bounce handling — each is a setting or a replaceable
service.

- **Templates**: MJML + Scriban files in the app's repository, one file per culture, with a fallback
  chain. Embedded defaults (the account mails) can be overridden file by file.
- **Queued, never inline**: a mail is a Spark Messaging message on `mail-transactional` or `mail-bulk`,
  so a relay outage delays mail instead of failing the request, and retries render the same mail.
- **Transports**: SMTP (MailKit), Mailpit for local development, an `.eml` pickup folder, or your own.
- **Safety**: token-carrying payloads encrypted with Data Protection and scrubbed once done;
  per-mail expiry; a suppression list checked at queue and send time; 5xx refusals logged loudly.
- **Bulk and bounces**: campaign fan-out (one mail per recipient), one-click `List-Unsubscribe`
  (RFC 8058), VERP + a bounce endpoint with a pluggable parser for DSNs and ARF complaints.

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

**Startup refuses**: no transport; two registered transports; both `Smtp:Host` and `PickupFolder`
(for the fallback, `UseSmtpTransport()` and `UsePickupFolderTransport()`); no `From:Address`; no
`spark.AddMessaging()`; a template file that does not parse; the bounce endpoint enabled without a
secret of 32+ characters; `Development:RedirectTo` in Production; in Development, a transport that
delivers to real recipients without `Development:RedirectTo`; and a template with no neutral file (a
warning in Development).

### Transports

Register one, after `AddMailManager()` (`spark.AddMailManager().UseMailpitTransport()`):

| Registration | Transport | Real recipients? |
|---|---|---|
| `UseSmtpTransport(smtp => …)` | SMTP through MailKit, bound from `Spark:Mail:Smtp` (`Host` required), one connection per mail; the callback runs after binding. `Security`: `Auto` (default: TLS on 465, STARTTLS when offered), `None` (never TLS, even when the relay advertises STARTTLS — for a relay on a private network), `StartTls` (required), `SslOnConnect`. `UserName`/`Password` when the relay authenticates. | yes — **no** when the host is loopback (`localhost`, `127.0.0.0/8`, `::1`) |
| `UseMailpitTransport(mailpit => …)` | Plain SMTP into a local [Mailpit](https://mailpit.axllent.org/): `localhost:1025`, no TLS, no auth; `Spark:Mail:Mailpit:{Host,Port,Tags}` and the callback override it. SMTP, not Mailpit's JSON send API, so Mailpit shows the exact MIME a relay would get. Adds Mailpit's `X-Tags`: the template (`/` → `-`), the lane (`transactional`/`bulk`) and `Tags`, for filtering in its UI. | no |
| `UsePickupFolderTransport(folder?)` | Each mail written as `{deliveryId}.eml` into `Spark:Mail:PickupFolder` or `folder` (relative to the content root). | no |
| `AddMailTransport<T>()` | Your `ISparkMailTransport` (an HTTP API provider, a test double). | yes, unless `T` overrides `DeliversToRealRecipients => false` |

Explicit registration wins. **With none registered**, the configuration picks, as before: SMTP when
`Spark:Mail:Smtp:Host` is set, else the pickup folder when `Spark:Mail:PickupFolder` is set — so an
existing configuration (the CodeCoverage compose file) keeps working unchanged. A second
registration is a startup error.

`ISparkMailTransport` has two default members a custom transport may override:
`SendAsync(…, SparkMailSendContext context, …)` (the delivery id, template, stream and lane; the
default forwards to the plain `SendAsync`) and `bool DeliversToRealRecipients => true`.

Mailpit, locally:

```
docker run -d --name mailpit -p 1025:1025 -p 8025:8025 axllent/mailpit
```

then `spark.AddMailManager().UseMailpitTransport()`; the UI is at http://localhost:8025.

**AutoStart** (opt-in): `Spark:Mail:Mailpit:AutoStart=true` makes the host start Mailpit itself —
**only in the Development environment** (elsewhere it is ignored with one Information line).

| `Spark:Mail:Mailpit:…` | Default | |
|---|---|---|
| `AutoStart` | `false` | |
| `Mode` | `Binary` | `Binary` runs `mailpit` from `PATH` (or `ExecutablePath`) with `--smtp 127.0.0.1:{Port} --listen 127.0.0.1:{UiPort}`; it is **never downloaded**. `Docker` runs `docker run --rm -d --name {ContainerName} --label spark.mailpit.owner={ApplicationName} -p {Port}:1025 -p {UiPort}:8025 axllent/mailpit:{ImageTag}`. |
| `ExecutablePath` | — | Binary mode. |
| `UiPort` | `8025` | |
| `ContainerName` | `spark-mailpit` | Docker mode; an existing container with this name is reused (started when stopped). |
| `ImageTag` | `v1.31.3` (pinned) | Docker mode. |

- It runs as a hosted service that **never blocks startup** and never waits: the mail lanes' retry
  covers the first seconds. The UI URL is logged once Mailpit is up.
- Whatever already **listens on the SMTP port** is reused, and nothing is started.
- A missing binary is one warning with install hints (`winget install axllent.mailpit`,
  `scoop install mailpit`, or https://github.com/axllent/mailpit/releases); Docker not installed or its
  daemon down is one warning. The app starts either way.
- **Shutdown stops only what this process started**: in binary mode the whole process tree is
  killed; in Docker mode `docker stop` is sent only to a container that carries
  `spark.mailpit.owner={ApplicationName}` **and** that this process created or started — a container
  that was already running, or that someone else made, stays up.
- **Windows, binary mode**: the Mailpit process is put in a Job Object with
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, held for the host's lifetime, so Mailpit dies with the host
  even when it crashes or the debugger stops it. If the assignment fails, one warning, and the
  graceful stop at shutdown still applies.
- **Linux and macOS, binary mode**: there is no reliable parent-death hook for a process started
  with `Process.Start`, and no polling watchdog by design. The graceful stop at shutdown applies; a
  Mailpit left over from a crash is harmless, because the next run finds it listening on the port and
  adopts it.
- Every command is started with an argument list, never through a shell.

**Failure classification**: an SMTP 5xx reply (sender or recipient refused) and an authentication
failure throw `NonRetryableException` — dead-lettered at once and **logged as an error**, because a
relay refusing the sender (a `553` from Postfix `ALLOWED_SENDER_DOMAINS`) refuses every mail the same
way. 4xx replies, dropped connections and timeouts are retried with the lane's backoff.

### Development

`Spark:Mail:Development:RedirectTo` sends every mail to one address (the original recipient is in
`X-Spark-Original-To`). In Development it is silent; in any other non-Production environment
(Staging, E2E, a custom name) it still redirects and logs a warning per mail; in **Production**
(`IHostEnvironment.IsProduction()`) startup refuses it, because it would divert every user's mail.

**Development fails closed.** In the Development environment, a transport whose
`DeliversToRealRecipients` is true (remote SMTP, a custom transport that does not say otherwise)
refuses startup unless `Development:RedirectTo` is set — a developer machine pointed at a real relay
would otherwise mail real people (a copied database, a seeded address). Either set the redirect in
user secrets:

```
dotnet user-secrets set "Spark:Mail:Development:RedirectTo" "you@example.com"
```

or catch everything locally with `UseMailpitTransport()` (the recipe above; UI at
http://localhost:8025). Mailpit, the pickup folder and loopback SMTP need nothing.

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
| `mail-transactional` | unthrottled, **`High` priority**, 10 attempts, backoff 30 s → 2 h (about 4 h in all) | account mail (reset, confirm), notifications |
| `mail-bulk` | 20 per minute (GCRA), **`Low` priority**, same retries | campaigns |

Declared with `Configure`, so `Spark:Messaging:Queues:mail-bulk:MaxPerInterval` (etc.) overrides them.
`MaxPerInterval` is a rate with a burst allowance: right after an idle period a window can hold ~3× the
figure (measured, S-M3); state the rate alone (1 per 3 s) for no burst.

The priorities (#460 M16) make Spark Messaging's single feeder claim account mail ahead of a campaign's
backlog in each look-ahead window, without starving the campaign (Messaging README, *Priority lanes*).
Apps get them by default; `Spark:Messaging:Queues:mail-bulk:Priority=Normal` (etc.) overrides them.

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
kept. Fed by permanent bounces (every stream), spam complaints (every stream), unsubscribes (one
stream) and the app.

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
  over 120/min, 400 unparseable, **503 when disabled**. A report for an unknown delivery id is still
  204 (logged, nothing suppressed); the endpoint never answers 404. Explicitly exempt from antiforgery.
- **Complaints (ARF, RFC 5965, #460 M16)** go to the same endpoint, with the same secret, rate limit and
  503-when-disabled: the default parser recognises `multipart/report; report-type=feedback-report`,
  reads `Feedback-Type` from the `message/feedback-report` part, and finds the delivery from
  `Original-Mail-From` (the VERP address), else the original's `Return-Path`, else the original's
  `Message-ID` (`<{deliveryId}@domain>`, which every MailManager mail carries and feedback loops usually
  keep when they redact addresses), else the address the report was delivered to. `abuse` and `fraud`
  mark the delivery `Complained` and suppress **the delivery record's address** for every stream with
  `SparkMailSuppressionReason.Complaint` (lift it with `RemoveAsync`); `not-spam`, `virus`,
  `auth-failure` and `other` are recorded on the delivery and suppress nothing. An unmatched report is
  logged and answered 204 (the relay drops it, no retry); a feedback report without the feedback part
  or `Feedback-Type` is malformed: 400, which the pipe also drops. Feedback-loop addresses and the
  Postfix side: `docs/guide-outgoing-mail.md` §8.6.

The Postfix side (pipe → curl → this endpoint) is a documented recipe in
`docs/guide-outgoing-mail.md` §8.3 (spikes S-M5, S-M5b). The pipe maps this endpoint's answer to its
exit code, so Postfix retries only what can still succeed:

| Endpoint answer | Pipe exit | Postfix |
|---|---|---|
| 2xx | 0 | delivered |
| 400, 413, 422 — the report can never be accepted | 0, with a `dropped` line | delivered (dropped), logged |
| 401, 403 — wrong secret; 404 — wrong URL (the endpoint never answers 404) | 75 | deferred, retried until fixed |
| 503 — the endpoint is disabled | 75 | deferred, delivered once it is enabled |
| 429, other 5xx, no answer, anything else | 75 | deferred, retried |

A **disabled** endpoint answers 503, so reports sent before it is enabled wait in Postfix's queue
instead of being dropped. Since the endpoint never answers 404, a 404 means the relay's URL does not
reach it (a wrong `SPARK_BOUNCE_URL`, or an app without MailManager), and the pipe retries it. A deferred report lives until
Postfix's queue lifetime (5 days by default); when bounces pile up, look for the 401 in the app log.

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
