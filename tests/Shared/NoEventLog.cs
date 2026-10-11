using System.Runtime.CompilerServices;

// Compiled into EVERY test project (*.Tests) by Directory.Build.targets; not a file of any one project.
namespace MintPlayer.Spark.TestProcess;

/// <summary>
/// No test host writes to the Windows EventLog. The default host builders (<c>WebApplication.CreateBuilder</c>,
/// <c>Host.CreateDefaultBuilder</c>) add the EventLog logger provider on Windows, at Warning and above. A test host
/// has no use for it: it fills the machine's Application log with test noise, and it is the one provider that throws
/// <see cref="ObjectDisposedException"/> on a write after its host is disposed, which turned a late warning into a
/// failed test (Q-M11e, docs/seo_analyzer_PRD.md §12).
/// </summary>
/// <remarks>
/// Defense in depth, not the fix: the fix is that Messaging no longer logs after its <c>StopAsync</c> returns. The
/// filter goes through an environment variable because every default host reads them into its configuration, so it
/// reaches every test host (and every app an E2E test starts as a child process) without each factory opting in. A
/// provider filtered to <c>None</c> is never called. A value already set is left alone.
/// </remarks>
internal static class NoEventLog
{
    internal const string Variable = "Logging__EventLog__LogLevel__Default";

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
            Environment.SetEnvironmentVariable(Variable, "None");
    }
}
