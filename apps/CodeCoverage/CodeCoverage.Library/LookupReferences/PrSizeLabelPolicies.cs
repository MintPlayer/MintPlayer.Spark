using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.LookupReferences;

/// <summary>
/// Whether a repository labels its pull requests by size, or defers to its account (docs/coverage_pr_size_labels_PRD.md
/// D1). Three named states for the reasons on <see cref="EDeleteBranchPolicy"/>.
/// </summary>
public enum EPrSizeLabelPolicy
{
    /// <summary>Defer to the owning account's setting. The default for every repository, including stored ones.</summary>
    Inherit,

    /// <summary>Label this repository's pull requests, whatever the account says.</summary>
    Enabled,

    /// <summary>Never label this repository's pull requests.</summary>
    Disabled,
}

/// <summary>The selectable values of <see cref="EPrSizeLabelPolicy"/>, rendered as a dropdown.</summary>
public sealed class PrSizeLabelPolicy : TransientLookupReference<EPrSizeLabelPolicy>
{
    private PrSizeLabelPolicy() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<PrSizeLabelPolicy> Items { get; } =
    [
        new PrSizeLabelPolicy
        {
            Key = EPrSizeLabelPolicy.Inherit,
            Description = "Use the account's setting",
            Values = _TS("Use account setting", "Utiliser le paramètre du compte", "Accountinstelling gebruiken"),
        },
        new PrSizeLabelPolicy
        {
            Key = EPrSizeLabelPolicy.Enabled,
            Description = "Label this repository's pull requests by size",
            Values = _TS("Label pull requests", "Étiqueter les pull requests", "Pull requests labelen"),
        },
        new PrSizeLabelPolicy
        {
            Key = EPrSizeLabelPolicy.Disabled,
            Description = "Never label, whatever the account setting says",
            Values = _TS("Never label", "Ne jamais étiqueter", "Nooit labelen"),
        },
    ];
}

/// <summary>How a file matching a <c>PrSizeIgnoreRule</c> counts towards a pull request's size (PRD D5).</summary>
public enum EPrSizeIgnoreMode
{
    /// <summary>The file does not count at all.</summary>
    Ignore,

    /// <summary>Only the file's net deletions count: <c>max(deletions − additions, 0)</c>.</summary>
    IgnoreAdditions,
}

/// <summary>The selectable values of <see cref="EPrSizeIgnoreMode"/>, rendered as a dropdown.</summary>
public sealed class PrSizeIgnoreMode : TransientLookupReference<EPrSizeIgnoreMode>
{
    private PrSizeIgnoreMode() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<PrSizeIgnoreMode> Items { get; } =
    [
        new PrSizeIgnoreMode
        {
            Key = EPrSizeIgnoreMode.Ignore,
            Description = "The file does not count",
            Values = _TS("Ignore", "Ignorer", "Negeren"),
        },
        new PrSizeIgnoreMode
        {
            Key = EPrSizeIgnoreMode.IgnoreAdditions,
            Description = "Only the file's net deletions count",
            Values = _TS("Ignore additions", "Ignorer les ajouts", "Toevoegingen negeren"),
        },
    ];
}
