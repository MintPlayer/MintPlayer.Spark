using CodeCoverage.Entities;
using CodeCoverage.LookupReferences;
using CodeCoverage.Onboarding;
using Xunit;

namespace CodeCoverage.Tests.Onboarding;

/// <summary>
/// The chooser's form starts from what the repository has now (#615, owner 2026-11-09: the checkboxes "don't always
/// correspond with what features have been applied"). MintPlayer.Spark showed every box unchecked while it had all six.
/// </summary>
public class OnboardingFeatureDetectionTests
{
    private static Repository Repo(EDependencyUpdatesPolicy dependencies = EDependencyUpdatesPolicy.Inherit, EPrSizeLabelPolicy sizes = EPrSizeLabelPolicy.Inherit,
        bool uploaded = false)
        => new() { Name = "tools", FullName = "acme/tools", DefaultBranch = "main", DependencyUpdates = dependencies, PrSizeLabels = sizes,
            LatestCoverage = uploaded ? new CoverageSummary() : null };

    [Fact]
    public void Item615_a_repository_set_up_outside_the_chooser_shows_every_feature_it_has()
    {
        // The shape of Spark's own coverage.yml: versioning with lines, the queue on, the dependency switch from the account.
        const string yml = "versioning:\n  lines: {}\nmergeQueue:\n  enabled: true\n";

        var features = OnboardingFeatureDetection.Detect(Repo(uploaded: true), new Account { DependencyUpdates = true, PrSizeLabels = true }, yml);

        features.Should().BeEquivalentTo(Enum.GetValues<EOnboardingFeature>());
    }

    [Fact]
    public void Item615_a_repository_with_nothing_set_up_shows_nothing()
        => OnboardingFeatureDetection.Detect(Repo(), account: null, coverageYml: "gate:\n  patchTarget: 80\n").Should().BeEmpty();

    [Fact]
    public void Item615_a_switched_off_queue_and_versioning_without_lines_are_read_as_they_are()
        => OnboardingFeatureDetection.Detect(Repo(), null, "versioning: {}\nmergeQueue:\n  enabled: false\n")
            .Should().BeEquivalentTo([EOnboardingFeature.Versioning]);

    [Fact]
    public void Item615_the_repository_setting_wins_over_the_account_and_the_file_can_still_opt_out()
    {
        var account = new Account { DependencyUpdates = true, PrSizeLabels = true };

        OnboardingFeatureDetection.Detect(Repo(EDependencyUpdatesPolicy.Disabled, EPrSizeLabelPolicy.Disabled), account, null).Should().BeEmpty();
        OnboardingFeatureDetection.Detect(Repo(EDependencyUpdatesPolicy.Enabled), null, "dependencies:\n  enabled: false\n").Should().BeEmpty();
        OnboardingFeatureDetection.Detect(Repo(EDependencyUpdatesPolicy.Enabled, EPrSizeLabelPolicy.Enabled), null, null)
            .Should().BeEquivalentTo([EOnboardingFeature.DependencyUpdates, EOnboardingFeature.SizeLabels]);
    }
}
