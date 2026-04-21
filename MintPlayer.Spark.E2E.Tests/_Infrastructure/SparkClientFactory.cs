using MintPlayer.Spark.Client;

namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// Factory helpers that produce a <see cref="SparkClient"/> configured for the e2e Fleet host.
/// Fleet listens on HTTPS with a self-signed cert, so the underlying <see cref="HttpClient"/>
/// needs <c>ServerCertificateCustomValidationCallback</c> set to accept any cert; real
/// production consumers won't need that hook.
/// </summary>
internal static class SparkClientFactory
{
    /// <summary>
    /// Returns a <see cref="SparkClient"/> bound to the Fleet instance owned by
    /// <paramref name="host"/>. The client owns its internal <see cref="HttpClient"/>, so
    /// callers should wrap it in a <c>using</c>.
    /// </summary>
    public static SparkClient ForFleet(FleetTestHost host)
        => new(CreateHttpClient(host), ownsClient: true);

    /// <summary>
    /// Returns a raw <see cref="HttpClient"/> configured to talk to Fleet (self-signed cert
    /// validator + base address). For the tiny subset of tests that need to inspect
    /// transport-level artefacts <see cref="SparkClient"/> drops — <c>Set-Cookie</c>
    /// attributes like <c>Secure</c>/<c>SameSite</c>, in particular.
    /// </summary>
    public static HttpClient CreateHttpClient(FleetTestHost host)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        return new HttpClient(handler) { BaseAddress = new Uri(host.FleetUrl) };
    }

    /// <summary>
    /// Same as <see cref="ForFleet"/> but immediately signs the client in with the admin
    /// credentials the <see cref="FleetTestHost"/> seeded. Matches the common
    /// "log in first, then drive the API" pattern every Security/*.cs test follows.
    /// </summary>
    public static async Task<SparkClient> ForFleetAsAdminAsync(FleetTestHost host)
    {
        var client = ForFleet(host);
        try
        {
            await client.LoginAsync(host.AdminEmailAddress, host.AdminPass);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
