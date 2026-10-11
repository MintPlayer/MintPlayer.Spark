using System.Collections.Concurrent;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Http;
using MintPlayer.Spark.IdentityProvider.Extensions;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// D40 of <c>docs/seo_analyzer_PRD.md</c>: the back-channel logout client follows redirects only within the
/// origin of the registered <c>backchannel_logout_uri</c>. Before, it followed none (<c>MaxRedirects = 0</c>),
/// so a relying party that moved its endpoint behind a same-origin 307 lost every logout. A cross-origin
/// hop stays refused: it would resend the logout token to a URI the client never registered.
/// The relying party is a Kestrel server on 127.0.0.1, opened through the provider's own configuration;
/// <c>localhost</c> on the same port is a different origin.
/// </summary>
public class OidcBackChannelRedirectTests : IAsyncLifetime
{
    private WebApplication server = null!;
    private int port;
    private readonly ConcurrentQueue<string> received = new();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        server = builder.Build();

        server.MapPost("/logout", (HttpContext http) => Temporary(http, "/landing"));
        server.MapPost("/moved", (HttpContext http) => Temporary(http, $"http://localhost:{port}/landing"));
        server.MapPost("/chain/{n:int}", (HttpContext http, int n) => Temporary(http, n == 0 ? "/landing" : $"/chain/{n - 1}"));
        server.MapPost("/landing", async (HttpContext http) =>
        {
            var body = await new StreamReader(http.Request.Body).ReadToEndAsync();
            received.Enqueue(http.Request.Host + " " + body);
            return body;
        });

        await server.StartAsync();
        port = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
    }

    private static void Temporary(HttpContext http, string location)
    {
        http.Response.StatusCode = 307;
        http.Response.Headers.Location = location;
    }

    public async Task DisposeAsync() => await server.DisposeAsync();

    private HttpClient BackChannelClient()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Spark:IdentityProvider:ClientRequests:AllowedNetworks:0"] = "127.0.0.1/32",
            ["Spark:IdentityProvider:ClientRequests:AllowedPorts:0"] = port.ToString(),
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        new SparkBuilder(services, configuration).AddIdentityProvider();
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>()
            .CreateClient(SparkIdentityProviderExtensions.BackChannelLogoutHttpClient);
    }

    private static FormUrlEncodedContent Token() => new([KeyValuePair.Create("logout_token", "test-token")]);

    [Fact]
    public async Task A_same_origin_redirect_is_followed_with_the_token()
    {
        using var response = await BackChannelClient().PostAsync($"http://127.0.0.1:{port}/logout", Token());

        (await response.Content.ReadAsStringAsync()).Should().Be("logout_token=test-token");
    }

    [Fact]
    public async Task A_cross_origin_redirect_is_refused_and_the_token_is_not_resent()
    {
        var act = () => BackChannelClient().PostAsync($"http://127.0.0.1:{port}/moved", Token());

        (await act.Should().ThrowAsync<SafeHttpBlockedException>()).Which.Reason.Should().Be(SafeHttpBlockReason.CrossOriginRedirect);
        received.Should().BeEmpty();
    }

    [Fact]
    public async Task The_redirect_limit_is_respected()
    {
        var client = BackChannelClient();

        using var three = await client.PostAsync($"http://127.0.0.1:{port}/chain/2", Token());
        (await three.Content.ReadAsStringAsync()).Should().Be("logout_token=test-token");

        var act = () => client.PostAsync($"http://127.0.0.1:{port}/chain/3", Token());
        (await act.Should().ThrowAsync<SafeHttpBlockedException>()).Which.Reason.Should().Be(SafeHttpBlockReason.TooManyRedirects);
    }
}
