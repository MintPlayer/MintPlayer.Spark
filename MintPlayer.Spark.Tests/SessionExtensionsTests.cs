using Microsoft.Extensions.Logging;
using NSubstitute;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests;

public class SessionExtensionsTests
{
    [Fact]
    public void IgnoreMaxRequests_LiftsBudgetWithinScope()
    {
        var (session, advanced) = CreateAsyncSession(originalMax: 30, currentRequests: 5);

        using (session.IgnoreMaxRequests())
        {
            advanced.MaxNumberOfRequestsPerSession.Should().Be(int.MaxValue);
        }
    }

    [Fact]
    public void IgnoreMaxRequests_RestoresOriginalMaxOnDispose()
    {
        var (session, advanced) = CreateAsyncSession(originalMax: 30, currentRequests: 0);

        var scope = session.IgnoreMaxRequests();
        scope.Dispose();

        advanced.MaxNumberOfRequestsPerSession.Should().Be(30);
    }

    [Fact]
    public void IgnoreMaxRequests_RestoresCustomOriginalMax()
    {
        var (session, advanced) = CreateAsyncSession(originalMax: 75, currentRequests: 0);

        using (session.IgnoreMaxRequests())
        {
            advanced.MaxNumberOfRequestsPerSession.Should().Be(int.MaxValue);
        }

        advanced.MaxNumberOfRequestsPerSession.Should().Be(75);
    }

    [Fact]
    public void IgnoreMaxRequests_LogsWarning_WhenScopeExceedsExpectedMax()
    {
        var (session, advanced) = CreateAsyncSession(originalMax: 30, currentRequests: 0);
        var logger = Substitute.For<ILogger>();

        var scope = session.IgnoreMaxRequests(expectedMaximumRequests: 10, logger: logger);
        advanced.NumberOfRequests.Returns(25); // simulate 25 requests performed
        scope.Dispose();

        logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "Log")
            .Should().HaveCount(1);
    }

    [Fact]
    public void IgnoreMaxRequests_DoesNotLog_WhenScopeStaysWithinExpectedMax()
    {
        var (session, advanced) = CreateAsyncSession(originalMax: 30, currentRequests: 0);
        var logger = Substitute.For<ILogger>();

        var scope = session.IgnoreMaxRequests(expectedMaximumRequests: 50, logger: logger);
        advanced.NumberOfRequests.Returns(20);
        scope.Dispose();

        logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "Log")
            .Should().BeEmpty();
    }

    [Fact]
    public void IgnoreMaxRequests_DoesNotLog_WhenLoggerNotProvided()
    {
        var (session, advanced) = CreateAsyncSession(originalMax: 30, currentRequests: 0);

        var scope = session.IgnoreMaxRequests(expectedMaximumRequests: 5);
        advanced.NumberOfRequests.Returns(100); // would otherwise trigger a warning
        scope.Dispose();

        // No throw, no logger to verify against — successful completion is the assertion.
    }

    [Fact]
    public void IgnoreMaxRequests_FallsBackTo30_WhenOriginalMaxAlreadyMaxValue()
    {
        var (session, advanced) = CreateAsyncSession(originalMax: int.MaxValue, currentRequests: 0);
        var logger = Substitute.For<ILogger>();

        var scope = session.IgnoreMaxRequests(logger: logger);
        advanced.NumberOfRequests.Returns(31); // just over the implicit 30 floor
        scope.Dispose();

        logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "Log")
            .Should().HaveCount(1);
    }

    [Fact]
    public void IgnoreMaxRequests_SyncSession_LiftsAndRestores()
    {
        var (session, advanced) = CreateSyncSession(originalMax: 30, currentRequests: 0);

        using (session.IgnoreMaxRequests())
        {
            advanced.MaxNumberOfRequestsPerSession.Should().Be(int.MaxValue);
        }

        advanced.MaxNumberOfRequestsPerSession.Should().Be(30);
    }

    [Fact]
    public void IgnoreMaxRequests_DisposingTwice_OnlyRestoresOnce()
    {
        var (session, advanced) = CreateAsyncSession(originalMax: 30, currentRequests: 0);

        var scope = session.IgnoreMaxRequests();
        scope.Dispose();
        advanced.MaxNumberOfRequestsPerSession = 99; // simulate later mutation
        scope.Dispose();

        advanced.MaxNumberOfRequestsPerSession.Should().Be(99);
    }

    private static (IAsyncDocumentSession Session, IAsyncAdvancedSessionOperations Advanced) CreateAsyncSession(
        int originalMax, int currentRequests)
    {
        var advanced = Substitute.For<IAsyncAdvancedSessionOperations>();
        var session = Substitute.For<IAsyncDocumentSession>();

        // Back the substituted property with a real backing field via Returns + Do so reads
        // and writes both work like a regular property.
        var backingMax = originalMax;
        advanced.MaxNumberOfRequestsPerSession.Returns(_ => backingMax);
        advanced
            .When(a => a.MaxNumberOfRequestsPerSession = Arg.Any<int>())
            .Do(ci => backingMax = ci.Arg<int>());

        advanced.NumberOfRequests.Returns(currentRequests);
        session.Advanced.Returns(advanced);

        return (session, advanced);
    }

    private static (IDocumentSession Session, IAdvancedSessionOperations Advanced) CreateSyncSession(
        int originalMax, int currentRequests)
    {
        var advanced = Substitute.For<IAdvancedSessionOperations>();
        var session = Substitute.For<IDocumentSession>();

        var backingMax = originalMax;
        advanced.MaxNumberOfRequestsPerSession.Returns(_ => backingMax);
        advanced
            .When(a => a.MaxNumberOfRequestsPerSession = Arg.Any<int>())
            .Do(ci => backingMax = ci.Arg<int>());

        advanced.NumberOfRequests.Returns(currentRequests);
        session.Advanced.Returns(advanced);

        return (session, advanced);
    }
}
