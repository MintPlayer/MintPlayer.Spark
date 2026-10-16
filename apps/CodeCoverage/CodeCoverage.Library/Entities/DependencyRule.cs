using CodeCoverage.LookupReferences;
using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// An account-wide dependency rule (docs/code-coverage/dependency-updates-PRD.md §4.2, Q9, R6), embedded in
/// <see cref="Account.DependencyRules"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>An enforced rule is a ceiling</b> the repository's <c>coverage.yml</c> cannot exceed: the file may narrow it (a lower
/// threshold, an extra ignore), never loosen it, and an <c>approve</c> comment cannot lift a group above it (§7.5). A
/// rule that is not enforced is a default the file replaces whenever any of the file's own globs or rules names the
/// package.
/// </para>
/// <para>
/// §4.2's <c>pin &lt;range&gt;</c> action became <see cref="MaxUpdate"/>: the bot never parses a range or a version beyond
/// the R17 floor (R1), so "stay on this major" is expressed as "no update above minor".
/// </para>
/// </remarks>
[ValueObject]
public partial class DependencyRule
{
    /// <summary>Stable key of the row, so a save populates onto the stored row (see <see cref="EventColumnMapping.Id"/>).</summary>
    [ValueKey]
    public string Id { get; set; } = string.Empty;

    /// <summary>A glob over the dependency name (<c>@angular/*</c>, <c>Microsoft.CodeAnalysis*</c>); <c>*</c> spans <c>/</c>.</summary>
    public string Match { get; set; } = string.Empty;

    /// <summary>Only records of this ecosystem (<c>npm</c>, <c>nuget</c>, <c>github-actions</c>, <c>platform:dotnet</c>, ...);
    /// empty matches every ecosystem.</summary>
    public string? Ecosystem { get; set; }

    /// <summary>The largest update planned without an approval; for an enforced rule, also the largest one an approval may admit.</summary>
    [LookupReference(typeof(DependencyThreshold))]
    public EDependencyThreshold MaxUpdate { get; set; } = EDependencyThreshold.Minor;

    /// <summary>Never update the matching dependencies; they are listed as ignored.</summary>
    public bool Ignore { get; set; }

    /// <summary>Move the matching dependencies as one group.</summary>
    public string? Group { get; set; }

    /// <summary>A ceiling the repository's file cannot exceed (default), or a default it replaces.</summary>
    public bool Enforced { get; set; } = true;
}
