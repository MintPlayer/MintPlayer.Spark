using CodeCoverage.Commands;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.PackageVersioning;
using Xunit;
using Line = CodeCoverage.PackageVersioning.GitWriterPromoteLineResult;
using Target = CodeCoverage.PackageVersioning.GitWriterBackportTargetResult;

namespace CodeCoverage.Tests.Commands;

/// <summary>#660 G2/G3: how the git writer's answer to a <c>/backport</c> or a <c>/release</c> becomes the comment's reaction.</summary>
public class CommandOutcomeMappingTests
{
    private static GitWriterBackportResult Backport(params string?[] pushes)
        => new(GitWriterBackportResult.Backported, null, null, null, [.. pushes.Select((p, i) => new Target($"release/{i}.x", $"backport/7-{i}", null, p, null))], null, 0);

    [Fact]
    public void A_backport_that_opened_a_pull_request_on_any_line_is_done()
    {
        BackportRecipient.OutcomeOf(Backport(Target.Created, Target.Conflicted)).Should().Be(ECommandOutcome.Done);
        BackportRecipient.OutcomeOf(Backport(Target.Drafted)).Should().Be(ECommandOutcome.Done, "a draft to resolve is still a pull request");
    }

    [Fact]
    public void A_backport_every_line_already_held_changed_nothing()
        => BackportRecipient.OutcomeOf(Backport(Target.Exists, Target.Empty)).Should().Be(ECommandOutcome.NoChange);

    [Fact]
    public void A_backport_that_no_line_took_is_refused_and_one_that_did_not_run_failed()
    {
        BackportRecipient.OutcomeOf(Backport(Target.Conflicted, "rejected")).Should().Be(ECommandOutcome.Refused);
        BackportRecipient.OutcomeOf(new GitWriterBackportResult("failed", "clone", null, null, null, null, 0)).Should().Be(ECommandOutcome.Failed);
        BackportRecipient.OutcomeOf(null).Should().Be(ECommandOutcome.Failed);
    }

    private static GitWriterPromoteResult Release(params string[] outcomes)
        => new(GitWriterPromoteResult.Promoted, null, [.. outcomes.Select((o, i) => new Line($"release/{i}.x", o, new string('a', 40), null, null))], null, 0);

    [Fact]
    public void A_release_maps_promoted_contained_and_refused_lines()
    {
        ReleaseRecipient.OutcomeOf(Release(Line.Promoted, Line.Unchanged)).Should().Be(ECommandOutcome.Done);
        ReleaseRecipient.OutcomeOf(Release(Line.Contained, Line.Unchanged)).Should().Be(ECommandOutcome.NoChange);
        ReleaseRecipient.OutcomeOf(Release(Line.VersionRefused)).Should().Be(ECommandOutcome.Refused);
        ReleaseRecipient.OutcomeOf(new GitWriterPromoteResult("failed", "push", null, null, 0)).Should().Be(ECommandOutcome.Failed);
    }

    [Theory]
    [InlineData(ECommandOutcome.Received, EForgeReaction.Eyes)]
    [InlineData(ECommandOutcome.Started, EForgeReaction.Rocket)]
    [InlineData(ECommandOutcome.Done, EForgeReaction.Hooray)]
    [InlineData(ECommandOutcome.NoChange, EForgeReaction.ThumbsUp)]
    [InlineData(ECommandOutcome.Refused, EForgeReaction.ThumbsDown)]
    [InlineData(ECommandOutcome.Failed, EForgeReaction.ThumbsDown)]
    public void Each_state_shows_one_reaction(ECommandOutcome outcome, EForgeReaction reaction)
        => CommandLifecycle.ReactionFor(outcome).Should().Be(reaction);
}
