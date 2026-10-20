# MintPlayer.Spark.Challenges.HCaptcha

hCaptcha for [MintPlayer.Spark.Challenges](../MintPlayer.Spark.Challenges/README.md). Needs the consent primitive (`spark.AddConsent()`): visitors who refuse third-party-challenge consent get ALTCHA instead (GA9). Written against the vendor's documented siteverify API; keys go in user-secrets (`Spark:Challenges:HCaptcha`).

```csharp
spark.AddChallenges(c => c.UseHCaptcha());
```
