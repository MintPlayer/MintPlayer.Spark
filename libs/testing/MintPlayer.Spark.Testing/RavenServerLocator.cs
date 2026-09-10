using System.Reflection;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// Provides the embedded RavenDB server from a location outside the project's output directory,
/// so its 623 MB of binaries never become a build artifact.
/// </summary>
/// <remarks>
/// <para>
/// <c>RavenDB.Embedded</c> ships a <b>623 MB</b> <c>RavenDBServer</c> tree and copies it into
/// <c>$(OutDir)</c> on every build, by two routes: NuGet <c>contentFiles</c> marked
/// <c>copyToOutput</c>, and its own <c>CopyRavenDBServer</c> target. Nx declares
/// <c>{projectRoot}/bin/Debug</c> as a cached output, so every referencing project turned into a
/// ~600 MB cache artifact. Uploading those to the self-hosted cache aborted with an nginx 499, and
/// Nx escalates a cache failure into a task failure, which took the whole test sweep down with it.
/// Both copy routes are suppressed in <c>Directory.Build.targets</c>.
/// </para>
/// <para>
/// The server directory has to be <b>writable</b>: measured on 2026-09-10, starting the embedded
/// server creates a <c>Temp</c> subdirectory next to the binaries. So this cannot simply point at
/// the restored NuGet package — that would write into a shared, content-addressed package store
/// used by every solution on the machine. Instead the binaries are copied once into a temp
/// directory and reused from there.
/// </para>
/// <para>
/// The copy happens at run time rather than in MSBuild on purpose. The test task runs
/// <c>dotnet test</c> in no-build mode, so on an Nx build cache hit MSBuild never executes — a
/// build-time copy would simply be missing. Provisioning on first use is self-healing: it costs
/// one local file copy per machine per package version, and nothing at all afterwards.
/// </para>
/// <para>
/// The path is version-scoped because projects in this repository pin different
/// <c>RavenDB.TestDriver</c> versions, and two server builds must not share a directory.
/// </para>
/// </remarks>
public static class RavenServerLocator
{
    /// <summary>Written once a copy has fully completed, so a half-finished copy is never reused.</summary>
    private const string CompletionMarker = ".spark-provisioned";

    private static readonly Lazy<string?> _serverDirectory = new(Provision, isThreadSafe: true);

    /// <summary>
    /// A writable directory holding the RavenDB server binaries, or <see langword="null"/> when it
    /// could not be provisioned.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> leaves RavenDB on its default of <c>AppContext.BaseDirectory</c>.
    /// Note that this is <em>not</em> a working fallback here — the build no longer copies the
    /// server into the output directory, so that path is empty and the server will fail to start.
    /// It is returned rather than thrown so the failure surfaces from RavenDB's own start-up,
    /// where the message names the missing directory, instead of from a static constructor as a
    /// <c>TypeInitializationException</c>. If tests fail with the server not found, provisioning
    /// is what to investigate.
    /// </remarks>
    public static string? ServerDirectory => _serverDirectory.Value;

    private static string? Provision()
    {
        var version = ReadEmbeddedPackageVersion();
        if (string.IsNullOrEmpty(version))
            return null;

        var source = Path.Combine(
            NuGetPackageRoot(), "ravendb.embedded", version, "contentFiles", "any", "any", "RavenDBServer");

        if (!File.Exists(Path.Combine(source, "Raven.Server.dll")))
            return null;

        var target = Path.Combine(Path.GetTempPath(), "MintPlayer.Spark", "RavenDBServer", version);

        if (IsProvisioned(target))
            return target;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            return ProvisionOnce(source, target, version) ? target : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsProvisioned(string target)
        => File.Exists(Path.Combine(target, CompletionMarker));

    /// <summary>
    /// Copies the server exactly once across all processes racing for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three test projects resolve the same RavenDB version through
    /// <c>MintPlayer.Spark.Testing</c>, and <c>nx run-many</c> runs them as concurrent processes.
    /// Without a lock, a cold machine copies 623 MB once per process — measured as roughly 1.9 GB
    /// of simultaneous I/O, enough to starve timing-sensitive E2E tests into failing.
    /// </para>
    /// <para>
    /// The lock is a file rather than a named mutex because named mutexes are Windows-only in
    /// .NET, and CI runs on Linux. <see cref="FileOptions.DeleteOnClose"/> releases it even if the
    /// holder is killed, so a crash cannot wedge every later run.
    /// </para>
    /// </remarks>
    private static bool ProvisionOnce(string source, string target, string version)
    {
        var lockPath = $"{target}.lock";
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);

        while (true)
        {
            if (IsProvisioned(target))
                return true;

            FileStream? held = null;
            try
            {
                held = new FileStream(
                    lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                // Someone else is provisioning.
            }

            if (held is not null)
            {
                using (held)
                {
                    // Re-check: the winner may have finished between our first check and the lock.
                    if (IsProvisioned(target))
                        return true;

                    CopyIntoPlace(source, target, version);
                    CleanStaleStaging(target);
                    return IsProvisioned(target);
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                // The holder died or wedged. Do the copy ourselves rather than fail: wasteful, but
                // the staged publish below means a duplicate copy is safe.
                CopyIntoPlace(source, target, version);
                return IsProvisioned(target);
            }

            Thread.Sleep(500);
        }
    }

    /// <summary>
    /// Copies into a private staging directory and moves it into place, so no other process can
    /// observe a partial tree: the completion marker only becomes visible through the rename.
    /// </summary>
    private static void CopyIntoPlace(string source, string target, string version)
    {
        var staging = $"{target}.staging-{Environment.ProcessId}";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);

        CopyDirectory(source, staging);
        File.WriteAllText(Path.Combine(staging, CompletionMarker), version);

        try
        {
            Directory.Move(staging, target);
        }
        catch (IOException)
        {
            // Another process published first, or the destination is otherwise occupied. Its copy
            // is as good as ours; keep whichever is actually complete.
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Removes staging directories abandoned by a process that died mid-copy.</summary>
    private static void CleanStaleStaging(string target)
    {
        var parent = Path.GetDirectoryName(target);
        if (parent is null)
            return;

        var prefix = $"{Path.GetFileName(target)}.staging-";
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);

        foreach (var directory in Directory.GetDirectories(parent, $"{prefix}*"))
        {
            try
            {
                if (Directory.GetCreationTimeUtc(directory) < cutoff)
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another process may own it; leaving a stale directory is harmless.
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private static string NuGetPackageRoot()
    {
        var configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(configured))
            return configured;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }

    /// <summary>
    /// Reads the package version out of the test assembly's <c>.deps.json</c>. That file is small,
    /// identical on every machine, and already sits next to the assembly, so unlike an absolute
    /// path it survives an Nx cache replay onto a different machine intact.
    /// </summary>
    private static string? ReadEmbeddedPackageVersion()
    {
        foreach (var depsFile in EnumerateDepsFiles())
        {
            string content;
            try
            {
                content = File.ReadAllText(depsFile);
            }
            catch (IOException)
            {
                continue;
            }

            var match = Regex.Match(content, "\"RavenDB\\.Embedded/([^\"]+)\"", RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups[1].Value;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateDepsFiles()
    {
        var baseDirectory = AppContext.BaseDirectory;

        // Prefer the entry assembly's own deps.json; under a test runner the entry assembly may be
        // the runner itself, so fall back to scanning the directory.
        var entryName = Assembly.GetEntryAssembly()?.GetName().Name;
        if (!string.IsNullOrEmpty(entryName))
        {
            var preferred = Path.Combine(baseDirectory, $"{entryName}.deps.json");
            if (File.Exists(preferred))
                yield return preferred;
        }

        string[] others;
        try
        {
            others = Directory.GetFiles(baseDirectory, "*.deps.json");
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }

        foreach (var file in others)
            yield return file;
    }
}
