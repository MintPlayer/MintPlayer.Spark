using CodeCoverage.LookupReferences;
using MintPlayer.Spark.Abstractions;

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
public partial class EventColumnMapping
{
    /// <summary>
    /// Stable key for this row, so the generic UI's inline collection editor can identify it across
    /// saves. Derived from the event type, because one board maps each event at most once.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Which GitHub event this rule reacts to.</summary>
    /// <remarks>
    /// One of <see cref="WebhookEventType"/>'s keys. A lookup rather than free text: the set is
    /// closed, and a typo in a free-text field would produce a rule that silently never fires.
    /// <para>
    /// The <see cref="LookupReferenceAttribute"/> is what puts the dropdown on the form, and it has
    /// to live here rather than in the generated model JSON — model synchronization strips a
    /// hand-added <c>lookupReferenceType</c> (observed), because it derives that field from the
    /// attribute. <c>editMode</c> and <c>isReadOnly</c> survive hand-editing; this does not.
    /// </para>
    /// </remarks>
    [LookupReference(typeof(WebhookEventType))]
    public string EventType { get; set; } = string.Empty;

    /// <summary>The board column this rule moves the card to.</summary>
    /// <remarks>
    /// The single-select option id, from the board's cached <see cref="GitHubProject.Columns"/>.
    /// <para>
    /// An option <em>id</em>, deliberately, not a column name. Names are renameable and not unique;
    /// storing the name would leave every rule on a board broken the moment someone renamed a
    /// column, and broken silently, because the move would simply match nothing.
    /// </para>
    /// <para>
    /// <b>Why this is a plain string and not a lookup, when <see cref="EventType"/> is a lookup.</b>
    /// Spark has two lookup shapes and neither fits a per-board value:
    /// <list type="bullet">
    /// <item><see cref="TransientLookupReference{TKey}"/> is a <em>static</em> set declared in C# and
    /// fixed at compile time — right for the eighteen webhook events, impossible for columns that a
    /// user creates on GitHub.</item>
    /// <item><see cref="DynamicLookupReference{TValue}"/> is persisted and editable at runtime, but
    /// it is <em>one global set per lookup name</em>: a single <c>LookupReferences/{Name}</c>
    /// document with one <c>Values</c> list. Columns are per <em>board</em> — one board has
    /// Backlog/In&#160;Progress/Done, another has Triage/Doing/Shipped — so a dynamic lookup would
    /// offer every board's columns on every other board.</item>
    /// </list>
    /// What is needed is an option source scoped to the <em>parent document</em>, and <b>Spark does
    /// express that — via <c>[Reference]</c>, not via a lookup.</b> The named query is a
    /// <c>Custom.*</c> method reading <c>CustomQueryArgs.Parent</c>, which Spark resolves and
    /// authorizes; it works from an embedded <c>AsDetail</c> row because the client sends the
    /// <em>root</em> document as the parent for <c>isArray</c> child columns. See
    /// <c>ProjectColumnActions.Project_Columns</c>.
    /// </para>
    /// <para>
    /// It costs no new collection: the query returns the board's own embedded
    /// <see cref="GitHubProject.Columns"/> as a plain in-memory sequence, which the query executor
    /// accepts alongside <c>IQueryable</c> and <c>IRavenQueryable</c>. Promoting columns to
    /// documents would buy only a server-resolved breadcrumb and direct queryability, at the price
    /// of a collection whose create/update/delete lifecycle the reconciler would have to manage.
    /// </para>
    /// <para>
    /// The value is still validated where it matters — the recipient checks the id against the
    /// board's cached columns before calling GitHub, and reports a vanished column on the rule
    /// itself. A dropdown makes the wrong value unlikely; it does not make it impossible, because
    /// a column can be deleted on GitHub after a rule was saved.
    /// </para>
    /// </remarks>
    [Reference(typeof(ProjectColumn), "Project_Columns")]
    public string TargetColumnOptionId { get; set; } = string.Empty;

    /// <summary>Turns this one rule off without deleting it.</summary>
    /// <remarks>
    /// Present so a user can turn a rule off to diagnose behaviour and turn it back on, rather than
    /// deleting and retyping it.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Also move the cards of the issues a pull request closes, alongside the PR's own card.
    /// Ignored for issue events.
    /// </summary>
    /// <remarks>
    /// The linked issues move <em>in addition to</em> the pull request's own card, never instead
    /// of it.
    /// <para>
    /// Defaults to <see langword="true"/>, because the issue is the work item a board tracks and
    /// the pull request is only how the work gets done — "PR ready for review" almost always means
    /// "the thing that PR closes is ready for review".
    /// </para>
    /// <para>
    /// Ignored for issue events because GitHub models no issue-to-issue closing link, so there is
    /// nothing for an issue rule to resolve. Extending it to an issue's linked pull requests, or to
    /// its sub-issues, was considered and declined: an issue rule is about the issue.
    /// </para>
    /// <para>
    /// This flag chooses <em>which items</em> the rule moves. Whether any of them is added to the
    /// board when absent is a separate question, answered by <see cref="AutoAddToBoard"/>.
    /// </para>
    /// </remarks>
    public bool MoveLinkedIssues { get; set; } = true;

    /// <summary>
    /// Add an item to the board when this rule has to move it and it is not on the board yet.
    /// </summary>
    /// <remarks>
    /// Governs the event's own subject — the issue on an issue event, the pull request on a
    /// pull-request event — and, when <see cref="MoveLinkedIssues"/> is on, each linked issue on
    /// the same terms.
    /// <para>
    /// Defaults to <see langword="true"/>, because the alternative is a rule that looks configured
    /// and does nothing: <c>IssuesOpened</c> fires on an issue that by definition was not on the
    /// board a moment ago, and a board tracking pull requests has the same problem on
    /// <c>PullRequestOpened</c>. Turning it off is how you say "only move cards a human put here".
    /// </para>
    /// <para>
    /// Per rule rather than per board, because the right answer differs by event: a "merged → Done"
    /// rule may want only work already tracked, while a team that opens PRs before filing the issue
    /// wants "ready for review" to recruit it.
    /// </para>
    /// <para>
    /// This is not "add linked items to the board". It shipped once under that reading, as a field
    /// named <c>AddLinkedIfMissing</c> scoped to linked issues alone, which left the item the event
    /// was actually about governed by hard-coded policy no rule could override — issues always
    /// added, pull requests never. Adding every PR to a board that tracks issues does bury them,
    /// but that is what this default is for, not an invariant to enforce behind the user's back.
    /// </para>
    /// </remarks>
    public bool AutoAddToBoard { get; set; } = true;

    /// <summary>When this rule last moved a card, or empty if it never has.</summary>
    /// <remarks>
    /// The first thing to look at when someone reports that automation "stopped working": a rule
    /// that has never fired is configured wrong, one that fired until a date stopped working then.
    /// </remarks>
    public DateTime? LastFiredAtUtc { get; set; }

    /// <summary>Why this rule last failed, or empty if its last attempt succeeded.</summary>
    /// <remarks>
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
    /// </remarks>
    public string? LastError { get; set; }

    /// <summary>When the last failure happened; empty when the rule is healthy.</summary>
    public DateTime? LastErrorAtUtc { get; set; }
}
