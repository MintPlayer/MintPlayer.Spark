using System.Net;
using System.Runtime.CompilerServices;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// Every HTTP call in this test process goes to a server on this machine (WireMock, the in-process
/// test host, the embedded RavenDB), so none of them may consult the machine's proxy settings.
/// </summary>
/// <remarks>
/// With Windows' "Automatically detect settings" (WPAD) on, the first <see cref="HttpClient"/>
/// request to a host NAME in a process waits while Windows searches the network for a proxy.
/// Measured 2026-10-07 for <c>GET http://localhost:{wiremock}/api/v3/user</c>: 1.0, 1.1, 1.6 and
/// 2.6 s, and once more than 9.9 s, which failed
/// <c>DevWebSocketEndpointTests.A_developer_outside_the_allow_list_is_closed</c> on its 10 s bound.
/// The same request took 33 ms with the proxy off. Production code under test builds its own
/// handlers (<c>GitHubInstallationService</c>'s shared Octokit adapter), so only the process-wide
/// default reaches all of them. Without this the suite's result depended on the network the
/// machine happened to be on.
/// </remarks>
internal static class NoSystemProxy
{
    [ModuleInitializer]
    internal static void Initialize() => HttpClient.DefaultProxy = new WebProxy();
}
