namespace CodeCoverage.Onboarding;

/// <summary>What the onboarding chooser sets up (#568 D3). Release lines need versioning, and bring it with them.</summary>
public enum EOnboardingFeature
{
    Coverage,
    DependencyUpdates,
    MergeQueue,
    ReleaseLines,
    SizeLabels,
    Versioning,

    /// <summary>
    /// #649 D11: the workflows' setup-dotnet steps read the SDK from global.json instead of pinning one, on the default branch
    /// and on the .NET release lines. Opt-in, never forced (other people use the app, owner 2026-11-12).
    /// </summary>
    SdkFromGlobalJson,
}
