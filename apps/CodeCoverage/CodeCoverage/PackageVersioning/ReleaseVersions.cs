using System.Globalization;
using System.Net;
using System.Text.Json;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.Versioning.Releases;
using CodeCoverage.Versioning.Versions;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// Whether a version is on nuget.org or npm already (segment P, P5: a version asked for must not exist on the registry).
/// Both are asked, because the server does not know a package's ecosystem before the writer reads the graph; an id on
/// the wrong registry is at worst a needless refusal. Anonymous reads, as <c>PublicPackageRegistry</c>'s.
/// </summary>
public static class PublishedVersions
{
    public const string NuGetIndex = "https://api.nuget.org/v3-flatcontainer/";
    public const string NpmRegistry = "https://registry.npmjs.org/";

    /// <summary>True when a registry has <paramref name="version"/> of <paramref name="packageId"/>, false when none
    /// has, null when a registry could not be asked (the caller refuses rather than guess).</summary>
    public static async Task<bool?> ExistsAsync(HttpClient http, string packageId, string version, CancellationToken cancellationToken)
    {
        // A NuGet id has no '@' or '/'; an npm name is lower-case. An id that fits neither registry is not asked there.
        var asks = new List<Task<bool?>>();
        if (!packageId.Contains('@') && !packageId.Contains('/'))
            asks.Add(AskAsync(http, $"{NuGetIndex}{packageId.ToLowerInvariant()}/index.json", "versions", version.ToLowerInvariant(), cancellationToken));
        if (packageId == packageId.ToLowerInvariant())
            asks.Add(AskAsync(http, NpmRegistry + packageId.Replace("/", "%2F", StringComparison.Ordinal), "versions", version, cancellationToken));
        var answers = await Task.WhenAll(asks);
        if (answers.Any(a => a == true)) return true;
        return answers.Any(a => a is null) ? null : false;
    }

    private static async Task<bool?> AskAsync(HttpClient http, string url, string property, string version, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // npm's abbreviated document: the versions without every readme.
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.npm.install-v1+json, application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return false;
            if (!response.IsSuccessStatusCode) return null;
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty(property, out var versions)) return false;
            return versions.ValueKind switch
            {
                JsonValueKind.Array => versions.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == version),
                JsonValueKind.Object => versions.TryGetProperty(version, out _),
                _ => false,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}

/// <summary>A person's new tag to check (segment P, P5), queued so the writer never holds the webhook.</summary>
[MessageQueue(CoverageQueues.Versioning)]
public sealed record HandTagMessage
{
    public required string RepositoryId { get; init; }
    public required string Tag { get; init; }
    public required string TagObject { get; init; }
    public required string PusherLogin { get; init; }
}

public partial class TagPushedEventsRecipient : IRecipient<ForgeWebhookMessage<TagPushed>>
{
    [Inject] private readonly IMessageBus messageBus;

    public Task HandleAsync(ForgeWebhookMessage<TagPushed> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        // Only version-tag names reach the queue: any other tag (v1.0, a release name) is none of this.
        if (!VersionTag.TryParse(evt.Tag, out var tag) || tag.Name != evt.Tag) return Task.CompletedTask;
        return messageBus.BroadcastAsync(new HandTagMessage
        {
            RepositoryId = evt.RepositoryId, Tag = evt.Tag, TagObject = evt.TagObject, PusherLogin = evt.PusherLogin,
        }, cancellationToken);
    }
}

/// <summary>
/// A person's version tag in a repository whose current lines are promoted (segment P, P5, Q6b = C): the writer checks
/// it against the current line of its package's ecosystem and deletes it; a valid one is recorded as a
/// <see cref="ReleaseVersion"/> that the line's next <c>/release</c> places, an invalid one gets a commit comment.
/// </summary>
/// <remarks>A tag whose id is no package at its commit (Spark's <c>schemas/v3</c>) is left alone by the writer, and a
/// repository without <c>versioning.lines.current: promote</c> is not even asked about.</remarks>
public partial class HandTagRecipient : IRecipient<HandTagMessage>
{
    [Inject] private readonly Raven.Client.Documents.Session.IAsyncDocumentSession session;
    [Inject] private readonly IForgeIntegrationResolver integrations;
    [Inject] private readonly IEnumerable<IForgeVersioning> forges;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly IGitWriterSidecar writer;
    [Inject] private readonly IHttpClientFactory httpClients;
    [Inject] private readonly TimeProvider clock;
    [Inject] private readonly ILogger<HandTagRecipient> logger;

    public async Task HandleAsync(HandTagMessage message, CancellationToken cancellationToken = default)
    {
        var repository = await session.LoadAsync<Repository>(message.RepositoryId, cancellationToken);
        if (repository is null || repository.Connection == RepositoryConnection.Disconnected || repository.Archived) return;
        if (forges.FirstOrDefault(f => f.Provider == repository.Provider) is not { } forge) return;
        if (queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } git) return;
        if (repository.DefaultBranch is not { } defaultBranch) return;
        if (!VersionTag.TryParse(message.Tag, out var tag)) return;

        try
        {
            var yml = await integrations.For(repository).GetFileContentAsync(repository, defaultBranch, CoverageYml.FileName, cancellationToken);
            if (VersioningYml.Parse(yml).Config is not { Lines.PromotesCurrent: true, EffectiveLines: { } pattern } config) return;
            var current = pattern.CurrentLines(await git.ListBranchesAsync(repository, SupportedLines.BranchPrefix(pattern), cancellationToken));
            if (current.Count == 0) return;

            var push = await git.GetWriterAccessAsync(repository, null, false, cancellationToken);
            if (push.Access is not { } access)
            {
                logger.LogWarning("Hand tag {Tag}: no writer access for {Repository}: {Reason}", message.Tag, repository.FullName, push.RefusalReason);
                return;
            }
            GitWriterHandTagResult result;
            try
            {
                result = await writer.VersioningHandTagAsync(new GitWriterHandTagRequest(
                    repository.Provider.ToCanonicalString(), repository.GitHubId.ToString(CultureInfo.InvariantCulture), access.CloneUrl,
                    new CommitGraph.SidecarCredential(access.Username, access.Password), message.Tag, message.TagObject,
                    [.. current.Select(l => new GitWriterPromoteLine(pattern.Format(l), l.Ecosystem, l.Major, false))], config.ToSettings()), cancellationToken);
            }
            finally
            {
                await git.RevokeWriterAccessAsync(repository, access, CancellationToken.None);
            }
            if (result.Outcome != GitWriterHandTagResult.Planned)
            {
                logger.LogWarning("Hand tag {Tag} in {Repository}: the writer failed ({Reason}): {Detail}", message.Tag, repository.FullName, result.Reason, result.Detail);
                return;
            }

            var refusal = result.Tag == GitWriterHandTagResult.Refused ? result.Detail : null;
            if (result.Tag == GitWriterHandTagResult.Recorded)
            {
                // The writer deleted the tag; the registry is asked only now, so a refusal here is still a deleted tag.
                refusal = await PublishedVersions.ExistsAsync(httpClients.CreateClient(), tag.PackageId, tag.Version.ToString(), cancellationToken) switch
                {
                    true => $"{tag.Name} is published already",
                    null => "the package registries could not be asked whether it is published already; push the tag again to retry",
                    false => null,
                };
                if (refusal is null)
                {
                    await RecordAsync(session, repository.Id!, tag, message.PusherLogin, ReleaseVersion.FromTag, clock.GetUtcNow().UtcDateTime, cancellationToken);
                    await session.SaveChangesAsync(cancellationToken);
                }
            }
            if (result.Tag is GitWriterHandTagResult.Recorded or GitWriterHandTagResult.Refused && result.Commit is { } commit)
                await forge.CommentOnCommitAsync(repository, commit, Comment(message.PusherLogin, tag, result.Branch, refusal), cancellationToken);
        }
        catch (ForgeAccessDeniedException ex)
        {
            logger.LogWarning(ex, "Hand tag: missing permission {Permission} on {Repository}", ex.RequiredPermission, repository.FullName);
        }
        catch (Exception ex) when (ex is ForgeRequestException or GitWriterRefusedException or HttpRequestException && ForgeRateLimitedException.Find(ex) is null)
        {
            logger.LogWarning(ex, "Hand tag {Tag} in {Repository} failed", message.Tag, repository.FullName);
        }
    }

    /// <summary>Records <paramref name="tag"/> as asked for and not placed, unless it is recorded already.</summary>
    internal static async Task RecordAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string repositoryId, VersionTag tag, string actor, string source,
        DateTime nowUtc, CancellationToken cancellationToken)
    {
        var id = ReleaseVersion.DocumentId(repositoryId, tag.Name);
        if (await session.LoadAsync<ReleaseVersion>(id, cancellationToken) is { } existing)
        {
            if (existing.PlacedCommit is null) existing.Actor = actor;
            return;
        }
        await session.StoreAsync(new ReleaseVersion
        {
            Id = id, Repository = repositoryId, PackageId = tag.PackageId, Version = tag.Version.ToString(), Tag = tag.Name, Actor = actor, Source = source,
            RecordedUtc = nowUtc,
        }, id, cancellationToken);
    }

    internal static string Comment(string pusher, VersionTag tag, string? branch, string? refusal) => refusal is null
        ? $"@{pusher}: `{tag.Name}` is recorded and was removed from here; it goes on the next promotion of `{branch}` (`/release`), which releases {tag.PackageId} {tag.Version}."
        : $"@{pusher}: `{tag.Name}` was removed: {refusal}. A version tag in this repository is placed by `/release`; see the release lines' documentation.";
}
