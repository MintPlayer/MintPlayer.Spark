namespace CodeCoverage.Entities;

/// <summary>
/// One automation rule: "when this GitHub event happens, move the card to this column".
/// <para>
/// Embedded in <see cref="GitHubProject.EventMappings"/>, and the reason board configuration is a
/// persisted document at all. These are the user's rules; a design that re-read board state from
/// GitHub on every view would have nowhere to keep them, and disabling automation would have to
/// destroy them.
/// </para>
/// </summary>
public class EventColumnMapping
{
    /// <summary>
    /// Stable key for this row, so the generic UI's inline collection editor can identify it across
    /// saves. Derived from the event type, because one board maps each event at most once.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Which GitHub event this rule reacts to, one of <c>WebhookEventType</c>'s keys. A lookup
    /// rather than free text: the set is closed, and a typo in a free-text field would produce a
    /// rule that silently never fires.
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// The single-select option id to move the card to, from the board's cached
    /// <see cref="GitHubProject.Columns"/>.
    /// <para>
    /// An option <em>id</em>, deliberately, not a column name. Names are renameable and not unique;
    /// storing the name would leave every rule on a board broken the moment someone renamed a
    /// column, and broken silently, because the move would simply match nothing.
    /// </para>
    /// </summary>
    public string TargetColumnOptionId { get; set; } = string.Empty;

    /// <summary>
    /// False disables this one rule without deleting it. Present so a user can turn a rule off to
    /// diagnose behaviour and turn it back on, rather than deleting and retyping it.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
