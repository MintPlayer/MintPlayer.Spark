using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SeoTester.Crawler;
using SeoTester.Dns;
using SeoTester.Fetcher;
using SeoTester.Fetching;

namespace SeoTester.Tests._Infrastructure;

/// <summary>
/// The seo-fetcher service (<c>docs/seo_analyzer_PRD.md</c> D54) booted from its real <see cref="FetcherHost"/>: in
/// memory (<see cref="StartAsync"/>, a TestServer) or on a loopback port (<see cref="StartKestrelAsync"/>, real
/// sockets, for the latency measurement). With fakes, nothing leaves the machine and the SSRF-safe handlers still run
/// around them; without a <see cref="FakeWeb"/> the clients connect for real, under the configured address rules.
/// </summary>
public sealed class FetcherTestHost : IAsyncDisposable
{
    public const string Key = "fetcher-test-key-0123456789abcdef0123456789abcdef";
    public static readonly Uri BaseAddress = new("http://seo-fetcher.test/");

    private readonly WebApplication app;
    private readonly Uri? kestrel;

    private FetcherTestHost(WebApplication app, Uri? kestrel)
    {
        this.app = app;
        this.kestrel = kestrel;
    }

    public static Task<FetcherTestHost> StartAsync(FakeWeb? web = null, FakeDns? dns = null, FakeTls? tls = null, IReadOnlyDictionary<string, string?>? settings = null)
        => StartCoreAsync(web, dns, tls, settings, kestrel: false);

    public static Task<FetcherTestHost> StartKestrelAsync(FakeWeb? web = null, FakeDns? dns = null, FakeTls? tls = null)
        => StartCoreAsync(web, dns, tls, null, kestrel: true);

    private static async Task<FetcherTestHost> StartCoreAsync(FakeWeb? web, FakeDns? dns, FakeTls? tls, IReadOnlyDictionary<string, string?>? settings, bool kestrel)
    {
        var builder = FetcherHost.CreateBuilder([]);
        if (kestrel)
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        else
            builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var values = new Dictionary<string, string?> { [FetcherHost.KeySetting] = Key };
        foreach (var (k, v) in settings ?? new Dictionary<string, string?>())
            values[k] = v;
        builder.Configuration.AddInMemoryCollection(values);
        if (dns is not null)
            builder.Services.AddSingleton<ISeoDns>(dns);
        if (tls is not null)
            builder.Services.AddSingleton<ITlsProber>(tls);
        if (web is not null)
            foreach (var client in SeoHttp.MaxBodyBytes.Keys)
                builder.Services.AddHttpClient(client).ConfigurePrimaryHttpMessageHandler(web.CreateHandler);
        var app = FetcherHost.Build(builder);
        await app.StartAsync();
        Uri? address = kestrel ? new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/") : null;
        return new FetcherTestHost(app, address);
    }

    /// <summary>A handler into the in-memory server (the app's fetcher client's primary handler in the pipeline tests).</summary>
    public HttpMessageHandler CreateHandler() => kestrel is null ? app.GetTestServer().CreateHandler() : new SocketsHttpHandler { UseProxy = false };

    /// <summary>A raw client of the API (no key unless the test adds one).</summary>
    public HttpClient CreateClient() => new(CreateHandler()) { BaseAddress = kestrel ?? BaseAddress };

    /// <summary>The app's side of the API, as Production wires it.</summary>
    public RemoteSeoFetcher Remote(string key = Key, TimeSpan? margin = null)
        => new(CreateClient(), Options.Create(new SeoFetcherClientOptions { Key = key, Margin = margin ?? TimeSpan.FromSeconds(15) }));

    public ValueTask DisposeAsync() => app.DisposeAsync();
}

/// <summary>
/// A minimal HTTP/1.1 server on a loopback address, answering by path (<c>Connection: close</c>): the real target the
/// fetcher's address rules are tested against. Counts every connection it accepts.
/// </summary>
public sealed class LoopbackHttpServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private int connections;

    public LoopbackHttpServer(IPAddress address, Func<string, string> respond)
    {
        listener = new TcpListener(address, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop.Token);
                }
                catch (Exception) when (stop.IsCancellationRequested)
                {
                    return;
                }
                Interlocked.Increment(ref connections);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[8192];
                        var read = 0;
                        while (read < buffer.Length && !Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n"))
                        {
                            var n = await stream.ReadAsync(buffer.AsMemory(read));
                            if (n == 0) break;
                            read += n;
                        }
                        var path = Encoding.ASCII.GetString(buffer, 0, read).Split(' ') is { Length: > 1 } parts ? parts[1] : "/";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(respond(path)));
                    }
                });
            }
        });
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public int Connections => Volatile.Read(ref connections);

    public static string Ok(string body) => $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    public static string Redirect(string location) => $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
    }
}
