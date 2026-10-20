# Phone verification

A user proves they hold a phone number by entering a one-time code sent to it by SMS. The verified number is
an anti-bot signal (commerce PRD D29, X13): a platform can require one before a buyer joins a queue or buys
limited stock. It is **opt-in**; nothing of it exists until you add it.

Two packages:

- **`MintPlayer.Spark.Sms`**: the `ISparkSmsSender` seam and `SparkTestSmsSender`, a test adapter that
  records messages and delivers nothing. No RavenDB, no Spark runtime.
- **`MintPlayer.Spark.Authorization`**: phone verification on top of it, `spark.AddPhoneVerification()`.

## Turning it on

```csharp
builder.Services.AddSparkTestSms();              // tests and development; a vendor adapter in production
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>();
    spark.AddPhoneVerification(o =>
    {
        o.DefaultCountryCallingCode = "32";        // read "0470 12 34 56" as +32 470 12 34 56
        o.AllowedCountryCallingCodes = ["32", "31", "33"];
    });
});
```

`AddPhoneVerification` maps three routes under `/spark/auth/manage` (signed-in callers, antiforgery on
the POSTs):

| Route | Body | Answers |
|---|---|---|
| `GET phone` | | `{ phoneNumber, phoneNumberVerified, pendingPhoneNumber, pendingExpiresAtUtc }` |
| `POST phone/send` | `{ phoneNumber }` | 200 `{ outcome: "codeSent", phoneNumber, expiresAtUtc }`; 400 `invalidNumber` / `countryNotAllowed`; 429 with `Retry-After` (`rateLimited`); 502 `notSent` (the vendor refused) |
| `POST phone/verify` | `{ code }` | 200 `{ outcome: "verified", phoneNumber }`; 400 `wrongCode` (with `attemptsLeft`), `expired`, `tooManyAttempts`, `noPendingCode`, `numberInUse` |

Every answer carries an `outcome` the client can switch on; a 400 is a validation problem keyed by
`phoneNumber` or `code`. Server code uses `ISparkPhoneVerification` directly (`SendCodeAsync`,
`VerifyAsync`, `GetStatusAsync`).

A verified number lands on the user as **`SparkUser.PhoneNumber`** (E.164) and
**`SparkUser.PhoneNumberConfirmed = true`**, Identity's own fields: read those to gate a feature. Verifying
another number replaces it; until the new code is entered the old number stays verified.

## E.164

Numbers are stored and sent as E.164: `+`, the country calling code and the subscriber number, 15 digits at
most (`SparkPhoneNumbers.TryNormalize`). Accepted input: `+32 470 12 34 56`, `0032470123456`, and with
`DefaultCountryCallingCode` a national number with its trunk `0` (`0470/12.34.56`). Spaces, hyphens, dots,
slashes and parentheses are dropped; anything else refuses the number. Without a default country a national
number is refused: guessing the country sends the code to a stranger.

⚠️ It is a syntax check, not a numbering-plan check, and the trunk-`0` rule is wrong for the few countries
that keep the `0` internationally. Whether the number is real is proven by the code arriving.

## Codes

- **Random, short-lived, single-use.** `CodeLength` digits (6) from a cryptographic generator, valid for
  `CodeLifetime` (10 minutes). A new send replaces the pending code. A matching code is consumed before the
  user is changed, so a replay finds nothing pending.
- **Hashed.** Only an HMAC-SHA256 of the code is stored, under a random per-code key, over the user id, the
  number and the code: a code is good only for the account and number it was sent for, and a database read
  yields no code.
- **Attempt cap.** `MaxAttemptsPerCode` (5) wrong guesses; the last one voids the code. Each guess is
  written under optimistic concurrency, so parallel guesses commit one at a time and cannot slip past the cap.
- **One account per number** (`RequireUniqueNumber`, on by default): a number another account verified
  answers `numberInUse`. The number is reserved as a Spark claim (`ISparkClaims`, commerce X12: compare-exchange
  `spark-phone/{e164}` → user id, no index query and so no wait on a stale index), so two accounts verifying one number at the same instant cannot both win. A reservation whose account no
  longer holds the number (deleted, or verified another number since) is taken over; changing number releases
  the old reservation.

## Limits: SMS pumping

A sent message costs money, and SMS pumping (fake sign-ups that send codes to premium-rate numbers) is the
attack. Every limit counts **sends**, and each send is recorded before the message goes out, under optimistic
concurrency:

| Option | Default | Limits |
|---|---|---|
| `ResendInterval` | 60 s | time between two codes for one account |
| `MaxSendsPerUser` | 5 per `SendWindow` | codes for one account, to any numbers |
| `MaxSendsPerNumber` | 5 per `SendWindow` | codes to one number, from **all** accounts together |
| `SendWindow` | 24 h | the window both caps count over |
| `AllowedCountryCallingCodes` | every country | the strongest guard: list only the countries you sell in |

A vendor refusal (`notSent`) still uses a slot, so retries cannot multiply messages.

## Storage

- `SparkPhoneVerifications/{userId}`: the pending code's hash, salt, number, expiry and attempts, and the
  account's send times. Deleted with the account.
- `SparkPhoneNumberSends/{SHA-256 of the number}`: one number's send times, shared by every account. The
  number itself is not stored.

Both carry `@expires` one send window after their last send, so RavenDB reaps them;
`AddPhoneVerification()` turns document expiration on.

## Options

Bound from `Spark:Auth:PhoneVerification`, then the `configure` callback.

| Key | Default |
|---|---|
| `CodeLength` | `6` (4–10) |
| `CodeLifetime` | `00:10:00` |
| `MaxAttemptsPerCode` | `5` |
| `ResendInterval` | `00:01:00` |
| `SendWindow` | `1.00:00:00` |
| `MaxSendsPerUser` | `5` |
| `MaxSendsPerNumber` | `5` |
| `DefaultCountryCallingCode` | none |
| `AllowedCountryCallingCodes` | `[]` (every country) |
| `RequireUniqueNumber` | `true` |
| `MessageTemplate` | `Your verification code is {code}. It expires in {minutes} minutes.` |

## SMS vendors

The framework picks no vendor (K7). Which adapters ship is **open decision Q-O1** (commerce PRD §11; the
recommendation is Twilio and one EU provider). Until then a host implements `ISparkSmsSender` itself:

```csharp
public sealed class MyVendorSms(HttpClient http) : ISparkSmsSender
{
    public async Task<SparkSmsReceipt> SendAsync(SparkSmsMessage message, CancellationToken ct = default)
    {
        // message.To is E.164; message.Body is final. Never log the body: it holds the code.
        …
        return new SparkSmsReceipt(Accepted: true, ProviderMessageId: id);
    }
}

builder.Services.AddSingleton<ISparkSmsSender, MyVendorSms>();
```

A vendor refusal is a receipt with `Accepted = false`; throw only when the vendor cannot be reached.
Keep vendor credentials in user secrets or the host's secret store.

## Testing

`SparkTestSmsSender` (registered by `AddSparkTestSms()`) records every message: `Sent`, `LastTo(number)`,
`Clear()`. In Development it also logs the body, so a developer can read the code from the console.
`tests/MintPlayer.Spark.Tests/Authorization/Extensions/PhoneVerificationTests.cs` drives the routes with it
and a settable `TimeProvider`.
