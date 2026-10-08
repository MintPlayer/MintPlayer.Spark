using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Client.Authorization;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// Where a Spark app lives in the repository, and what its host needs to know to build and run it.
/// Paths are relative to the repository root unless stated otherwise.
/// </summary>
/// <param name="AppName">Display name, used in log and failure messages (<c>Fleet</c>).</param>
/// <param name="ProjectDirectory">The app project's directory, e.g. <c>apps/Fleet/Fleet</c>. It is the process's content root.</param>
/// <param name="ProjectFileName">The <c>.csproj</c> inside <paramref name="ProjectDirectory"/>.</param>
/// <param name="DatabasePrefix">Prefix of the per-host app database; a random suffix is appended.</param>
/// <param name="CoverageSlug">
/// Prefix of the host's coverage report directory (<c>coverage/{slug}-host-{env}-{suffix}/</c>).
/// ⚠️ <c>tools/verify-coverage-paths.mjs</c> knows each slug by name (<c>fleet</c>, <c>qna</c>, <c>hr</c>, <c>sparkid</c>); another app's
/// slug must be added there, or its report is rejected.
/// </param>
public sealed record SparkAppDescriptor(
    string AppName,
    string ProjectDirectory,
    string ProjectFileName,
    string DatabasePrefix,
    string CoverageSlug)
{
    /// <summary>The Angular workspace, relative to <see cref="ProjectDirectory"/>.</summary>
    public string ClientAppDirectory { get; init; } = "ClientApp";

    /// <summary>The built bundle, relative to <see cref="ClientAppDirectory"/>. The stale-bundle check reads it.</summary>
    public string BundleOutputDirectory { get; init; } = Path.Combine("dist", "ClientApp", "browser");

    /// <summary>
    /// Sources outside the app's own <c>ClientApp/src</c> that go into its bundle. The npm libraries are
    /// consumed from SOURCE (<c>tsconfig.base.json</c> maps them there), so a change in one reaches the
    /// browser only through a rebuild, and the stale-bundle check must see it.
    /// </summary>
    public IReadOnlyList<string> BundleSourceRoots { get; init; } =
    [
        Path.Combine("libs", "node_packages", "ng-spark"),
    ];

    /// <summary>The dotnet-coverage settings file in the E2E test project directory.</summary>
    public string CoverageSettingsFile { get; init; } = "fleet-host.coverage.xml";

    /// <summary>
    /// Whether the app sends mail through the pickup-folder transport (<c>Spark:Mail:PickupFolder</c>).
    /// The host then points that folder at a per-host directory (<see cref="SparkAppTestHost.MailPickupFolder"/>),
    /// so two hosts never share one and a test reads only its own host's mail. Leave it off for an app that
    /// configures SMTP: MailManager refuses to start with both set.
    /// </summary>
    public bool UsesMailPickup { get; init; }
}

/// <summary>What <see cref="SparkAppTestHost.ConfigureAppSettings"/> can use to build the override settings.</summary>
/// <param name="HttpsUrl">The app's https base URL.</param>
/// <param name="HttpUrl">The app's plain-http base URL.</param>
/// <param name="HttpPort">The port of <paramref name="HttpUrl"/>.</param>
/// <param name="RavenUrls">The embedded Raven server's URLs.</param>
/// <param name="ProjectDirectory">Absolute path of the app project (the content root).</param>
public sealed record SparkAppHostContext(
    string HttpsUrl,
    string HttpUrl,
    int HttpPort,
    string[] RavenUrls,
    string ProjectDirectory);

/// <summary>
/// Runs a real Spark app, backed by an embedded RavenDB, so tests can drive its full Angular SPA and
/// ASP.NET Core stack end to end. Owns (1) the embedded Raven server and the per-host databases, (2) the
/// app's <c>dotnet run</c> subprocess, optionally under <c>dotnet-coverage</c>, (3) a generated
/// <c>appsettings.{Env}.json</c>, (4) a seeded admin, (5) the per-host mail pickup folder.
/// </summary>
/// <remarks>
/// A subclass names its app with a <see cref="SparkAppDescriptor"/> and adds what only that app needs
/// through <see cref="ConfigureAppSettings"/>, <see cref="ExtraDatabases"/> and <see cref="SeedAsync"/>.
/// </remarks>
public abstract class SparkAppTestHost : IAsyncLifetime
{
    protected SparkAppTestHost(SparkAppDescriptor app) => App = app;

    /// <summary>The app this host runs.</summary>
    public SparkAppDescriptor App { get; }

    /// <summary>
    /// The ASP.NET environment this host runs as, which also names its <c>appsettings.{Env}.json</c>
    /// override. Two hosts of the same app in one test session need different names: both write that
    /// file into the app's project directory and delete it on dispose.
    /// <para>
    /// Must never be <c>Development</c>: the framework relaxes several checks there (Data Protection key
    /// storage, replication certificates, mail fail-closed), and the host exists to prove the strict path.
    /// </para>
    /// </summary>
    public string EnvironmentName { get; init; } = "E2E";

    /// <summary>
    /// Requests per window the E2E host allows, written into its generated settings and read back by the
    /// one test that deliberately exceeds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>Every test shares one bucket.</b> The limiter partitions by remote IP and every test connects
    /// from 127.0.0.1, so one heavy test pushes UNRELATED tests into 429s. The production default of 150
    /// per 10 seconds is too small for the suite's bursts: about 25 fast API tests at ~6 requests each fill a
    /// window between them, and one browser boot is a dozen or more <c>/spark</c> calls on its own. The
    /// symptom of an exhausted bucket is non-deterministic failures, a different set each run.
    /// </para>
    /// <para>
    /// ⚠ <b>Raised, never disabled.</b> The limiter stays wired at the same pipeline position and still
    /// returns 429, so <c>RateLimitTests</c> keeps proving that the shipped app really is rate limited.
    /// Each host is its own process with its own limiter; the bucket is shared within a host, not across hosts.
    /// </para>
    /// </remarks>
    public const int RateLimitPermits = 1000;

    protected string Suffix { get; } = Guid.NewGuid().ToString("N")[..8];
    private readonly string _password = GeneratePassword();

    /// <summary>The per-host app database.</summary>
    protected string TestDatabase => $"{App.DatabasePrefix}-{Suffix}";
    private string AdminUserName => $"admin-{Suffix}";
    private string AdminEmail => $"admin-{Suffix}@e2e.local";

    /// <summary>The group (and ASP.NET Identity role) the seeded admin gets.</summary>
    protected virtual string AdminGroup => "Administrators";

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
    /// Serialises the one-time build work every host would otherwise do concurrently, across apps too.
    /// <para>
    /// xUnit runs distinct collections in parallel, so two hosts start together — and two
    /// <c>dotnet run</c>s racing to build produced <c>CS2012: cannot open … .dll for writing, being used by
    /// another process</c>. Both hosts then timed out waiting for a server that never started, which
    /// surfaced as every test in the suite failing in a millisecond. Two different apps share the
    /// framework's library outputs, so the gate is global, not per app.
    /// </para>
    /// <para>
    /// Building once behind this gate and running with <c>--no-build</c> removes the race rather than
    /// narrowing it: no amount of retrying makes two compilers safe on one output directory.
    /// </para>
    /// </summary>
    private static readonly SemaphoreSlim BuildGate = new(1, 1);
    private static readonly HashSet<string> BuiltProjects = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether to measure coverage <b>inside</b> the app subprocess. Opt-in through the
    /// <c>SPARK_E2E_HOST_COVERAGE</c> environment variable (<c>1</c> or <c>true</c>); CI sets it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>XPlat Code Coverage</c> collector instruments the test process only. The app runs as a
    /// separate <c>dotnet</c> process, so every framework line the E2E suite exercises there used to count
    /// as uncovered. With this on, the app runs under <c>dotnet-coverage collect</c> (a local tool,
    /// <c>.config/dotnet-tools.json</c>) filtered to <c>MintPlayer.Spark*</c> by
    /// <see cref="SparkAppDescriptor.CoverageSettingsFile"/>, and writes its own cobertura report under this
    /// project's <c>coverage/</c> directory, where the upload glob
    /// <c>tests/*/coverage/**/coverage.cobertura.xml</c> already finds it.
    /// </para>
    /// <para>
    /// Off by default because it costs a slower host start and a tool restore, which a local run of one
    /// test class has no use for.
    /// </para>
    /// </remarks>
    private static readonly bool HostCoverageEnabled =
        Environment.GetEnvironmentVariable("SPARK_E2E_HOST_COVERAGE") is { } flag
        && (flag == "1" || flag.Equals("true", StringComparison.OrdinalIgnoreCase));

    private string CoverageSessionId => $"spark-e2e-{EnvironmentName}-{Suffix}";
    private string? _hostCoverageReport;

    private SparkTestDriverHost? _raven;
    private Process? _appProcess;
    private string? _appUrl;
    private string? _appHttpUrl;
    private string? _mailPickupFolder;
    private string? _overrideSettingsFile;
    private readonly List<string> _temporaryFiles = new();
    private readonly List<string> _appLog = new();
    private readonly object _logLock = new();

    /// <summary>Base URL of the running app (HTTPS, self-signed).</summary>
    public string AppUrl => _appUrl ?? throw new InvalidOperationException("Host not initialized");

    /// <summary>The app's plain-http base URL.</summary>
    public string AppHttpUrl => _appHttpUrl ?? throw new InvalidOperationException("Host not initialized");

    /// <summary>URLs of the embedded Raven server, for tests that build cross-module payloads.</summary>
    public string[] RavenUrls => RavenServer.Store.Urls;
    public string AdminName => AdminUserName;
    public string AdminEmailAddress => AdminEmail;
    public string AdminPass => _password;

    /// <summary>
    /// The folder the app writes every mail into as an <c>.eml</c> file, when
    /// <see cref="SparkAppDescriptor.UsesMailPickup"/> is set. Per host, deleted on dispose.
    /// </summary>
    public string MailPickupFolder => _mailPickupFolder
        ?? throw new InvalidOperationException($"{App.AppName} is not hosted in mail pickup mode, or the host is not initialized.");

    /// <summary>The mails picked up so far, oldest first, as <c>.eml</c> file paths.</summary>
    public IReadOnlyList<string> PickedUpMails() =>
        Directory.Exists(MailPickupFolder)
            ? Directory.EnumerateFiles(MailPickupFolder, "*.eml").OrderBy(File.GetLastWriteTimeUtc).ToList()
            : [];

    /// <summary>
    /// Waits for a picked-up mail to <paramref name="to"/> whose subject contains <paramref name="subjectPart"/>.
    /// Mail is queued through Messaging, so it lands a moment after the request that caused it; the
    /// timeout is a failure bound, never an expected duration.
    /// </summary>
    public async Task<MimeKit.MimeMessage> WaitForMailAsync(string to, string subjectPart, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            foreach (var path in PickedUpMails())
            {
                MimeKit.MimeMessage message;
                try { message = await MimeKit.MimeMessage.LoadAsync(path); }
                catch (IOException) { continue; } // still being written
                if (message.To.Mailboxes.Any(m => string.Equals(m.Address, to, StringComparison.OrdinalIgnoreCase))
                    && message.Subject?.Contains(subjectPart, StringComparison.OrdinalIgnoreCase) == true)
                    return message;
            }

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"No mail to {to} with '{subjectPart}' in its subject was picked up. Mails: "
                    + string.Join(", ", PickedUpMails().Select(Path.GetFileName)) + $"\n{RecentLog(40)}");
            await Task.Delay(250);
        }
    }

    /// <summary>The embedded Raven server, for subclasses that seed extra databases.</summary>
    protected SparkTestDriverHost RavenServer => _raven ?? throw new InvalidOperationException("Host not initialized");

    /// <summary>
    /// Returns the last <paramref name="maxLines"/> lines captured from the app's stdout/stderr.
    /// Useful for surfacing server-side exception details inside an assertion failure message
    /// when the HTTP response body doesn't include them (production 500, etc.).
    /// </summary>
    public string RecentLog(int maxLines = 60)
    {
        lock (_logLock) return string.Join('\n', _appLog.TakeLast(maxLines));
    }

    /// <summary>Databases the app needs besides <see cref="TestDatabase"/>; created empty before it starts.</summary>
    protected virtual IEnumerable<string> ExtraDatabases => [];

    /// <summary>
    /// Adds the app's own entries to the generated <c>appsettings.{Env}.json</c>. The base has already set
    /// <c>Spark:RavenDb</c>, <c>Spark:DataProtection:Storage = RavenDb</c>, <c>Spark:HttpsRedirection = false</c>,
    /// <c>Spark:RateLimiter:PermitLimit</c> and (in pickup mode) <c>Spark:Mail:PickupFolder</c>; an entry set
    /// here wins. A file the app reads from its content root is registered with
    /// <see cref="RegisterTemporaryFile"/> so dispose removes it.
    /// </summary>
    protected virtual Task ConfigureAppSettings(JsonObject settings, SparkAppHostContext context) => Task.CompletedTask;

    /// <summary>Seeds app-specific data once the app is serving and the admin exists.</summary>
    protected virtual Task SeedAsync() => Task.CompletedTask;

    /// <summary>Deletes <paramref name="path"/> on dispose (best-effort).</summary>
    protected void RegisterTemporaryFile(string path) => _temporaryFiles.Add(path);

    /// <summary>Opens a store on the app database. The caller disposes it.</summary>
    protected IDocumentStore OpenAppStore()
    {
        var store = new DocumentStore { Urls = RavenServer.Store.Urls, Database = TestDatabase };
        store.Initialize();
        return store;
    }

    /// <summary>
    /// Registers an additional user and patches the Raven document so the user is email-confirmed
    /// and belongs to the given group (matching a name declared in the app's App_Data/security.json).
    /// Returns the user's document id.
    /// </summary>
    /// <param name="groupName">A group to grant by claim, or null for a plain signed-in user (QnA's users earn their groups).</param>
    /// <param name="roleName">
    /// An ASP.NET Identity <b>role</b> to grant as well as the group claim, or null for none.
    /// </param>
    /// <param name="userName">
    /// The public handle registration requires (G-Q22), or null for the email's local part (E2E local
    /// parts are unique per run). Never the email itself: a user name cannot contain <c>@</c>.
    /// </param>
    /// <remarks>
    /// ⚠️ The group claim and the role are not interchangeable, and which one a rule reads is not
    /// obvious from the outside. Fleet's <c>CarActions.CurrentUserIsAdmin</c> is
    /// <c>CurrentUser.IsInRole("Administrators")</c> — a <b>role</b> — so a user seeded with only the
    /// <c>group=Administrators</c> claim is still filtered down to rows they created. The symptom is
    /// an empty grid during setup, which reads like a broken query rather than a missing role.
    /// The seeded admin gets both, which is why it behaves as expected.
    /// </remarks>
    public async Task<string> SeedUserAsync(string email, string password, string? groupName, string? roleName = null, string? userName = null)
    {
        await RegisterAsync(email, password, userName ?? email[..email.IndexOf('@')], $"Seed register for '{email}'");

        using var appStore = OpenAppStore();

        // Registration writes the document; finding it again goes through an index, and indexes
        // are eventually consistent. Without this the lookup intermittently ran before the index
        // caught up and the test failed during *setup*, reporting a seeding error for a row-level
        // authorization case — which reads like the feature broke rather than the fixture racing.
        //
        // Waiting on the store rather than per-query (`WaitForNonStaleResults`) deliberately: the
        // per-query form has to be remembered on every query anyone adds later, and is silent when
        // forgotten. This also throws with the actual index errors if they never settle, instead
        // of leaving a mystery failure further down.
        await appStore.WaitForIndexingAsync(TestDatabase);

        using var session = appStore.OpenAsyncSession();

        var user = await session.Query<SparkUser>()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == email.ToUpperInvariant())
            ?? throw new InvalidOperationException($"Seeded user '{email}' not visible in '{TestDatabase}' after register.");

        user.EmailConfirmed = true;
        if (groupName is not null && !user.Claims.Any(c => c.ClaimType == "group" && c.ClaimValue == groupName))
            user.Claims.Add(new SparkUserClaim { ClaimType = "group", ClaimValue = groupName });

        if (roleName is not null && !user.Roles.Contains(roleName))
            user.Roles.Add(roleName);

        await session.SaveChangesAsync();
        return user.Id!;
    }

    /// <summary>
    /// Whether an ongoing RavenDB ETL task with this name exists on the app database — the real
    /// proof that a deployment took effect, as opposed to merely being authorized.
    /// <para>
    /// Requires a licence that includes the ETL feature. The repository's default licence does not;
    /// a RavenDB developer licence does. <see cref="SparkTestDriver"/> reads
    /// <c>RAVENDB_LICENSE</c> or the repo-root <c>raven-license.log</c>.
    /// </para>
    /// </summary>
    public async Task<bool> EtlTaskExistsAsync(string taskName)
    {
        using var appStore = OpenAppStore();

        var task = await appStore.Maintenance.SendAsync(
            new Raven.Client.Documents.Operations.OngoingTasks.GetOngoingTaskInfoOperation(
                taskName, Raven.Client.Documents.Operations.OngoingTasks.OngoingTaskType.RavenEtl));

        return task is not null;
    }

    /// <summary>
    /// Drops a seeded user's password hash, leaving the session it already holds intact: the shape of
    /// an account whose only way in is a passkey (or an external login), which no public endpoint
    /// produces, since every account starts with a password or a provider.
    /// </summary>
    public async Task RemovePasswordAsync(string userId)
    {
        using var appStore = OpenAppStore();
        using var session = appStore.OpenAsyncSession();
        var user = await session.LoadAsync<SparkUser>(userId)
            ?? throw new InvalidOperationException($"User '{userId}' is not stored.");
        user.PasswordHash = null;
        await session.SaveChangesAsync();
    }

    /// <summary>
    /// Point-loads a document from the app database by id. Deliberately not a query: these
    /// assertions include "this was NOT written", and an absence assertion against an
    /// eventually-consistent index passes whether or not the property holds.
    /// </summary>
    public async Task<T?> LoadAsync<T>(string documentId) where T : class
    {
        using var appStore = OpenAppStore();
        using var session = appStore.OpenAsyncSession();
        return await session.LoadAsync<T>(documentId);
    }

    /// <summary>
    /// The stored version's change vector: the etag a raw <c>/po/delete</c> or <c>/po/purge</c> must name
    /// (#467, D14). Read from the database, so it also works for a soft-deleted row.
    /// </summary>
    public async Task<string> EtagAsync(string documentId)
    {
        using var appStore = OpenAppStore();
        using var session = appStore.OpenAsyncSession();
        var document = await session.LoadAsync<object>(documentId)
            ?? throw new InvalidOperationException($"'{documentId}' is not stored, so it has no etag.");
        return session.Advanced.GetChangeVectorFor(document);
    }

    /// <summary>
    /// Waits for the app database's indexes to catch up.
    /// </summary>
    /// <remarks>
    /// A test that writes over HTTP and then reads back through a <b>query</b> needs this: queries are
    /// answered from indexes, and RavenDB indexes are eventually consistent, so the row is reliably
    /// absent for a moment after the write returns 200. Reads of a single object by id do not need it —
    /// those load the document directly.
    /// <para>
    /// The symptom without it is not a wrong value but a missing row, which reads like a broken query
    /// rather than a timing problem. <c>WaitForIndexingAsync</c> polls for non-stale with a timeout, so
    /// this is a failure bound and never a fixed sleep.
    /// </para>
    /// </remarks>
    public async Task WaitForIndexingAsync()
    {
        using var appStore = OpenAppStore();
        await appStore.WaitForIndexingAsync(TestDatabase);
    }

    public async Task InitializeAsync()
    {
        _raven = new SparkTestDriverHost();
        await _raven.InitializeAsync();

        var ravenUrls = _raven.Store.Urls;

        // Embedded Raven may persist databases across test-process invocations — wipe + recreate
        // so every run starts from a known-empty state.
        foreach (var database in new[] { TestDatabase }.Concat(ExtraDatabases))
        {
            DeleteIfExists(_raven.Store, database);
            _raven.Store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(database)));
        }

        await BuildOnceAsync(App);

        _appUrl = await StartAppAsync(ravenUrls);

        // Seed the admin via the real /register endpoint (so the password hash matches whatever
        // Identity's PasswordHasher version is configured for) and then patch the group claim
        // directly in Raven so the user is a member of the admin group.
        await SeedAdminUserAsync(ravenUrls);
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        if (HostCoverageEnabled && _appProcess is { HasExited: false })
            await StopHostCoverageAsync(_appProcess);

        if (_appProcess is { HasExited: false })
        {
            // The whole tree: `dotnet run` is a runner with the app as its child, and killing only one of
            // them leaves the app holding its DLLs, so the next build fails with MSB3027/MSB3021.
            try { _appProcess.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }

            try { await _appProcess.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token); }
            catch { /* best-effort */ }
        }
        _appProcess?.Dispose();

        foreach (var file in _overrideSettingsFile is null ? _temporaryFiles : _temporaryFiles.Prepend(_overrideSettingsFile))
        {
            if (!File.Exists(file)) continue;
            try { File.Delete(file); }
            catch { /* best-effort */ }
        }

        if (_mailPickupFolder is not null && Directory.Exists(_mailPickupFolder))
        {
            try { Directory.Delete(_mailPickupFolder, recursive: true); }
            catch { /* best-effort */ }
        }

        if (_raven is not null)
            await _raven.DisposeAsync();
    }

    /// <summary>
    /// Ends the coverage session so the hits the app has collected are written to its report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>A kill loses the report.</b> The hits live in the instrumented process until the
    /// collector asks for them, and <c>Kill(entireProcessTree)</c> takes the collector down with
    /// the app, so nothing is ever written. <c>dotnet-coverage shutdown</c> is the graceful path, and
    /// it works the same on Windows and Linux, which a Ctrl+C or SIGTERM would not: Windows has no
    /// way to send a console signal to one child without also hitting this test process.
    /// </para>
    /// <para>
    /// Measured 2026-09-27: shutdown fetches the hits from the process while it is still running,
    /// writes the report, ends the process, and the collector then exits by itself, all in about
    /// half a second. Both waits here are failure bounds, not expected durations. If either one
    /// runs out, the caller's kill is still the fallback, and the missing report is caught by
    /// <c>tools/verify-coverage-paths.mjs</c>.
    /// </para>
    /// </remarks>
    private async Task StopHostCoverageAsync(Process collector)
    {
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = FindRepoRoot() };
        foreach (var arg in new[] { "tool", "run", "dotnet-coverage", "shutdown", CoverageSessionId, "--nologo" })
            psi.ArgumentList.Add(arg);

        try
        {
            var (exitCode, output) = await RunToCompletionAsync(psi, TimeSpan.FromMinutes(2));
            if (exitCode != 0)
                lock (_logLock) _appLog.Add($"[coverage] shutdown exited {exitCode}: {output}");

            await collector.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(1)).Token);
        }
        catch (Exception ex)
        {
            lock (_logLock) _appLog.Add($"[coverage] graceful shutdown failed, falling back to kill: {ex.Message}");
        }

        if (_hostCoverageReport is not null && !File.Exists(_hostCoverageReport))
            lock (_logLock) _appLog.Add($"[coverage] no report was written at {_hostCoverageReport}");
    }

    /// <summary>
    /// Registers through the real endpoint with the typed client. Since #460 (M5) register requires an
    /// antiforgery token like every mutating account route; a bare POST is refused with an empty 400.
    /// The client warms up for the token, as a browser holding the app would already have it.
    /// </summary>
    private async Task RegisterAsync(string email, string password, string userName, string failurePrefix)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var client = new SparkClient(new HttpClient(handler) { BaseAddress = new Uri(AppUrl) }, ownsClient: true);
        try
        {
            await client.RegisterAsync(email, password, userName);
        }
        catch (SparkClientException ex)
        {
            throw new InvalidOperationException($"{failurePrefix} failed ({(int)ex.StatusCode}): {ex.Message}", ex);
        }
    }

    private async Task SeedAdminUserAsync(string[] ravenUrls)
    {
        // Register via the public endpoint so the password hash is compatible with whatever
        // PasswordHasher version the app's Identity is configured with.
        await RegisterAsync(AdminEmail, _password, AdminUserName, "Register");

        // Now patch the stored user: mark email confirmed + add the admin group claim and role.
        using var appStore = OpenAppStore();

        using var session = appStore.OpenAsyncSession();
        var user = await session.Query<SparkUser>()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == AdminEmail.ToUpperInvariant());

        if (user == null)
        {
            var databases = RavenServer.Store.Maintenance.Server.Send(new GetDatabaseNamesOperation(0, 50));
            string dump = "";
            foreach (var dbName in databases)
            {
                using var s = RavenServer.Store.OpenAsyncSession(dbName);
                var users = await s.Query<SparkUser>().Take(5).ToListAsync();
                dump += $"\n  embedded db='{dbName}': {users.Count} user(s) [{string.Join(", ", users.Select(u => u.Email))}]";
            }
            throw new InvalidOperationException($"Registered user '{AdminEmail}' not found in embedded '{TestDatabase}'. Embedded URLs: [{string.Join(",", ravenUrls)}]. DBs:{dump}");
        }

        user.EmailConfirmed = true;
        if (!user.Claims.Any(c => c.ClaimType == "group" && c.ClaimValue == AdminGroup))
            user.Claims.Add(new SparkUserClaim { ClaimType = "group", ClaimValue = AdminGroup });
        if (!user.Roles.Contains(AdminGroup))
            user.Roles.Add(AdminGroup);

        await session.SaveChangesAsync();
    }

    /// <summary>
    /// Whether the built Angular bundle is older than any source that goes into it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>This used to ask only whether <c>dist/</c> existed and was non-empty</b> — which meant a
    /// bundle built at any point in the past was accepted forever. That is not a stale-cache
    /// inconvenience; it silently removes the client from the test. Every Playwright assertion then
    /// runs the *old* frontend against the *new* server and reports whatever mismatch that produces as
    /// a product failure.
    /// </para>
    /// <para>
    /// It cost real time when the route table moved: the grid tests failed because a bundle from
    /// earlier the same day still issued <c>GET /spark/queries/{id}/execute</c>, which no longer
    /// exists. The symptom — a grid that renders its chrome and never its rows — looks exactly like a
    /// broken query, and nothing anywhere said "this frontend is not the one you just changed".
    /// </para>
    /// <para>
    /// The comparison is deliberately crude: newest source timestamp against oldest output timestamp,
    /// over the client's own sources and the libraries it consumes from source. Crude in the safe
    /// direction — it rebuilds when unsure, and the alternative is being wrong in the direction that
    /// hides a regression.
    /// </para>
    /// </remarks>
    private static bool IsAngularBundleStale(IEnumerable<string> sourceRoots, string distPath)
    {
        if (!Directory.Exists(distPath))
            return true;

        var outputs = Directory.EnumerateFiles(distPath, "*", SearchOption.AllDirectories).ToArray();
        if (outputs.Length == 0)
            return true;

        var builtAt = outputs.Min(f => File.GetLastWriteTimeUtc(f));

        foreach (var root in sourceRoots)
        {
            if (!Directory.Exists(root))
                continue;

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                // The libraries' own build outputs are not inputs; including them would make every
                // bundle look stale forever.
                if (file.Contains($"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                if (File.GetLastWriteTimeUtc(file) > builtAt)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the app and its Angular bundle exactly once per test process, whichever host asks first.
    /// </summary>
    private static async Task BuildOnceAsync(SparkAppDescriptor app)
    {
        var repoRoot = FindRepoRoot();
        var project = Path.Combine(repoRoot, app.ProjectDirectory, app.ProjectFileName);

        await BuildGate.WaitAsync();
        try
        {
            if (BuiltProjects.Contains(project))
                return;

            await EnsureAngularBundleAsync(app, repoRoot);

            // Opt-in, LOCAL ONLY: `npm run test:local:e2e` sets this after `nx run-many -t build` has
            // just built every app, so a second `dotnet build` per app is pure overhead. Unset (CI and
            // a plain `nx test`) keeps the build below. Skipping with a stale bin/ runs the stale app,
            // so set it only right after building.
            if (Environment.GetEnvironmentVariable("SPARK_E2E_SKIP_APP_BUILD") == "1")
            {
                Console.Error.WriteLine($"[SparkAppTestHost] SPARK_E2E_SKIP_APP_BUILD=1: skipping `dotnet build` of {app.AppName}; using the existing bin/ output.");
                BuiltProjects.Add(project);
                return;
            }

            // --disable-build-servers: no reused MSBuild node, compiler server or MSBuild server. Those
            // outlive `dotnet build` and inherit its redirected stdout, so the pipe never reaches EOF and
            // RunToCompletionAsync waited on it until they idled out (~15 min) — measured in M14 as a
            // local E2E run stuck after the QnA collection, with Fleet's build long finished.
            var psi = new ProcessStartInfo("dotnet", $"build \"{project}\" --configuration Debug --disable-build-servers");
            psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            var (exitCode, output) = await RunToCompletionAsync(psi, TimeSpan.FromMinutes(10));
            if (exitCode != 0)
                throw new InvalidOperationException($"Building {app.AppName} failed (exit {exitCode}).\n{output}");

            BuiltProjects.Add(project);
        }
        finally
        {
            BuildGate.Release();
        }
    }

    private static async Task EnsureAngularBundleAsync(SparkAppDescriptor app, string repoRoot)
    {
        var clientApp = Path.Combine(repoRoot, app.ProjectDirectory, app.ClientAppDirectory);
        var distPath = Path.Combine(clientApp, app.BundleOutputDirectory);
        var sourceRoots = app.BundleSourceRoots
            .Select(r => Path.Combine(repoRoot, r))
            .Prepend(Path.Combine(clientApp, "src"));
        if (!IsAngularBundleStale(sourceRoots, distPath))
            return;

        // ⚠️ On Windows npm is a .cmd, and handing its bare name to ProcessStartInfo is not the
        // same as running it from a shell: cmd.exe ends up with a relative %0, so %~dp0 resolves to
        // the working directory and npm.cmd then hunts for its own internals under
        // the ClientApp's own node_modules/npm/ — which does not exist. The failure is a
        // MODULE_NOT_FOUND for npm-prefix.js that reads like a broken install,
        // and it takes the whole suite down in fixture startup, before a single test body runs.
        //
        // Route it through `cmd /c` so cmd does the PATH search and launches npm.cmd by absolute
        // path. This is what MintPlayer.AspNetCore.SpaServices' own NodeScriptRunner does, for the
        // same reason and with the same comment.
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd", "/c npm run build") { WorkingDirectory = clientApp }
            : new ProcessStartInfo("npm", "run build") { WorkingDirectory = clientApp };
        // `npm run build` delegates to an nx build of the app. On CI that is a NESTED nx invocation
        // inside the outer `nx affected --target=test` process; sharing the outer run's daemon and
        // remote cache from a test subprocess is a lock-contention hang waiting to happen, so opt this
        // child out of both.
        psi.Environment["NX_DAEMON"] = "false";
        psi.Environment["NX_SELF_HOSTED_REMOTE_CACHE_SERVER"] = "";
        psi.Environment["NX_SELF_HOSTED_REMOTE_CACHE_ACCESS_TOKEN"] = "";

        var (exitCode, output) = await RunToCompletionAsync(psi, TimeSpan.FromMinutes(10));
        if (exitCode != 0)
            throw new InvalidOperationException($"npm run build for {app.AppName} failed (exit {exitCode}).\n{output}");
    }

    /// <summary>
    /// Runs a process to completion, draining stdout and stderr <b>concurrently</b> and killing the
    /// whole tree on timeout. The previous sequential <c>ReadToEndAsync</c> pattern deadlocked:
    /// once the child filled the stderr pipe buffer while the parent was still awaiting stdout EOF,
    /// child and parent blocked each other forever — which on CI surfaced as the test run hanging
    /// with no output at all.
    /// </summary>
    protected static async Task<(int ExitCode, string Output)> RunToCompletionAsync(ProcessStartInfo psi, TimeSpan timeout)
    {
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{psi.FileName} {psi.Arguments}'");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }
            throw new TimeoutException(
                $"'{psi.FileName} {psi.Arguments}' did not finish within {timeout.TotalMinutes:0} minutes.\n"
                + $"stdout so far: {await stdoutTask}\nstderr so far: {await stderrTask}");
        }

        // The process has exited, but a grandchild that inherited the pipes (a build server) can keep
        // them open indefinitely. The exit code is what decides; the output is diagnostics, so wait for
        // it only briefly rather than for the grandchild's lifetime.
        var drained = Task.WhenAll(stdoutTask, stderrTask);
        if (await Task.WhenAny(drained, Task.Delay(TimeSpan.FromSeconds(30))) != drained)
            return (proc.ExitCode, "(output not drained: a child process still holds the pipes)");
        return (proc.ExitCode, $"stdout: {await stdoutTask}\nstderr: {await stderrTask}");
    }

    /// <summary>
    /// Starts the app, on fresh ports again when the ones picked were taken before it could bind them.
    /// GetFreeTcpPort releases its port before the app binds it, so in a parallel sweep another process
    /// (an embedded RavenDB, another host) can take it first and the app exits with "address already in
    /// use". That used to fail the whole collection, after a 120 s wait for a process that had exited.
    /// </summary>
    private async Task<string> StartAppAsync(string[] ravenUrls)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await StartAppOnceAsync(ravenUrls);
            }
            catch (PortTakenException) when (attempt < 3)
            {
                _appProcess?.Dispose();
                _appProcess = null;
                lock (_logLock) _appLog.Clear();
            }
        }
    }

    private sealed class PortTakenException(string message, Exception inner) : Exception(message, inner);

    private async Task<string> StartAppOnceAsync(string[] ravenUrls)
    {
        var httpsPort = GetFreeTcpPort();
        var httpPort = GetFreeTcpPort();
        var httpsUrl = $"https://localhost:{httpsPort}";
        _appHttpUrl = $"http://localhost:{httpPort}";

        var repoRoot = FindRepoRoot();
        var appDir = Path.Combine(repoRoot, App.ProjectDirectory);
        var appProject = Path.Combine(appDir, App.ProjectFileName);

        // ASP.NET Core reads appsettings.{Environment}.json from the content root. By default
        // that's `Directory.GetCurrentDirectory()` — i.e. the working directory of the app
        // process, which we set below to appDir (the project source dir). So the override
        // file must sit next to appDir/appsettings.json. DisposeAsync cleans it up.
        _overrideSettingsFile = Path.Combine(appDir, $"appsettings.{EnvironmentName}.json");

        var spark = new JsonObject
        {
            ["RavenDb"] = new JsonObject
            {
                ["Urls"] = new JsonArray(ravenUrls[0]),
                ["Database"] = TestDatabase,
                ["EnsureDatabaseCreated"] = true,
            },
            // #460 D5: the app's base appsettings no longer choose a key store, and outside Development
            // Spark refuses to start without one. The key ring lives in the per-host database.
            ["DataProtection"] = new JsonObject { ["Storage"] = "RavenDb" },
            ["HttpsRedirection"] = false,
            ["RateLimiter"] = new JsonObject { ["PermitLimit"] = RateLimitPermits },
            // The identity provider's machine endpoints have a budget of their own (PRD D9); inert in apps
            // that do not host the identity provider.
            ["IdentityProvider"] = new JsonObject { ["RateLimits"] = new JsonObject { ["PermitLimit"] = RateLimitPermits } },
        };

        if (App.UsesMailPickup)
        {
            // #460 M8: in pickup mode every mail is an .eml file. An absolute path wins over the content
            // root, so each host gets its own folder instead of all of them writing into the project.
            _mailPickupFolder = Path.Combine(Path.GetTempPath(), "spark-e2e-mail", $"{App.CoverageSlug}-{EnvironmentName}-{Suffix}");
            Directory.CreateDirectory(_mailPickupFolder);
            spark["Mail"] = new JsonObject { ["PickupFolder"] = _mailPickupFolder };
        }

        var settings = new JsonObject { ["Spark"] = spark };
        await ConfigureAppSettings(settings, new SparkAppHostContext(httpsUrl, _appHttpUrl, httpPort, ravenUrls, appDir));
        await File.WriteAllTextAsync(_overrideSettingsFile, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // WorkingDirectory=appDir so (a) ASP.NET Core's ContentRoot resolves to the project source,
        // making appsettings.{env}.json + ClientApp/dist/ paths work, and (b) `--no-launch-profile`
        // keeps launchSettings.json from overriding our ASPNETCORE_URLS / ENVIRONMENT.
        string[] runArgs = ["run", "--project", appProject, "--configuration", "Debug", "--no-build", "--no-launch-profile"];
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = appDir,
        };

        if (HostCoverageEnabled)
        {
            // The same `dotnet run`, wrapped. dotnet-coverage follows child processes, so the
            // runner's app child is measured too; the module filter in the settings keeps both the
            // runner's own assemblies and the app's out of the report. One report per host, because
            // hosts run concurrently and each session ends in its own DisposeAsync.
            var projectDir = Path.Combine(repoRoot, "tests", "MintPlayer.Spark.E2E.Tests");
            _hostCoverageReport = Path.Combine(projectDir, "coverage", $"{App.CoverageSlug}-host-{EnvironmentName}-{Suffix}", "coverage.cobertura.xml");
            // "This host's tests ran": tools/verify-coverage-paths.mjs requires a host report only for
            // the apps that wrote this marker, so a run that filters an app's tests out does not fail
            // while a host that started and then lost its report still does.
            var reportDir = Path.GetDirectoryName(_hostCoverageReport)!;
            Directory.CreateDirectory(reportDir);
            File.WriteAllText(Path.Combine(reportDir, "host-started.txt"), $"{App.AppName} {EnvironmentName} {DateTimeOffset.UtcNow:O}");
            string[] collectArgs =
            [
                "tool", "run", "dotnet-coverage", "collect",
                "--session-id", CoverageSessionId,
                "--settings", Path.Combine(projectDir, App.CoverageSettingsFile),
                "--output-format", "cobertura",
                "--output", _hostCoverageReport,
                "--nologo",
                "--", "dotnet",
            ];
            foreach (var arg in collectArgs) psi.ArgumentList.Add(arg);
        }

        foreach (var arg in runArgs) psi.ArgumentList.Add(arg);
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = EnvironmentName;
        psi.Environment["ASPNETCORE_URLS"] = $"{httpsUrl};http://localhost:{httpPort}";

        _appProcess = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start the {App.AppName} process");

        // Both pipes are drained for the process's whole life. An unread pipe fills its buffer and then
        // blocks the child on its next write — on CI that was a hang with no output at all.
        _appProcess.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) lock (_logLock) _appLog.Add("[out] " + e.Data);
        };
        _appProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) lock (_logLock) _appLog.Add("[err] " + e.Data);
        };
        _appProcess.BeginOutputReadLine();
        _appProcess.BeginErrorReadLine();

        try
        {
            await WaitForReadyAsync(httpsUrl);
        }
        catch (TimeoutException ex)
        {
            string dump;
            bool portTaken;
            // An exited process's last lines may still be in the pipes; WaitForExit drains them.
            if (_appProcess.HasExited)
                _appProcess.WaitForExit();
            lock (_logLock)
            {
                dump = string.Join('\n', _appLog.TakeLast(120));
                portTaken = _appProcess.HasExited
                    && _appLog.Any(l => l.Contains("address already in use", StringComparison.OrdinalIgnoreCase));
            }
            var message = $"{ex.Message}\n\n--- {App.AppName} process output (last 120 lines) ---\n{dump}";
            if (portTaken)
                throw new PortTakenException(message, ex);
            throw new TimeoutException(message, ex);
        }
        return httpsUrl;
    }

    private async Task WaitForReadyAsync(string baseUrl)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            // An exited process never becomes ready: say so now, not after the full wait.
            if (_appProcess is { HasExited: true } exited)
                throw new TimeoutException($"{App.AppName} exited with code {exited.ExitCode} before it became ready at {baseUrl}");

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
        throw new TimeoutException($"{App.AppName} did not become ready at {baseUrl} within 120s");
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

    protected static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "MintPlayer.Spark.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate MintPlayer.Spark.slnx starting from " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// Exposes the protected <see cref="SparkTestDriver.Store"/> so <see cref="SparkAppTestHost"/>
/// can seed into the embedded Raven. Inheriting a non-test-class type keeps xUnit from
/// picking up this file's base class.
/// </summary>
public sealed class SparkTestDriverHost : SparkTestDriver
{
    public new IDocumentStore Store => base.Store;
}
