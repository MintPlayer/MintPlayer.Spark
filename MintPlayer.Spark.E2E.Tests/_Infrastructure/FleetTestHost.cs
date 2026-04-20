using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// Spins up a real Fleet demo app instance backed by an embedded RavenDB, so Playwright
/// can drive a full Angular SPA + ASP.NET Core stack end-to-end. Owns the lifetime of
/// (1) the embedded Raven server, (2) the Fleet dotnet subprocess, and (3) seeded users.
/// </summary>
public sealed class FleetTestHost : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private readonly string _password = GeneratePassword();
    private string TestDatabase => $"SparkFleetE2E-{_suffix}";
    private string TestModulesDatabase => $"SparkModulesE2E-{_suffix}";
    private string AdminUserName => $"admin-{_suffix}";
    private string AdminEmail => $"admin-{_suffix}@e2e.local";
    private string AdminPassword => _password;

    /// <summary>
    /// Per-fixture random password that satisfies ASP.NET Identity's default validator
    /// (1 lowercase, 1 uppercase, 1 digit, 1 non-alphanumeric, 6+ chars). Randomizing
    /// per run keeps static-analysis scanners from flagging the source as a leaked secret.
    /// </summary>
    private static string GeneratePassword()
    {
        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=');
        return $"Aa1!{token}";
    }

    private SparkTestDriverHost? _raven;
    private Process? _fleetProcess;
    private string? _fleetUrl;
    private readonly List<string> _fleetLog = new();
    private readonly object _logLock = new();

    /// <summary>Base URL of the running Fleet instance (HTTPS, self-signed — use <see cref="BrowserOptions"/>).</summary>
    public string FleetUrl => _fleetUrl ?? throw new InvalidOperationException("Host not initialized");
    public string AdminName => AdminUserName;
    public string AdminEmailAddress => AdminEmail;
    public string AdminPass => AdminPassword;

    public async Task InitializeAsync()
    {
        _raven = new SparkTestDriverHost();
        await _raven.InitializeAsync();

        var ravenUrls = _raven.Store.Urls;

        // Embedded Raven may persist databases across test-process invocations — wipe + recreate
        // so every run starts from a known-empty state.
        DeleteIfExists(_raven.Store, TestDatabase);
        DeleteIfExists(_raven.Store, TestModulesDatabase);
        _raven.Store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(TestDatabase)));
        _raven.Store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(TestModulesDatabase)));

        await EnsureAngularBundleAsync();

        _fleetUrl = await StartFleetAsync(ravenUrls);

        // Seed the admin via the real /register endpoint (so the password hash matches whatever
        // Identity's PasswordHasher version is configured for) and then patch the group claim
        // directly in Raven so the user is a member of the Administrators group.
        await SeedAdminUserAsync(ravenUrls);
    }

    public async Task DisposeAsync()
    {
        if (_fleetProcess is { HasExited: false })
        {
            try { _fleetProcess.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }

            try { await _fleetProcess.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token); }
            catch { /* best-effort */ }
        }
        _fleetProcess?.Dispose();

        if (_overrideSettingsFile is not null && File.Exists(_overrideSettingsFile))
        {
            try { File.Delete(_overrideSettingsFile); }
            catch { /* best-effort */ }
        }

        if (_raven is not null)
            await _raven.DisposeAsync();
    }

    private async Task SeedAdminUserAsync(string[] ravenUrls)
    {
        // Register via the public endpoint so the password hash is compatible with whatever
        // PasswordHasher version Fleet's Identity is configured with.
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(_fleetUrl!) };

        var response = await client.PostAsJsonAsync("/spark/auth/register", new
        {
            email = AdminEmail,
            password = AdminPassword,
        });
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Register failed ({(int)response.StatusCode}): {body}");
        }

        // Now patch the stored user: mark email confirmed + add the Administrators group claim.
        using var appStore = new DocumentStore { Urls = ravenUrls, Database = TestDatabase };
        appStore.Initialize();

        using var session = appStore.OpenAsyncSession();
        var user = await session.Query<SparkUser>()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == AdminEmail.ToUpperInvariant());

        if (user == null)
        {
            var databases = _raven!.Store.Maintenance.Server.Send(new Raven.Client.ServerWide.Operations.GetDatabaseNamesOperation(0, 50));
            string dump = "";
            foreach (var dbName in databases)
            {
                using var s = _raven.Store.OpenAsyncSession(dbName);
                var users = await s.Query<SparkUser>().Take(5).ToListAsync();
                dump += $"\n  embedded db='{dbName}': {users.Count} user(s) [{string.Join(", ", users.Select(u => u.Email))}]";
            }
            throw new InvalidOperationException($"Registered user '{AdminEmail}' not found in embedded '{TestDatabase}'. Embedded URLs: [{string.Join(",", ravenUrls)}]. DBs:{dump}");
        }

        user.EmailConfirmed = true;
        user.UserName ??= AdminUserName;
        user.NormalizedUserName ??= AdminUserName.ToUpperInvariant();
        if (!user.Claims.Any(c => c.ClaimType == "group" && c.ClaimValue == "Administrators"))
            user.Claims.Add(new SparkUserClaim { ClaimType = "group", ClaimValue = "Administrators" });
        if (!user.Roles.Contains("Administrators"))
            user.Roles.Add("Administrators");

        await session.SaveChangesAsync();
    }

    private static async Task EnsureAngularBundleAsync()
    {
        var repoRoot = FindRepoRoot();
        var distPath = Path.Combine(repoRoot, "Demo", "Fleet", "Fleet", "ClientApp", "dist", "ClientApp", "browser");
        if (Directory.Exists(distPath) && Directory.EnumerateFileSystemEntries(distPath).Any())
            return;

        var clientApp = Path.Combine(repoRoot, "Demo", "Fleet", "Fleet", "ClientApp");
        var npm = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
        var psi = new ProcessStartInfo(npm, "run build")
        {
            WorkingDirectory = clientApp,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"npm run build failed (exit {proc.ExitCode}).\nstdout: {stdout}\nstderr: {stderr}");
    }

    private string? _overrideSettingsFile;

    private async Task<string> StartFleetAsync(string[] ravenUrls)
    {
        var httpsPort = GetFreeTcpPort();
        var httpPort = GetFreeTcpPort();
        var httpsUrl = $"https://localhost:{httpsPort}";

        var repoRoot = FindRepoRoot();
        var fleetDir = Path.Combine(repoRoot, "Demo", "Fleet", "Fleet");
        var fleetProject = Path.Combine(fleetDir, "Fleet.csproj");

        // ASP.NET Core reads appsettings.{Environment}.json from the content root. By default
        // that's `Directory.GetCurrentDirectory()` — i.e. the working directory of the Fleet
        // process, which we set below to fleetDir (the project source dir). So the override
        // file must sit next to fleetDir/appsettings.json. DisposeAsync cleans it up.
        _overrideSettingsFile = Path.Combine(fleetDir, "appsettings.E2E.json");
        var overrideJson = $$"""
        {
          "Spark": {
            "RavenDb": {
              "Urls": ["{{ravenUrls[0].Replace("\\", "\\\\")}}"],
              "Database": "{{TestDatabase}}",
              "EnsureDatabaseCreated": true
            }
          },
          "SparkReplication": {
            "ModuleName": "Fleet",
            "ModuleUrl": "{{httpsUrl}}",
            "SparkModulesUrls": ["{{ravenUrls[0].Replace("\\", "\\\\")}}"],
            "SparkModulesDatabase": "{{TestModulesDatabase}}"
          }
        }
        """;
        await File.WriteAllTextAsync(_overrideSettingsFile, overrideJson);

        // `dotnet run` builds Fleet if needed and runs it. WorkingDirectory=fleetDir so
        // (a) ASP.NET Core's ContentRoot resolves to the project source, making
        // appsettings.{env}.json + ClientApp/dist/ paths work, and (b) `--no-launch-profile`
        // keeps launchSettings.json from overriding our ASPNETCORE_URLS / ENVIRONMENT.
        var psi = new ProcessStartInfo("dotnet", $"run --project \"{fleetProject}\" --configuration Debug --no-launch-profile")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = fleetDir,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "E2E";
        psi.Environment["ASPNETCORE_URLS"] = $"{httpsUrl};http://localhost:{httpPort}";

        _fleetProcess = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Fleet process");

        _fleetProcess.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) lock (_logLock) _fleetLog.Add("[out] " + e.Data);
        };
        _fleetProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) lock (_logLock) _fleetLog.Add("[err] " + e.Data);
        };
        _fleetProcess.BeginOutputReadLine();
        _fleetProcess.BeginErrorReadLine();

        try
        {
            await WaitForReadyAsync(httpsUrl);
        }
        catch (TimeoutException ex)
        {
            string dump;
            lock (_logLock) dump = string.Join('\n', _fleetLog.TakeLast(120));
            throw new TimeoutException($"{ex.Message}\n\n--- Fleet process output (last 120 lines) ---\n{dump}", ex);
        }
        return httpsUrl;
    }

    private static async Task WaitForReadyAsync(string baseUrl)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync($"{baseUrl}/");
                if ((int)response.StatusCode < 500)
                    return;
            }
            catch
            {
                // Not up yet.
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"Fleet did not become ready at {baseUrl} within 120s");
    }

    private static void DeleteIfExists(IDocumentStore store, string databaseName)
    {
        try
        {
            store.Maintenance.Server.Send(new DeleteDatabasesOperation(databaseName, hardDelete: true));
        }
        catch
        {
            // Database didn't exist — ignore.
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "MintPlayer.Spark.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate MintPlayer.Spark.sln starting from " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// Exposes the protected <see cref="SparkTestDriver.Store"/> so <see cref="FleetTestHost"/>
/// can seed into the embedded Raven. Inheriting a non-test-class type keeps xUnit from
/// picking up this file's base class.
/// </summary>
internal sealed class SparkTestDriverHost : SparkTestDriver
{
    public new IDocumentStore Store => base.Store;
}
