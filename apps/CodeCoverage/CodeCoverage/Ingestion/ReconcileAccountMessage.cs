using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.Ingestion;

/// <summary>
/// Queued when a webhook says an installation's repository set changed; processed by
/// <see cref="ReconcileAccountRecipient"/>, which asks GitHub what that set actually is now.
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
/// </summary>
[MessageQueue("coverage-reconcile-account")]
public record ReconcileAccountMessage
{
    public required long AccountGitHubId { get; init; }
}
