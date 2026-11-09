using CodeCoverage.Dependencies;
using CodeCoverage.Entities;
using CodeCoverage.MergeQueue;
using CodeCoverage.PackageVersioning;

namespace CodeCoverage.Onboarding;

/// <summary>
/// Which onboarding features a repository has now, read from where each one lives, not from what the chooser last asked
/// for (#615, owner 2026-11-09: the checkboxes "don't always correspond with what features have been applied"). A feature
/// can arrive without the chooser: the dependency switch opens its own onboarding pull request, older repositories came
/// through "Set up versioning", and anyone can edit <c>coverage.yml</c>. The chooser starts its form from this, plus what an
/// open onboarding pull request carries.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="EOnboardingFeature.Coverage"/>: a coverage report has been uploaded (<see cref="Repository.LatestCoverage"/>).</item>
/// <item><see cref="EOnboardingFeature.DependencyUpdates"/>: the setting is on (<see cref="Repository.ResolveDependencyUpdates"/>) and
/// <c>dependencies.enabled</c> does not opt out (Q6).</item>
/// <item><see cref="EOnboardingFeature.MergeQueue"/>: <c>mergeQueue.enabled: true</c>.</item>
/// <item><see cref="EOnboardingFeature.ReleaseLines"/>: <c>versioning.lines</c> is present.</item>
/// <item><see cref="EOnboardingFeature.SizeLabels"/>: the setting is on (<see cref="Repository.ResolvePrSizeLabels"/>).</item>
/// <item><see cref="EOnboardingFeature.Versioning"/>: <c>versioning:</c> is present.</item>
/// </list>
/// A section the file has but cannot be read counts as present: the owner put it there.
/// </remarks>
public static class OnboardingFeatureDetection
{
    /// <param name="coverageYml">The default branch's <c>coverage.yml</c>, or null when it has none or it could not be read.</param>
    public static IReadOnlySet<EOnboardingFeature> Detect(Repository repository, Account? account, string? coverageYml)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var features = new HashSet<EOnboardingFeature>();

        if (repository.LatestCoverage is not null)
            features.Add(EOnboardingFeature.Coverage);
        if (Repository.ResolveDependencyUpdates(repository, account) && DependenciesYml.Parse(coverageYml).Section.Enabled)
            features.Add(EOnboardingFeature.DependencyUpdates);
        if (Repository.ResolvePrSizeLabels(repository, account).Enabled)
            features.Add(EOnboardingFeature.SizeLabels);
        if (MergeQueueYml.Parse(coverageYml, repository.DefaultBranch ?? string.Empty).Section is { Enabled: true })
            features.Add(EOnboardingFeature.MergeQueue);
        if (VersioningYml.Parse(coverageYml).Config is { } versioning)
        {
            features.Add(EOnboardingFeature.Versioning);
            if (versioning.EffectiveLines is not null)
                features.Add(EOnboardingFeature.ReleaseLines);
        }
        return features;
    }
}
