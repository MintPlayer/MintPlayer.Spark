using System.Reflection;
using CodeCoverage.Forge;
using MintPlayer.Spark.Messaging.Abstractions;
using Xunit;

namespace CodeCoverage.Tests.Infrastructure;

/// <summary>
/// Every neutral forge event has a producer and a consumer.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the contract a second forge is bought with.</b> The promise is that adding GitLab or
/// Bitbucket means writing a normaliser that publishes these events — and that the app, as a
/// consumer, only ever writes a class implementing
/// <c>IRecipient&lt;ForgeWebhookMessage&lt;T&gt;&gt;</c>. That promise only holds if the vocabulary
/// is actually complete in both directions.
/// </para>
/// <para>
/// ⚠️ <b>It was not, and nothing said so.</b> <c>RepositoryRenamed</c> and
/// <c>RepositoryConnectionChanged</c> were declared, documented, and had zero producers and zero
/// consumers from the day they were written. The corresponding facts were handled inline in the
/// GitHub library instead — so the contract <em>looked</em> complete, a second forge would have
/// found two of its six events inert, and no test, analyzer or build step would have mentioned it.
/// </para>
/// <para>
/// A declared event with no implementation is worse than a missing one: a missing event is an
/// obvious gap, while a declared one is a promise the next person reasonably believes.
/// </para>
/// </remarks>
public class ForgeEventContractTests
{
    /// <summary>Every record declared alongside <see cref="ForgeEvents"/>.</summary>
    private static IEnumerable<Type> NeutralEvents()
        => typeof(ForgeEvents).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IForgeEvent).IsAssignableFrom(t));

    /// <summary>The app types that consume a neutral event.</summary>
    private static IEnumerable<Type> ConsumedEvents()
        => typeof(CodeCoverage.Recipients.ForgeEventsRecipient).Assembly
            .GetTypes()
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRecipient<>))
            .Select(i => i.GetGenericArguments()[0])
            .Where(m => m.IsGenericType && m.GetGenericTypeDefinition() == typeof(ForgeWebhookMessage<>))
            .Select(m => m.GetGenericArguments()[0])
            .Distinct();

    /// <summary>
    /// The events the GitHub normaliser publishes, read from its source.
    /// </summary>
    /// <remarks>
    /// Source text rather than reflection: a producer is a <c>new EventName(...)</c> inside a
    /// broadcast, which leaves no signature to reflect over. Crude, and it errs toward reporting a
    /// producer that does not exist rather than missing one that does — the safe direction, since
    /// the assertion below is "nothing is unproduced".
    /// </remarks>
    private static HashSet<string> ProducedEventNames()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "apps")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var integration = Path.Combine(dir!.FullName, "apps", "CodeCoverage", "CodeCoverage.GithubIntegration");
        Assert.True(Directory.Exists(integration), $"Expected the GitHub library at {integration}");

        var source = string.Join('\n', Directory
            .EnumerateFiles(integration, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText));

        return [.. NeutralEvents()
            .Select(t => t.Name)
            .Where(name => source.Contains($"new {name}(", StringComparison.Ordinal))];
    }

    [Fact]
    public void Every_neutral_event_has_a_consumer()
    {
        var consumed = ConsumedEvents().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var orphans = NeutralEvents().Select(t => t.Name).Where(n => !consumed.Contains(n)).OrderBy(n => n).ToArray();

        Assert.True(orphans.Length == 0,
            "These neutral events are declared but nothing consumes them. A second forge raising one "
            + "would find it silently dropped:\n  " + string.Join("\n  ", orphans));
    }

    [Fact]
    public void Every_neutral_event_has_a_producer()
    {
        var produced = ProducedEventNames();
        var orphans = NeutralEvents().Select(t => t.Name).Where(n => !produced.Contains(n)).OrderBy(n => n).ToArray();

        Assert.True(orphans.Length == 0,
            "These neutral events are declared but GitHub never raises one. Either the fact is being "
            + "handled inline — which is what a second forge would have to reimplement — or the event "
            + "is dead weight:\n  " + string.Join("\n  ", orphans));
    }

    /// <summary>
    /// Proves the finders see a real corpus, so a green run cannot mean "found nothing to check".
    /// </summary>
    [Fact]
    public void The_finders_see_the_events_that_actually_exist()
    {
        var events = NeutralEvents().Select(t => t.Name).ToArray();

        // The six the vocabulary is documented as having.
        Assert.Contains(nameof(BranchCommitPushed), events);
        Assert.Contains(nameof(PullRequestUpdated), events);
        Assert.Contains(nameof(PullRequestMerged), events);
        Assert.Contains(nameof(OwnerRenamed), events);
        Assert.Contains(nameof(RepositoryRenamed), events);
        Assert.Contains(nameof(RepositoryConnectionChanged), events);

        Assert.NotEmpty(ConsumedEvents());
        Assert.NotEmpty(ProducedEventNames());
    }

    /// <summary>
    /// ⚠️ The app must not name a forge to consume an event. If this fails, the neutral envelope has
    /// stopped being neutral and a second forge would need the app edited rather than added to.
    /// </summary>
    [Fact]
    public void No_consumer_names_a_forge()
    {
        var consumers = typeof(CodeCoverage.Recipients.ForgeEventsRecipient).Assembly
            .GetTypes()
            .Where(t => t.GetInterfaces().Any(i =>
                i.IsGenericType
                && i.GetGenericTypeDefinition() == typeof(IRecipient<>)
                && i.GetGenericArguments()[0] is { IsGenericType: true } m
                && m.GetGenericTypeDefinition() == typeof(ForgeWebhookMessage<>)))
            .Select(t => t.Name)
            .ToArray();

        Assert.NotEmpty(consumers);
        Assert.All(consumers, name =>
        {
            Assert.DoesNotContain("GitHub", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("GitLab", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Bitbucket", name, StringComparison.OrdinalIgnoreCase);
        });
    }
}
