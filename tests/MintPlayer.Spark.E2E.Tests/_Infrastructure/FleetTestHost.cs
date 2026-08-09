using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Replication.Abstractions.Models;
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
    /// <summary>
    /// The ASP.NET environment this host runs as, which also names its <c>appsettings.{Env}.json</c>
    /// override. Parameterised so two hosts with different replication settings can run in the same
    /// test session — they would otherwise fight over one override file in the Fleet project
    /// directory, each deleting the other's on dispose.
    /// <para>
    /// Must never be <c>Development</c>: <c>SparkReplicationCertificateMode.Auto</c> resolves to
    /// Development there, which would silently relax the certificate requirement the default host
    /// exists to prove.
    /// </para>
    /// </summary>
    public string EnvironmentName { get; init; } = "E2E";

    /// <summary>
    /// Cross-module certificate enforcement. Defaults to <c>Production</c> — the strict setting, so
    /// the shared host keeps proving that an uncertificated caller is refused. A host that needs to
    /// exercise what happens <i>after</i> authentication succeeds sets <c>Development</c>, which
    /// accepts any caller naming a registered module.
    /// </summary>
    public SparkReplicationCertificateMode CertificateMode { get; init; } = SparkReplicationCertificateMode.Production;

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

    /// <summary>
    /// Serialises the one-time build work every host would otherwise do concurrently.
    /// <para>
    /// xUnit runs distinct collections in parallel, so two hosts start together — and two
    /// <c>dotnet run</c>s racing to build Fleet produced <c>CS2012: cannot open … .dll for writing,
    /// being used by another process</c>. Both hosts then timed out waiting for a server that never
    /// started, which surfaced as every test in the suite failing in a millisecond.
    /// </para>
    /// <para>
    /// Building once behind this gate and running with <c>--no-build</c> removes the race rather
    /// than narrowing it: no amount of retrying makes two compilers safe on one output directory.
    /// </para>
    /// </summary>
    private static readonly SemaphoreSlim BuildGate = new(1, 1);
    private static bool _fleetBuilt;

    private SparkTestDriverHost? _raven;
    private Process? _fleetProcess;
    private string? _fleetUrl;
    private string? _fleetHttpUrl;
    private readonly List<string> _fleetLog = new();
    private readonly object _logLock = new();

    /// <summary>Base URL of the running Fleet instance (HTTPS, self-signed — use <see cref="BrowserOptions"/>).</summary>
    public string FleetUrl => _fleetUrl ?? throw new InvalidOperationException("Host not initialized");
    /// <summary>
    /// The plain-http base URL. The OIDC issuer runs here in tests: the JWT handler fetches the
    /// discovery document from the issuer itself, and over https that means the host trusting its
    /// own development certificate — which is true on a dev machine and not on a CI runner.
    /// </summary>
    public string FleetHttpUrl => _fleetHttpUrl ?? throw new InvalidOperationException("Host not initialized");

    /// <summary>URLs of the embedded Raven server, for tests that build cross-module payloads.</summary>
    public string[] RavenUrls => _raven?.Store.Urls ?? throw new InvalidOperationException("Host not initialized");
    public string AdminName => AdminUserName;
    public string AdminEmailAddress => AdminEmail;
    public string AdminPass => AdminPassword;

    /// <summary>
    /// Returns the last <paramref name="maxLines"/> lines captured from Fleet's stdout/stderr.
    /// Useful for surfacing server-side exception details inside an assertion failure message
    /// when the HTTP response body doesn't include them (production 500, etc.).
    /// </summary>
    public string RecentLog(int maxLines = 60)
    {
        lock (_logLock) return string.Join('\n', _fleetLog.TakeLast(maxLines));
    }

    /// <summary>
    /// Registers an additional user and patches the Raven document so the user is email-confirmed
    /// and belongs to the given group (matching a name declared in Fleet's App_Data/security.json).
    /// Used by row-level-authz tests to seed a second non-admin account.
    /// </summary>
    public async Task SeedUserAsync(string email, string password, string groupName)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(_fleetUrl!) };

        var registerResp = await client.PostAsJsonAsync("/spark/auth/register", new { email, password });
        if (!registerResp.IsSuccessStatusCode)
        {
            var body = await registerResp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Seed register for '{email}' failed ({(int)registerResp.StatusCode}): {body}");
        }

        using var appStore = new DocumentStore { Urls = _raven!.Store.Urls, Database = TestDatabase };
        appStore.Initialize();

        // Registration writes the document; finding it again goes through an index, and indexes
        // are eventually consistent. Without this the lookup intermittently ran before the index
        // caught up and the test failed during *setup*, reporting a seeding error for a row-level
        // authorization case — which reads like the feature broke rather than the fixture racing.
        //
        // Waiting on the store rather than per-query (`WaitForNonStaleResults`) deliberately: the
        // per-query form has to be remembered on every query anyone adds later, and is silent when
        // forgotten. This also throws with the actual index errors if they never settle, instead
        // of leaving a mystery failure further down.
        appStore.WaitForIndexing(TestDatabase);

        using var session = appStore.OpenAsyncSession();

        var user = await session.Query<SparkUser>()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == email.ToUpperInvariant())
            ?? throw new InvalidOperationException($"Seeded user '{email}' not visible in '{TestDatabase}' after register.");

        user.EmailConfirmed = true;
        user.UserName ??= email;
        user.NormalizedUserName ??= email.ToUpperInvariant();
        if (!user.Claims.Any(c => c.ClaimType == "group" && c.ClaimValue == groupName))
            user.Claims.Add(new SparkUserClaim { ClaimType = "group", ClaimValue = groupName });

        await session.SaveChangesAsync();
    }

    /// <summary>
    /// Registers a module in the shared SparkModules database, the way a real module registers
    /// itself at startup. Cross-module endpoints refuse a caller naming a module with no entry
    /// here, so this is the difference between "unknown module" and "known but maybe unauthorized"
    /// — which are the two refusals worth telling apart.
    /// </summary>
    /// <remarks>
    /// Writes a point-loadable document id rather than relying on a query, matching how
    /// <c>IModuleDirectory</c> looks modules up: an index would answer these authentication-gating
    /// lookups from a possibly-stale view.
    /// </remarks>
    public async Task SeedModuleAsync(string moduleName, string? clientCertificateThumbprint = null)
    {
        using var modulesStore = new DocumentStore { Urls = _raven!.Store.Urls, Database = TestModulesDatabase };
        modulesStore.Initialize();

        var documentId = ModuleInformation.DocumentId(moduleName);
        using (var session = modulesStore.OpenAsyncSession())
        {
            await session.StoreAsync(new ModuleInformation
            {
                AppName = moduleName,
                AppUrl = $"https://localhost:1/{moduleName}",
                DatabaseName = $"{moduleName}-e2e",
                DatabaseUrls = _raven.Store.Urls,
                RegisteredAtUtc = DateTime.UtcNow,
                ClientCertificateThumbprint = clientCertificateThumbprint,
            }, documentId);
            await session.SaveChangesAsync();
        }

        // Read back through a fresh session. A seeding helper that silently writes nowhere turns
        // every downstream assertion into a mystery — the failure surfaces as "the endpoint
        // refused a registered module", which reads like the product is broken.
        using var verify = modulesStore.OpenAsyncSession();
        _ = await verify.LoadAsync<ModuleInformation>(documentId)
            ?? throw new InvalidOperationException(
                $"Seeded module '{moduleName}' is not readable at '{documentId}' in '{TestModulesDatabase}'. "
                + $"Modules present: {await DescribeModulesAsync()}");
    }

    /// <summary>
    /// Everything in the shared SparkModules database, for failure messages. The interesting case
    /// is a lookup that misses while the document plainly exists — which points at the two
    /// processes disagreeing about the database, not at the document.
    /// </summary>
    public async Task<string> DescribeModulesAsync()
    {
        using var modulesStore = new DocumentStore { Urls = _raven!.Store.Urls, Database = TestModulesDatabase };
        modulesStore.Initialize();
        using var session = modulesStore.OpenAsyncSession();

        var all = await session.Advanced.AsyncRawQuery<ModuleInformation>("from @all_docs where startsWith(id(), 'moduleInformations/')").ToListAsync();
        var ids = all.Select(m => session.Advanced.GetDocumentId(m) ?? "(no id)");

        // Also sweep every other database on the server. The failure worth diagnosing is a record
        // that exists somewhere other than where the lookup reads, and naming only the expected
        // database cannot tell that apart from a record that was never written.
        var elsewhere = new List<string>();
        foreach (var name in _raven.Store.Maintenance.Server.Send(new GetDatabaseNamesOperation(0, 100)))
        {
            if (name == TestModulesDatabase) continue;
            using var other = session.Advanced.DocumentStore.OpenAsyncSession(name);
            var found = await other.Advanced
                .AsyncRawQuery<ModuleInformation>("from @all_docs where startsWith(id(), 'moduleInformations/')")
                .ToListAsync();
            if (found.Count > 0)
                elsewhere.Add($"{name}:[{string.Join(",", found.Select(f => other.Advanced.GetDocumentId(f)))}]");
        }

        return $"db='{TestModulesDatabase}' urls=[{string.Join(",", _raven.Store.Urls)}] docs=[{string.Join(", ", ids)}]"
             + (elsewhere.Count > 0 ? $" ALSO-IN {string.Join(" ", elsewhere)}" : " (no module docs in any other database)");
    }

    /// <summary>
    /// Registers a confidential <c>client_credentials</c> application and the scope that gives its
    /// tokens an audience, then returns the client secret.
    /// <para>
    /// The <c>group</c> claim is the entire authorization integration: a machine token carrying
    /// <c>group = "{group}"</c> is governed by the same <c>security.json</c> as a person, because
    /// group membership is resolved from claims and nothing else knows what a client is.
    /// </para>
    /// </summary>
    public async Task<string> SeedMachineClientAsync(string clientId, string scopeName, string audience, string group)
    {
        var secret = $"S{Guid.NewGuid():N}!a";

        using var appStore = new DocumentStore { Urls = _raven!.Store.Urls, Database = TestDatabase };
        appStore.Initialize();
        using var session = appStore.OpenAsyncSession();

        await session.StoreAsync(new OidcScope
        {
            Name = scopeName,
            DisplayName = scopeName,
            Enabled = true,
            // The audience comes from the scope, not the client — so this is what makes the issued
            // token addressed to this resource server rather than to everything the issuer serves.
            Audiences = [audience],
        });

        await session.StoreAsync(new OidcApplication
        {
            ClientId = clientId,
            DisplayName = clientId,
            ClientType = "confidential",
            Enabled = true,
            Secrets = [new ClientSecret { Hash = ClientSecretHasher.Hash(secret), CreatedAt = DateTime.UtcNow }],
            AllowedGrantTypes = ["client_credentials"],
            AllowedScopes = [scopeName],
            Claims = [new ClientClaim { Type = "group", Value = group }],
        });

        await session.SaveChangesAsync();
        appStore.WaitForIndexing(TestDatabase);

        return secret;
    }

    /// <summary>
    /// Point-loads a document from the app database by id. Deliberately not a query: these
    /// assertions include "this was NOT written", and an absence assertion against an
    /// eventually-consistent index passes whether or not the property holds.
    /// </summary>
    public async Task<T?> LoadAsync<T>(string documentId) where T : class
    {
        using var appStore = new DocumentStore { Urls = _raven!.Store.Urls, Database = TestDatabase };
        appStore.Initialize();
        using var session = appStore.OpenAsyncSession();
        return await session.LoadAsync<T>(documentId);
    }

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

        await BuildOnceAsync();

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

        if (_signingKeyFile is not null && File.Exists(_signingKeyFile))
        {
            try { File.Delete(_signingKeyFile); }
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

    /// <summary>
    /// Builds Fleet and its Angular bundle exactly once per test process, whichever host asks first.
    /// </summary>
    private static async Task BuildOnceAsync()
    {
        await BuildGate.WaitAsync();
        try
        {
            if (_fleetBuilt)
                return;

            await EnsureAngularBundleAsync();

            var repoRoot = FindRepoRoot();
            var fleetProject = Path.Combine(repoRoot, "Demo", "Fleet", "Fleet", "Fleet.csproj");
            var psi = new ProcessStartInfo("dotnet", $"build \"{fleetProject}\" --configuration Debug")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Building Fleet failed (exit {proc.ExitCode}).\nstdout: {stdout}\nstderr: {stderr}");

            _fleetBuilt = true;
        }
        finally
        {
            BuildGate.Release();
        }
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
    private string? _signingKeyFile;

    /// <summary>
    /// An RSA key in the shape <c>OidcSigningKeyService</c> reads: base64url RSA parameters.
    /// </summary>
    private static string NewSigningKeyJson()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var p = rsa.ExportParameters(true);
        static string B64(byte[] data) =>
            Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            N = B64(p.Modulus!),
            E = B64(p.Exponent!),
            D = B64(p.D!),
            P = B64(p.P!),
            Q = B64(p.Q!),
            DP = B64(p.DP!),
            DQ = B64(p.DQ!),
            QI = B64(p.InverseQ!),
        });
    }

    private async Task<string> StartFleetAsync(string[] ravenUrls)
    {
        var httpsPort = GetFreeTcpPort();
        var httpPort = GetFreeTcpPort();
        var httpsUrl = $"https://localhost:{httpsPort}";
        _fleetHttpUrl = $"http://localhost:{httpPort}";

        var repoRoot = FindRepoRoot();
        var fleetDir = Path.Combine(repoRoot, "Demo", "Fleet", "Fleet");
        var fleetProject = Path.Combine(fleetDir, "Fleet.csproj");

        // ASP.NET Core reads appsettings.{Environment}.json from the content root. By default
        // that's `Directory.GetCurrentDirectory()` — i.e. the working directory of the Fleet
        // process, which we set below to fleetDir (the project source dir). So the override
        // file must sit next to fleetDir/appsettings.json. DisposeAsync cleans it up.
        _overrideSettingsFile = Path.Combine(fleetDir, $"appsettings.{EnvironmentName}.json");

        // The provider auto-generates a signing key only in Development, and deliberately: a key
        // that materialises on first use in production is a key nobody backed up, and it silently
        // invalidates every token still in flight when the host restarts. Tests are not Development,
        // so they supply one — which also means the E2E exercises the configured-key path rather
        // than the convenience path.
        _signingKeyFile = Path.Combine(fleetDir, $"oidc-signing-key.{EnvironmentName}.json");
        await File.WriteAllTextAsync(_signingKeyFile, NewSigningKeyJson());
        var overrideJson = $$"""
        {
          "Spark": {
            "RavenDb": {
              "Urls": ["{{ravenUrls[0].Replace("\\", "\\\\")}}"],
              "Database": "{{TestDatabase}}",
              "EnsureDatabaseCreated": true
            },
            "Replication": {
              "ModuleName": "Fleet",
              "ModuleUrl": "{{httpsUrl}}",
              "SparkModulesUrls": ["{{ravenUrls[0].Replace("\\", "\\\\")}}"],
              "SparkModulesDatabase": "{{TestModulesDatabase}}",
              "ClientCertificate": { "Mode": "{{CertificateMode}}" }
            },
            "HttpsRedirection": false,
            "JwtBearer": { "Audience": "fleet-api" }
          },
          "SparkIdentityProvider": {
            "Issuer": "http://localhost:{{httpPort}}",
            "SigningKeyPath": "oidc-signing-key.{{EnvironmentName}}.json"
          }
        }
        """;
        await File.WriteAllTextAsync(_overrideSettingsFile, overrideJson);

        // `dotnet run` builds Fleet if needed and runs it. WorkingDirectory=fleetDir so
        // (a) ASP.NET Core's ContentRoot resolves to the project source, making
        // appsettings.{env}.json + ClientApp/dist/ paths work, and (b) `--no-launch-profile`
        // keeps launchSettings.json from overriding our ASPNETCORE_URLS / ENVIRONMENT.
        var psi = new ProcessStartInfo("dotnet", $"run --project \"{fleetProject}\" --configuration Debug --no-build --no-launch-profile")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = fleetDir,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = EnvironmentName;
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
