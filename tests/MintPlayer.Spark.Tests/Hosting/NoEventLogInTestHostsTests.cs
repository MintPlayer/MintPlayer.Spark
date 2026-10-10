using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MintPlayer.Spark.Tests.Hosting;

/// <summary>
/// Pins <c>tests/Shared/NoEventLog.cs</c>: a default test host never calls the Windows EventLog provider, so a
/// warning written after the host's providers are disposed cannot throw from it (Q-M11e, docs/seo_analyzer_PRD.md
/// §12). Only Windows registers the provider; elsewhere this holds trivially.
/// </summary>
public class NoEventLogInTestHostsTests
{
    [Fact]
    public async Task A_default_host_logs_a_warning_after_its_EventLog_provider_is_disposed_without_throwing()
    {
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILogger<NoEventLogInTestHostsTests>>();

        foreach (var provider in app.Services.GetServices<ILoggerProvider>().Where(p => p.GetType().Name == "EventLogLoggerProvider"))
            provider.Dispose();

        var act = () => logger.LogWarning("Written after the EventLog provider was disposed");
        act.Should().NotThrow();
        Environment.GetEnvironmentVariable("Logging__EventLog__LogLevel__Default").Should().Be("None");
    }
}
