using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeCoverage.CommitGraph;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Services;

/// <summary>
/// GitHub's half of the avatar resolution (D28 step 4): one GraphQL request asks, per commit,
/// which account authored it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One aliased <c>object(oid:)</c> per author</b>, so a page of 200 rows with 60 distinct
/// authors costs one request of about one point (spike S12). The SHAs, the owner and the name are
/// GraphQL <em>variables</em>; the query text is built from loop indices alone, so nothing a
/// repository or a commit contains can change the query.
/// </para>
/// <para>
/// ⚠️ <b>Installation only.</b> Anonymous GraphQL answers 403 (S12), and spending the server's
/// shared anonymous REST budget on pictures is not worth it (D23), so a repository without an
/// installation resolves nobody here and its authors get initials.
/// </para>
/// </remarks>
[Register(typeof(IForgeAvatarResolver), ServiceLifetime.Scoped)]
public partial class GitHubAvatarResolver : IForgeAvatarResolver
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IGitHubInstallationService installations;
    [Inject] private readonly ILogger<GitHubAvatarResolver> logger;

    /// <summary>Aliases per request. A page has at most 200 rows, so at most two requests.</summary>
    private const int BatchSize = 100;

    public EForgeProvider Provider => EForgeProvider.GitHub;

    public async Task<IReadOnlyDictionary<string, string?>?> ResolveAsync(
        Repository repository, IReadOnlyList<AvatarLookup> lookups, CancellationToken cancellationToken = default)
    {
        var valid = lookups.Where(l => Sha().IsMatch(l.Sha)).ToList();
        if (valid.Count == 0) return new Dictionary<string, string?>();

        var account = repository.Account is null ? null : await session.LoadAsync<Account>(repository.Account, cancellationToken);
        if (account?.InstallationId is not { } installationId) return null;

        var slash = repository.FullName.IndexOf('/');
        if (slash <= 0) return null;
        var owner = repository.FullName[..slash];
        var name = repository.FullName[(slash + 1)..];

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var connection = await installations.CreateGraphQLConnectionAsync(installationId, EClientType.Installation);
            foreach (var batch in valid.Chunk(BatchSize))
            {
                var body = await connection.Run(BuildRequest(owner, name, batch), cancellationToken);
                if (!Parse(body, batch, result)) return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not resolve commit authors of {FullName}; they get initials this time", repository.FullName);
            return null;
        }

        return result;
    }

    /// <summary>The request body: a fixed query shape over <c>c0..cN</c> with every value a variable.</summary>
    private static string BuildRequest(string owner, string name, AvatarLookup[] batch)
    {
        var query = new StringBuilder("query($owner: String!, $name: String!");
        for (var i = 0; i < batch.Length; i++) query.Append($", $o{i}: GitObjectID!");
        query.Append(") { repository(owner: $owner, name: $name) {");
        for (var i = 0; i < batch.Length; i++)
            query.Append($" c{i}: object(oid: $o{i}) {{ ... on Commit {{ author {{ user {{ databaseId login avatarUrl }} }} }} }}");
        query.Append(" } }");

        var variables = new Dictionary<string, object> { ["owner"] = owner, ["name"] = name };
        for (var i = 0; i < batch.Length; i++) variables[$"o{i}"] = batch[i].Sha;

        return JsonSerializer.Serialize(new { query = query.ToString(), variables });
    }

    /// <summary>Records each alias's answer under its email. False when the repository itself was not readable.</summary>
    private static bool Parse(string body, AvatarLookup[] batch, Dictionary<string, string?> result)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("repository", out var repository)
            || repository.ValueKind != JsonValueKind.Object)
            return false;

        for (var i = 0; i < batch.Length; i++)
        {
            string? url = null;
            if (repository.TryGetProperty($"c{i}", out var commit) && commit.ValueKind == JsonValueKind.Object
                && commit.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object
                && author.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
            {
                // The numeric id, not the login: a bot's login maps to a different account than the
                // one that made the commit (S12). The avatarUrl GitHub returns is already id-based.
                url = user.TryGetProperty("avatarUrl", out var avatarUrl) ? avatarUrl.GetString() : null;
                if (url is null && user.TryGetProperty("databaseId", out var id) && id.TryGetInt64(out var databaseId))
                    url = $"https://avatars.githubusercontent.com/u/{databaseId}?v=4";
            }
            result[batch[i].Email] = url;
        }
        return true;
    }

    [GeneratedRegex("^(?:[0-9a-f]{40}|[0-9a-f]{64})$")]
    private static partial Regex Sha();
}
