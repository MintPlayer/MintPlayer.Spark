using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using NSubstitute;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>A mail the account endpoints handed to <see cref="IEmailSender{TUser}"/>.</summary>
internal sealed record CapturedMail(string Kind, string Email, string Content)
{
    /// <summary>The link, HTML-decoded (the sender receives markup-safe text).</summary>
    public string Link => System.Net.WebUtility.HtmlDecode(Content);

    public string Query(string name)
    {
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(Link).Query);
        return query[name].ToString();
    }
}

internal sealed class CapturingEmailSender : IEmailSender<SparkUser>
{
    public List<CapturedMail> Sent { get; } = [];

    public Task SendConfirmationLinkAsync(SparkUser user, string email, string confirmationLink)
    {
        Sent.Add(new("confirm", email, confirmationLink));
        return Task.CompletedTask;
    }

    public Task SendPasswordResetLinkAsync(SparkUser user, string email, string resetLink)
    {
        Sent.Add(new("reset-link", email, resetLink));
        return Task.CompletedTask;
    }

    public Task SendPasswordResetCodeAsync(SparkUser user, string email, string resetCode)
    {
        Sent.Add(new("reset-code", email, resetCode));
        return Task.CompletedTask;
    }
}

/// <summary>
/// A real Identity host over the test database: Spark's stores, sign-in manager and endpoints,
/// cookie + bearer, a permissive antiforgery validator (the stamping is asserted separately, by
/// route metadata) and a capturing mail sender.
/// </summary>
internal sealed class AccountTestHost : IAsyncDisposable
{
    public const string BaseUrl = "https://app.example";
    public const string Password = "Correct-horse-1";

    public IHost Host { get; }
    public TestServer Server { get; }
    public CapturingEmailSender Mail { get; }

    private AccountTestHost(IHost host, CapturingEmailSender mail)
    {
        Host = host;
        Server = host.GetTestServer();
        Mail = mail;
    }

    public static async Task<AccountTestHost> StartAsync(
        IDocumentStore store,
        SparkLocalCredentials mode = SparkLocalCredentials.Full,
        Action<SparkAuthenticationOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        bool publicBaseUrl = true,
        Action<IEndpointRouteBuilder>? endpoints = null)
    {
        var mail = new CapturingEmailSender();

        var host = await new HostBuilder()
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(publicBaseUrl
                ? new Dictionary<string, string?> { ["Spark:Auth:PublicBaseUrl"] = BaseUrl }
                : []))
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddSingleton(store);
                    s.AddSparkAuthentication<SparkUser>();
                    s.Configure<SparkAuthenticationOptions>(o =>
                    {
                        o.LocalCredentials = mode;
                        o.BackfillUsersOnStartup = false;
                        configure?.Invoke(o);
                    });
                    s.Configure<IdentityOptions>(o =>
                    {
                        var options = new SparkAuthenticationOptions();
                        configure?.Invoke(options);
                        if (options.RequireConfirmedEmail)
                            o.SignIn.RequireConfirmedEmail = true;
                    });
                    // An external provider, so Disabled mode has a way in.
                    s.AddAuthentication().AddCookie("GitLab", "GitLab", _ => { });
                    s.AddSingleton<IEmailSender<SparkUser>>(mail);
                    s.AddSingleton(PermissiveAntiforgery());
                    s.AddAuthorization();
                    s.AddRouting();
                    services?.Invoke(s);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseAntiforgery();
                    app.UseEndpoints(e =>
                    {
                        e.MapSparkIdentityApi<SparkUser>(mode);
                        endpoints?.Invoke(e);
                    });
                }))
            .StartAsync();

        return new AccountTestHost(host, mail);
    }

    public HttpClient Client() => Server.CreateClient();

    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Host.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    public Task<SparkUser> CreateUserAsync(string userName, string email, string? password = Password, bool confirmed = true)
        => WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<SparkUser>>();
            var user = new SparkUser { UserName = userName, Email = email, EmailConfirmed = confirmed };
            var result = password is null ? await users.CreateAsync(user) : await users.CreateAsync(user, password);
            result.Succeeded.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Code)));
            return user;
        });

    public Task<SparkUser?> FindByEmailAsync(string email)
        => WithScopeAsync(sp => sp.GetRequiredService<UserManager<SparkUser>>().FindByEmailAsync(email));

    /// <summary>Signs in through <c>/spark/auth/login?useCookies=true</c> and returns the cookie header to replay.</summary>
    public async Task<string> CookieSignInAsync(HttpClient client, string identifier, string password = Password)
    {
        var response = await client.PostAsJsonAsync("/spark/auth/login?useCookies=true", new { email = identifier, password });
        response.EnsureSuccessStatusCode();
        return CookieHeader(response);
    }

    public static string CookieHeader(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? string.Join("; ", cookies.Select(c => c.Split(';')[0]))
            : string.Empty;

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, string? cookie = null, object? body = null, string? bearer = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(cookie))
            request.Headers.Add("Cookie", cookie);
        if (!string.IsNullOrEmpty(bearer))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    /// <summary>RFC 6238 TOTP (SHA-1, 30 s, 6 digits) for an RFC 4648 base32 key — what an authenticator app computes.</summary>
    public static string Totp(string base32Key, DateTimeOffset? at = null)
    {
        var key = Base32Decode(base32Key);
        var counter = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        var message = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(message);

        var hash = HMACSHA1.HashData(key, message);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        input = input.TrimEnd('=').Replace(" ", string.Empty).ToUpperInvariant();
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in input)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }
        return [.. output];
    }

    private static IAntiforgery PermissiveAntiforgery()
    {
        var antiforgery = Substitute.For<IAntiforgery>();
        antiforgery.ValidateRequestAsync(Arg.Any<HttpContext>()).Returns(Task.CompletedTask);
        antiforgery.IsRequestValidAsync(Arg.Any<HttpContext>()).Returns(true);
        antiforgery.GetAndStoreTokens(Arg.Any<HttpContext>())
            .Returns(new AntiforgeryTokenSet("request", "cookie", "field", "header"));
        return antiforgery;
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}
