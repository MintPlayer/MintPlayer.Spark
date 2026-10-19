# Consent: banner, records, unsubscribe links

Spark's consent primitive (`docs/commerce_PRD.md` X21, GA9, §4.20) is generic. Commerce, challenges, analytics and
marketing mail all use it, and so can any app. It has four parts:

- **purposes** that packages and the host declare;
- a **record** per visitor (X1), account or e-mail address, holding every decision with its proof;
- the **server API** (`ISparkConsent`, `ISparkConsentLinks`, `SparkConsentGate`) and `/spark/consent` endpoints;
- the **client** in `@mintplayer/ng-spark/consent`: a banner, a settings page and an unsubscribe page, all replaceable.

---

## The legal rules it encodes

D37 requires each rule to be checked against the official text. On 2026-10-19 the EUR-Lex pages came back empty to
the agent's fetch, so the articles below were **not re-read** for this implementation. **Confirm them** against
[Regulation (EU) 2016/679 (GDPR)](https://eur-lex.europa.eu/eli/reg/2016/679/oj) and
[Directive 2002/58/EC (ePrivacy)](https://eur-lex.europa.eu/eli/dir/2002/58/oj) before relying on them.

| Rule | Source | What Spark does |
|---|---|---|
| Storing or reading information on the device needs consent, unless it is strictly necessary for the service asked for | ePrivacy Art. 5(3) | `necessary` is always granted and never asked. The visitor cookie (X1) and the record of the choice itself are strictly necessary. Everything else is a purpose. |
| Consent is a clear affirmative act. Silence, pre-ticked boxes and inactivity are not consent | GDPR Art. 4(11), Recital 32; CJEU C-673/17 *Planet49* | An undecided purpose reads as **not granted** on the server (`HasAsync`) and on the client (`has`, `whenGranted`). No box is ticked unless the visitor granted that purpose. Closing the banner records nothing. |
| The controller must be able to demonstrate consent | GDPR Art. 7(1) | Every decision stores the policy version, the time, the source (banner, settings, checkout, unsubscribe), the language and a SHA-256 of the texts shown. The history is kept (`HistoryLimit`, default 100). |
| Withdrawing must be as easy as giving | GDPR Art. 7(3) | "Reject all" sits beside "Accept all" with the same style. The settings page withdraws in one click. A refusal is recorded like a grant. |
| Consent must be informed | GDPR Art. 4(11), Art. 13(1)(e) | Each purpose has a translated title, a description and its vendors. Bump `PolicyVersion` when they change, and every earlier decision goes stale and is asked again. |
| Marketing e-mail needs prior consent. The soft opt-in covers a customer's own similar products, with a free and easy objection on every message | ePrivacy Art. 13(1)–(2) | The `marketing-mail` purpose. A checkout checkbox records it for the buyer's address (`SparkConsentSubject.Email`). Every mail carries an unsubscribe link and `List-Unsubscribe` headers. ⚠️ The soft opt-in's exact scope is a member-state matter: **confirm per country**. |
| An objection to direct marketing ends that processing | GDPR Art. 21(2)–(3) | The unsubscribe link records a refusal at once. A withdrawal is honoured even for a purpose that is no longer declared. |
| Access, portability, erasure | GDPR Art. 15, 17, 20 | Authorization's personal-data export has a `consent` section, and account deletion erases the account's records and its address's records. |

**Per-country points (⚠️ confirm, D37):**

- **Validity.** `ConsentValidity` defaults to 13 months and `RefusalValidity` to 6 months. These follow the CNIL's
  guidance (délibération n° 2020-091), which is French and not EU law. Other authorities differ.
- **"Reject all" on the first layer.** The CNIL requires it, and the EDPB cookie-banner taskforce report
  (17 January 2023) records that most authorities agree. The banner always shows it.
- **The soft opt-in's conditions** (above).

---

## Server

```csharp
var spark = builder.Services.AddSpark(builder.Configuration);
spark.AddConsent(o =>
{
    o.PolicyVersion = "2026-10";          // Spark:Consent:PolicyVersion
    o.PolicyUrl = "/legal/privacy";
});

// A purpose of your own (the built-in ones are analytics, marketing-mail, third-party-challenge, third-party-media).
builder.Services.AddConsentPurpose(new SparkConsentPurpose("video",
    TranslatedString.Of(("en", "Videos"), ("nl", "Video's")),
    TranslatedString.Of(("en", "Play videos hosted by Vimeo."), ("nl", "Video's van Vimeo afspelen.")),
    [new SparkConsentVendor("Vimeo", "https://vimeo.com/privacy")]));
```

Declaring a built-in name again replaces its texts. Do that to name the vendors you actually use.

### A package that needs consent

```csharp
services.RequiresConsent(SparkConsentPurposes.ThirdPartyChallenge, "Challenges.Turnstile");
```

`UseSpark()` refuses to start when a requirement cannot be met: either the primitive is not registered
(`spark.AddConsent()`), or the purpose is not declared, in which case the banner could never ask for it. Commerce
runs the same check in its own startup validation (§4.2).

### Gating

```csharp
// In a request: the signed-in account, else the visitor.
if (!await services.AllowsAsync(SparkConsentPurposes.ThirdPartyChallenge, ct))
    return altcha;                       // §4.12: a refusal falls back to ALTCHA, it never blocks the visitor

// In a mail job.
if (!await consent.AllowsMarketingMailAsync(userId, email, ct))
    return;
var headers = links.CreateListUnsubscribeHeaders(SparkConsentSubject.Email(email));   // List-Unsubscribe + -Post
var bodyLink = links.CreateUnsubscribeUrl(SparkConsentSubject.Email(email));
```

`AllowsAsync` answers `false` when the primitive is not registered. That is the safe direction.

### Recording outside the banner

```csharp
// The checkout's unticked "send me news about similar events" checkbox, ticked by the buyer.
await consent.RecordAsync(SparkConsentSubject.Email(buyerEmail),
    new SparkConsentChoice(new() { [SparkConsentPurposes.MarketingMail] = true }, SparkConsentSources.Checkout, language));
```

### Endpoints

| | |
|---|---|
| `GET /spark/consent/` | `SparkConsentState`: purposes, the caller's decisions, `needsPrompt` |
| `POST /spark/consent/` | `SparkConsentChoice`: records decisions. Antiforgery applies, so a cross-site page cannot grant consent in the visitor's name. A guest gets the visitor cookie here. |
| `GET /spark/consent/unsubscribe?token=` | what the unsubscribe page shows (a masked address). Changes nothing. |
| `POST /spark/consent/unsubscribe` | `{ token }`: the page's button. The token is the credential, so there is no antiforgery check. |
| `POST /spark/consent/unsubscribe/one-click?token=` | the RFC 8058 target of `List-Unsubscribe-Post`. Mapped for POST only, because link scanners prefetch GETs. |

All of them answer 404 until `AddConsent()` runs.

### Records

The id is `SparkConsentRecords/{account|visitor|email}/{key}`. An e-mail record's key is the SHA-256 of the normalised
address, so the address never appears in an id. Every write goes through a cluster-wide session, the same kind the
sign-in merge uses. A visitor's record expires with the visitor cookie (`Spark:Visitor:Lifetime`).

**Sign-in merge.** `SparkConsentVisitorMergeHandler` runs in X1's exactly-once merge. For each purpose the **newer**
decision wins, both histories are kept, and the visitor's record is deleted.

**Staleness.** A decision taken under another `PolicyVersion`, or older than its validity, reads as undecided, and the
banner asks again. Purposes in `NonExpiringPurposes` (default `marketing-mail`) stand until withdrawn, and the banner
does not ask about them; checkout and the settings page do.

---

## Client: `@mintplayer/ng-spark/consent`

```ts
// app.component.html: once, in the shell
<spark-consent-banner />

// routes
provideRouter([{ path: 'consent', children: sparkConsentRoutes() }], withComponentInputBinding())
// /consent/settings: link it from the footer, so withdrawing is as easy as giving
// /consent/unsubscribe?token=…: Spark:Consent:UnsubscribePagePath points here
```

```ts
const consent = inject(SparkConsentService);
await consent.whenGranted('analytics');       // never resolves while refused or undecided
loadAnalyticsScript();
```

- **Banner.** A non-modal region fixed to the bottom of the page, so the page stays usable. "Reject all" and "Accept
  all" share one class, sit side by side, and neither has focus by default. "Choose" opens the settings in place. It
  is hidden once every purpose it asks about is decided.
- **Settings.** One checkbox per purpose, each with its description and vendors (linked to their privacy policies).
  "Strictly necessary" is shown checked and disabled. "Reject all", "Accept all" and "Save" have the same look.
  "Withdraw all consent" appears on the page once something was granted.
- **Unsubscribe page.** Opening it changes nothing. One button unsubscribes, with no sign-in.
- **Replaceable.** Each component is built on `SparkConsentService` only, so an app can write its own.
- **Accessibility.** Native buttons and checkboxes, labelled inputs, `aria-describedby` for the descriptions, a
  polite live region for "saved", and visible focus. The axe checks in E2E (GA10, M17) cover the banner.
