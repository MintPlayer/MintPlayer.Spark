using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Options;
using SeoTester.Crawler;
using SeoTester.Fetcher;
using SeoTester.Fetching;
using SeoTester.Tests._Infrastructure;
using Xunit.Abstractions;

namespace SeoTester.Tests.Fetcher;

/// <summary>
/// The seo-fetcher writes nothing to disk (<c>docs/seo_analyzer_PRD.md</c> §12, "Untrusted content: proof tests"): what
/// a site answers lives in memory for one call and is gone. Proved by running the real fetcher process
/// (<c>SeoTester.Fetcher</c>, its own process so its environment is its own) with every folder it could write to by
/// default (current directory and content root, <c>TMP</c>/<c>TEMP</c>/<c>TMPDIR</c>, <c>ASPNETCORE_TEMP</c>, the home
/// and app-data folders) pointed at one folder the process may not write to: Windows denies the user every kind of
/// write on it, elsewhere it is mode 0555. A write there fails loudly (the call fails), it cannot slip by between two
/// scans. Its own binaries (the test's output folder) are searched afterwards for any file holding the marker the site
/// served. Deterministic: no timing, no watcher, no scan of folders other processes share.
/// Red with a temp-file write added to the fetch handler: the call fails.
/// </summary>
public class FetcherFilesystemTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_fetcher_service_works_with_no_writable_folder_and_leaves_no_file()
    {
        var marker = "fetched-marker-" + Guid.NewGuid().ToString("N");
        var started = DateTime.UtcNow.AddSeconds(-2);
        // A 1 MB page holding the marker, and a browser POST of 512 KB (over every buffering threshold of Kestrel and HttpClient).
        var page = $"<html><body><p>{marker}</p><p>{new string('x', 1024 * 1024)}</p><p>{marker}</p></body></html>";
        using var site = new LoopbackHttpServer(IPAddress.Parse("127.0.0.2"), _ => LoopbackHttpServer.Ok(page));
        var root = Directory.CreateTempSubdirectory("seo-fetcher-readonly-");
        var port = FreePort();
        Process? process = null;
        var log = new StringBuilder();
        try
        {
            ReadOnly(root, true);
            CanWrite(root.FullName).Should().BeFalse("the folder really refuses writes (else the test proves nothing)");
            process = Start(root.FullName, port, site.Port, log);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            await WaitForHealthAsync(http, process, log);
            var fetcher = new RemoteSeoFetcher(http, Options.Create(new SeoFetcherClientOptions { Key = FetcherTestHost.Key, Margin = TimeSpan.FromSeconds(15) }));

            var get = await fetcher.FetchAsync(new Uri($"http://127.0.0.2:{site.Port}/page"), 2 * 1024 * 1024, CancellationToken.None);
            get.StatusCode.Should().Be(200, get.Error);
            Encoding.UTF8.GetString(get.Body!).Should().Be(page);
            var post = await fetcher.FetchAsync(new SeoFetchRequest(new Uri($"http://127.0.0.2:{site.Port}/form"), 2 * 1024 * 1024)
            {
                Method = "POST",
                Client = SeoHttp.Browser,
                Headers = [new("content-type", "text/plain")],
                Body = Encoding.UTF8.GetBytes(marker + new string('y', 512 * 1024)),
            }, CancellationToken.None);
            post.StatusCode.Should().Be(200, post.Error);
        }
        finally
        {
            if (process is not null)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
                process.Dispose();
            }
            output.WriteLine(log.ToString());
            ReadOnly(root, false);
        }

        root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Select(f => f.FullName).Should().BeEmpty();
        root.Delete(recursive: true);
        Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)
            .Where(f => File.GetLastWriteTimeUtc(f) >= started && new FileInfo(f).Length < 64 * 1024 * 1024 && Holds(f, marker))
            .Should().BeEmpty("the fetcher wrote nothing next to its binaries");
    }

    private static Process Start(string root, int port, int sitePort, StringBuilder log)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SeoTester.Fetcher.exe" : "SeoTester.Fetcher");
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            ArgumentList =
            {
                $"--urls=http://127.0.0.1:{port}",
                $"--contentRoot={root}",
                $"--{FetcherHost.KeySetting}={FetcherTestHost.Key}",
                "--Spark:SafeHttp:AllowedNetworks:0=127.0.0.2/32",
                $"--Spark:SafeHttp:AllowedPorts:0={sitePort}",
            },
        };
        foreach (var name in new[] { "TMP", "TEMP", "TMPDIR", "ASPNETCORE_TEMP", "HOME", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "XDG_DATA_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME" })
            info.Environment[name] = root;
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        // The runtime's diagnostics IPC socket lives in TMPDIR on Linux; the container does not need it either.
        info.Environment["DOTNET_EnableDiagnostics"] = "0";
        var process = Process.Start(info)!;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitForHealthAsync(HttpClient http, Process process, StringBuilder log)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (process.HasExited)
                throw new InvalidOperationException($"The fetcher exited with {process.ExitCode}: {log}");
            try
            {
                using var health = await http.GetAsync(SeoFetcherProtocol.HealthPath.TrimStart('/'));
                if (health.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(200);
        }
        throw new TimeoutException("The fetcher did not answer its health check: " + log);
    }

    /// <summary>Takes away (or gives back) every kind of write on <paramref name="dir"/> and everything below it.</summary>
    private static void ReadOnly(DirectoryInfo dir, bool on)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = dir.GetAccessControl();
            var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny);
            if (on)
                security.AddAccessRule(rule);
            else
                security.RemoveAccessRule(rule);
            dir.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(dir.FullName, on
                ? UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, "probe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Holds(string file, string marker)
    {
        try
        {
            return File.ReadAllBytes(file).AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
