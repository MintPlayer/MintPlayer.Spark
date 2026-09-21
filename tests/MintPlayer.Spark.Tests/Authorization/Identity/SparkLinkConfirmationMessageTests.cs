using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Tests.Authorization.Identity;

/// <summary>
/// The link-confirmation wording.
/// </summary>
/// <remarks>
/// Pinning prose looks like testing nothing, and would be if this were decoration. It is not: the
/// message is the only place a person who did <b>not</b> start the sign-in is told that somebody
/// else's matched their address, and the difference between a request and a notification decides
/// whether they have anything to do about it. These assertions are about that difference, not about
/// phrasing — none of them pins a whole sentence.
/// </remarks>
public class SparkLinkConfirmationMessageTests
{
    private static string Body(string? identity = "octocat", string? app = "Coverage")
        => SparkLinkConfirmationMessage.Body("GitLab", identity, "https://app.test/c?token=k.s",
            TimeSpan.FromHours(1), app);

    [Fact]
    public void The_body_says_nothing_has_happened_yet()
    {
        Body().Should().Contain("Nobody has been signed in")
            .And.Contain("nothing has been added to your account yet",
                "a message that reads as a notification of something already done leaves the person "
                + "who did not start it with no action to take");
    }

    [Fact]
    public void The_body_tells_a_reader_who_did_not_start_it_that_doing_nothing_is_an_answer()
    {
        Body().Should().Contain("If it was not you, do nothing");
    }

    [Fact]
    public void The_body_names_the_identity_that_asked()
    {
        Body().Should().Contain("octocat",
            "a reader has to be able to tell whose sign-in matched their address before they can "
            + "decide whether to report it");
    }

    [Fact]
    public void The_body_survives_a_provider_that_did_not_say_who()
    {
        var body = Body(identity: null);

        body.Should().Contain("Somebody signed in with GitLab")
            .And.NotContain(" as ,", "an absent identity must not leave a dangling clause");
    }

    [Fact]
    public void The_body_carries_the_link_and_its_window()
    {
        Body().Should().Contain("https://app.test/c?token=k.s")
            .And.Contain("works once")
            .And.Contain("1 hour");
    }

    [Theory]
    [InlineData(30, "30 minutes")]
    [InlineData(60, "1 hour")]
    [InlineData(120, "2 hours")]
    public void The_window_reads_naturally_at_the_durations_worth_configuring(int minutes, string expected)
    {
        SparkLinkConfirmationMessage
            .Body("GitLab", null, "https://app.test/c", TimeSpan.FromMinutes(minutes))
            .Should().Contain(expected);
    }

    [Fact]
    public void An_unnamed_application_stays_generic_rather_than_inventing_a_name()
    {
        SparkLinkConfirmationMessage.Subject("GitLab").Should().Be(
            "Confirm adding GitLab to your account");
        SparkLinkConfirmationMessage.Subject("GitLab", "Coverage").Should().Be(
            "Confirm adding GitLab to your Coverage account");

        Body(app: null).Should().Contain("on your account.",
            "no name to print means no name, not a placeholder standing in for one");
    }
}
