using MintPlayer.Spark;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Commands.Batches;
using Raven.Client.Documents.Operations;

namespace CodeCoverage.Migrations;

/// <summary>
/// Points every <see cref="ApiToken"/> at its <see cref="Account"/> document and drops the four
/// fields that used to describe the owner instead: <c>AccountLogin</c>, <c>AccountOwnerKey</c>,
/// <c>Provider</c> and <c>AccountId</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every shape an older token can have resolves here</b>, in order of how much the token proves:
/// an <c>Account</c> already set; the <c>Provider</c> + <c>AccountId</c> pair (the document id
/// spelled out); the <c>provider:login</c> owner key; a bare <c>AccountLogin</c> from before keys
/// existed, read as a GitHub login (the only forge then, as <c>M_202609210900</c> also assumed). Each
/// candidate must name an Account document that exists. That makes <c>M_202609221000</c>'s AccountId
/// backfill redundant, which is why it was deleted with this change.
/// </para>
/// <para>
/// ⚠️ <b>The old fields are deleted explicitly.</b> RavenDB keeps fields a class no longer declares,
/// so loading the new <see cref="ApiToken"/> and saving it would leave all four on the document. A
/// patch with <c>delete</c> is the only thing that removes them.
/// </para>
/// <para>
/// A token whose account cannot be resolved keeps <c>Account</c> null and is named in a warning. It
/// fails closed: the row filter matches nobody, and the upload path authorizes nothing for a token
/// without an account. Such a token is for an account Coverage no longer knows, and is fixed by
/// hand, or revoked.
/// </para>
/// </remarks>
public partial class M_202610021200_ApiTokenReferencesItsAccount : ISparkMigration
{
    public static long Version => 202610021200;
    public static string? Description => "ApiToken references its Account; the owner fields move to ApiTokens_Overview";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ILogger<M_202610021200_ApiTokenReferencesItsAccount> logger;

    private const string Script = """
        this.Account = args.account;
        delete this.AccountLogin;
        delete this.AccountOwnerKey;
        delete this.Provider;
        delete this.AccountId;
        """;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var (accountIds, byOwnerKey) = await LoadAccountsAsync(cancellationToken);

        using var session = store.OpenAsyncSession();
        // Deferred patches cost no request each, but SaveChanges per batch does; the scope keeps a
        // large instance from hitting the session's 30-request limit (see M_202609221000's note on
        // why that limit turns into a restart loop).
        using var requestScope = session.IgnoreMaxRequests(expectedMaximumRequests: 500, logger: logger);

        var migrated = 0;
        var pending = 0;
        var unresolvable = new List<string>();

        await using var stream = await session.Advanced.StreamAsync<LegacyApiToken>(
            startsWith: "ApiTokens/", token: cancellationToken);

        while (await stream.MoveNextAsync())
        {
            var id = stream.Current.Id;
            var token = stream.Current.Document;
            var account = Resolve(token, accountIds, byOwnerKey);
            if (account is null)
                unresolvable.Add($"{id} ({token.Description ?? "no description"}, owner {token.AccountOwnerKey ?? token.AccountLogin ?? "unknown"})");

            session.Advanced.Defer(new PatchCommandData(id, null, new PatchRequest
            {
                Script = Script,
                Values = { ["account"] = account },
            }));
            migrated++;

            if (++pending == 256)
            {
                await session.SaveChangesAsync(cancellationToken);
                pending = 0;
            }
        }

        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Pointed {Count} API tokens at their Account document.", migrated);

        if (unresolvable.Count > 0)
        {
            // Warning, and named: these tokens now authorize nothing and appear in no list until they
            // are given an account or revoked.
            logger.LogWarning(
                "{Count} API tokens could not be resolved to an existing Account and were left without one: {Tokens}",
                unresolvable.Count, string.Join("; ", unresolvable.Take(50)));
        }
    }

    /// <summary>The account document id this token belongs to, or null when nothing it carries names one that exists.</summary>
    internal static string? Resolve(LegacyApiToken token, IReadOnlySet<string> accountIds, IReadOnlyDictionary<string, string> byOwnerKey)
    {
        if (token.Account is { Length: > 0 } account && accountIds.Contains(account))
            return account;

        if (token.AccountId is { } numericId
            && Enum.TryParse<EForgeProvider>(token.Provider, ignoreCase: true, out var provider)
            && accountIds.Contains(Account.DocumentId(provider, numericId)))
            return Account.DocumentId(provider, numericId);

        if (token.AccountOwnerKey is { Length: > 0 } key && byOwnerKey.TryGetValue(key, out var byKey))
            return byKey;

        if (token.AccountLogin is { Length: > 0 } login
            && byOwnerKey.TryGetValue(new ForgeOwner(EForgeProvider.GitHub, login).ToString(), out var byLogin))
            return byLogin;

        return null;
    }

    private async Task<(HashSet<string> Ids, Dictionary<string, string> ByOwnerKey)> LoadAccountsAsync(CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byOwnerKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var session = store.OpenAsyncSession();
        await using var stream = await session.Advanced.StreamAsync<Account>(
            startsWith: "Accounts/", token: cancellationToken);

        while (await stream.MoveNextAsync())
        {
            var id = stream.Current.Id;
            var account = stream.Current.Document;
            ids.Add(id);
            if (account.Login is { Length: > 0 })
                byOwnerKey[account.OwnerKey] = id;
        }

        return (ids, byOwnerKey);
    }

    /// <summary>
    /// An ApiToken document as any earlier version may have written it — the fields this migration
    /// reads, including the four it removes. Read-only; nothing is ever saved through it.
    /// </summary>
    internal sealed class LegacyApiToken
    {
        public string? Account { get; set; }
        public string? AccountOwnerKey { get; set; }
        public string? AccountLogin { get; set; }
        public long? AccountId { get; set; }
        public string? Provider { get; set; }
        public string? Description { get; set; }
    }
}
