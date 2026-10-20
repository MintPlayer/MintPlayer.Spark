# MintPlayer.Spark.Sms

SMS sending for MintPlayer.Spark (`docs/commerce_PRD.md` X13, D29). One seam, `ISparkSmsSender`, that phone
verification in `MintPlayer.Spark.Authorization` sends through. No RavenDB and no Spark runtime.

- **`ISparkSmsSender.SendAsync(SparkSmsMessage)`**: the recipient in E.164 (`+32470123456`), the final
  body, and an optional purpose tag. A vendor refusal is a receipt with `Accepted = false`; an exception
  means the vendor could not be reached.
- **`SparkTestSmsSender`**: records every message (`Sent`, `LastTo(number)`) and delivers nothing. In
  Development it also logs the body, so a verification code can be read from the console.

## Setup

```csharp
builder.Services.AddSparkTestSms();   // tests and development
```

## Vendors

The framework picks no vendor (K7). Which vendor adapters ship (Twilio and an EU provider are recommended) is
open decision **Q-O1** in `docs/commerce_PRD.md` §11; until it is taken, a host implements
`ISparkSmsSender` itself and registers it as a singleton. An adapter never logs the body (it may hold a
one-time code) or its credentials.
