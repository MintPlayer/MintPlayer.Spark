using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.LookupReferences;

/// <summary>
/// Whether the app corrects a repository's pull-request titles, or defers to its account
/// (docs/coverage_package_versioning_PRD.md D14). Three named states for the reasons on <see cref="EDeleteBranchPolicy"/>.
/// </summary>
public enum EPrTitlePolicy
{
    /// <summary>Defer to the owning account's setting. The default for every repository, including stored ones.</summary>
    Inherit,

    /// <summary>Correct this repository's pull-request titles, whatever the account says.</summary>
    Enabled,

    /// <summary>Never touch this repository's pull-request titles.</summary>
    Disabled,
}

/// <summary>The selectable values of <see cref="EPrTitlePolicy"/>, rendered as a dropdown.</summary>
public sealed class PrTitlePolicy : TransientLookupReference<EPrTitlePolicy>
{
    private PrTitlePolicy() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<PrTitlePolicy> Items { get; } =
    [
        new PrTitlePolicy
        {
            Key = EPrTitlePolicy.Inherit,
            Description = "Use the account's setting",
            Values = _TS("Use account setting", "Utiliser le paramètre du compte", "Accountinstelling gebruiken"),
        },
        new PrTitlePolicy
        {
            Key = EPrTitlePolicy.Enabled,
            Description = "Rewrite pull-request titles to '<type>: <title> (#issue)'",
            Values = _TS("Correct titles", "Corriger les titres", "Titels corrigeren"),
        },
        new PrTitlePolicy
        {
            Key = EPrTitlePolicy.Disabled,
            Description = "Never touch pull-request titles, whatever the account setting says",
            Values = _TS("Never correct", "Ne jamais corriger", "Nooit corrigeren"),
        },
    ];
}

/// <summary>Whether the app creates a GitHub Release for every version tag it pushes (D15), or defers to its account.</summary>
public enum EReleaseOnTagPolicy
{
    /// <summary>Defer to the owning account's setting. The default for every repository, including stored ones.</summary>
    Inherit,

    /// <summary>Create a Release per tag.</summary>
    Enabled,

    /// <summary>Push tags only.</summary>
    Disabled,
}

/// <summary>The selectable values of <see cref="EReleaseOnTagPolicy"/>, rendered as a dropdown.</summary>
public sealed class ReleaseOnTagPolicy : TransientLookupReference<EReleaseOnTagPolicy>
{
    private ReleaseOnTagPolicy() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<ReleaseOnTagPolicy> Items { get; } =
    [
        new ReleaseOnTagPolicy
        {
            Key = EReleaseOnTagPolicy.Inherit,
            Description = "Use the account's setting",
            Values = _TS("Use account setting", "Utiliser le paramètre du compte", "Accountinstelling gebruiken"),
        },
        new ReleaseOnTagPolicy
        {
            Key = EReleaseOnTagPolicy.Enabled,
            Description = "Create a Release for every version tag",
            Values = _TS("Create Releases", "Créer des releases", "Releases aanmaken"),
        },
        new ReleaseOnTagPolicy
        {
            Key = EReleaseOnTagPolicy.Disabled,
            Description = "Push tags only, whatever the account setting says",
            Values = _TS("Tags only", "Tags uniquement", "Alleen tags"),
        },
    ];
}

/// <summary>Whether the app cuts <c>release/&lt;major&gt;.x</c> when a platform major moves (D12), or defers to its account.</summary>
public enum EAutoCutReleaseBranchPolicy
{
    /// <summary>Defer to the owning account's setting. The default for every repository, including stored ones.</summary>
    Inherit,

    /// <summary>Cut the branch.</summary>
    Enabled,

    /// <summary>Never cut a branch.</summary>
    Disabled,
}

/// <summary>The selectable values of <see cref="EAutoCutReleaseBranchPolicy"/>, rendered as a dropdown.</summary>
public sealed class AutoCutReleaseBranchPolicy : TransientLookupReference<EAutoCutReleaseBranchPolicy>
{
    private AutoCutReleaseBranchPolicy() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<AutoCutReleaseBranchPolicy> Items { get; } =
    [
        new AutoCutReleaseBranchPolicy
        {
            Key = EAutoCutReleaseBranchPolicy.Inherit,
            Description = "Use the account's setting",
            Values = _TS("Use account setting", "Utiliser le paramètre du compte", "Accountinstelling gebruiken"),
        },
        new AutoCutReleaseBranchPolicy
        {
            Key = EAutoCutReleaseBranchPolicy.Enabled,
            Description = "Create release/<major>.x when a platform major moves",
            Values = _TS("Cut release branches", "Créer les branches de release", "Releasebranches aanmaken"),
        },
        new AutoCutReleaseBranchPolicy
        {
            Key = EAutoCutReleaseBranchPolicy.Disabled,
            Description = "Never create a release branch, whatever the account setting says",
            Values = _TS("Never cut", "Ne jamais créer", "Nooit aanmaken"),
        },
    ];
}
