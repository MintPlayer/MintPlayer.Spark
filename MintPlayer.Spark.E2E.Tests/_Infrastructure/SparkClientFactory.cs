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
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        var http = new HttpClient(handler) { BaseAddress = new Uri(host.FleetUrl) };
        return new SparkClient(http, ownsClient: true);
    }

    /// <summary>
    /// Same as <see cref="ForFleet"/> but immediately signs the client in with the admin
    /// credentials the <see cref="FleetTestHost"/> seeded. Matches the common
    /// <c>SparkApi.LoginAsync(...)</c> pattern the tests used to call.
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
