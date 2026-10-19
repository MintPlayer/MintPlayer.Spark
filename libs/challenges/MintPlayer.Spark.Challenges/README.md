# MintPlayer.Spark.Challenges

Bot defence for MintPlayer.Spark: ALTCHA built in (self-hosted proof of work), hosted providers in adapter packages (consent-gated, ALTCHA as the fallback), runtime-switchable challenge points, a 449 step-up for Spark actions, a risk pipeline that blocks only on deterministic evidence, a decision log, and opt-in deceptive measures behind explicit triggers.

```csharp
spark.AddChallenges(c => c.UseAltcha());
```

See [docs/guide-challenges.md](../../../docs/guide-challenges.md).
