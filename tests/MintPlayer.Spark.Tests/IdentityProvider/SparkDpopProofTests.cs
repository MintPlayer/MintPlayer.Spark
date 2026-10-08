using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.Authorization.ResourceServer;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// <see cref="SparkDpopProof.ValidateAsync"/> (RFC 9449 §4.3), the one validator both the identity provider's
/// token endpoint and Spark resource servers run. Pure: no host, no database; the replay store is a set.
/// </summary>
public class SparkDpopProofTests
{
    private const string Url = "https://rs.test/api/items";

    private static Func<string, DateTime, Task<bool>> NewReplayStore()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return (key, _) => Task.FromResult(seen.Add(key));
    }

    [Fact]
    public async Task A_valid_proof_answers_the_RFC_7638_thumbprint_of_its_key()
    {
        using var key = new DpopTestKey();

        var (jkt, error) = await SparkDpopProof.ValidateAsync(key.Proof("GET", Url), "GET", Url, null, NewReplayStore());

        error.Should().BeNull();
        jkt.Should().Be(key.Thumbprint, "the token is bound to this value, and a resource server compares it verbatim");
    }

    [Theory]
    [InlineData("JWT")]
    [InlineData("at+jwt")]
    public async Task A_proof_not_typed_dpop_jwt_is_refused(string typ)
    {
        using var key = new DpopTestKey();

        var (jkt, error) = await SparkDpopProof.ValidateAsync(key.Proof("GET", Url, typ: typ), "GET", Url, null, NewReplayStore());

        jkt.Should().BeNull();
        error.Should().Contain("typ");
    }

    [Theory]
    [InlineData("HS256")]
    [InlineData("none")]
    public async Task A_symmetric_or_unsigned_algorithm_is_refused(string alg)
    {
        using var key = new DpopTestKey();

        var (jkt, error) = await SparkDpopProof.ValidateAsync(key.Proof("GET", Url, alg: alg), "GET", Url, null, NewReplayStore());

        jkt.Should().BeNull("a proof of possession must show possession of a PRIVATE key");
        error.Should().Contain("algorithm");
    }

    [Fact]
    public async Task A_jwk_carrying_private_key_material_is_refused()
    {
        using var key = new DpopTestKey();

        var (jkt, error) = await SparkDpopProof.ValidateAsync(key.Proof("GET", Url, includePrivateKey: true), "GET", Url, null, NewReplayStore());

        jkt.Should().BeNull("RFC 9449 §4.3 item 7: the jwk MUST NOT contain a private key");
        error.Should().Contain("private key");
    }

    [Fact]
    public async Task A_proof_signed_by_another_key_than_its_embedded_jwk_is_refused()
    {
        using var key = new DpopTestKey();
        using var other = new DpopTestKey();

        var proof = key.Proof("GET", Url, signWith: other);
        var (jkt, error) = await SparkDpopProof.ValidateAsync(proof, "GET", Url, null, NewReplayStore());

        jkt.Should().BeNull("otherwise anyone could bind a token to a key they do not hold");
        error.Should().Contain("signature");
    }

    [Fact]
    public async Task A_proof_for_another_method_is_refused()
    {
        using var key = new DpopTestKey();

        var (jkt, error) = await SparkDpopProof.ValidateAsync(key.Proof("GET", Url), "POST", Url, null, NewReplayStore());

        jkt.Should().BeNull();
        error.Should().Contain("method");
    }

    [Theory]
    [InlineData("https://rs.test/api/other")]
    [InlineData("https://attacker.test/api/items")]
    [InlineData("not a url")]
    public async Task A_proof_for_another_url_is_refused(string htu)
    {
        using var key = new DpopTestKey();

        var (jkt, error) = await SparkDpopProof.ValidateAsync(key.Proof("GET", htu), "GET", Url, null, NewReplayStore());

        jkt.Should().BeNull("a proof captured for one endpoint must not open another");
        error.Should().Contain("URL");
    }

    [Theory]
    [InlineData(-180)] // older than the 2-minute lifetime
    [InlineData(300)]  // beyond the 1-minute allowance for clock skew
    public async Task A_proof_whose_iat_is_not_fresh_is_refused(int offsetSeconds)
    {
        using var key = new DpopTestKey();

        var proof = key.Proof("GET", Url, issuedAt: DateTimeOffset.UtcNow.AddSeconds(offsetSeconds));
        var (jkt, error) = await SparkDpopProof.ValidateAsync(proof, "GET", Url, null, NewReplayStore());

        jkt.Should().BeNull();
        error.Should().Contain("fresh");
    }

    [Fact]
    public async Task With_an_access_token_the_proof_must_carry_its_hash()
    {
        using var key = new DpopTestKey();
        const string accessToken = "the-access-token";

        (await SparkDpopProof.ValidateAsync(key.Proof("GET", Url), "GET", Url, accessToken, NewReplayStore()))
            .Error.Should().NotBeNull("a proof without ath could be replayed with any token bound to the key");

        (await SparkDpopProof.ValidateAsync(key.Proof("GET", Url, accessToken: "another-token"), "GET", Url, accessToken, NewReplayStore()))
            .Error.Should().Contain("access token");

        var (jkt, error) = await SparkDpopProof.ValidateAsync(key.Proof("GET", Url, accessToken: accessToken), "GET", Url, accessToken, NewReplayStore());
        error.Should().BeNull();
        jkt.Should().Be(key.Thumbprint);
    }

    [Fact]
    public async Task A_proof_is_accepted_once()
    {
        using var key = new DpopTestKey();
        var store = NewReplayStore();
        var proof = key.Proof("GET", Url);

        (await SparkDpopProof.ValidateAsync(proof, "GET", Url, null, store)).Error.Should().BeNull();

        var (jkt, error) = await SparkDpopProof.ValidateAsync(proof, "GET", Url, null, store);
        jkt.Should().BeNull("a captured proof replayed within its lifetime must not work again");
        error.Should().Contain("already used");
    }

    [Fact]
    public async Task A_proof_without_jti_is_refused()
    {
        using var key = new DpopTestKey();

        var (jkt, error) = await SparkDpopProof.ValidateAsync(key.Proof("GET", Url, jti: ""), "GET", Url, null, NewReplayStore());

        jkt.Should().BeNull("without a jti a replay cannot be detected");
        error.Should().NotBeNull();
    }

    [Fact]
    public void The_ath_is_the_base64url_SHA256_of_the_token()
    {
        SparkDpopProof.AccessTokenHash("abc").Should().Be(
            Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes("abc"))));
    }
}

/// <summary>
/// A client's DPoP key (EC P-256) and the proofs it signs. Proofs are assembled by hand rather than through
/// <c>JsonWebTokenHandler</c> so a test can produce exactly the malformed header it is about.
/// </summary>
internal sealed class DpopTestKey : IDisposable
{
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static string B64(byte[] bytes) => Base64UrlEncoder.Encode(bytes);

    public Dictionary<string, string> PublicJwk
    {
        get
        {
            var p = key.ExportParameters(includePrivateParameters: false);
            return new Dictionary<string, string>
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["x"] = B64(p.Q.X!),
                ["y"] = B64(p.Q.Y!),
            };
        }
    }

    /// <summary>RFC 7638: SHA-256 over the required members in lexicographic order, no whitespace.</summary>
    public string Thumbprint
    {
        get
        {
            var jwk = PublicJwk;
            var canonical = $"{{\"crv\":\"{jwk["crv"]}\",\"kty\":\"{jwk["kty"]}\",\"x\":\"{jwk["x"]}\",\"y\":\"{jwk["y"]}\"}}";
            return B64(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }
    }

    public string Proof(
        string method,
        string url,
        string? accessToken = null,
        DateTimeOffset? issuedAt = null,
        string? jti = null,
        string typ = "dpop+jwt",
        string alg = "ES256",
        bool includePrivateKey = false,
        DpopTestKey? signWith = null)
    {
        var jwk = PublicJwk;
        if (includePrivateKey)
            jwk["d"] = B64(key.ExportParameters(includePrivateParameters: true).D!);

        var header = new Dictionary<string, object> { ["typ"] = typ, ["alg"] = alg, ["jwk"] = jwk };
        var payload = new Dictionary<string, object>
        {
            ["htm"] = method,
            ["htu"] = url,
            ["iat"] = (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(),
        };
        if (jti != "")
            payload["jti"] = jti ?? Guid.NewGuid().ToString("N");
        if (accessToken is not null)
            payload["ath"] = SparkDpopProof.AccessTokenHash(accessToken);

        var signingInput = B64(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + B64(JsonSerializer.SerializeToUtf8Bytes(payload));
        if (alg == "none")
            return signingInput + ".";

        // ECDsa.SignData answers IEEE P1363 (r || s), which is the JWS encoding of ES256.
        var signature = (signWith ?? this).key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return signingInput + "." + B64(signature);
    }

    public void Dispose() => key.Dispose();
}
