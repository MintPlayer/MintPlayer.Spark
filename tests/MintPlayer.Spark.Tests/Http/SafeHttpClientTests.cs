using System.IO.Compression;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Http;

namespace MintPlayer.Spark.Tests.Http;

/// <summary>
/// Spike S1 (<c>docs/seo_analyzer_PRD.md</c>): the SSRF-safe named client end to end, against a Kestrel
/// server on 127.0.0.1. Loopback is refused by default, so every case that should get through opens
/// exactly <c>127.0.0.1/32</c> and the server's port, the explicit test-only allowance. The names
/// <c>a.test</c> and <c>b.test</c> resolve to 127.0.0.1 through a fake resolver, to give two origins.
/// </summary>
public class SafeHttpClientTests : IAsyncLifetime
{
    private const string Name = "Tests.SafeHttp";
    private WebApplication server = null!;
    private int port;

    private sealed class LoopbackResolver : ISafeHttpDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
            => Task.FromResult(new[] { IPAddress.Loopback });
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        server = builder.Build();

        server.MapGet("/ok", () => "hello");
        server.MapGet("/redirect", (string to) => Results.Redirect(to));
        server.MapGet("/loop/{n:int}", (int n) => n == 0 ? Results.Text("done") : Results.Redirect($"/loop/{n - 1}"));
        server.MapGet("/big", async (HttpContext http, int bytes, bool length) =>
        {
            if (length)
                http.Response.ContentLength = bytes;
            var chunk = new byte[64 * 1024];
            for (var sent = 0; sent < bytes; sent += chunk.Length)
                await http.Response.Body.WriteAsync(chunk.AsMemory(0, Math.Min(chunk.Length, bytes - sent)));
        });
        server.MapGet("/gzip", async (HttpContext http, int? zeros, string? text) =>
        {
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                if (text is not null)
                    gzip.Write(System.Text.Encoding.UTF8.GetBytes(text));
                else
                    gzip.Write(new byte[zeros ?? 0]);
            }
            http.Response.Headers.ContentEncoding = "gzip";
            http.Response.ContentLength = buffer.Length;
            await http.Response.Body.WriteAsync(buffer.ToArray());
        });
        server.MapGet("/auth", (HttpContext http) => http.Request.Headers.Authorization.ToString());
        server.MapPost("/temporary", (HttpContext http) =>
        {
            http.Response.StatusCode = 307;
            http.Response.Headers.Location = "/method";
        });
        server.Map("/method", async (HttpContext http) =>
            http.Request.Method + ":" + await new StreamReader(http.Request.Body).ReadToEndAsync());
        // 303 with a Location, written by hand (Results has no SeeOther helper).
        server.MapPost("/303", (HttpContext http) =>
        {
            http.Response.StatusCode = 303;
            http.Response.Headers.Location = "/method";
        });

        await server.StartAsync();
        port = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
    }

    public async Task DisposeAsync() => await server.DisposeAsync();

    private HttpClient Client(Action<SafeHttpOptions>? configure = null, bool allowLoopback = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISafeHttpDnsResolver, LoopbackResolver>();
        services.AddSafeHttpClient(Name, configure: o =>
        {
            o.AllowedPorts.Add(port);
            if (allowLoopback)
                o.AllowedNetworks.Add("127.0.0.1/32");
            configure?.Invoke(o);
        });
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient(Name);
    }

    private string Url(string pathAndQuery, string host = "127.0.0.1") => $"http://{host}:{port}{pathAndQuery}";

    private static async Task<SafeHttpBlockedException> Blocked(Func<Task> act)
        => (await act.Should().ThrowAsync<SafeHttpBlockedException>()).Which;

    [Fact]
    public async Task Loopback_is_refused_by_default()
    {
        var client = Client(allowLoopback: false);

        var thrown = await Blocked(() => client.GetStringAsync(Url("/ok")));

        thrown.Reason.Should().Be(SafeHttpBlockReason.Address);
        thrown.Message.Should().NotContain("127.0.0.1");
    }

    [Fact]
    public async Task A_name_resolving_to_loopback_is_refused_by_default()
    {
        var client = Client(allowLoopback: false);

        (await Blocked(() => client.GetStringAsync(Url("/ok", "a.test")))).Reason.Should().Be(SafeHttpBlockReason.Address);
    }

    [Fact]
    public async Task An_explicitly_allowed_server_is_reached()
    {
        var client = Client();

        (await client.GetStringAsync(Url("/ok"))).Should().Be("hello");
        (await client.GetStringAsync(Url("/ok", "a.test"))).Should().Be("hello");
    }

    [Fact]
    public async Task The_server_port_is_refused_unless_listed()
    {
        var services = new ServiceCollection();
        services.AddSafeHttpClient(Name, configure: o => o.AllowedNetworks.Add("127.0.0.1/32"));
        var client = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient(Name);

        (await Blocked(() => client.GetStringAsync(Url("/ok")))).Reason.Should().Be(SafeHttpBlockReason.Port);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/", SafeHttpBlockReason.Address)]
    [InlineData("http://10.0.0.1/", SafeHttpBlockReason.Address)]
    [InlineData("http://[::1]/", SafeHttpBlockReason.Address)]
    [InlineData("http://[::ffff:169.254.169.254]/", SafeHttpBlockReason.Address)]
    [InlineData("http://2130706434/", SafeHttpBlockReason.Address)]   // 127.0.0.2 in decimal; only 127.0.0.1/32 is allowed
    [InlineData("ftp://example.com/", SafeHttpBlockReason.Scheme)]
    [InlineData("file:///etc/passwd", SafeHttpBlockReason.Scheme)]
    [InlineData("http://example.com:6379/", SafeHttpBlockReason.Port)]
    public async Task A_redirect_is_checked_like_the_request(string target, SafeHttpBlockReason reason)
    {
        var client = Client();

        var thrown = await Blocked(() => client.GetStringAsync(Url("/redirect?to=" + Uri.EscapeDataString(target))));

        thrown.Reason.Should().Be(reason);
        thrown.Message.Should().NotContain("169.254").And.NotContain("10.0.0.1");
    }

    [Fact]
    public async Task Redirects_are_followed_up_to_the_limit_and_no_further()
    {
        var client = Client(o => o.MaxRedirects = 5);

        (await client.GetStringAsync(Url("/loop/5"))).Should().Be("done");
        (await Blocked(() => client.GetStringAsync(Url("/loop/6")))).Reason.Should().Be(SafeHttpBlockReason.TooManyRedirects);
    }

    [Fact]
    public async Task No_redirect_is_followed_when_the_limit_is_zero()
    {
        var client = Client(o => o.MaxRedirects = 0);

        (await Blocked(() => client.GetStringAsync(Url("/loop/1")))).Reason.Should().Be(SafeHttpBlockReason.TooManyRedirects);
    }

    [Theory]
    [InlineData(false)]   // chunked: caught while streaming
    [InlineData(true)]    // Content-Length: refused before reading
    public async Task A_response_over_the_byte_cap_is_refused(bool length)
    {
        var client = Client(o => o.MaxResponseBytes = 1024 * 1024);

        (await Blocked(() => client.GetByteArrayAsync(Url($"/big?bytes={2 * 1024 * 1024}&length={length}"))))
            .Reason.Should().Be(SafeHttpBlockReason.ResponseTooLarge);
        (await client.GetByteArrayAsync(Url($"/big?bytes={512 * 1024}&length={length}"))).Length.Should().Be(512 * 1024);
    }

    [Fact]
    public async Task A_decompression_bomb_is_refused_although_its_wire_size_is_small()
    {
        var client = Client(o =>
        {
            o.MaxResponseBytes = 1024 * 1024;
            o.MaxDecompressedBytes = 1024 * 1024;
        });

        (await Blocked(() => client.GetByteArrayAsync(Url($"/gzip?zeros={64 * 1024 * 1024}"))))
            .Reason.Should().Be(SafeHttpBlockReason.DecompressedTooLarge);
    }

    [Fact]
    public async Task A_compressed_response_is_decompressed()
    {
        var client = Client();

        using var response = await client.GetAsync(Url("/gzip?text=hello%20world"));

        (await response.Content.ReadAsStringAsync()).Should().Be("hello world");
        response.Content.Headers.ContentEncoding.Should().BeEmpty();
    }

    [Fact]
    public async Task Authorization_is_dropped_when_a_redirect_changes_origin()
    {
        var client = Client();

        async Task<string> Via(string host)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url("/redirect?to=" + Uri.EscapeDataString(Url("/auth", host)), "a.test"));
            request.Headers.Authorization = new("Bearer", "secret-for-test");
            using var response = await client.SendAsync(request);
            return await response.Content.ReadAsStringAsync();
        }

        (await Via("a.test")).Should().Be("Bearer secret-for-test");
        (await Via("b.test")).Should().BeEmpty();
    }

    [Fact]
    public async Task See_other_turns_a_post_into_a_get_and_temporary_redirect_keeps_the_post()
    {
        var client = Client();

        using var seeOther = await client.PostAsync(Url("/303"), new StringContent("body"));
        using var temporary = await client.PostAsync(Url("/temporary"), new StringContent("body"));

        (await seeOther.Content.ReadAsStringAsync()).Should().Be("GET:");
        (await temporary.Content.ReadAsStringAsync()).Should().Be("POST:body");
    }

    [Fact]
    public void The_primary_handler_uses_no_proxy_cookies_or_automatic_redirects()
    {
        using var handler = SafeHttpServiceCollectionExtensions.CreatePrimaryHandler(new SafeHttpOptions(), new LoopbackResolver());

        handler.UseProxy.Should().BeFalse();
        handler.UseCookies.Should().BeFalse();
        handler.AllowAutoRedirect.Should().BeFalse();
        handler.AutomaticDecompression.Should().Be(DecompressionMethods.None);
        handler.ConnectCallback.Should().NotBeNull();
    }

    [Fact]
    public void The_timeout_comes_from_the_options()
    {
        var client = Client(o => o.Timeout = TimeSpan.FromSeconds(7));

        client.Timeout.Should().Be(TimeSpan.FromSeconds(7));
    }
}
