using CodeCoverage.Commands;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Logging;
using Raven.Client.Documents;

namespace CodeCoverage.Tests.Commands;

/// <summary>A forge's comment reactions in memory: every add gets a new id, and <see cref="On"/> is what a comment shows now.</summary>
internal sealed class FakeCommentReactions : IForgeCommentReactions
{
    private long next = 1;

    public EForgeProvider Provider => EForgeProvider.GitHub;

    public List<(long CommentId, EForgeReaction Reaction, long Id)> Added { get; } = [];

    public List<(long CommentId, long ReactionId)> Removed { get; } = [];

    /// <summary>Throws on every add when set, as a forge without the permission would.</summary>
    public Exception? Fault { get; set; }

    /// <summary>The App's reactions on <paramref name="commentId"/> now, oldest first.</summary>
    public IReadOnlyList<EForgeReaction> On(long commentId)
        => [.. Added.Where(a => a.CommentId == commentId && !Removed.Contains((commentId, a.Id))).Select(a => a.Reaction)];

    public Task<long> AddAsync(Repository repository, long commentId, EForgeReaction reaction, CancellationToken cancellationToken = default)
    {
        if (Fault is { } fault) throw fault;
        var id = next++;
        Added.Add((commentId, reaction, id));
        return Task.FromResult(id);
    }

    public Task RemoveAsync(Repository repository, long commentId, long reactionId, CancellationToken cancellationToken = default)
    {
        Removed.Add((commentId, reactionId));
        return Task.CompletedTask;
    }
}

/// <summary>Log scopes that keep nothing but what was asked of them: the keys opened and the one-line entries.</summary>
internal sealed class RecordingLoggerScopes<T> : ILoggerScopeProvider<T>
{
    public List<(string Key, string? Related, DateTime? ExpiresAtUtc)> Opened { get; } = [];

    public List<(string Key, string Message)> Lines { get; } = [];

    public ILoggerScope CreateScope() => new Scope(LogIds.New());

    public ILoggerScope CreateScope(string key, string? relatedPersistentObjectId = null, DateTime? expiresAtUtc = null)
    {
        Opened.Add((key, relatedPersistentObjectId, expiresAtUtc));
        return new Scope(LogIds.ForKey(key));
    }

    public Task LogAsync(string key, LogLevel level, string message, Exception? exception = null, string? relatedPersistentObjectId = null, DateTime? expiresAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        Opened.Add((key, relatedPersistentObjectId, expiresAtUtc));
        Lines.Add((key, message));
        return Task.CompletedTask;
    }

    private sealed class Scope(string id) : ILoggerScope
    {
        public string Id { get; } = id;
        public ILogger Logger => NullLogger.Instance;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }
}

internal static class CommandTestKit
{
    public static CommandLifecycle Lifecycle(IDocumentStore store, IForgeCommentReactions reactions, TimeProvider? time = null,
        RecordingLoggerScopes<CommandLifecycle>? logs = null, int? retentionDays = null)
        => new(store, [reactions], logs ?? new RecordingLoggerScopes<CommandLifecycle>(),
            new ConfigurationBuilder().AddInMemoryCollection(retentionDays is { } days
                ? new Dictionary<string, string?> { ["Coverage:Retention:CommandInvocationDays"] = days.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                : []).Build(),
            time ?? TimeProvider.System, NullLogger<CommandLifecycle>.Instance);

    /// <summary>The command record of a comment in a repository.</summary>
    public static async Task<CommandInvocation?> InvocationAsync(IDocumentStore store, Repository repository, long commentId)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<CommandInvocation>(CommandInvocation.DocumentId(repository.Provider, repository.GitHubId, commentId));
    }
}
