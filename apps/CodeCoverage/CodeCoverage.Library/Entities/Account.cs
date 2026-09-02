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
    /// <summary>Document id of this account, <c>Accounts/{GitHubId}</c>.</summary>
    public string? Id { get; set; }

    /// <summary>GitHub's numeric id for this user or organization; stable even when the login is renamed.</summary>
    public long GitHubId { get; set; }

    /// <summary>The GitHub login (user name or organization slug) of this account.</summary>
    public string Login { get; set; } = string.Empty;

    /// <summary>"User" or "Organization".</summary>
    public string Type { get; set; } = "User";

    /// <summary>URL of the account's GitHub avatar image, shown next to the login.</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>GitHub App installation on this account, when the app is installed.</summary>
    public long? InstallationId { get; set; }

    public static string DocumentId(long gitHubId) => $"Accounts/{gitHubId}";
}
