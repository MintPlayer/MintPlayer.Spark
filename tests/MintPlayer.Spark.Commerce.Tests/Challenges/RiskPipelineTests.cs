using MintPlayer.Spark.Challenges;
using MintPlayer.Spark.Challenges.Deceptive;
using MintPlayer.Spark.Challenges.Risk;

namespace MintPlayer.Spark.Commerce.Tests.Challenges;

/// <summary>
/// The risk policy (§4.12, research §6.7) as a pure function, and the startup rules of D29 and GA11: block only on
/// deterministic evidence; a shared address never refuses; a country alone never refuses; deceptive measures need an
/// explicit trigger; a signal keyed on nationality, residence or card country is refused.
/// </summary>
public class RiskPipelineTests
{
    private static readonly SparkRiskOptions Options = new();

    private static SparkRiskAction Decide(bool challenge, params SparkRiskEvidence[] evidence)
        => SparkRiskScoring.Decide(evidence, Options, challenge, out _);

    [Fact]
    public void Probabilistic_evidence_never_blocks_however_strong()
    {
        var strong = Enumerable.Range(0, 20).Select(i => new SparkRiskEvidence($"s{i}", 0.6)).ToArray();
        Decide(challenge: false, strong).Should().Be(SparkRiskAction.Slow);
        Decide(challenge: true, new SparkRiskEvidence("a", 0.5), new SparkRiskEvidence("b", 0.5)).Should().Be(SparkRiskAction.Challenge);
        Decide(challenge: true, new SparkRiskEvidence("rule", 0.1, Minimum: SparkRiskAction.Block)).Should().Be(SparkRiskAction.Slow);
    }

    [Fact]
    public void Deterministic_evidence_blocks_or_shadow_queues_when_that_is_on()
    {
        var replay = new SparkRiskEvidence("admission-token-replayed", 1, Deterministic: true);
        Decide(challenge: true, replay).Should().Be(SparkRiskAction.Block);
        var shadow = new SparkRiskOptions { ShadowQueue = SparkShadowQueueMode.DeterministicOnly };
        SparkRiskScoring.Decide([replay], shadow, true, out _).Should().Be(SparkRiskAction.ShadowQueue);
    }

    [Fact]
    public void The_score_is_a_noisy_or_lowered_by_positive_evidence()
    {
        Math.Abs(SparkRiskScoring.Score([new("a", 0.3), new("b", 0.3)]) - 0.51).Should().BeLessThan(1e-9);
        Math.Abs(SparkRiskScoring.Score([new("a", 0.3), new("passkey", -0.2)]) - 0.1).Should().BeLessThan(1e-9);
        SparkRiskScoring.Score([new("passkey", -0.2)]).Should().Be(0);
        Decide(false).Should().Be(SparkRiskAction.Allow);
        Decide(false, new SparkRiskEvidence("honeypot", 0.3)).Should().Be(SparkRiskAction.Flag);
    }

    [Fact]
    public void A_country_alone_never_moves_a_decision_past_flag()
    {
        // GA11 (Reg. 2018/302): even "deterministic" country evidence is not decisive.
        Decide(true, new SparkRiskEvidence("geo", 0.95, Deterministic: true, CountryBased: true)).Should().Be(SparkRiskAction.Flag);
        Decide(true, new SparkRiskEvidence("geo", 0.5, CountryBased: true, Minimum: SparkRiskAction.Slow)).Should().Be(SparkRiskAction.Flag);
        // With other evidence that decides on its own, the decision stands.
        Decide(true, new SparkRiskEvidence("geo", 0.5, CountryBased: true), new SparkRiskEvidence("replay", 1, Deterministic: true)).Should().Be(SparkRiskAction.Block);
    }

    [Fact]
    public async Task Many_identities_on_one_address_or_prefix_are_all_allowed()
    {
        // S12 item S4 at CI size: 2,000 identities behind one IPv4 and 500 inside one IPv6 /64.
        var clock = new MintPlayer.Spark.Commerce.Tests.WaitingRoom.TestClock(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
        var memory = new SparkRiskMemory(clock);
        var monitor = new StaticMonitor(new SparkChallengesOptions());
        var signal = new SharedAddressSignal(memory, monitor);
        var decisions = new List<SparkRiskAction>();
        foreach (var (address, count) in new[] { ("198.51.100.10", 2_000), ("2001:db8:1:2::/64", 500) })
            for (var i = 0; i < count; i++)
            {
                var evidence = await signal.EvaluateAsync(new SparkRiskContext("queue-join", $"visitor:{address}-{i}", address), default);
                decisions.Add(Decide(true, evidence is null ? [] : [evidence]));
            }
        decisions.Should().OnlyContain(d => d == SparkRiskAction.Allow);
    }

    [Fact]
    public async Task The_velocity_rule_slows_down_beyond_human_plausibility()
    {
        var clock = new MintPlayer.Spark.Commerce.Tests.WaitingRoom.TestClock(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
        var signal = new VelocitySignal(new SparkRiskMemory(clock), new StaticMonitor(new SparkChallengesOptions()));
        var context = new SparkRiskContext("hold", "user:u1", "203.0.113.7");
        SparkRiskEvidence? last = null;
        for (var i = 0; i < 31; i++)
            last = await signal.EvaluateAsync(context, default);
        Decide(true, last!).Should().Be(SparkRiskAction.Slow);
        clock.Advance(TimeSpan.FromMinutes(1));
        (await signal.EvaluateAsync(context, default)).Should().BeNull();
    }

    [Fact]
    public void Startup_refuses_deceptive_measures_without_an_explicit_trigger_and_forbidden_risk_bases()
    {
        var options = new SparkChallengesOptions();
        options.Deceptive.Tarpit.Enabled = true;
        options.Deceptive.FakeSoldOut.Enabled = true;
        options.Deceptive.FakeSoldOut.Trigger = SparkDeceptiveTrigger.RiskScore;
        options.Points.Add("admission");
        var (errors, _) = SparkChallengesExtensions.Check(options, [("card-country", SparkRiskBasis.CardIssuingCountry), ("geo", SparkRiskBasis.NetworkCountry)]);

        errors.Should().HaveCount(4);
        errors.Should().Contain(e => e.Contains("Tarpit") && e.Contains("Trigger"));
        errors.Should().Contain(e => e.Contains("FakeSoldOut") && e.Contains("TriggerScore"));
        errors.Should().Contain(e => e.Contains("admission"));
        errors.Should().Contain(e => e.Contains("card-country") && e.Contains("2018/302"));

        options.Deceptive.Tarpit.Trigger = SparkDeceptiveTrigger.Deterministic;
        options.Deceptive.FakeSoldOut.TriggerScore = 0.95;
        options.Points.Clear();
        var (none, warnings) = SparkChallengesExtensions.Check(options, [("geo", SparkRiskBasis.NetworkCountry)]);
        none.Should().BeEmpty();
        warnings.Should().HaveCount(2);
    }

    [Fact]
    public void A_deceptive_measure_fires_on_its_trigger_only()
    {
        var deterministic = new SparkDeceptiveMeasureOptions { Enabled = true, Trigger = SparkDeceptiveTrigger.Deterministic };
        var score = new SparkDeceptiveMeasureOptions { Enabled = true, Trigger = SparkDeceptiveTrigger.RiskScore, TriggerScore = 0.8 };
        var suspicious = new SparkRiskDecision(SparkRiskAction.Slow, 0.9, [new("a", 0.9)]);
        var honeypot = new SparkRiskDecision(SparkRiskAction.Flag, 0.3, [new("honeypot", 0.3, DeceptiveTrigger: true)]);
        var country = new SparkRiskDecision(SparkRiskAction.Flag, 0.9, [new("geo", 0.9, Deterministic: true, CountryBased: true)]);

        SparkDeceptiveMeasures.Fires(deterministic, suspicious).Should().BeFalse();
        SparkDeceptiveMeasures.Fires(deterministic, honeypot).Should().BeTrue();
        SparkDeceptiveMeasures.Fires(deterministic, country).Should().BeFalse();
        SparkDeceptiveMeasures.Fires(score, suspicious).Should().BeTrue();
        SparkDeceptiveMeasures.Fires(new SparkDeceptiveMeasureOptions { Enabled = true }, honeypot).Should().BeFalse();
    }

    internal sealed class StaticMonitor(SparkChallengesOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<SparkChallengesOptions>
    {
        public SparkChallengesOptions CurrentValue => value;

        public SparkChallengesOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<SparkChallengesOptions, string?> listener) => null;
    }
}
