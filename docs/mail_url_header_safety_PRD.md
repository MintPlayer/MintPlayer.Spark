# Mail: link schemes and header values (#706) — PRD

Issue: [#706](https://github.com/MintPlayer/MintPlayer.Spark/issues/706). Plan:
[mail_url_header_safety_plan.md](mail_url_header_safety_plan.md). Branch `fix/706-mail-url-scheme`, one pull request.

## The problem

`MintPlayer.Spark.MailManager` HTML-encodes every string a caller gives a template, so a value cannot break out of
an attribute or add MJML tags. Encoding does not touch a URL's scheme. A `javascript:`, `data:` or relative value in
`href="{{ link }}"` reaches the rendered mail unchanged. No test runs the injection paths against the real
renderer, and nothing checks header values before they are queued.

## Investigation (2026-11-23, two read-only passes plus spikes S1 and S2)

**I1. The notification half of the issue is already closed.** `SparkNotifier.NotifyAsync` refuses a URL that fails
`SparkLinkUrl.IsAllowed` before anything is stored (`libs/notifications/MintPlayer.Spark.Notifications/Services/SparkNotifier.cs:41-42`,
covered by the theory in `tests/MintPlayer.Spark.Tests/Notifications/NotificationTests.cs:116-126`). `Absolute`
(:144-156) only ever sees an app path or an absolute http(s) URL. The Angular client also checks the protocol
(`libs/node_packages/ng-spark/notifications/src/spark-notifications.service.ts:239-254`). The issue's "returns any URL
unchanged" is true of `Absolute` in isolation, but not of the path into it.

**I2. The renderer has no defence of its own.**
- Every `href` in the 40 shipped `.mjml` files is `href="{{ link }}"` or `href="{{ portal_url }}"`. No template has a
  `src`.
- An app overrides any embedded template by name (`Templates/Mail/`), and campaign data reaches any variable. So the
  framework callers' care does not bind what ends up in an `href`.
- `SparkMailRenderer` (`libs/mail/MintPlayer.Spark.MailManager/Templates/SparkMailRenderer.cs:54-90`) encodes
  (:59, :168), runs Scriban (:60), then Mjml.Net (:73), and checks nothing.

**I3. A shipped caller already puts a non-URL into `href`.** `SparkAuthMail.SendPasswordResetCodeAsync`
(`libs/authorization/MintPlayer.Spark.Authorization/Identity/SparkAuthMail.cs:59-64`) passes the reset *code* as
`link`. `PasswordReset.mjml:15-16` renders it as a button, `<a href="CODE">`, which is a relative link in a mail. Its
own remark says "a code is sent as a reset mail without a button", but the template has no such branch.

**I4. Headers (spike S2, `MailSpike706Tests.S2_mimekit_and_crlf`, MailKit 4.18.2):**

| Input | MimeKit | Effect |
|---|---|---|
| CR/LF in the display name (`ToName`) | RFC 2047-encodes it: `To: =?utf-8?q?Eve=0D=0ABcc=3A?= "victim@evil.example" <to@…>` | no new header, but a mangled name |
| CR/LF in a header value (`X-Spark-Campaign`, `X-Spark-Template`, `X-Spark-Original-To`) | strips the line break: `X-Spark-Campaign: c1Bcc: victim@evil.example` | no new header, silently altered value |
| CR/LF (or any non-address) in `To` | `new MailboxAddress(name, address)` throws `ParseException` | thrown **in the handler**, not at `SendAsync` |

No input in S2 produced a header injection. The last row is a defect of its own, though:
- `SparkMailHandler` turns only `SparkMailTemplateException` and `CryptographicException` into `NonRetryableException`
  (`libs/mail/MintPlayer.Spark.MailManager/Messaging/SparkMailHandlers.cs:45-50`).
- The `MailboxAddress` is built after that block (:54, :104), so a `ParseException` is retried like a transport outage.
  The transactional lane retries for about four hours (`docs/guide-outgoing-mail.md` §0).
- `SparkMailer.SendAsync` checks only that `Template` and `To` are not blank (`SparkMailer.cs:40-41`), so the caller
  never hears about it.

**I5. What Mjml.Net emits (spike S1).** For `SparkAuth/PasswordReset` the rendered HTML has exactly the template's two
`href`s: always double-quoted and entity-encoded (`&amp;`, `&quot;`). There is no `src` or `background`. With
`link = "javascript:alert(1)"`, `href="javascript:alert(1)"` is in the output (`MailSpike706Tests.S1_…`: "javascript
link reaches href: True"). Two more facts, both measured in `MailRendererSafetyTests`:
- An empty `href` on `mj-button` is dropped by Mjml.Net (no `href=""` in the output).
- An `href='…'` in raw HTML inside `mj-text` is rewritten with double quotes.

## Decisions

**D1. The renderer checks every `href` and `src` in the rendered HTML.** It does not use a URL-typed template value.
- Every template shares one `{{ var }}`-in-an-attribute pattern, and an app template or campaign can use any
  variable. A typed value only protects the callers who opt in.
- The rendered HTML is what the mail client follows, after Scriban, includes and MJML.
- Mjml.Net's output is regular (I5), so one attribute pattern over the HTML reads every link. Single quotes and
  unquoted values are read too, for an app template that writes raw `<a href='…'>`.

**D2. Allowed values:**
- An absolute `http`/`https` URL with a host, by the existing rule `SparkLinkUrl.IsAllowed`. An app path is
  **refused**: a mail has no base URL, so `/runs/7` is a dead link.
- `mailto:` in an `href`, with no whitespace or control characters.

Everything else is refused: `javascript:`, `data:`, `file:`, a relative path, an empty value and credentials.
`SparkLinkUrl` (`libs/spark/MintPlayer.Spark.Abstractions/SparkLinkUrl.cs`) stays the one rule. The mail check adds
only `mailto:` and leaves out app paths.

**D3. A refused link fails the render: `SparkMailTemplateException`, naming the template and attribute, not the
value.** The handler already records that as `Failed` and does not retry (`SparkMailHandlers.cs:45-50`). Stripping
the link instead would send a mail whose button does nothing, a silent defect. The value is left out of the message
because it may be personal data.

**D4. `PasswordReset[.nl].mjml` shows a `link` that is not an http(s) URL as a code, in text, with no button.** The
branch is `{{ if link | string.starts_with "http" }}`, so it needs no new variable. This fixes I3 and keeps the code
mail working under D3.
- A separate `code` variable was the first idea. Under `StrictVariables` (`SparkMailRenderer.cs:99`) it would break
  every caller and every app override that does not define it, for example `MailManagerTests.cs:122-128` and
  `MailSpikeM2Tests`.
- A value like `httpx` takes the button branch, and the D1 check then refuses it, so it fails loudly.

**D5. `SparkMailer` refuses control characters in header-bound values, at the call.** The values are `To`, `ToName`,
`Template` and `Stream`, and on a campaign `CampaignId`, `Template`, `Stream` and each recipient's `Email`/`Name`. A
refused value throws `ArgumentException`. S2 shows MimeKit already prevents injection, so this is not a security
fix. It makes a bad value the caller's error instead of a silently mangled header (I4 rows 1-2).

**D6. A recipient address MimeKit cannot parse is not retried.** The handler builds the recipient inside the
non-retryable block and maps `ParseException` like a template error: `Failed`, `NonRetryableException`. The mailer
also refuses a `To` that `new MailboxAddress(name, to)` rejects. That is the handler's own construction, so the
common case is caught at the call (I4 row 3). `TryParse` is not used: it accepts `Name <a@b>`, which the handler's
constructor then rejects. A
campaign recipient is a per-mail message, so the handler path covers it.

**D7. Tests run against the real renderer, mailer and handler** (the issue's acceptance), not against a copy of the
encoding as `MailSpikeM4Tests` does.

## Acceptance

- [x] A non-http(s) URL (and an app path, and an empty value) never reaches an `href`/`src` in a rendered mail: the
  render refuses it (D1-D3). Red first against `SparkMailRenderer`.
- [x] A quote breakout and an `<mj-raw>` injection attempt, through the real renderer, stay inside their attribute or
  text (D7).
- [x] A reset code renders as text, not as a link (D4).
- [x] A CR/LF in `To`/`ToName`/`Template`/`CampaignId`/a recipient is refused by `SparkMailer` (D5).
- [x] An unparseable address reaching the handler is `Failed` and not retried (D6).

## Out of scope

- The plain-text part. It holds no links a client follows as markup.
- The `background` attribute and CSS `url()`. No template emits them (I5). The check reads `href` and `src` only,
  so a future template that sets an image background needs a look first.
