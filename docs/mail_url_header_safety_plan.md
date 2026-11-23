# Mail: link schemes and header values (#706) — plan

PRD: [mail_url_header_safety_PRD.md](mail_url_header_safety_PRD.md). Branch `fix/706-mail-url-scheme`, one pull request.

## Status

**2026-11-23:** branch cut from master `2ac96dc0a`. Two read-only investigation passes (renderer and templates;
notifier, mailer and handlers) and spikes S1 and S2 are done. Their results are PRD I1-I5, and the decisions are D1-D7.

**M1-M5 done (2026-11-23).**
- **Red.** The new tests ran first against unchanged source: 25 failed, 5 passed.
  - The 5 that passed were the two allowed links, the https image, the quote breakout and the `<mj-raw>` injection.
    Escaping already held for those, as the issue's audit said. They stay as regression tests against the real
    renderer (D7).
  - Example red messages:
    - "Expected render to throw SparkMailTemplateException, but no exception was thrown" (`javascript:` and the rest)
    - "Did not expect mail.Html to contain href="K7Q2-9XPA"" (I3)
    - "Expected handle to throw NonRetryableException, but MimeKit.ParseException was thrown: Invalid addr-spec token"
      (I4)
  - Every `SendAsync`/`SendCampaignAsync` case: no `ArgumentException`.
- **Fix.**
  - `SparkMailRenderer.CheckLinks`: start tags only, then `href`/`src`.
  - `PasswordReset[.nl].mjml`: the code branch.
  - `SparkMailer`: `RefuseControlCharacters` and `RefuseUnbuildableRecipient`.
  - `SparkMailHandler`: `ParseException` added to the non-retryable catch.
- **One test corrected after the first green run.** The empty-link test used `mj-button`, and Mjml.Net drops an
  empty `href` there (no link, so nothing to refuse). It now refuses a raw `<a href="">` and asserts that the button
  has no `href=""`.
- **Green.** The `Mail` namespace plus `NotificationTests`: 105/106 on the first run, the one failure being the empty
  link above. `MailRendererSafetyTests` 20/20 after the correction.
- **Docs.** The MailManager README ("Links are checked for their scheme", plus the `To` bullet under Sending) and
  `guide-outgoing-mail.md` §8.1.

**NEXT: M6.** Push, open the PR, then the checks and patch coverage.

## Spikes

- **S1 (done): what Mjml.Net emits.** `tests/MintPlayer.Spark.Tests/Mail/MailSpike706Tests.cs`, `S1_…`. PasswordReset
  renders two `href`s, double-quoted and entity-encoded, with no `src`. A `javascript:` link reaches `href` today.
- **S2 (done): MimeKit and CR/LF.** Same file, `S2_…`:
  - a display name is RFC 2047-encoded;
  - a header value loses its line break;
  - an address throws `ParseException`.

  None of them injects a header (PRD I4).

The spike class stays in the repository, like `MailSpikeM2Tests`/`M4Tests`. Its output is the evidence.

## Milestones

Each milestone starts with a red test and records the red message and the green count here.

- **M1 — renderer link check (D1-D3, D7).** `MailRendererSafetyTests`, using the real renderer on the embedded
  templates plus a temp-folder app template:
  - refused: `javascript:`, `data:`, `/app/path`, `relative`, and an empty value;
  - rendered: `https://`, `http://`, and `mailto:` in a raw `<a href='…'>` app template;
  - a quote breakout and an `<mj-raw>` injection stay escaped.

  Fix: a `LinkPolicy` check over the rendered HTML in `SparkMailRenderer.RenderAsync`.
- **M2 — reset code as text (D4).** Red: the reset-code mail has no `href` holding the code. Fix: in
  `SparkAuthMail`, `code`/`link`, plus `PasswordReset.mjml` and `.nl.mjml`.
- **M3 — mailer refuses control characters (D5) and an unparseable `To` (D6).** `MailManagerTests` cases on
  `SendAsync` and `SendCampaignAsync`.
- **M4 — handler does not retry an unparseable recipient (D6).** Red: a queued message with `To = "not an address"`
  throws something other than `NonRetryableException`.
- **M5 — docs.** MailManager README: a link section and header values. A note in `docs/guide-outgoing-mail.md` §8.1.
- **M6 — checks.** Type-check, the four new or changed test classes once, push, open the PR, then CI and patch
  coverage via the codecoverage MCP.
