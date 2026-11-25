using CodeCoverage.Webhooks;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Apps;
using MintPlayer.Spark.Abstractions.Storage;

namespace CodeCoverage.Tests;

/// <summary>
/// An <see cref="IWebhookPublisher"/> that records each published event (#691 D41), and the
/// <see cref="CoverageWebhookPublisher"/> wrapping it that a feature's handler takes.
/// </summary>
public sealed class RecordingWebhooks : IWebhookPublisher
{
    public sealed record Published(object Data, WebhookOwner Owner, string? Key);

    private readonly List<Published> published = [];

    public RecordingWebhooks()
        => Publisher = new CoverageWebhookPublisher(new ServiceCollection().AddSingleton<IWebhookPublisher>(this).BuildServiceProvider());

    /// <summary>The wrapper to pass to the handler under test.</summary>
    public CoverageWebhookPublisher Publisher { get; }

    public IReadOnlyList<Published> All { get { lock (published) return [.. published]; } }

    public IReadOnlyList<TEvent> Of<TEvent>() => [.. All.Select(p => p.Data).OfType<TEvent>()];

    public Task<string?> PublishAsync<TEvent>(TEvent data, WebhookOwner owner, ISparkSession session, CancellationToken cancellationToken = default)
        where TEvent : notnull
        => Record(data, owner, null);

    public Task<string?> PublishAsync<TEvent>(TEvent data, WebhookOwner owner, string idempotencyKey, ISparkSession session, CancellationToken cancellationToken = default)
        where TEvent : notnull
        => Record(data, owner, idempotencyKey);

    private Task<string?> Record(object data, WebhookOwner owner, string? key)
    {
        lock (published) published.Add(new Published(data, owner, key));
        return Task.FromResult<string?>(key ?? Guid.NewGuid().ToString("N"));
    }
}
