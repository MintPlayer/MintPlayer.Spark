using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// A GitHub Projects V2 board this app knows about, and the automation rules a user has configured
/// for it.
/// <para>
/// <b>Why this is a document rather than a live GraphQL query.</b> The alternative considered was a
/// <c>clrType</c>-less query that listed boards from GitHub on every view. The deciding fact is that
/// an enabled board has to be persisted either way — the webhook recipient must answer "does any
/// board automate this event for this repository?" without calling GitHub, or every delivery costs
/// an API round trip before it can decide to do nothing. Once that document exists, a live query
/// buys nothing and costs real capability: composed types get no row filtering and no redaction, no
/// detail page, no per-row <c>can</c> block, and a GitHub outage renders the grid as a 500.
/// </para>
/// <para>
/// Boards are discovered by reconciliation, on the same nightly cron and the same manual Resync
/// button as repositories, and are <b>never deleted</b> — a board that vanishes is marked
/// disconnected, exactly as a repository is, so a transient GitHub failure cannot destroy a user's
/// rules.
/// </para>
/// </summary>
[GenerateIndex]
public class GitHubProject
{
    /// <summary>Document id of this board, <c>GitHubProjects/{NodeId}</c>.</summary>
    public string? Id { get; set; }

    /// <summary>
    /// Derives the document id from GitHub's GraphQL node id, so discovery is an idempotent upsert:
    /// re-running the reconciler over a board it already knows updates that document instead of
    /// creating a second one.
    /// <para>
    /// The node id is used rather than the board <see cref="Number"/> because the number is only
    /// unique within an owner, while the node id is globally unique — and because a board moved
    /// between owners keeps its node id and would otherwise appear twice.
    /// </para>
    /// </summary>
    public static string DocumentId(string nodeId) => $"GitHubProjects/{nodeId}";

    /// <summary>The GitHub user or organization that owns this board.</summary>
    [Reference(typeof(Account))]
    public string? Account { get; set; }

    /// <summary>GitHub login of the owning user or organization.</summary>
    /// <remarks>
    /// The field row security filters on, so a caller only sees boards for owners they manage.
    /// </remarks>
    public string OwnerLogin { get; set; } = string.Empty;

    /// <summary>The GitHub App installation that can act on this board.</summary>
    /// <remarks>
    /// Needed to mint the installation token every Projects V2 mutation runs under — a user token
    /// would tie automation to whoever happened to configure it.
    /// </remarks>
    public long InstallationId { get; set; }

    /// <summary>GitHub's GraphQL node id for the board.</summary>
    /// <remarks>Globally unique and stable across renames and owner changes.</remarks>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>The board's number within its owner, as it appears in the URL.</summary>
    /// <remarks>Shown to users because it is what they recognise.</remarks>
    public int Number { get; set; }

    /// <summary>The board's title on GitHub.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The board's single-select "Status" field, whose options are its columns.</summary>
    /// <remarks>
    /// Its node id, required by every card-move mutation alongside the target option id.
    /// </remarks>
    public string? StatusFieldId { get; set; }

    /// <summary>The board's columns as last read from GitHub.</summary>
    /// <remarks>
    /// Cached so a webhook delivery can resolve a target column without an API call; refreshed by
    /// the <c>SyncColumns</c> action and by nightly reconciliation.
    /// </remarks>
    public List<ProjectColumn> Columns { get; set; } = [];

    /// <summary>The automation rules for this board.</summary>
    /// <remarks>
    /// Preserved when <see cref="AutomationEnabled"/> is turned off, so disabling automation is
    /// reversible rather than destructive.
    /// </remarks>
    public List<EventColumnMapping> EventMappings { get; set; } = [];

    /// <summary>Master switch: off means discovered but inert, and no event does anything.</summary>
    /// <remarks>
    /// Defaults to false deliberately, because discovery is automatic. Boards appear without anyone
    /// asking for them, and a default of true would mean installing the app on an organization
    /// silently started moving cards on every board it could see.
    /// </remarks>
    public bool AutomationEnabled { get; set; }

    /// <summary>Whether the GitHub App can still see this board.</summary>
    /// <remarks>
    /// Mirrors <see cref="Repository.Connection"/>, including its default: every document written
    /// before this field existed deserializes to <see cref="RepositoryConnection.Connected"/>, so
    /// no migration is needed and the nightly reconciler corrects the ones that are actually gone.
    /// </remarks>
    public RepositoryConnection Connection { get; set; } = RepositoryConnection.Connected;

    /// <summary>Why the board is disconnected, from <c>DisconnectedReasons</c>; null while connected.</summary>
    public string? DisconnectedReason { get; set; }

    /// <summary>When the board was last disconnected (UTC); null while connected.</summary>
    public DateTime? DisconnectedAtUtc { get; set; }

    /// <summary>When the board's columns were last read from GitHub (UTC).</summary>
    public DateTime? ColumnsSyncedAtUtc { get; set; }
}
