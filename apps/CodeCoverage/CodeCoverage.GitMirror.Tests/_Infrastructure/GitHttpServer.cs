using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeCoverage.GitMirror.Tests._Infrastructure;

/// <summary>
/// A local, authenticated smart-HTTP git server: Kestrel on a loopback port running <c>git http-backend</c> as CGI, the
/// way a forge serves clones. It works wherever git does (Windows and the Ubuntu runners), needs no TLS, no network and no
/// daemon. Basic authentication is required when <see cref="RequireCredential"/> is set; it records whether a request
/// carried the right credential, never the credential itself.
/// </summary>
internal sealed class GitHttpServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private int requests, authorized, rejected;

    private GitHttpServer(WebApplication app, string root)
    {
        this.app = app;
        Root = root;
    }

    /// <summary>The directory whose bare repositories are served: <c>{BaseUrl}{name}.git</c>.</summary>
    public string Root { get; }

    public Uri BaseUrl { get; private set; } = null!;

    /// <summary>When set, every request must carry exactly this Basic credential; others get 401.</summary>
    public (string User, string Password)? RequireCredential { get; set; }

    /// <summary>A delay before each request is served (to hold a clone in flight).</summary>
    public TimeSpan Delay { get; set; }

    public int Requests => Volatile.Read(ref requests);
    public int AuthorizedRequests => Volatile.Read(ref authorized);
    public int RejectedRequests => Volatile.Read(ref rejected);

    public Uri UrlOf(string repository) => new(BaseUrl, repository + ".git");

    public string PathOf(string repository) => Path.Combine(Root, repository + ".git");

    public static async Task<GitHttpServer> StartAsync(string root)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var server = new GitHttpServer(app, root);
        app.Run(server.ServeAsync);
        await app.StartAsync();
        server.BaseUrl = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/");
        return server;
    }

    private async Task ServeAsync(HttpContext context)
    {
        Interlocked.Increment(ref requests);
        if (RequireCredential is { } required)
        {
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{required.User}:{required.Password}"));
            if (context.Request.Headers.Authorization.ToString() != expected)
            {
                Interlocked.Increment(ref rejected);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Basic realm=\"git\"";
                return;
            }
            Interlocked.Increment(ref authorized);
        }
        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, context.RequestAborted);

        var psi = FixtureGit.StartInfo(null, ["http-backend"]);
        psi.StandardOutputEncoding = null;
        var env = psi.Environment;
        env["GIT_PROJECT_ROOT"] = Root;
        env["GIT_HTTP_EXPORT_ALL"] = "1";
        env["REQUEST_METHOD"] = context.Request.Method;
        env["PATH_INFO"] = context.Request.Path.Value ?? "/";
        env["QUERY_STRING"] = context.Request.QueryString.HasValue ? context.Request.QueryString.Value![1..] : "";
        env["CONTENT_TYPE"] = context.Request.ContentType ?? "";
        env["REMOTE_ADDR"] = "127.0.0.1";
        env["REMOTE_USER"] = RequireCredential?.User ?? "";
        if (context.Request.ContentLength is { } length)
            env["CONTENT_LENGTH"] = length.ToString();
        if (context.Request.Headers["Git-Protocol"].ToString() is { Length: > 0 } protocol)
            env["GIT_PROTOCOL"] = protocol;
        if (context.Request.Headers.ContentEncoding.ToString() is { Length: > 0 } encoding)
            env["HTTP_CONTENT_ENCODING"] = encoding;

        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var input = Task.Run(async () =>
        {
            try
            {
                await context.Request.Body.CopyToAsync(process.StandardInput.BaseStream);
            }
            finally
            {
                process.StandardInput.Close();
            }
        });

        var output = process.StandardOutput.BaseStream;
        var header = await ReadHeaderAsync(output);
        foreach (var line in header.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Status", StringComparison.OrdinalIgnoreCase))
                context.Response.StatusCode = int.Parse(value.Split(' ')[0]);
            else
                context.Response.Headers.Append(name, value);
        }
        await output.CopyToAsync(context.Response.Body);
        await input;
        await process.WaitForExitAsync();
        await stderr;
    }

    /// <summary>The CGI header block, up to the blank line, read byte by byte so nothing of the body is consumed.</summary>
    private static async Task<string> ReadHeaderAsync(Stream output)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (await output.ReadAsync(one) == 1)
        {
            if (one[0] == '\r')
                continue;
            bytes.Add(one[0]);
            if (bytes.Count >= 2 && bytes[^1] == '\n' && bytes[^2] == '\n')
                break;
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
