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
        "and carries no row data at all.";
}
