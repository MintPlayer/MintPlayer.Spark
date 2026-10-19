# MintPlayer.Spark.Challenges.Recaptcha

Google reCAPTCHA (v2, v3) for [MintPlayer.Spark.Challenges](../MintPlayer.Spark.Challenges/README.md). Needs the consent primitive (`spark.AddConsent()`): visitors who refuse third-party-challenge consent get ALTCHA instead (GA9). Written against the vendor's documented siteverify API; keys go in user-secrets (`Spark:Challenges:Recaptcha`).

```csharp
spark.AddChallenges(c => c.UseRecaptcha());
```
