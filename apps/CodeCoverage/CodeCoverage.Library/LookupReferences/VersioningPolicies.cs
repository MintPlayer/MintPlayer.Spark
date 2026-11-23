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

/// <summary>
/// Whether the app keeps a changelog for a repository (docs/coverage_changelog_PRD.md D3, D6, D10), or defers to its
/// account: the release notes copied into the database for the repository page, and title correction switched on with it.
/// </summary>
public enum EChangelogPolicy
{
    /// <summary>Defer to the owning account's setting. The default for every repository, including stored ones.</summary>
    Inherit,

    /// <summary>Keep the changelog, whatever the account says.</summary>
    Enabled,

    /// <summary>No changelog page and no title correction on its behalf.</summary>
    Disabled,
}

/// <summary>The selectable values of <see cref="EChangelogPolicy"/>, rendered as a dropdown.</summary>
public sealed class ChangelogPolicy : TransientLookupReference<EChangelogPolicy>
{
    private ChangelogPolicy() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<ChangelogPolicy> Items { get; } =
    [
        new ChangelogPolicy
        {
            Key = EChangelogPolicy.Inherit,
            Description = "Use the account's setting",
            Values = _TS("Use account setting", "Utiliser le paramètre du compte", "Accountinstelling gebruiken"),
        },
        new ChangelogPolicy
        {
            Key = EChangelogPolicy.Enabled,
            Description = "Keep a changelog of every release and correct pull-request titles for it",
            Values = _TS("Keep a changelog", "Tenir un journal des modifications", "Changelog bijhouden"),
        },
        new ChangelogPolicy
        {
            Key = EChangelogPolicy.Disabled,
            Description = "No changelog, whatever the account setting says",
            Values = _TS("No changelog", "Pas de journal des modifications", "Geen changelog"),
        },
    ];
}

/// <summary>
/// Whether onboarding adds the "Type of change" section to a repository's pull-request template (docs/coverage_changelog_PRD.md
/// D12, D13), or defers to its account.
/// </summary>
public enum EPrTemplatePolicy
{
    /// <summary>Defer to the owning account's setting. The default for every repository, including stored ones.</summary>
    Inherit,

    /// <summary>Add the section when onboarding.</summary>
    Enabled,

    /// <summary>Never touch the pull-request template.</summary>
    Disabled,
}

/// <summary>The selectable values of <see cref="EPrTemplatePolicy"/>, rendered as a dropdown.</summary>
public sealed class PrTemplatePolicy : TransientLookupReference<EPrTemplatePolicy>
{
    private PrTemplatePolicy() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<PrTemplatePolicy> Items { get; } =
    [
        new PrTemplatePolicy
        {
            Key = EPrTemplatePolicy.Inherit,
            Description = "Use the account's setting",
            Values = _TS("Use account setting", "Utiliser le paramètre du compte", "Accountinstelling gebruiken"),
        },
        new PrTemplatePolicy
        {
            Key = EPrTemplatePolicy.Enabled,
            Description = "Add a 'Type of change' section to the pull-request template when onboarding",
            Values = _TS("Add the section", "Ajouter la section", "Sectie toevoegen"),
        },
        new PrTemplatePolicy
        {
            Key = EPrTemplatePolicy.Disabled,
            Description = "Never touch the pull-request template, whatever the account setting says",
            Values = _TS("Leave the template alone", "Ne pas toucher au modèle", "Sjabloon niet aanraken"),
        },
    ];
}

/// <summary>
/// The account's default for <see cref="EPrTemplatePolicy"/> (D13). Zero is Enabled, so a stored account without the
/// field reads the default without a backfill (a patch script is what D16 rules out for new code).
/// </summary>
public enum EPrTemplateDefault
{
    /// <summary>Add the section when onboarding. The default.</summary>
    Enabled,

    /// <summary>Never touch pull-request templates.</summary>
    Disabled,
}

/// <summary>The selectable values of <see cref="EPrTemplateDefault"/>, rendered as a dropdown.</summary>
public sealed class PrTemplateDefault : TransientLookupReference<EPrTemplateDefault>
{
    private PrTemplateDefault() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<PrTemplateDefault> Items { get; } =
    [
        new PrTemplateDefault
        {
            Key = EPrTemplateDefault.Enabled,
            Description = "Add a 'Type of change' section to the pull-request template when onboarding",
            Values = _TS("Add the section", "Ajouter la section", "Sectie toevoegen"),
        },
        new PrTemplateDefault
        {
            Key = EPrTemplateDefault.Disabled,
            Description = "Never touch pull-request templates",
            Values = _TS("Leave templates alone", "Ne pas toucher aux modèles", "Sjablonen niet aanraken"),
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
