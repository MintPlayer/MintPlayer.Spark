using CodeCoverage.Entities;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;

namespace CodeCoverage.Actions;

/// <summary>
/// Exists to say why publishing <c>PrSizeLabel</c> is safe; like <see cref="DependencyRuleActions"/>, the type is an
/// <c>asDetailType</c> only (<see cref="Account.PrSizeLabelScale"/>), and its grant buys the nested form's metadata.
/// </summary>
public partial class PrSizeLabelActions : DefaultPersistentObjectActions<PrSizeLabel>, ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "This type declares no query and owns no collection: its rows are the PrSizeLabelScale list " +
        "embedded in one Account document, and they reach a caller only as part of that account's " +
        "page. Scoping is therefore the account's, in AccountActions' row filter, and writes go " +
        "through the account's save, which that filter narrows to accounts the caller manages. The " +
        "grant here is for the nested form's metadata, which is the type's attribute definitions " +
        "and carries no row data at all. A standalone " +
        "document of this type is refused outright, by IsAllowedAsync.";

    /// <summary>
    /// Refuses every standalone document of this type. The type-level <c>New</c>/<c>Edit</c>/<c>Delete</c> grant
    /// lets any signed-in user reach <c>/spark/po/create</c> for it, and without this they could store rows that
    /// belong to no owner they manage. Rows inside the owner's document are mapped by the owner's save, which
    /// checks the owner's row security, and never come through here.
    /// </summary>
    public override Task<bool> IsAllowedAsync(string action, PrSizeLabel entity) => Task.FromResult(false);
}

/// <summary>
/// Exists to say why publishing <c>PrSizeIgnoreRule</c> is safe: an <c>asDetailType</c> only, embedded in an account
/// (<see cref="Account.PrSizeIgnoreRules"/>) or a repository (<see cref="Repository.PrSizeIgnoreRules"/>).
/// </summary>
public partial class PrSizeIgnoreRuleActions : DefaultPersistentObjectActions<PrSizeIgnoreRule>, ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "This type declares no query and owns no collection: its rows are the PrSizeIgnoreRules lists " +
        "embedded in one Account or Repository document, and they reach a caller only as part of that " +
        "owner's page. Scoping is therefore the owner's, in AccountActions' and RepositoryActions' row " +
        "filters and their owner-settings redaction, and writes go through the owner's save. The grant " +
        "here is for the nested form's metadata, which carries no row data at all. A standalone " +
        "document of this type is refused outright, by IsAllowedAsync.";

    /// <summary>
    /// Refuses every standalone document of this type. The type-level <c>New</c>/<c>Edit</c>/<c>Delete</c> grant
    /// lets any signed-in user reach <c>/spark/po/create</c> for it, and without this they could store rows that
    /// belong to no owner they manage. Rows inside the owner's document are mapped by the owner's save, which
    /// checks the owner's row security, and never come through here.
    /// </summary>
    public override Task<bool> IsAllowedAsync(string action, PrSizeIgnoreRule entity) => Task.FromResult(false);
}
