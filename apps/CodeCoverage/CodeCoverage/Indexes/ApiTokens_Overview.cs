using CodeCoverage.Entities;
using CodeCoverage.Forge;
using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Indexes;

namespace CodeCoverage.Indexes;

/// <summary>
/// Upload tokens with their owning account's login, key and forge — the index behind
/// <see cref="VApiToken"/> and every token list.
/// </summary>
/// <remarks>
/// <c>LoadDocument</c> makes the index track the account: renaming it re-indexes its tokens, so the
/// login a grid shows and the key the row filter matches are the account's current ones, not a copy
/// taken when the token was minted.
/// </remarks>
public partial class ApiTokens_Overview : SparkIndexCreationTask<ApiToken>
{
    public ApiTokens_Overview()
    {
        Map = tokens => from token in tokens
                        let account = LoadDocument<Account>(token.Account)
                        select new VApiToken
                        {
                            Id = token.Id,
                            Scope = token.Scope,
                            Account = token.Account,
                            AccountLogin = account != null ? account.Login : null,
                            // ForgeOwner.ToString(), spelled out: an index map cannot call it. The
                            // provider spellings are ForgeProviders.ToCanonicalString's.
                            AccountOwnerKey = account != null
                                ? (account.Provider == EForgeProvider.GitHub ? "github"
                                    : account.Provider == EForgeProvider.GitLab ? "gitlab"
                                    : "bitbucket") + ":" + account.Login
                                : null,
                            Provider = account != null ? (EForgeProvider?)account.Provider : null,
                            RepositoryIds = token.RepositoryIds,
                            Description = token.Description,
                            CreatedByUserId = token.CreatedByUserId,
                            CreatedAtUtc = token.CreatedAtUtc,
                            RevokedAtUtc = token.RevokedAtUtc,
                        };

        // Only what the token document cannot answer for: these three are read from the account and
        // exist nowhere on the token, so unstored they would project as null. The rest is read back
        // from the document, as in Commits_ByRepository.
        Store(nameof(VApiToken.AccountLogin), FieldStorage.Yes);
        Store(nameof(VApiToken.AccountOwnerKey), FieldStorage.Yes);
        Store(nameof(VApiToken.Provider), FieldStorage.Yes);
    }
}
