using CodeCoverage.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Identity;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// The two ways sending can be asked for when it cannot happen.
/// </summary>
/// <remarks>
/// Both throw rather than return quietly, which is the decision worth pinning. A confirmation that
/// is never sent is a link that is never made, and the user sees a sign-in that appears to do
/// nothing at all — the exact failure the ConfirmByEmail startup guard exists to prevent, arriving
/// later and with nothing in the log to connect it to a cause.
/// </remarks>
public class SmtpLinkConfirmationSenderTests
{
    private static SmtpLinkConfirmationSender NewSender(CoverageMailOptions options)
        => new(Options.Create(options), NullLogger<SmtpLinkConfirmationSender>.Instance);

    private static readonly CoverageMailOptions Configured = new()
    {
        Host = "coverage-smtp",
        FromAddress = "no-reply@coverage.mintplayer.test",
    };

    [Fact]
    public async Task Sending_without_a_host_configured_throws_rather_than_discarding()
    {
        var sender = NewSender(new CoverageMailOptions { FromAddress = "a@b.test" });

        var act = async () => await sender.SendLinkConfirmationAsync(
            new SparkUser { Id = "users/alice", Email = "alice@test.org" }, "GitLab", "alice", "https://x/y");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Coverage:Mail:Host*");
    }

    [Fact]
    public async Task Sending_without_a_from_address_throws_rather_than_discarding()
    {
        var sender = NewSender(new CoverageMailOptions { Host = "coverage-smtp" });

        var act = async () => await sender.SendLinkConfirmationAsync(
            new SparkUser { Id = "users/alice", Email = "alice@test.org" }, "GitLab", "alice", "https://x/y");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// Not reachable through the linking flow, which finds the account <em>by</em> its email — but
    /// the contract does not promise that, and a silent return would look like a delivery problem
    /// rather than a missing address.
    /// </summary>
    [Fact]
    public async Task A_user_with_no_address_is_reported_rather_than_skipped()
    {
        var sender = NewSender(Configured);

        var act = async () => await sender.SendLinkConfirmationAsync(
            new SparkUser { Id = "users/alice", Email = null }, "GitLab", "alice", "https://x/y");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no email address*");
    }

    /// <summary>
    /// ⚠️ Unset is a supported state, not a misconfiguration: Spark ships no transport, so an
    /// application that registers none simply cannot send — which is exactly how the startup guard
    /// detects the one combination that matters.
    /// </summary>
    [Theory]
    [InlineData(null, null, false)]
    [InlineData("coverage-smtp", null, false)]
    [InlineData(null, "a@b.test", false)]
    [InlineData("  ", "a@b.test", false)]
    [InlineData("coverage-smtp", "a@b.test", true)]
    public void IsConfigured_needs_both_halves(string? host, string? from, bool expected)
        => new CoverageMailOptions { Host = host, FromAddress = from }.IsConfigured
            .Should().Be(expected);

    /// <summary>
    /// The default port is the relay container's submission port, not 25 — the app hands mail to
    /// the relay, and only the relay speaks 25 to the outside world.
    /// </summary>
    [Fact]
    public void The_default_port_is_submission()
        => new CoverageMailOptions().Port.Should().Be(587);
}
