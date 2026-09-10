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
    /// could not be provisioned — in which case callers should leave <c>ServerDirectory</c> unset
    /// and let RavenDB fall back to <c>AppContext.BaseDirectory</c>.
    /// </summary>
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

        if (File.Exists(Path.Combine(target, CompletionMarker)))
            return target;

        try
        {
            // Copy into a private staging directory and move it into place, so a crashed or
            // concurrent run can never leave a partial tree that a later run would trust.
            var staging = $"{target}.staging-{Environment.ProcessId}";
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);

            CopyDirectory(source, staging);
            File.WriteAllText(Path.Combine(staging, CompletionMarker), version);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                Directory.Move(staging, target);
            }
            catch (IOException) when (File.Exists(Path.Combine(target, CompletionMarker)))
            {
                // Another process finished first. Its copy is as good as ours.
                Directory.Delete(staging, recursive: true);
            }

            return File.Exists(Path.Combine(target, CompletionMarker)) ? target : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall back to RavenDB's default rather than failing every test on a disk problem.
            return null;
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
