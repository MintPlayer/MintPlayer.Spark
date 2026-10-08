using WireMock.Server;

namespace MintPlayer.Spark.Tests.Webhooks.GitHub._Infrastructure;

/// <summary>
/// Pays WireMock's one-time plugin scan before a test starts its clock.
/// </summary>
/// <remarks>
/// <para>
/// The first request a WireMock server receives in a process builds a <c>RequestMessage</c>, whose
/// constructor asks <c>TypeLoader.TryLoadStaticInstance&lt;IMimeKitUtils&gt;</c> for the multipart
/// plugin. WireMock.Net 2.15's <c>TypeLoader.TryFindTypeInDlls</c> finds it by enumerating every
/// <c>*.dll</c> next to the test host, in name order, and calling <c>Assembly.Load</c> and
/// <c>GetTypes()</c> on each until it reaches <c>WireMock.Net.MimePart.dll</c>, which sorts near the
/// end. This test project's output folder holds 168 DLLs, so the scan loads about 140 assemblies
/// (BouncyCastle, GraphQL, Microsoft.CodeAnalysis.CSharp, Raven.*, …). The result is cached in a
/// static, so later requests in the same process cost a few milliseconds.
/// </para>
/// <para>
/// Measured 2026-10-08, first <c>User.Current()</c> against WireMock in a fresh process: 0.9 s with
/// the output folder already on disk, 10.8 s and 24.6 s right after deleting <c>bin</c> and
/// rebuilding (freshly written files). The second call took 3 ms. That first request is what
/// <c>DevWebSocketEndpointTests</c> used to make inside its 10 s receive bound, so whichever of its
/// tests ran first in a fresh process could fail without the endpoint being slow at all.
/// </para>
/// </remarks>
internal static class WireMockWarmUp
{
    private static readonly Lazy<Task> Once = new(RunAsync);

    /// <summary>Runs the scan once per test process; later callers await the same task.</summary>
    public static Task EnsureAsync() => Once.Value;

    private static async Task RunAsync()
    {
        // A server of its own, so no fixture's request log ever holds the warm-up request.
        using var server = WireMockServer.Start();
        using var http = new HttpClient();
        using var _ = await http.GetAsync(server.Url + "/__warm-up");
    }
}
