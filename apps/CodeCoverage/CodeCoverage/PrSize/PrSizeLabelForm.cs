using CodeCoverage.Entities;
using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.PrSize;

/// <summary>
/// How the labeller's settings read on the Account and Repository pages (docs/coverage_pr_size_labels_PRD.md FR2, D8): the
/// repository says what is in effect and where it comes from, and a forge without labels shows its settings read-only with
/// the reason.
/// </summary>
/// <remarks>
/// Called from the load hooks after <c>base.OnLoadAsync</c> has blanked the protected attributes, so each caller asks the
/// same deny-by-default question first and calls this only for a reader who may see the settings.
/// </remarks>
public static class PrSizeLabelForm
{
    public const string UnavailableKey = "prSizeLabelsUi.unavailable";

    /// <summary>The sentence for an effective value and its source (<c>repository</c>, <c>organization</c>, <c>default</c>).</summary>
    public static string EffectiveKey(bool enabled, string source) => $"prSizeLabelsUi.effective.{(enabled ? "on" : "off")}.{source}";

    public static void ShapeRepository(PersistentObject po, Repository repository, Account? account, bool supported, Func<string, string> translate)
    {
        if (po.Attributes.FirstOrDefault(a => a.Name == nameof(Repository.PrSizeLabelsEffective)) is { } effective)
        {
            var (enabled, source) = Repository.ResolvePrSizeLabels(repository, account);
            effective.Value = translate(supported ? EffectiveKey(enabled, source) : UnavailableKey);
        }
        if (!supported)
            ReadOnly(po, nameof(Repository.PrSizeLabels), nameof(Repository.PrSizeIgnoreRules));
    }

    public static void ShapeAccount(PersistentObject po, bool supported, Func<string, string> translate)
    {
        if (supported) return;
        // Drawn on no page in the model; shown here, for this object alone.
        if (po.Attributes.FirstOrDefault(a => a.Name == nameof(Account.PrSizeLabelsUnavailable)) is { } unavailable)
        {
            unavailable.Value = translate(UnavailableKey);
            unavailable.ShowedOn = EShowedOn.PersistentObject;
        }
        ReadOnly(po, nameof(Account.PrSizeLabels), nameof(Account.PrSizeLabelScale), nameof(Account.PrSizeIgnoreRules));
    }

    private static void ReadOnly(PersistentObject po, params string[] names)
    {
        foreach (var attribute in po.Attributes.Where(a => names.Contains(a.Name)))
            attribute.IsReadOnly = true;
    }
}
