using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// A GitHub user or organization that owns repositories. Created/updated from
/// GitHub App installation webhooks; document id is Accounts/{GitHubId} so
/// webhook upserts are idempotent.
/// </summary>
[GenerateIndex]
public class Account
{
    public string? Id { get; set; }

    public long GitHubId { get; set; }

    public string Login { get; set; } = string.Empty;

    /// <summary>"User" or "Organization".</summary>
    public string Type { get; set; } = "User";

    public string? AvatarUrl { get; set; }

    /// <summary>GitHub App installation on this account, when the app is installed.</summary>
    public long? InstallationId { get; set; }

    public static string DocumentId(long gitHubId) => $"Accounts/{gitHubId}";
}
