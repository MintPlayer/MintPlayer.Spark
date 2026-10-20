# Challenges and bot defence (`MintPlayer.Spark.Challenges`)

Commerce PRD §4.12 (D29, GA9, GA11), research §6. Generic package (alias `challenges`, ng entry
`@mintplayer/ng-spark/challenges`), usable outside sales.

## Providers

```csharp
spark.AddConsent();                                         // required by every hosted provider (GA9)
spark.AddChallenges(c => c.UseAltcha().UseTurnstile());     // Turnstile; ALTCHA for visitors who refuse consent
```

| Provider | Package | Consent | Verification |
|---|---|---|---|
| ALTCHA (built in) | `MintPlayer.Spark.Challenges` | none (no third party) | altcha-lib v1 SHA-256 proof of work; signature = Data Protection seal over point + challenge |
| Turnstile | `.Turnstile` → `UseTurnstile()` | `third-party-challenge` | `POST challenges.cloudflare.com/turnstile/v0/siteverify` (form) |
| hCaptcha | `.HCaptcha` → `UseHCaptcha()` | `third-party-challenge` | `POST api.hcaptcha.com/siteverify` (form, `sitekey`) |
| reCAPTCHA v2/v3 | `.Recaptcha` → `UseRecaptcha()` | `third-party-challenge` | `POST www.google.com/recaptcha/api/siteverify`; v3 minimum score + action |
| Friendly Captcha v2 | `.FriendlyCaptcha` → `UseFriendlyCaptcha()` | `third-party-challenge` | `POST {global|eu}.frcapi.com/api/v2/captcha/siteverify` (JSON, `X-API-Key`) |
| Your own | `UseChallenge<T>(name)` / `UseHostedChallenge<T>(name, vendor)` | as declared | — |

The last `Use*` call is the provider; `Spark:Challenges:Provider` overrides it at runtime. A hosted provider declares
`RequiresConsent` (startup refuses it without `AddConsent()`, X21); a visitor without consent is offered ALTCHA
(`consentFallback: true`) and never blocked. Secrets (`SecretKey`, `Secret`, `ApiKey`) go in user-secrets. Add the
vendor's script origin to the CSP `script-src`/`frame-src`. Adapters are tested against public-documentation fixtures
only (D37); no live calls were made.

## Points (runtime-switchable)

`Spark:Challenges:Points`: `register`, `queue-join`, `hold` (step-up only, when risk ≥ `StepUpScore`), `checkout`, `form`.
`admission` is refused: the queue position is the proof. Read through `IOptionsMonitor`, so a reloading configuration
source switches a point without a redeploy. A host keeping operator settings in `MintPlayer.Spark.Settings` replaces
`ISparkChallengeSwitch`:

```csharp
public static class ChallengeSettings { public static readonly SparkSetting<string> Points = new("Challenges:Points", "", "Comma-separated challenge points."); }
sealed class SettingsSwitch(ISparkSettings settings) : ISparkChallengeSwitch
{
    public async ValueTask<bool> IsEnabledAsync(string point, CancellationToken ct = default)
        => point != "admission" && (await settings.GetAsync(ChallengeSettings.Points, ct)).Split(',').Contains(point);
}
```

**Step-up in a Spark action** (the 449 retry): `if (!await challenges.StepUpAsync(Retry, SparkChallengePoints.Hold,
decision)) refuse;` before anything with side effects. The browser needs `provideSparkChallenges()` (client method
`spark.challenge`). A response is single-use (`challenges/used/…` claim for `ReplayWindow`).

## Risk pipeline

`ISparkRiskSignal` (`[SparkDiscoverable]`) produces evidence; `ISparkRiskPipeline.EvaluateAsync` combines it
(noisy-OR, positive evidence subtracts): < 0.3 Allow, < 0.7 Flag, < 0.9 Challenge (or Slow without a provider), else
Slow (429 + jittered `Retry-After`). **Only deterministic evidence blocks** (a replayed or forged token, a decoy hit);
with `Risk:ShadowQueue = DeterministicOnly` it shadow-queues instead. Built-in signals: recorded evidence, velocity
(> 30/min per identity and point → Slow), honeypot (0.3), `navigator.webdriver` (0.3), shared address (≤ 0.1). Signals
are time-boxed (20 ms). Every decision with evidence is logged (`SparkRiskDecisionRecord`, `IExpiring`, 90 days, no IP
address stored); `ISparkRiskDecisionLog.ForIdentityAsync` / `EraseAsync` serve appeals and GDPR requests.

**GA11 (Regulation (EU) 2018/302, Art. 3–5).** Startup refuses a signal whose `Basis` includes `Nationality`,
`Residence` or `CardIssuingCountry`. Evidence marked `CountryBased` (geo-IP) is never deterministic and can never by
itself move a decision past Flag. *Checked against EUR-Lex 32018R0302 (Art. 3–5) on 2026-10-20 (D37).*

## Deceptive measures (D29)

`Spark:Challenges:Deceptive:{Honeypot|Decoys|FakeSoldOut|PoisonedTokens|Tarpit}`: each `Enabled` (default false) and an
explicit `Trigger` (`Deterministic`, recommended, or `RiskScore` with `TriggerScore`); startup refuses an enabled
measure without one and warns while any is on. Every application is logged with the measure's name.

| Measure | Effect (waiting room) |
|---|---|
| Honeypot | A filled hidden field: the entry is created as a shadow entry (looks queued, never called). |
| Decoys | `GET …/priority-access`: deterministic evidence + a plausible fake answer. |
| FakeSoldOut | A blocked join is answered `sold-out` instead of `refused`. |
| PoisonedTokens | An identity with deterministic evidence gets a token that validates and is refused when spent. |
| Tarpit | The join is delayed (≤ 30 s). |

⚠️ Legal exposure: deceiving a visitor who is in fact human may be a misleading commercial practice (Directive
2005/29/EC, Art. 6–7) and an automated decision (GDPR Art. 22). Hence deterministic triggers by default, logging, and
a human appeal path (the decision id is shown to a refused visitor). Both texts checked against EUR-Lex (02005L0029-20260927, 32016R0679) on 2026-10-20 (D37).
