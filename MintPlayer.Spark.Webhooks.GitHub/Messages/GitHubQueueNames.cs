using Octokit.Webhooks;
using System.Text;

namespace MintPlayer.Spark.Webhooks.GitHub.Messages;

/// <summary>
/// Converts Octokit event type names to queue names.
/// e.g., "PullRequestEvent" → "spark-github-pull-request"
///       "IssuesEvent"      → "spark-github-issues"
/// </summary>
internal static class GitHubQueueNames
{
    public static string FromEventType<TEvent>() where TEvent : WebhookEvent
        => FromEventTypeName(typeof(TEvent).Name);

    internal static string FromEventTypeName(string typeName)
    {
        // Strip "Event" suffix
        var name = typeName;
        if (name.EndsWith("Event", StringComparison.Ordinal))
            name = name[..^5];

        // PascalCase → kebab-case
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0)
                sb.Append('-');
            sb.Append(char.ToLowerInvariant(c));
        }

        return $"spark-github-{sb}";
    }
}
