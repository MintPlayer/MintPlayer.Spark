using System.Text.Json.Nodes;
using CodeCoverage.ApiTokens;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Agents;
using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Tests.Mcp._Infrastructure;

/// <summary>
/// The MCP tests' data, made up and free of personal data: two GitHub organisations (<c>acme</c>, <c>other-org</c>), their
/// repositories with coverage, two users and their credentials.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>acme/open</c> (public): two commits on <c>main</c> (the second lowered coverage), the hotspot files, and a pull
/// request (#7) with patch coverage.</item>
/// <item><c>acme/secret</c> (private, once named <c>acme/old-secret</c>): seen only by <c>acme-dev</c>.</item>
/// <item><c>other-org/vault</c> (private): seen by nobody here.</item>
/// <item><c>acme/sprawl</c> (public): many long paths and one file whose lines alternate, for the output cap.</item>
/// </list>
/// <c>acme-dev</c>'s GitHub installations reach <c>acme</c>; <c>outsider</c>'s reach nothing.
/// </remarks>
internal static class CoverageMcpSeed
{
    public const long OpenId = 9101, SecretId = 9102, VaultId = 9103, SprawlId = 9104;

    /// <summary>Commit's id in the model (App_Data/Model/Commit.json): a grant names its types by id, never by name (D28).</summary>
    public const string CommitTypeId = "ec83c3f7-45d7-44e8-928a-2daf9a4b3ffe";

    public const string OpenParent = "1111111111111111111111111111111111111111";
    public const string OpenHead = "2222222222222222222222222222222222222222";
    public const string OpenPullRequestHead = "3333333333333333333333333333333333333333";
    public const string SecretHead = "4444444444444444444444444444444444444444";
    public const string VaultHead = "5555555555555555555555555555555555555555";
    public const string SprawlHead = "6666666666666666666666666666666666666666";

    public const string DevUserId = "SparkUsers/acme-dev";
    public const string OutsiderUserId = "SparkUsers/outsider";

    /// <summary>The hotspot files of <c>acme/open</c>'s head: (path, coverable, covered, uncovered ranges).</summary>
    public static readonly (string Path, int Coverable, int Covered, int Ranges)[] OpenHeadFiles =
    [
        ("src/big-untested.ts", 200, 0, 1),
        ("src/half.ts", 300, 150, 30),
        ("src/one-gap.ts", 120, 20, 1),
        ("src/a-tie.ts", 50, 10, 2),
        ("src/b-tie.ts", 45, 5, 1),
        ("lib/done.ts", 10, 10, 0),
    ];

    /// <summary>The same files at the parent: big-untested did not exist yet, half had 20 fewer uncovered lines.</summary>
    public static readonly (string Path, int Coverable, int Covered, int Ranges)[] OpenParentFiles =
    [
        ("src/half.ts", 300, 170, 25),
        ("src/one-gap.ts", 120, 20, 1),
        ("src/a-tie.ts", 50, 10, 2),
        ("src/b-tie.ts", 45, 5, 1),
        ("lib/done.ts", 10, 10, 0),
    ];

    /// <param name="DevCommitsOnlyToken">acme-dev's token whose grant allows the <c>Commit</c> type only.</param>
    public sealed record Credentials(string DevToken, string DevCommitsOnlyToken, string OutsiderToken, string UploadToken);

    public static async Task<Credentials> SeedAsync(IDocumentStore store, StubGitHubApi.Script gitHub)
    {
        using var session = store.OpenAsyncSession();

        await StoreRepositoryAsync(session, OpenId, "acme", "open", isPrivate: false, latestSha: OpenHead);
        await StoreRepositoryAsync(session, SecretId, "acme", "secret", isPrivate: true, latestSha: SecretHead, previousFullNames: ["acme/old-secret"]);
        await StoreRepositoryAsync(session, VaultId, "other-org", "vault", isPrivate: true, latestSha: VaultHead);
        await StoreRepositoryAsync(session, SprawlId, "acme", "sprawl", isPrivate: false, latestSha: SprawlHead);

        await StoreCommitAsync(session, OpenId, OpenParent, "main", DateTimeOffset.Parse("2026-09-01T10:00:00Z"), OpenParentFiles,
            message: "Parse the config file");
        await StoreCommitAsync(session, OpenId, OpenHead, "main", DateTimeOffset.Parse("2026-09-02T10:00:00Z"), OpenHeadFiles,
            message: "Add the exporter\n\nA long body that the tools never show.", parentSha: OpenParent, deltaVsParent: -2.5);
        await StoreCommitAsync(session, OpenId, OpenPullRequestHead, "feature/export", DateTimeOffset.Parse("2026-09-03T10:00:00Z"),
            [("src/feature.ts", 10, 8, 1)], message: "Export to CSV", pullRequest: 7, uncoveredLines: [4, 5],
            patch: new PatchCoverage { DiffBaseRef = OpenHead, MergeBaseSha = OpenHead, LinesCovered = 3, LinesCoverable = 5, FilesInDiff = 2, FilesMatched = 1 });
        await StoreCommitAsync(session, SecretId, SecretHead, "main", DateTimeOffset.Parse("2026-09-04T10:00:00Z"), [("src/secret.ts", 40, 30, 2)],
            message: "Rotate the keys");
        await StoreCommitAsync(session, VaultId, VaultHead, "main", DateTimeOffset.Parse("2026-09-05T10:00:00Z"), [("src/vault.ts", 40, 10, 3)],
            message: "Vault");

        // 60 files with long paths (more than one page), and one whose lines alternate: 3000 uncovered ranges.
        var sprawl = Enumerable.Range(0, 60)
            .Select(i => ($"src/{new string('n', 180)}/module-{i:D2}/{new string('f', 60)}.ts", 100 + i, 10, 3))
            .Append(("src/zigzag.ts", 6000, 3000, 3000))
            .ToArray();
        await StoreCommitAsync(session, SprawlId, SprawlHead, "main", DateTimeOffset.Parse("2026-09-06T10:00:00Z"), sprawl, message: "Sprawl");

        // GitHub's three-dot comparison for the pull request: lines 1-5 of src/feature.ts added; README.md is not measured.
        gitHub.Comparisons[$"acme/open/compare/{OpenHead}...{OpenPullRequestHead}"] = new JsonObject
        {
            ["merge_base_commit"] = new JsonObject { ["sha"] = OpenHead },
            ["files"] = new JsonArray(
                new JsonObject { ["filename"] = "src/feature.ts", ["status"] = "modified", ["patch"] = "@@ -1,0 +1,5 @@\n+a\n+b\n+c\n+d\n+e" },
                new JsonObject { ["filename"] = "README.md", ["status"] = "modified", ["patch"] = "@@ -1,1 +1,2 @@\n x\n+y" }),
        }.ToJsonString();

        // The users: GitHub-linked, as the website signs them in. acme-dev's installations reach acme.
        var devToken = await StoreUserAsync(session, DevUserId, "acme-dev", 7001, gitHub, organisations: [(8001, "acme")]);
        var devCommitsOnlyToken = await StoreTokenAsync(session, DevUserId, "stamp-acme-dev", types: [new AgentGrantType { TypeId = CommitTypeId }]);
        var outsiderToken = await StoreUserAsync(session, OutsiderUserId, "outsider", 7002, gitHub, organisations: []);

        // An upload token of acme's: a valid covt_ credential, for its own endpoints only (D23).
        var uploadToken = ApiTokenService.GenerateTokenValue();
        await session.StoreAsync(new ApiToken
        {
            Hash = ApiTokenService.Hash(uploadToken),
            Scope = CodeCoverage.LookupReferences.ApiTokenScope.Account,
            Account = Account.DocumentId(EForgeProvider.GitHub, 8001),
            Description = "ci",
            CreatedByUserId = DevUserId,
            CreatedAtUtc = DateTime.UtcNow,
        });

        await session.SaveChangesAsync();
        return new Credentials(devToken, devCommitsOnlyToken, outsiderToken, uploadToken);
    }

    private static async Task StoreRepositoryAsync(
        IAsyncDocumentSession session, long id, string owner, string name, bool isPrivate, string latestSha, List<string>? previousFullNames = null)
    {
        await session.StoreAsync(new Repository
        {
            GitHubId = id,
            Provider = EForgeProvider.GitHub,
            Name = name,
            FullName = $"{owner}/{name}",
            OwnerLogin = owner,
            IsPrivate = isPrivate,
            DefaultBranch = "main",
            PreviousFullNames = previousFullNames ?? [],
            LatestCoverage = new CoverageSummary { LinesCovered = 80, LinesCoverable = 100, BranchesCovered = 6, BranchesTotal = 10, FilesCount = 5 },
            LatestCoverageSha = latestSha,
        }, Repository.DocumentId(EForgeProvider.GitHub, id));
    }

    /// <summary>A commit with a finalized build, its tree summary and one <see cref="FileCoverage"/> per file.</summary>
    private static async Task StoreCommitAsync(
        IAsyncDocumentSession session, long repositoryId, string sha, string branch, DateTimeOffset date,
        (string Path, int Coverable, int Covered, int Ranges)[] files, string message,
        string? parentSha = null, double? deltaVsParent = null, int? pullRequest = null, int[]? uncoveredLines = null, PatchCoverage? patch = null)
    {
        var commitId = Commit.DocumentId(EForgeProvider.GitHub, repositoryId, sha);
        var buildId = Build.DocumentId(EForgeProvider.GitHub, repositoryId, sha, runId: 1, runAttempt: 1);
        var covered = files.Sum(f => f.Covered);
        var coverable = files.Sum(f => f.Coverable);

        await session.StoreAsync(new Commit
        {
            Repository = Repository.DocumentId(EForgeProvider.GitHub, repositoryId),
            Sha = sha,
            Branch = branch,
            PullRequestNumber = pullRequest,
            ParentSha = parentSha,
            Message = message,
            AuthoredAt = date,
            Coverage = new CoverageSummary { LinesCovered = covered, LinesCoverable = coverable, FilesCount = files.Length },
            CoverageDeltaVsParent = deltaVsParent,
            LatestBuildId = buildId,
        }, commitId);

        await session.StoreAsync(new Build
        {
            Commit = commitId,
            CiRunId = 1,
            CiRunAttempt = 1,
            Status = "Finalized",
            CreatedAtUtc = date.UtcDateTime,
            Patch = patch,
        }, buildId);

        await session.StoreAsync(new BuildTreeSummary
        {
            BuildId = buildId,
            Files = [.. files.Select(f => new TreeFileSummary { Path = f.Path, LinesCovered = f.Covered, LinesCoverable = f.Coverable })],
        }, BuildTreeSummary.DocumentId(buildId));

        foreach (var file in files)
        {
            await session.StoreAsync(new FileCoverage
            {
                BuildId = buildId,
                Path = file.Path,
                Lines = uncoveredLines is { } explicitly
                    ? [.. Enumerable.Range(1, file.Coverable).Select(n => new LineCoverage
                        { Number = n, Status = explicitly.Contains(n) ? LineStatus.NotCovered : LineStatus.Covered, Hits = explicitly.Contains(n) ? 0 : 1 })]
                    : Lines(file.Coverable, file.Covered, file.Ranges),
                Branches = [new LineBranchCoverage { Line = 1, Arity = 2, Floor = 1 }],
            }, FileCoverage.DocumentId(buildId, file.Path));
        }
    }

    /// <summary>
    /// <paramref name="coverable"/> lines, <paramref name="covered"/> of them covered, the uncovered ones in exactly
    /// <paramref name="ranges"/> runs: one covered line between two runs, the remaining covered lines after the last.
    /// </summary>
    public static List<LineCoverage> Lines(int coverable, int covered, int ranges)
    {
        var uncovered = coverable - covered;
        var lines = new List<LineCoverage>(coverable);
        var coveredLeft = covered;
        for (var run = 0; run < ranges; run++)
        {
            var length = uncovered / ranges + (run < uncovered % ranges ? 1 : 0);
            for (var i = 0; i < length; i++)
                lines.Add(Line(lines.Count + 1, covered: false));
            if (run < ranges - 1)
            {
                lines.Add(Line(lines.Count + 1, covered: true));
                coveredLeft--;
            }
        }
        while (coveredLeft-- > 0)
            lines.Add(Line(lines.Count + 1, covered: true));
        return lines;
    }

    private static LineCoverage Line(int number, bool covered)
        => new() { Number = number, Status = covered ? LineStatus.Covered : LineStatus.NotCovered, Hits = covered ? 1 : 0 };

    /// <summary>A GitHub-linked user, as a GitHub sign-in stores one, and a personal access token of theirs.</summary>
    private static async Task<string> StoreUserAsync(
        IAsyncDocumentSession session, string id, string login, long gitHubId, StubGitHubApi.Script gitHub, (long Id, string Login)[] organisations)
    {
        var stamp = "stamp-" + login;
        await session.StoreAsync(new SparkUser
        {
            UserName = login,
            NormalizedUserName = login.ToUpperInvariant(),
            SecurityStamp = stamp,
            Logins = [new SparkUserLogin { LoginProvider = "GitHub", ProviderKey = gitHubId.ToString(), ProviderDisplayName = "GitHub" }],
        }, id);

        gitHub.Users[StubGitHubApi.TokenFor(id)] = (gitHubId, login, organisations);
        return await StoreTokenAsync(session, id, stamp, types: null);
    }

    /// <summary>A personal access token and its grant: everything exposed (<paramref name="types"/> null) or the chosen types, read only.</summary>
    private static async Task<string> StoreTokenAsync(IAsyncDocumentSession session, string userId, string stamp, List<AgentGrantType>? types)
    {
        var token = TestPersonalAccessTokens.Create(userId, out var tokenId);
        await session.StoreAsync(new AgentGrant
        {
            UserId = userId,
            Kind = EAgentKind.PersonalAccessToken,
            ClientId = tokenId,
            Name = "tests",
            Permissions = EAgentPermission.Read,
            Types = types,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            TokenHash = TestPersonalAccessTokens.Hash(token),
            SecurityStamp = stamp,
        }, AgentGrantIds.PersonalAccessToken(userId, tokenId));
        return token;
    }
}
