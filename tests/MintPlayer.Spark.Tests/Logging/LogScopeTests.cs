using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Logging;
using MintPlayer.Spark.Logging.Models;
using MintPlayer.Spark.Storage.InMemory;
using MintPlayer.Spark.Storage.Raven;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Logging;

/// <summary>
/// MintPlayer.Spark.Logging's scopes (#660 G7–G10, <c>docs/code-coverage/command-reactions-PRD.md</c>): what an
/// <see cref="ILogger"/> logs inside a scope lands in one <see cref="Log"/> document, on every store.
/// </summary>
public abstract class LogScopeTests : IAsyncLifetime
{
    private ServiceProvider services = null!;

    protected ISparkStore Store { get; private set; } = null!;

    protected abstract Task<ISparkStore> CreateStoreAsync();

    public virtual async Task InitializeAsync()
    {
        Store = await CreateStoreAsync();
        services = Build(Store, new Dictionary<string, string?> { ["Logging:Document:LogLevel:Default"] = "Information" });
    }

    public virtual async Task DisposeAsync() => await services.DisposeAsync();

    private static ServiceProvider Build(ISparkStore store, Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var collection = new ServiceCollection();
        collection.AddSingleton(store);
        collection.AddLogging(logging => logging.AddConfiguration(configuration.GetSection("Logging")));
        SparkLoggingExtensions.AddLogServices(collection);
        return collection.BuildServiceProvider();
    }

    private ILoggerScopeProvider Scopes => services.GetRequiredService<ILoggerScopeProvider>();

    /// <summary>Elsewhere in the code: a logger resolved on its own, knowing nothing of the scope.</summary>
    private ILogger Elsewhere => services.GetRequiredService<ILogger<LogScopeTests>>();

    private async Task<Log?> LoadAsync(string id)
    {
        using var session = Store.OpenSession();
        return await session.LoadAsync<Log>(id);
    }

    [Fact]
    public async Task What_any_logger_in_the_flow_logs_inside_a_scope_is_saved_in_one_log()
    {
        string id;
        await using (var scope = Scopes.CreateScope())
        {
            id = scope.Id;
            scope.Logger.LogInformation("Started {Number}", 42);
            await Task.Yield();
            Elsewhere.LogWarning(new InvalidOperationException("boom"), "Deep down");
        }

        id.Should().StartWith(LogIds.Prefix);
        var log = await LoadAsync(id);
        log.Should().NotBeNull();
        log!.Entries.Select(e => e.Text).Should().Equal("Started 42", "Deep down");
        log.Entries[0].Level.Should().Be("Information");
        log.Entries[0].Exception.Should().BeNull();
        log.Entries[1].Level.Should().Be("Warning");
        log.Entries[1].Text.Should().NotContain("boom", "the exception is its own field (G12)");
        log.Entries[1].Exception.Should().Contain("InvalidOperationException").And.Contain("boom");
    }

    [Fact]
    public async Task Nothing_is_saved_outside_a_scope_or_for_an_empty_scope()
    {
        Elsewhere.LogInformation("No scope");
        string id;
        await using (var scope = Scopes.CreateScope())
            id = scope.Id;

        (await LoadAsync(id)).Should().BeNull("an empty scope writes nothing");
    }

    [Fact]
    public async Task An_entry_goes_to_the_innermost_scope_only()
    {
        string outer, inner;
        await using (var o = Scopes.CreateScope())
        {
            outer = o.Id;
            Elsewhere.LogInformation("Outer before");
            await using (var i = Scopes.CreateScope())
            {
                inner = i.Id;
                Elsewhere.LogInformation("Inner");
            }
            Elsewhere.LogInformation("Outer after");
        }

        (await LoadAsync(outer))!.Entries.Select(e => e.Text).Should().Equal("Outer before", "Outer after");
        (await LoadAsync(inner))!.Entries.Select(e => e.Text).Should().Equal("Inner");
    }

    [Fact]
    public async Task A_key_reopens_its_log_and_appends_keeping_the_related_record_and_expiry()
    {
        var expires = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await using (var first = Scopes.CreateScope("commands/github/1/2", relatedPersistentObjectId: "CommandInvocations/github/1/2"))
            first.Logger.LogInformation("Received");
        await Scopes.LogAsync("commands/github/1/2", LogLevel.Information, "Done", expiresAtUtc: expires);

        var log = await LoadAsync(LogIds.ForKey("commands/github/1/2"));
        log!.Entries.Select(e => e.Text).Should().Equal("Received", "Done");
        log.RelatedPersistentObjectId.Should().Be("CommandInvocations/github/1/2");
        log.ExpiresAtUtc.Should().Be(expires);
    }

    [Fact]
    public async Task Scopes_with_one_key_closing_together_all_land()
    {
        await Task.WhenAll(Enumerable.Range(0, 5).Select(n => Task.Run(async () =>
        {
            await using var scope = Scopes.CreateScope("tests/concurrent");
            scope.Logger.LogInformation("Writer {N}", n);
        })));

        (await LoadAsync(LogIds.ForKey("tests/concurrent")))!.Entries.Select(e => e.Text)
            .Should().BeEquivalentTo(Enumerable.Range(0, 5).Select(n => $"Writer {n}"));
    }

    [Fact]
    public async Task A_task_still_logging_after_its_scope_closed_is_dropped_not_filed_under_the_outer_log()
    {
        var release = new TaskCompletionSource();
        Task late;
        string outer, inner;
        await using (var o = Scopes.CreateScope())
        {
            outer = o.Id;
            Elsewhere.LogInformation("Outer");
            await using (var i = Scopes.CreateScope())
            {
                inner = i.Id;
                Elsewhere.LogInformation("Inner");
                late = Task.Run(async () =>
                {
                    await release.Task;
                    Elsewhere.LogInformation("Late");
                });
            }
            release.SetResult();
            await late;
        }

        (await LoadAsync(inner))!.Entries.Select(e => e.Text).Should().Equal("Inner");
        (await LoadAsync(outer))!.Entries.Select(e => e.Text).Should().Equal("Outer");
    }

    [Fact]
    public async Task The_Document_alias_filters_like_any_provider()
    {
        await using var filtered = Build(Store, new Dictionary<string, string?> { ["Logging:Document:LogLevel:Default"] = "Warning" });
        string id;
        await using (var scope = filtered.GetRequiredService<ILoggerScopeProvider>().CreateScope())
        {
            id = scope.Id;
            scope.Logger.LogInformation("Chatty");
            scope.Logger.LogWarning("Kept");
        }

        (await LoadAsync(id))!.Entries.Select(e => e.Text).Should().Equal("Kept");
    }

    [Fact]
    public async Task A_typed_provider_logs_under_its_category()
    {
        var typed = services.GetRequiredService<ILoggerScopeProvider<LogScopeTests>>();
        await using var scope = typed.CreateScope();
        scope.Logger.IsEnabled(LogLevel.Information).Should().BeTrue();
        scope.Id.Should().StartWith(LogIds.Prefix);
    }
}

public sealed class RavenLogScopeTests : LogScopeTests
{
    private readonly Driver driver = new();

    protected override async Task<ISparkStore> CreateStoreAsync()
    {
        await driver.InitializeAsync();
        return new RavenSparkStore(driver.DocumentStore);
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await driver.DisposeAsync();
    }

    private sealed class Driver : SparkTestDriver
    {
        public Raven.Client.Documents.IDocumentStore DocumentStore => Store;
    }
}

public sealed class InMemoryLogScopeTests : LogScopeTests
{
    protected override Task<ISparkStore> CreateStoreAsync()
        => Task.FromResult<ISparkStore>(new InMemorySparkStore(new InMemoryDatabase()));
}

public class LogIdsTests
{
    [Theory]
    [InlineData("commands/github/123/456")]
    [InlineData("merge-queue/MergeQueueEntries/1-A")]
    [InlineData("a/b")]
    [InlineData("runs/v1.2:3")]
    public void A_feature_prefix_and_an_id_is_a_key(string key)
        => LogIds.ForKey(key).Should().Be("Logs/" + key);

    [Theory]
    [InlineData("")]
    [InlineData("no-prefix")]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    [InlineData("double//slash")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("a/b c")]
    [InlineData("a/b'c")]
    [InlineData("a/b\nc")]
    public void Anything_else_is_refused(string key)
        => ((Action)(() => LogIds.ForKey(key))).Should().Throw<ArgumentException>();

    [Fact]
    public void A_key_is_at_most_200_characters()
    {
        LogIds.ForKey("a/" + new string('b', 198)).Should().NotBeNull();
        ((Action)(() => LogIds.ForKey("a/" + new string('b', 199)))).Should().Throw<ArgumentException>();
    }
}
