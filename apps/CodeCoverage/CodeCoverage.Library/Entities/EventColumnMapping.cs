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

    /// <summary>
    /// When this rule last moved a card (UTC), or null if it never has. The first thing to look at
    /// when someone reports that automation "stopped working": a rule that has never fired is
    /// configured wrong, one that fired until a date stopped working then.
    /// </summary>
    public DateTime? LastFiredAtUtc { get; set; }

    /// <summary>
    /// Why this rule last failed, or null if its last attempt succeeded.
    /// <para>
    /// This exists because the message queue is the wrong place to look for it, and that is worth
    /// spelling out. Spark's messaging does dead-letter a handler after <c>MaxAttempts</c> and does
    /// persist <c>LastError</c> on the message — but only when the recipient <em>throws</em>, and
    /// throwing is the wrong response to a single broken rule: one delivery can match several
    /// boards, so failing the whole message re-runs the moves that already succeeded and moves
    /// those cards twice. Completing the message is correct, which means the queue records nothing.
    /// </para>
    /// <para>
    /// And even when a message does dead-letter, a <c>SparkMessage</c> is invisible to this
    /// configuration screen and is deleted after the retention window. The rule is what the user
    /// looks at, so the failure belongs on the rule.
    /// </para>
    /// <para>
    /// The most likely value by far is "the target column no longer exists": a rule stores an
    /// option <em>id</em>, and nothing tells this app when a column is deleted — the Projects V2
    /// webhook events are organization-scoped and unsubscribed either way — so without this the
    /// rule stays syntactically valid and silently never matches.
    /// </para>
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>When <see cref="LastError"/> was recorded (UTC); null when the rule is healthy.</summary>
    public DateTime? LastErrorAtUtc { get; set; }
}
