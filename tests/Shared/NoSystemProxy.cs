using System.Net;
using System.Runtime.CompilerServices;

// Compiled into EVERY test project (*.Tests) by Directory.Build.targets; not a file of any one project.
namespace MintPlayer.Spark.TestProcess;

/// <summary>
/// Every HTTP call a test process makes goes to a server on this machine: WireMock, the in-process
/// test host, an app the E2E suite started, the embedded RavenDB. None of them may consult the
/// machine's proxy settings, or the suite's result depends on the network the machine is on.
/// </summary>
/// <remarks>
/// With Windows' "Automatically detect settings" (WPAD) on, the first <see cref="HttpClient"/>
/// request to a host NAME in a process waits while Windows searches the network for a proxy.
/// Measured 2026-10-07 for <c>GET http://localhost:{wiremock}/api/v3/user</c>: 1.0, 1.1, 1.6 and
/// 2.6 s, and once more than 9.9 s, which failed
/// <c>DevWebSocketEndpointTests.A_developer_outside_the_allow_list_is_closed</c> on its 10 s bound
/// on an idle machine. The same request took 33 ms with the proxy off; DNS and the TCP connect took
/// under 2 ms. Linux CI has no WPAD, so the failure only ever showed locally and looked random.
/// Production code under test builds its own handlers (e.g. <c>GitHubInstallationService</c>'s
/// shared Octokit adapter), so only the process-wide default reaches all of them.
/// See docs/test-suite-performance-PRD.md §3, "No proxy in test processes".
/// </remarks>
internal static class NoSystemProxy
{
    [ModuleInitializer]
    internal static void Initialize() => HttpClient.DefaultProxy = new WebProxy();
}
