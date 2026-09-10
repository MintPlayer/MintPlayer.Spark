namespace MintPlayer.Spark.Webhooks.GitHub.Configuration;

/// <summary>
/// The few fields needed to decide <em>who</em> a webhook concerns, read straight out of the raw
/// payload without materialising the event.
/// </summary>
/// <remarks>
/// These values come from the delivery body. The body's signature is verified before this is built,
/// so the values are as trustworthy as the delivery itself — but they are still attacker-influenced
/// content (anyone who can open a pull request controls <see cref="SenderLogin"/>). Route on them;
/// do not authorize on them.
/// </remarks>
/// <param name="EventName">The <c>X-GitHub-Event</c> header, e.g. <c>pull_request</c>.</param>
/// <param name="SenderLogin">
/// <c>sender.login</c> — the GitHub user whose action produced the delivery. Empty when the payload
/// carries no sender.
/// </param>
/// <param name="RepositoryFullName"><c>repository.full_name</c>, e.g. <c>MintPlayer/Spark</c>. Empty when absent.</param>
/// <param name="InstallationId"><c>installation.id</c>, or <c>0</c> when absent.</param>
public sealed record GitHubWebhookRoutingContext(
    string EventName,
    string SenderLogin,
    string RepositoryFullName,
    long InstallationId);
