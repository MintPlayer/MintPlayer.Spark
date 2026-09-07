using CodeCoverage.Entities;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;

namespace CodeCoverage.Actions;

/// <summary>
/// Exists to make <c>EventColumnMapping</c>'s nested form renderable, and to say why publishing the
/// type is safe.
/// </summary>
/// <remarks>
/// There is nothing to load or save here — a rule lives inside its board and is written through
/// <see cref="GitHubProject"/>'s own save. What the grant buys is <b>metadata</b>: an inline
/// <c>AsDetail</c> row is rendered from the nested type's attributes, and
/// <c>GET /spark/types</c> omits any type the caller has no <c>Query</c> right on. Without the
/// right the row rendered with <b>no fields at all</b> — an Add button that produced an empty row,
/// no error in the console and none in the log, because an absent type is indistinguishable from a
/// type with no visible attributes.
/// <para>
/// That is worth remembering as a rule rather than an incident: a type used <em>only</em> as an
/// <c>asDetailType</c> still needs a type-level grant, even though it owns no query, is never
/// queried, and never appears in a menu.
/// </para>
/// </remarks>
public partial class EventColumnMappingActions : DefaultPersistentObjectActions<EventColumnMapping>, ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "This type declares no query and owns no collection: its rows are the EventMappings list " +
        "embedded in one GitHubProject document, and they reach a caller only as part of that " +
        "board's page. Scoping is therefore the board's, in GitHubProjectVisibility's row filter — " +
        "GitHubProjectActions.OnLoadAsync applies it, so a caller who cannot see a board never " +
        "receives its rules, and writes go through the board's save under the same filter. The " +
        "grant here is for the nested form's metadata, which is the type's attribute definitions " +
        "and carries no row data at all.";
}
