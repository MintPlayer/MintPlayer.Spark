using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// Boots the OIDC provider in-process and seeds the records its endpoints reason about.
/// <para>
/// Everything below M12.2 in the plan was fixed by reading code and is unverified by anything
/// that speaks HTTP — two Criticals, a one-click account takeover and a cross-client
/// disclosure among them. This is where that changes: the tests built on this fixture are the
/// first to exercise <c>/connect/*</c> at all.
/// </para>
/// <para>
/// In-process on <c>TestServer</c> rather than a hosted demo app, so there is no subprocess,
/// no Angular build, and no shared state between cases. Note that <c>TestServer</c>'s
/// <c>HttpClient</c> does not manage cookies — anything cookie-driven (login, consent) must
/// thread them explicitly.
/// </para>
/// </summary>
public abstract class OidcTestHost : SparkTestDriver
{
    protected const string Issuer = "https://idp.test";

    private SparkEndpointFactory<OidcTestContext>? _factory;

    protected SparkEndpointFactory<OidcTestContext> Factory =>
        _factory ??= new SparkEndpointFactory<OidcTestContext>(
            Store,
            models: [],
            configureSpark: spark =>
            {
                spark.AddAuthentication<SparkUser>();
                spark.AddIdentityProvider(options =>
                {
                    // Pinned rather than derived from the Host header: O7's fix makes this
                    // required outside Development, and pinning it here means the tests also
                    // assert the value the endpoints actually stamp.
                    options.Issuer = Issuer;
                    options.SigningKeyPath = Path.Combine(
                        Path.GetTempPath(), "spark-oidc-test-" + Guid.NewGuid().ToString("N") + ".json");
                });
            },
            // Development so the provider generates its own signing key. Production refusing to
            // do that is the correct behaviour and is covered separately by R-K1.
            environment: "Development");

    protected HttpClient Client => Factory.CreateClient();

    public override async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();

        await base.DisposeAsync();
    }

    /// <summary>
    /// Seeds an application. Defaults describe the ordinary case — a confidential web client
    /// doing the authorization-code flow with PKCE — so each test names only what it is about.
    /// </summary>
    protected async Task<OidcApplication> SeedApplicationAsync(
        string clientId,
        string? secret = "s3cret-value-for-tests",
        string[]? redirectUris = null,
        string[]? allowedScopes = null,
        string[]? grantTypes = null,
        string[]? postLogoutRedirectUris = null,
        bool enabled = true,
        bool requirePkce = false,
        string consentType = "explicit",
        string clientType = "confidential")
    {
        // Boot the host before seeding: the IdP deploys OidcApplications_ByClientId from its
        // startup middleware, and waiting for an index that does not exist yet returns
        // immediately — which is why the suite failed a different test on each run depending on
        // which class happened to seed before anything booted.
        _ = Factory;

        var app = new OidcApplication
        {
            ClientId = clientId,
            DisplayName = clientId,
            ClientType = clientType,
            Enabled = enabled,
            RequirePkce = requirePkce,
            ConsentType = consentType,
            RedirectUris = [.. redirectUris ?? [$"https://{clientId}.test/cb"]],
            PostLogoutRedirectUris = [.. postLogoutRedirectUris ?? []],
            AllowedScopes = [.. allowedScopes ?? ["openid", "profile"]],
            AllowedGrantTypes = [.. grantTypes ?? ["authorization_code"]],
        };

        if (secret != null)
            app.Secrets.Add(new ClientSecret { Hash = ClientSecretHasher.Hash(secret) });

        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(app);

        // Define every scope the client is allowed to ask for. A real deployment does this; a
        // fixture that skipped it produced tokens whose `scope` claim was silently empty, because
        // issuance resolves scopes from OidcScope documents rather than from the client's list.
        // Addressed by a derived id, not found through an index: two applications sharing a
        // scope seed concurrently, and an index query is eventually consistent, so the second
        // would not see the first and would create a duplicate. That made the suite fail only
        // under full-run load — the same staleness trap this package's own lookups kept falling
        // into.
        foreach (var name in app.AllowedScopes)
        {
            var id = "OidcScopes/" + name.ToLowerInvariant();
            if (await session.LoadAsync<OidcScope>(id) != null)
                continue;

            await session.StoreAsync(new OidcScope
            {
                Id = id,
                Name = name,
                DisplayName = name,
                Enabled = true,
                Required = name == "openid",
            });
        }

        await session.SaveChangesAsync();

        // Client lookup rides OidcApplications_ByClientId, which is eventually consistent, so a
        // just-seeded application is not immediately findable. Without this the suite failed a
        // different pair of tests on each run, under full-suite load only.
        //
        // Worth carrying into M12.7: whatever registers an application cannot assume it is
        // usable the instant it returns.
        WaitForIndexing(Store);

        return app;
    }

    protected const string Password = "Aa1!test-password";

    /// <summary>
    /// Creates a user through <see cref="UserManager{TUser}"/> so the password hash matches
    /// whatever hasher Identity is configured with — seeding the document directly would store
    /// no usable credential and every sign-in would fail for the wrong reason.
    /// </summary>
    protected async Task<SparkUser> SeedUserAsync(string email, string password = Password)
    {
        // UserManager is scoped; the factory resolves from the root provider.
        using var scope = Factory.GetService<IServiceScopeFactory>().CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<SparkUser>>();

        var user = new SparkUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
        };

        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
            throw new InvalidOperationException("Seeding user failed: " + string.Join("; ", result.Errors.Select(e => e.Description)));

        return user;
    }

    /// <summary>Runs an action against a scoped <see cref="UserManager{TUser}"/>.</summary>
    protected async Task<T> WithUserManagerAsync<T>(Func<UserManager<SparkUser>, Task<T>> action)
    {
        using var scope = Factory.GetService<IServiceScopeFactory>().CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<UserManager<SparkUser>>());
    }

    /// <summary>
    /// A user with an authenticator configured and recovery codes issued. Returns the codes,
    /// which are shown to a user exactly once in a real system and are therefore the only way a
    /// test can hold one.
    /// </summary>
    protected async Task<string[]> SeedTwoFactorUserAsync(string email, int recoveryCodes = 3)
    {
        await SeedUserAsync(email);

        return await WithUserManagerAsync(async users =>
        {
            var user = await users.FindByEmailAsync(email)
                ?? throw new InvalidOperationException($"Seeded user '{email}' not found.");

            await users.ResetAuthenticatorKeyAsync(user);
            await users.SetTwoFactorEnabledAsync(user, true);

            var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, recoveryCodes);
            return codes?.ToArray() ?? throw new InvalidOperationException("No recovery codes issued.");
        });
    }

    /// <summary>
    /// A currently-valid authenticator code, computed from the user's stored key.
    /// <para>
    /// Identity cannot produce one for us: <c>AuthenticatorTokenProvider.GenerateAsync</c>
    /// deliberately returns an empty string, because in the real flow the code comes from the
    /// user's phone and the server only ever validates. So the test has to play the phone, which
    /// means implementing RFC 6238 exactly as
    /// <c>Rfc6238AuthenticationService</c> does — HMAC-SHA1 over a big-endian 30-second
    /// timestep, dynamically truncated to six digits, no modifier.
    /// </para>
    /// </summary>
    protected async Task<string> AuthenticatorCodeAsync(string email)
    {
        var key = await WithUserManagerAsync(async users =>
        {
            var user = await users.FindByEmailAsync(email)
                ?? throw new InvalidOperationException($"No user '{email}'.");
            return await users.GetAuthenticatorKeyAsync(user)
                ?? throw new InvalidOperationException($"User '{email}' has no authenticator key.");
        });

        return ComputeTotp(Base32Decode(key), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
    }

    private static string ComputeTotp(byte[] key, long timestep)
    {
        using var hmac = new HMACSHA1(key);
        var counter = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(timestep));
        var hash = hmac.ComputeHash(counter);

        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
                   | ((hash[offset + 1] & 0xff) << 16)
                   | ((hash[offset + 2] & 0xff) << 8)
                   | (hash[offset + 3] & 0xff);

        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string value)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        value = value.TrimEnd('=').Replace(" ", "").ToUpperInvariant();

        var bits = 0;
        var buffer = 0;
        var output = new List<byte>();

        foreach (var c in value)
        {
            var index = Alphabet.IndexOf(c);
            if (index < 0) throw new FormatException($"'{c}' is not base32.");

            buffer = (buffer << 5) | index;
            bits += 5;

            if (bits < 8) continue;

            output.Add((byte)(buffer >> (bits - 8)));
            bits -= 8;
        }

        return [.. output];
    }

    /// <summary>
    /// Completes the password step only. The returned browser holds the partial-authentication
    /// cookie and nothing more — which is precisely the state every "can 2FA be skipped?" case
    /// needs to start from.
    /// </summary>
    protected async Task<Browser> PasswordStepAsync(string email, string password = Password, string returnUrl = "/")
    {
        var browser = NewBrowser();
        var page = await browser.GetAsync($"/connect/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        var token = AntiforgeryTokenFrom(await page.Content.ReadAsStringAsync());

        var response = await browser.PostFormAsync("/connect/login", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = password,
            ["returnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = token,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().StartWith("/connect/two-factor",
            "a 2FA-enabled account must be sent to the second factor, not signed in");

        return browser;
    }

    /// <summary>Submits the two-factor form, fetching its antiforgery token first.</summary>
    protected async Task<HttpResponseMessage> SubmitTwoFactorAsync(
        Browser browser,
        string? code = null,
        string? recoveryCode = null,
        string returnUrl = "/",
        bool includeAntiforgery = true)
    {
        var query = recoveryCode is null ? "" : "&recovery=true";
        var page = await browser.GetAsync($"/connect/two-factor?returnUrl={Uri.EscapeDataString(returnUrl)}{query}");

        var form = new Dictionary<string, string> { ["returnUrl"] = returnUrl };

        if (recoveryCode is not null)
        {
            form["useRecoveryCode"] = "true";
            form["recoveryCode"] = recoveryCode;
        }
        else if (code is not null)
        {
            form["code"] = code;
        }

        if (includeAntiforgery)
            form["__RequestVerificationToken"] = AntiforgeryTokenFrom(await page.Content.ReadAsStringAsync());

        return await browser.PostFormAsync("/connect/two-factor", form);
    }

    /// <summary>
    /// A user agent: carries cookies across requests, which <see cref="HttpClient"/> from
    /// <c>TestServer</c> does not do, and does not follow redirects, so a test can assert on
    /// the hop itself rather than on wherever it lands.
    /// </summary>
    protected sealed class Browser(HttpClient client)
    {
        private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

        public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(new HttpRequestMessage(HttpMethod.Get, url));

        public Task<HttpResponseMessage> PostFormAsync(string url, IDictionary<string, string> form) =>
            SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) });

        /// <summary>For forms that repeat a field name, as checkbox groups do.</summary>
        public Task<HttpResponseMessage> PostRawAsync(string url, IEnumerable<KeyValuePair<string, string>> pairs) =>
            SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(pairs) });

        public bool HasCookie(string name) => _cookies.ContainsKey(name);

        public void DropCookie(string name) => _cookies.Remove(name);

        /// <summary>The antiforgery cookie name carries a generated suffix, so drop by prefix.</summary>
        public void DropCookiesStartingWith(string prefix)
        {
            foreach (var name in _cookies.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                _cookies.Remove(name);
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            if (_cookies.Count > 0)
                request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));

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

                    // An expiry in the past is a deletion, not a value.
                    if (value.Length == 0 || raw.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
                        _cookies.Remove(name);
                    else
                        _cookies[name] = value;
                }
            }

            return response;
        }
    }

    protected Browser NewBrowser() => new(Client);

    /// <summary>The antiforgery token rendered into a <c>/connect</c> form.</summary>
    protected static string AntiforgeryTokenFrom(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
        if (!match.Success)
            throw new InvalidOperationException("No antiforgery field in the rendered form.");

        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    /// <summary>
    /// Signs a user in through the real login page — GET for the form and its token, then POST.
    /// Returns the browser holding the resulting session, so consent tests start from a genuine
    /// authenticated state rather than a fabricated one.
    /// </summary>
    protected async Task<Browser> SignInAsync(string email, string password = Password, string returnUrl = "/")
    {
        var browser = NewBrowser();

        var form = await browser.GetAsync($"/connect/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        var token = AntiforgeryTokenFrom(await form.Content.ReadAsStringAsync());

        var response = await browser.PostFormAsync("/connect/login", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = password,
            ["returnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = token,
        });

        if (response.StatusCode != HttpStatusCode.Redirect)
            throw new InvalidOperationException($"Sign-in did not redirect (got {(int)response.StatusCode}).");

        return browser;
    }

    /// <summary>
    /// Drives authorize → consent and returns the minted authorization code. Going through the
    /// real hops (rather than seeding a code document) means the code carries everything the
    /// flow actually puts on it — the authorization id above all, whose absence made the
    /// revocation cascades dead code for so long.
    /// </summary>
    protected async Task<string> ObtainCodeAsync(
        OidcApplication app,
        string email,
        string[]? scopes = null,
        string? codeChallenge = null,
        string? redirectUri = null)
    {
        var browser = await SignInAsync(email);
        var scope = string.Join(' ', scopes ?? ["openid"]);
        var target = redirectUri ?? app.RedirectUris[0];

        var url = $"/connect/authorize?client_id={Uri.EscapeDataString(app.ClientId)}"
                + $"&redirect_uri={Uri.EscapeDataString(target)}&response_type=code"
                + $"&scope={Uri.EscapeDataString(scope)}"
                + (codeChallenge is null ? "" : $"&code_challenge={Uri.EscapeDataString(codeChallenge)}&code_challenge_method=S256");

        var authorize = await browser.GetAsync(url);
        if (authorize.StatusCode != HttpStatusCode.Redirect)
            throw new InvalidOperationException($"/connect/authorize returned {(int)authorize.StatusCode}.");

        var location = authorize.Headers.Location!.OriginalString;

        if (location.StartsWith("/connect/consent", StringComparison.Ordinal))
        {
            var requestId = location["/connect/consent?request_id=".Length..];
            var page = await browser.GetAsync(location);
            var token = AntiforgeryTokenFrom(await page.Content.ReadAsStringAsync());

            var form = new Dictionary<string, string>
            {
                ["request_id"] = requestId,
                ["decision"] = "allow",
                ["__RequestVerificationToken"] = token,
            };

            var consent = await client_PostScopes(browser, form, scopes ?? ["openid"]);
            location = consent.Headers.Location!.OriginalString;
        }

        var query = new Uri(location).Query;
        var code = System.Web.HttpUtility.ParseQueryString(query)["code"];
        return code ?? throw new InvalidOperationException($"No code in redirect: {location}");
    }

    private static async Task<HttpResponseMessage> client_PostScopes(
        Browser browser, Dictionary<string, string> form, string[] scopes)
    {
        // FormUrlEncodedContent takes one value per key, so a multi-scope grant is posted as
        // repeated 'scopes' fields the same way a browser would send checked boxes.
        var pairs = form.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value))
            .Concat(scopes.Select(s => new KeyValuePair<string, string>("scopes", s)))
            .ToList();

        return await browser.PostRawAsync("/connect/consent", pairs);
    }

    /// <summary>PKCE pair: a verifier and its S256 challenge.</summary>
    protected static (string Verifier, string Challenge) Pkce()
    {
        var verifier = OidcRequestReference.GenerateValue();
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    /// <summary>
    /// Writes an authorization request straight to storage, standing in for a completed
    /// <c>/connect/authorize</c> hop. Returns the opaque handle the browser would carry.
    /// <para>
    /// Seeding it rather than driving the real hop is deliberate for the consent tests: those
    /// assert what <c>/connect/consent</c> does with a handle, and going through the login
    /// pages first would make a consent failure indistinguishable from a login failure.
    /// </para>
    /// </summary>
    protected async Task<string> SeedAuthorizationRequestAsync(
        OidcApplication app,
        string subject,
        string[]? scopes = null,
        string? redirectUri = null,
        string status = "pending",
        DateTime? expiresAt = null)
    {
        var handle = OidcRequestReference.GenerateValue();
        var request = new OidcAuthorizationRequest
        {
            Id = OidcRequestReference.DocumentId(handle),
            ApplicationId = app.Id!,
            Subject = subject,
            RedirectUri = redirectUri ?? app.RedirectUris[0],
            Scopes = [.. scopes ?? ["openid"]],
            Status = status,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(10),
        };

        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(request);
        await session.SaveChangesAsync();
        return handle;
    }
}

/// <summary>Minimal context: these tests exercise <c>/connect/*</c>, not persistent objects.</summary>
public sealed class OidcTestContext : SparkContext
{
}
