using CodeCoverage.Onboarding;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;

namespace CodeCoverage.Actions;

/// <summary>
/// The "Set up features" form (the virtual <c>SetUpFeatures</c> type, #571 M10c). A JSON-only type has no CLR entity, so the
/// framework finds this class by name and duck-types its refresh hook (#649).
/// </summary>
public class SetUpFeaturesActions
{
    /// <summary>
    /// #649 D12 (owner: "we can show or enable the checkbox using a TriggersRefresh and OnRefresh override"): ticking or
    /// unticking dependency updates or release lines enables or disables "SDK from global.json".
    /// </summary>
    public Task OnRefreshAsync(SparkRefreshArgs args)
    {
        Shape(args.PersistentObject);
        return Task.CompletedTask;
    }

    /// <summary>
    /// "SDK from global.json" on the form only for a .NET repository (the hidden <c>DotnetRepository</c>, which the action sets),
    /// and enabled only with dependency updates or release lines, the two features that move or narrow the SDK; disabled, it is
    /// unticked. Establishes the whole state each time: a refresh hands a freshly scaffolded object, never the last one.
    /// </summary>
    internal static void Shape(PersistentObject form)
    {
        var sdk = form[nameof(EOnboardingFeature.SdkFromGlobalJson)];
        var enabled = IsTicked(form, nameof(EOnboardingFeature.DependencyUpdates)) || IsTicked(form, nameof(EOnboardingFeature.ReleaseLines));
        sdk.ShowedOn = IsTicked(form, DotnetRepository) ? EShowedOn.PersistentObject : EShowedOn.None;
        sdk.IsReadOnly = !enabled;
        if (!enabled)
            sdk.Value = false;
    }

    /// <summary>The hidden attribute that says whether the repository builds .NET.</summary>
    internal const string DotnetRepository = "DotnetRepository";

    private static bool IsTicked(PersistentObject form, string name) => form[name].GetValue<bool>() == true;
}
