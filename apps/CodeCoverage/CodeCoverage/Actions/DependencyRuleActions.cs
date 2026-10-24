using CodeCoverage.Entities;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;

namespace CodeCoverage.Actions;

/// <summary>
/// Exists to say why publishing <c>DependencyRule</c> is safe; like <see cref="EventColumnMappingActions"/>, the type is an
/// <c>asDetailType</c> only (<see cref="Account.DependencyRules"/>), and its grant buys the nested form's metadata.
/// </summary>
/// <remarks>
/// Without this class the application refused to start: security.json grants <c>QueryReadEditNewDelete/DependencyRule</c>
/// to the authenticated role, and Spark requires a type with a row-listing grant to decide its row security.
/// </remarks>
public partial class DependencyRuleActions : DefaultPersistentObjectActions<DependencyRule>, ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "This type declares no query and owns no collection: its rows are the DependencyRules list " +
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
    public override Task<bool> IsAllowedAsync(string action, DependencyRule entity) => Task.FromResult(false);
}
