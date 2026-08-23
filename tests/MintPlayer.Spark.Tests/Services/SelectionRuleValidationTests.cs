using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// A malformed <c>selectionRule</c> is refused when <c>customActions.json</c> loads.
/// </summary>
/// <remarks>
/// <see cref="SelectionRuleParser"/> has carried an <c>IsValid</c> since it was written,
/// documented as "call at configuration load so a typo fails loudly at startup", and
/// <c>docs/guide-custom-actions.md</c> promised the same. Nothing called it. A rule of
/// <c>"1-5"</c> therefore survived to the moment a user pressed the button, where <c>Parse</c>
/// threw <see cref="FormatException"/> out of the execute endpoint — a 500 on a user action
/// instead of a refused configuration.
/// </remarks>
public sealed class SelectionRuleValidationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();
    private readonly ILogger<CustomActionsConfigurationLoader> _logger = NullLogger<CustomActionsConfigurationLoader>.Instance;

    public SelectionRuleValidationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "spark-selrule-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "App_Data"));
        _hostEnv.ContentRootPath.Returns(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* the file watcher may briefly hold a lock — best-effort */ }
    }

    private CustomActionsConfigurationLoader CreateLoader() => new(_hostEnv, _logger);

    private void WriteConfig(string json) =>
        File.WriteAllText(Path.Combine(_tempDir, "App_Data", "customActions.json"), json);

    [Fact]
    public void A_malformed_rule_is_refused_when_the_configuration_loads()
    {
        WriteConfig(@"{ ""Archive"": { ""displayName"": { ""en"": ""Archive"" }, ""selectionRule"": ""1-5"" } }");
        using var loader = CreateLoader();

        var act = () => loader.GetConfiguration();

        act.Should().Throw<FormatException>()
            .WithMessage("*Archive*", "the message must name the offending action")
            .And.Message.Should().Contain("1-5", "and quote the rule that was rejected");
    }

    /// <summary>Fixing one typo only to be shown the next is the worst version of this message.</summary>
    [Fact]
    public void Every_malformed_rule_is_reported_together()
    {
        WriteConfig(@"{
            ""Archive"": { ""displayName"": { ""en"": ""Archive"" }, ""selectionRule"": ""1-5"" },
            ""Publish"": { ""displayName"": { ""en"": ""Publish"" }, ""selectionRule"": ""=abc"" }
        }");
        using var loader = CreateLoader();

        var act = () => loader.GetConfiguration();

        var message = act.Should().Throw<FormatException>().Which.Message;
        message.Should().Contain("Archive").And.Contain("Publish");
    }

    [Theory]
    [InlineData("=1")]
    [InlineData(">0")]
    [InlineData("<=5")]
    [InlineData("!=0")]
    [InlineData("1<X<5")]
    [InlineData("0<X")]
    public void A_well_formed_rule_loads(string rule)
    {
        WriteConfig(@"{ ""Copy"": { ""displayName"": { ""en"": ""Copy"" }, ""selectionRule"": """ + rule + @""" } }");
        using var loader = CreateLoader();

        loader.GetConfiguration()["Copy"].SelectionRule.Should().Be(rule);
    }

    /// <summary>
    /// An omitted rule means no requirement — it must not be mistaken for a malformed one, or
    /// every action that never wanted a selection would refuse to load.
    /// </summary>
    [Fact]
    public void An_omitted_rule_is_not_a_validation_failure()
    {
        WriteConfig(@"{ ""Refresh"": { ""displayName"": { ""en"": ""Refresh"" } } }");
        using var loader = CreateLoader();

        loader.GetConfiguration().Should().ContainKey("Refresh");
    }

    /// <summary>
    /// `"=0"` is a real predicate meaning "exactly zero selected", not an empty rule — the action
    /// disables the moment anything is ticked. It must load.
    /// </summary>
    [Fact]
    public void Exactly_zero_is_a_rule_not_an_absence()
    {
        WriteConfig(@"{ ""New"": { ""displayName"": { ""en"": ""New"" }, ""selectionRule"": ""=0"" } }");
        using var loader = CreateLoader();

        loader.GetConfiguration()["New"].SelectionRule.Should().Be("=0");
    }
}
