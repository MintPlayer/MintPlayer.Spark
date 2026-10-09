using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace MintPlayer.Spark.Authorization.ResourceServer;

/// <summary>
/// The resource-server half of sender-constrained tokens (<c>docs/identity_provider_platform_PRD.md</c> D8, I12).
/// ASP.NET's JwtBearer handler checks neither DPoP nor <c>cnf</c> (spike S2), so without this a stolen DPoP- or
/// certificate-bound token works like a plain bearer token.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>cnf.jkt</c>: the request uses the <c>DPoP</c> authorization scheme and carries a proof from that very
/// key, for this method and URL, with <c>ath</c> = the token's hash (RFC 9449 §7). A DPoP-bound token sent as
/// <c>Bearer</c> is refused (§7.1).</item>
/// <item><c>cnf.x5t#S256</c>: the TLS client certificate's thumbprint matches (RFC 8705 §3).</item>
/// <item>A token without <c>cnf</c> sent with the <c>DPoP</c> scheme is refused: it is not bound, and the client
/// believes it is.</item>
/// </list>
/// Proof ids are remembered in memory for the proof lifetime; across several instances of one resource server a
/// replay inside two minutes on a different instance is not detected.
/// </remarks>
internal static class SparkProofOfPossession
{
    public const string DpopSchemeItem = "Spark:ResourceServer:DPoP";

    /// <summary>The token from <c>Authorization: Bearer|DPoP &lt;token&gt;</c>; records which scheme it came with.</summary>
    public static string? ReadToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();
        if (header.StartsWith(SparkDpopProof.Header + " ", StringComparison.OrdinalIgnoreCase))
        {
            http.Items[DpopSchemeItem] = true;
            return header[(SparkDpopProof.Header.Length + 1)..].Trim();
        }
        return null;
    }

    /// <summary>Null when the request proves possession as the token demands, else why not.</summary>
    public static async Task<string?> ValidateAsync(HttpContext http, ClaimsPrincipal principal, string token, IMemoryCache cache)
    {
        var presentedAsDpop = http.Items.ContainsKey(DpopSchemeItem);
        var cnfClaim = principal.FindFirst("cnf")?.Value;
        string? jkt = null, x5t = null;
        if (!string.IsNullOrEmpty(cnfClaim))
        {
            try
            {
                using var cnf = JsonDocument.Parse(cnfClaim);
                if (cnf.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (cnf.RootElement.TryGetProperty("jkt", out var j) && j.ValueKind == JsonValueKind.String) jkt = j.GetString();
                    if (cnf.RootElement.TryGetProperty("x5t#S256", out var x) && x.ValueKind == JsonValueKind.String) x5t = x.GetString();
                }
            }
            catch (JsonException) { return "The token's cnf claim is not valid."; }
        }

        if (jkt is not null)
        {
            if (!presentedAsDpop)
                return "This token is DPoP-bound and must be sent with the DPoP scheme.";
            var proofs = http.Request.Headers[SparkDpopProof.Header];
            if (proofs.Count != 1 || string.IsNullOrEmpty(proofs[0]))
                return "Send exactly one DPoP proof.";
            var request = http.Request;
            var url = $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}";
            var (proofKey, error) = await SparkDpopProof.ValidateAsync(proofs[0]!, request.Method, url, token,
                (key, expiresAt) => Task.FromResult(TryUse(cache, key, expiresAt)));
            if (error is not null)
                return error;
            if (!string.Equals(proofKey, jkt, StringComparison.Ordinal))
                return "The DPoP proof is signed by another key than the token is bound to.";
        }
        else if (presentedAsDpop)
        {
            return "This token is not DPoP-bound; send it with the Bearer scheme.";
        }

        if (x5t is not null)
        {
            var certificate = await http.Connection.GetClientCertificateAsync(http.RequestAborted);
            if (certificate is null || !string.Equals(SparkDpopProof.CertificateThumbprint(certificate), x5t, StringComparison.Ordinal))
                return "This token is bound to a TLS client certificate that was not presented.";
        }

        return null;
    }

    private static readonly object Gate = new();

    private static bool TryUse(IMemoryCache cache, string key, DateTime expiresAt)
    {
        lock (Gate)
        {
            if (cache.TryGetValue(key, out _))
                return false;
            cache.Set(key, true, new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)));
            return true;
        }
    }
}
