using CodeCoverage.Forge;
using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.Ingestion;

/// <summary>
/// Queued when a webhook says an account's repository set changed; processed by
/// <see cref="ReconcileAccountRecipient"/>, which asks the forge what that set actually is now.
/// <para>
/// The webhook cannot be trusted to describe the change, only to announce that one happened.
/// Measured 2026-09-05: narrowing an installation from "all repositories" to a single selected
/// repository delivers <c>installation_repositories</c> with action <b>added</b>,
/// <c>repository_selection: "selected"</c>, the newly selected repository in
/// <c>repositories_added</c> — and <c>repositories_removed</c> <b>empty</b>. Every other repository
/// silently left the installation's reach and no event says so. An app that believed the payload
/// would go on advertising repositories it can no longer see, which is the bug this whole feature
/// exists to fix, arriving by a second route.
/// </para>
/// <para>
/// So the payload is applied for the timely case and this message is broadcast for the truthful
/// one. The nightly sweep would find it eventually; this closes the window to seconds.
/// </para>
/// <para>
/// On <see cref="Feedback.CoverageQueues.Publishing"/> because it calls GitHub, which is what
/// that queue carries — and because the licence caps subscriptions per database, so a third
/// queue name would silently kill one of the two that exist.
/// </para>
/// </summary>
[MessageQueue(Feedback.CoverageQueues.Publishing)]
public record ReconcileAccountMessage
{
    /// <summary>The forge that hosts the account.</summary>
    /// <remarks>
    /// ⚠️ <b>Required, because the recipient cannot otherwise know which forge to ask.</b> It
    /// hard-coded GitHub until 2026-09-22 — so renaming the id field alone would have produced a
    /// message that looked forge-neutral and still resolved every account against GitHub.
    /// </remarks>
    public required EForgeProvider Provider { get; init; }

    /// <summary>The forge's numeric id for the account, which its document id is keyed on.</summary>
    /// <remarks>
    /// Named for the concept rather than the forge. It was <c>AccountGitHubId</c>, which a second
    /// forge could not fill honestly — a contract name, not a variable name, since this type is
    /// serialised into the message queue.
    /// <para>
    /// ⚠️ <b>A rename here is a persisted-payload change.</b> <c>SparkMessage</c> stores the JSON,
    /// and Json.NET ignores a member it cannot bind — so an in-flight message written with the old
    /// name would deserialize to <c>0</c>, load nothing, and return silently. Renamed only because
    /// the queue was verified empty, and <c>M_202609221100</c> drops any stragglers rather than
    /// leaving them to fail quietly.
    /// </para>
    /// </remarks>
    public required long AccountId { get; init; }
}
