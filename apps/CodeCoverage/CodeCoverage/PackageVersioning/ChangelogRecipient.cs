using MintPlayer.Spark.Abstractions;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Versioning.Versions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// #693 M11 (D3, D16): copies a package Release's notes into a <see cref="ChangelogRelease"/> for the repository's changelog
/// page, and follows the Release's edits. Only a version tag (<c>&lt;PackageId&gt;/v&lt;version&gt;</c>) of a repository whose
/// <see cref="Repository.ResolveChangelog"/> is on is copied. Storage goes through <see cref="ISparkSession"/> only: loads by
/// id, no query, no index.
/// </summary>
public partial class ChangelogRecipient :
    IRecipient<ForgeWebhookMessage<ReleasePublished>>,
    IRecipient<ForgeWebhookMessage<ReleaseEdited>>
{
    [Inject] private readonly ISparkSession session;

    public async Task HandleAsync(ForgeWebhookMessage<ReleasePublished> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        if (evt.Notes is not { } notes) return;
        await RecordAsync(evt.RepositoryId, evt.Tag, notes, evt.PublishedAt.UtcDateTime, cancellationToken);
    }

    public Task HandleAsync(ForgeWebhookMessage<ReleaseEdited> message, CancellationToken cancellationToken = default)
        => RecordAsync(message.Event.RepositoryId, message.Event.Tag, message.Event.Notes, null, cancellationToken);

    /// <param name="publishedAt">Null for an edit: an edit keeps the published time of the copy, or uses now for a first copy.</param>
    private async Task RecordAsync(string repositoryId, string tag, ForgeReleaseNotes notes, DateTime? publishedAt, CancellationToken cancellationToken)
    {
        if (!VersionTag.TryParse(tag, out var version)) return;
        if (await session.LoadAsync<Repository>(repositoryId, cancellationToken) is not { Id: { } id } repository) return;
        var account = repository.Account is { Length: > 0 } accountId ? await session.LoadAsync<Account>(accountId, cancellationToken) : null;
        if (!Repository.ResolveChangelog(repository, account).Enabled) return;

        var documentId = ChangelogRelease.DocumentId(repository.Provider, repository.GitHubId, tag);
        var copy = await session.LoadAsync<ChangelogRelease>(documentId, cancellationToken);
        if (copy is null)
        {
            copy = new ChangelogRelease { Id = Id<ChangelogRelease>.FromTrusted(documentId), ReleasedAtUtc = publishedAt ?? DateTime.UtcNow };
            await session.StoreAsync(copy, documentId, cancellationToken);
        }
        else if (publishedAt is { } at)
            copy.ReleasedAtUtc = at;
        copy.Repository = id.Value;
        copy.Package = version.PackageId;
        copy.Version = version.Version.ToString();
        copy.Tag = tag;
        copy.Target = notes.Target;
        copy.Prerelease = notes.Prerelease;
        copy.Notes = notes.Body.Length > ChangelogRelease.MaxNotes ? notes.Body[..ChangelogRelease.MaxNotes] : notes.Body;
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A repository's copies, newest first, for the changelog page: the id prefix, paged, no query.</summary>
    public static async Task<List<ChangelogRelease>> ForRepositoryAsync(ISparkSession session, Repository repository, CancellationToken cancellationToken = default)
    {
        const int page = 1024;
        var prefix = ChangelogRelease.ForRepository(repository.Provider, repository.GitHubId);
        var releases = new List<ChangelogRelease>();
        for (var start = 0; ; start += page)
        {
            var batch = await session.LoadStartingWithAsync<ChangelogRelease>(prefix, start, page, cancellationToken: cancellationToken);
            releases.AddRange(batch.Where(r => string.Equals(r.Repository, repository.Id.Value, StringComparison.OrdinalIgnoreCase)));
            if (batch.Count < page) break;
        }
        return [.. releases.OrderByDescending(r => r.ReleasedAtUtc).ThenBy(r => r.Package, StringComparer.Ordinal)];
    }
}
