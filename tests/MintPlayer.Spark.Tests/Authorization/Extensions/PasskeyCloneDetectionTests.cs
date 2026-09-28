using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// Spike SP-E (#460) = #439's never-run SP2: does the passkey sign-in reject a sign counter that went
/// <em>down</em> from a non-zero baseline (a cloned authenticator), while still accepting the permanently
/// zero counter synced passkeys report? Driven through Spark's real <c>/passkeys/request-options</c> and
/// <c>/passkeys/sign-in</c> with a software authenticator: an ES256 key, a hand-built authenticator-data
/// block and a DER signature — nothing mocked on the server side.
/// </summary>
public class PasskeyCloneDetectionTests : SparkTestDriver
{
    private sealed class SoftwareAuthenticator
    {
        public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);

        /// <summary>COSE_Key (RFC 9053) for the public key: {1:2, 3:-7, -1:1, -2:x, -3:y}.</summary>
        public byte[] CosePublicKey()
        {
            var p = Key.ExportParameters(false);
            return [0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20, .. p.Q.X!, 0x22, 0x58, 0x20, .. p.Q.Y!];
        }

        public string Assertion(string challenge, uint signCount, string userId)
        {
            var clientData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                type = "webauthn.get",
                challenge,
                origin = "http://localhost",
                crossOrigin = false,
            }));

            // rpIdHash | flags (UP 0x01 | UV 0x04) | signCount (big-endian)
            var authenticatorData = new byte[37];
            SHA256.HashData(Encoding.UTF8.GetBytes("localhost")).CopyTo(authenticatorData, 0);
            authenticatorData[32] = 0x05;
            authenticatorData[33] = (byte)(signCount >> 24);
            authenticatorData[34] = (byte)(signCount >> 16);
            authenticatorData[35] = (byte)(signCount >> 8);
            authenticatorData[36] = (byte)signCount;

            var signature = Key.SignData([.. authenticatorData, .. SHA256.HashData(clientData)],
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

            return JsonSerializer.Serialize(new
            {
                id = B64(CredentialId),
                rawId = B64(CredentialId),
                type = "public-key",
                authenticatorAttachment = "platform",
                clientExtensionResults = new { },
                response = new
                {
                    clientDataJSON = B64(clientData),
                    authenticatorData = B64(authenticatorData),
                    signature = B64(signature),
                    userHandle = B64(Encoding.UTF8.GetBytes(userId)),
                },
            });
        }
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<(AccountTestHost Host, SoftwareAuthenticator Authenticator, string UserId)> StartAsync(uint storedCount)
    {
        var host = await AccountTestHost.StartAsync(Store, SparkLocalCredentials.Disabled,
            configure: o => o.Passkeys = SparkPasskeys.Enabled,
            services: s => s.Configure<IdentityPasskeyOptions>(o =>
            {
                o.UserVerificationRequirement = "required";
                o.ValidateOrigin = context => ValueTask.FromResult(context.Origin == "http://localhost");
            }));
        var user = await host.CreateUserAsync("passkey-user", "passkey@example.com", password: null);
        var authenticator = new SoftwareAuthenticator();

        await host.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<SparkUser>>();
            var stored = (await users.FindByIdAsync(user.Id!))!;
            var result = await users.AddOrUpdatePasskeyAsync(stored, new UserPasskeyInfo(
                authenticator.CredentialId, authenticator.CosePublicKey(), DateTimeOffset.UtcNow, storedCount,
                ["internal"], isUserVerified: true, isBackupEligible: false, isBackedUp: false,
                attestationObject: [], clientDataJson: []));
            result.Succeeded.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Code)));
            return true;
        });

        return (host, authenticator, user.Id!);
    }

    private static async Task<HttpResponseMessage> SignInAsync(AccountTestHost host, SoftwareAuthenticator authenticator, string userId, uint signCount)
    {
        using var client = host.Client();
        var options = await AccountTestHost.SendAsync(client, HttpMethod.Post, "/spark/auth/passkeys/request-options", body: new { });
        options.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await options.Content.ReadAsStringAsync());
        var challenge = json.RootElement.GetProperty("challenge").GetString()!;

        return await AccountTestHost.SendAsync(client, HttpMethod.Post, "/spark/auth/passkeys/sign-in",
            AccountTestHost.CookieHeader(options),
            new { credentialJson = authenticator.Assertion(challenge, signCount, userId) });
    }

    private static Task<uint> StoredCountAsync(AccountTestHost host, string userId, SoftwareAuthenticator authenticator)
        => host.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<SparkUser>>();
            var passkey = await users.GetPasskeyAsync((await users.FindByIdAsync(userId))!, authenticator.CredentialId);
            return passkey!.SignCount;
        });

    [Fact]
    public async Task SP_E_an_advancing_counter_signs_in_and_is_persisted()
    {
        var (host, authenticator, userId) = await StartAsync(storedCount: 5);
        await using var _ = host;

        var response = await SignInAsync(host, authenticator, userId, signCount: 6);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await StoredCountAsync(host, userId, authenticator)).Should().Be(6u);
    }

    [Fact]
    public async Task SP_E_a_counter_that_went_down_from_a_non_zero_baseline_is_refused()
    {
        var (host, authenticator, userId) = await StartAsync(storedCount: 5);
        await using var _ = host;

        var response = await SignInAsync(host, authenticator, userId, signCount: 3);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("passkey_failed");
        (await StoredCountAsync(host, userId, authenticator)).Should().Be(5u, "a refused assertion writes nothing");
    }

    [Fact]
    public async Task SP_E_a_counter_that_did_not_advance_is_refused()
    {
        var (host, authenticator, userId) = await StartAsync(storedCount: 5);
        await using var _ = host;

        var response = await SignInAsync(host, authenticator, userId, signCount: 5);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SP_E_a_permanently_zero_counter_synced_passkeys_report_still_signs_in()
    {
        var (host, authenticator, userId) = await StartAsync(storedCount: 0);
        await using var _ = host;

        var first = await SignInAsync(host, authenticator, userId, signCount: 0);
        var second = await SignInAsync(host, authenticator, userId, signCount: 0);

        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        second.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
