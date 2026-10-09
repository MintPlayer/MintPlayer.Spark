using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// I10 / D8: the signing-key ring without a host. <see cref="OidcKeyRing.InMemory"/> is what the
/// token-generator unit tests sign with, so it must behave like the real ring in the two ways they
/// rely on: it signs with its one active key, and it publishes only the public half.
/// </summary>
public class OidcKeyRingTests
{
    [Fact]
    public void An_in_memory_ring_signs_with_its_key_and_publishes_only_the_public_half()
    {
        using var rsa = RSA.Create(2048);
        var ring = OidcKeyRing.InMemory(new RsaSecurityKey(rsa) { KeyId = "unit-rsa" });

        var credentials = ring.GetSigningCredentials(null);
        credentials.Algorithm.Should().Be(SecurityAlgorithms.RsaSha256);
        credentials.Key.KeyId.Should().Be("unit-rsa");

        var published = ring.Jwks.Keys.Should().ContainSingle().Which;
        published.Kid.Should().Be("unit-rsa");
        published.N.Should().NotBeNullOrEmpty();
        published.D.Should().BeNull("the JWKS is public: no private exponent");
        published.P.Should().BeNull();
        published.Q.Should().BeNull();

        ring.ValidationKeys.Should().ContainSingle();
    }

    [Fact]
    public async Task A_token_an_in_memory_ring_signed_validates_against_its_validation_keys()
    {
        using var rsa = RSA.Create(2048);
        var ring = OidcKeyRing.InMemory(new RsaSecurityKey(rsa) { KeyId = "unit-rsa" });
        var handler = new JsonWebTokenHandler();

        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://idp.test",
            Audience = "api",
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = ring.GetSigningCredentials(null),
        });

        var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "https://idp.test",
            ValidAudience = "api",
            IssuerSigningKeys = ring.ValidationKeys,
        });

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Asking_an_in_memory_ring_for_an_ec_key_it_does_not_hold_fails_loudly()
    {
        using var rsa = RSA.Create(2048);
        var ring = OidcKeyRing.InMemory(new RsaSecurityKey(rsa) { KeyId = "unit-rsa" });

        var act = () => ring.GetSigningCredentials(SecurityAlgorithms.EcdsaSha256);

        act.Should().Throw<InvalidOperationException>(
            "falling back to the RSA key would stamp alg=ES256 on an RSA signature");
    }
}

/// <summary>
/// The persisted ring on a real provider host (I10). Every case here rotates or reads the host's one
/// key ring, so each case gets a host and database of its own (the parameterless constructor).
/// </summary>
public class OidcKeyRingRotationTests : OidcTestHost
{
    private OidcKeyRing Ring => Factory.GetService<OidcKeyRing>();

    private async Task<List<string>> PublishedKidsAsync()
    {
        var jwks = JsonDocument.Parse(await (await Client.GetAsync("/.well-known/jwks")).Content.ReadAsStringAsync()).RootElement;
        return [.. jwks.GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()!)];
    }

    private async Task<OidcKey?> LoadKeyAsync(string kid)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<OidcKey>("OidcKeys/" + kid);
    }

    [Fact]
    public async Task Startup_creates_one_active_rsa_and_one_active_ec_key_and_publishes_both()
    {
        var rsaKid = Ring.GetSigningCredentials(null).Key.KeyId;
        var ecKid = Ring.GetSigningCredentials(SecurityAlgorithms.EcdsaSha256).Key.KeyId;

        rsaKid.Should().NotBe(ecKid);
        var kids = await PublishedKidsAsync();
        kids.Should().Contain(rsaKid);
        kids.Should().Contain(ecKid);

        var rsaDoc = await LoadKeyAsync(rsaKid);
        rsaDoc.Should().NotBeNull("the ring persists its keys in OidcKeys");
        rsaDoc!.State.Should().Be(OidcKeyStates.Active);
        rsaDoc.ProtectedPrivateJwk.Should().NotContain("\"d\"",
            "the private half is stored protected with Data Protection, never as a clear JWK");

        (await LoadKeyAsync(ecKid))!.State.Should().Be(OidcKeyStates.Active);
    }

    [Fact]
    public async Task A_forced_rotation_signs_with_a_new_key_and_keeps_the_old_one_published()
    {
        var handler = new JsonWebTokenHandler();
        var oldKid = Ring.GetSigningCredentials(null).Key.KeyId;

        // Minted before the rotation: a relying party may still present it afterwards.
        var oldToken = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = "api",
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = Ring.GetSigningCredentials(null),
        });

        var changes = await Ring.RotateAsync(force: true);

        changes.Should().Contain(c => c.StartsWith("activated "));
        var newKid = Ring.GetSigningCredentials(null).Key.KeyId;
        newKid.Should().NotBe(oldKid, "a forced rotation signs with a new key at once");

        var retired = await LoadKeyAsync(oldKid);
        retired!.State.Should().Be(OidcKeyStates.Retired);
        retired.RetiredAt.HasValue.Should().BeTrue();
        (await LoadKeyAsync(newKid))!.State.Should().Be(OidcKeyStates.Active);

        var kids = await PublishedKidsAsync();
        kids.Should().Contain(newKid);
        kids.Should().Contain(oldKid, "a retired key stays in the JWKS while tokens it signed can still be presented");

        (await handler.ValidateTokenAsync(oldToken, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = "api",
            IssuerSigningKeys = Ring.ValidationKeys,
        })).IsValid.Should().BeTrue("validation accepts every published key, retired ones included");
    }

    [Fact]
    public async Task A_retired_key_past_its_retention_is_removed_from_the_ring_and_the_jwks()
    {
        var oldKid = Ring.GetSigningCredentials(null).Key.KeyId;
        await Ring.RotateAsync(force: true);

        // Age the retirement past Keys:RetainRetiredDays (14 by default).
        using (var session = Store.OpenAsyncSession())
        {
            var retired = await session.LoadAsync<OidcKey>("OidcKeys/" + oldKid);
            retired.RetiredAt = DateTime.UtcNow.AddDays(-15);
            await session.SaveChangesAsync();
        }

        var changes = await Ring.RotateAsync(force: false);

        changes.Should().Contain("removed " + oldKid);
        (await LoadKeyAsync(oldKid)).Should().BeNull();
        (await PublishedKidsAsync()).Should().NotContain(oldKid);
    }

    [Fact]
    public async Task An_unforced_rotation_with_nothing_due_changes_nothing()
    {
        var kid = Ring.GetSigningCredentials(null).Key.KeyId;

        var changes = await Ring.RotateAsync(force: false);

        changes.Should().BeEmpty("the keys were created at startup, 90 days from their rotation");
        Ring.GetSigningCredentials(null).Key.KeyId.Should().Be(kid);
    }

    [Fact]
    public async Task An_administrator_lists_and_rotates_the_keys_through_the_admin_endpoints()
    {
        var email = UserEmail("keyadmin");
        await SeedUserAsync(email);
        var browser = await KeyRingAdminBrowser.SignInAsync(Client, email, Password);
        var oldKid = Ring.GetSigningCredentials(null).Key.KeyId;

        // The default test security is permissive, which grants ManageAll/IdentityProvider.
        var list = await browser.GetAsync("/spark/identity-provider/admin/keys");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await list.Content.ReadAsStringAsync();
        raw.Should().Contain(oldKid);
        raw.Should().NotContain("protectedPrivateJwk", "the listing never carries key material");
        raw.Should().NotContain("ProtectedPrivateJwk");

        var rotate = await browser.PostWithXsrfAsync("/spark/identity-provider/admin/keys/rotate");
        rotate.StatusCode.Should().Be(HttpStatusCode.OK);

        Ring.GetSigningCredentials(null).Key.KeyId.Should().NotBe(oldKid);
    }
}

/// <summary>
/// The scheduled path (<c>Keys:RotationDays</c> 1, <c>Keys:PrePublishDays</c> 2): the next key is due for
/// publication at once but not yet for signing, which is exactly the "published ahead" window.
/// </summary>
public class OidcKeyRingPrePublishTests(OidcKeyRingPrePublishTests.PrePublishHost host)
    : OidcTestHost(host), IClassFixture<OidcKeyRingPrePublishTests.PrePublishHost>
{
    public sealed class PrePublishHost : OidcSharedHost
    {
        protected override void ConfigureIdentityProvider(SparkIdentityProviderOptions options)
        {
            options.Keys.RotationDays = 1;
            options.Keys.PrePublishDays = 2;
        }
    }

    [Fact]
    public async Task A_scheduled_rotation_publishes_the_next_key_ahead_without_signing_with_it()
    {
        var ring = Factory.GetService<OidcKeyRing>();
        var activeKid = ring.GetSigningCredentials(null).Key.KeyId;

        var changes = await ring.RotateAsync(force: false);

        var published = changes.Where(c => c.StartsWith("published ")).Select(c => c["published ".Length..]).ToList();
        published.Should().HaveCount(2, "one next key per key type (RSA and EC)");
        changes.Should().NotContain(c => c.StartsWith("activated "),
            "the active key is not due yet: it rotates a day after it was activated");

        ring.GetSigningCredentials(null).Key.KeyId.Should().Be(activeKid, "a Next key is published, not signing");

        var jwks = JsonDocument.Parse(await (await Client.GetAsync("/.well-known/jwks")).Content.ReadAsStringAsync()).RootElement;
        var kids = jwks.GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()).ToList();
        foreach (var kid in published)
            kids.Should().Contain(kid, "relying parties must have cached the next key before it signs");

        using var session = Store.OpenAsyncSession();
        foreach (var kid in published)
            (await session.LoadAsync<OidcKey>("OidcKeys/" + kid)).State.Should().Be(OidcKeyStates.Next);

        (await ring.RotateAsync(force: false)).Should().BeEmpty("a second run finds the next key already published");
    }
}

/// <summary>
/// The admin key endpoints for a signed-in user who is <b>not</b> an identity-provider administrator:
/// every right but <c>ManageAll/*</c>.
/// </summary>
public class OidcAdminKeysAccessTests(OidcAdminKeysAccessTests.NonAdminHost host)
    : OidcTestHost(host), IClassFixture<OidcAdminKeysAccessTests.NonAdminHost>
{
    public sealed class NonAdminHost : OidcSharedHost
    {
        protected override void ConfigureServices(IServiceCollection services)
            => services.UseSparkTestAccessControl(SparkTestAccessControl.Matching(
                resource => !resource.StartsWith("ManageAll/", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task A_non_administrator_cannot_list_the_signing_keys()
    {
        var email = UserEmail("nonadmin");
        await SeedUserAsync(email);
        var browser = await KeyRingAdminBrowser.SignInAsync(Client, email, Password);

        var response = await browser.GetAsync("/spark/identity-provider/admin/keys");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_non_administrator_cannot_rotate_the_signing_keys()
    {
        var email = UserEmail("nonadmin");
        await SeedUserAsync(email);
        var browser = await KeyRingAdminBrowser.SignInAsync(Client, email, Password);
        var ring = Factory.GetService<OidcKeyRing>();
        var kid = ring.GetSigningCredentials(null).Key.KeyId;

        // With a valid antiforgery token, so the 403 is the authorization answer and not a CSRF 400.
        var response = await browser.PostWithXsrfAsync("/spark/identity-provider/admin/keys/rotate");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        ring.GetSigningCredentials(null).Key.KeyId.Should().Be(kid, "nothing rotated");
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_list_the_signing_keys()
    {
        var response = await Client.GetAsync("/spark/identity-provider/admin/keys");

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("\"kid\"");
    }
}

/// <summary>
/// A cookie-carrying user agent that, unlike <see cref="OidcTestHost.Browser"/>, can read the
/// <c>XSRF-TOKEN</c> cookie back, which a POST to a Spark endpoint must echo in <c>X-XSRF-TOKEN</c>.
/// </summary>
internal sealed class KeyRingAdminBrowser(HttpClient client)
{
    private readonly Dictionary<string, string> cookies = new(StringComparer.Ordinal);

    /// <summary>Signs in at <c>/connect/login</c>, then mints an <c>XSRF-TOKEN</c> bound to that user.</summary>
    public static async Task<KeyRingAdminBrowser> SignInAsync(HttpClient client, string email, string password)
    {
        var browser = new KeyRingAdminBrowser(client);
        var form = await browser.GetAsync("/connect/login?returnUrl=%2F");
        var token = OidcTestHost.AntiforgeryTokenFrom(await form.Content.ReadAsStringAsync());

        var response = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/connect/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = email,
                ["password"] = password,
                ["returnUrl"] = "/",
                ["__RequestVerificationToken"] = token,
            }),
        });
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "the seeded user signs in");

        // The antiforgery token is bound to the identity, so it is minted after signing in.
        await browser.GetAsync("/spark");
        return browser;
    }

    public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(new HttpRequestMessage(HttpMethod.Get, url));

    public Task<HttpResponseMessage> PostWithXsrfAsync(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("") };
        if (cookies.TryGetValue("XSRF-TOKEN", out var xsrf))
            request.Headers.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(xsrf));
        return SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        if (cookies.Count > 0)
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}")));

        var response = await client.SendAsync(request);

        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var raw in setCookies)
            {
                var pair = raw.Split(';', 2)[0];
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var name = pair[..eq];
                var value = pair[(eq + 1)..];
                if (value.Length == 0 || raw.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
                    cookies.Remove(name);
                else
                    cookies[name] = value;
            }
        }

        return response;
    }
}
