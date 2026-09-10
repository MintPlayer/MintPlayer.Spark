using MintPlayer.Spark.Testing;
using Xunit;

namespace MintPlayer.Spark.Tests.Testing;

/// <summary>
/// The provisioning contract for the embedded RavenDB server.
/// <para>
/// The server is deliberately no longer copied into <c>bin/</c> — it is a 623 MB build output and
/// Nx caches <c>bin/Debug</c>. See <c>docs/guide-ravendb-test-server.md</c>.
/// </para>
/// </summary>
public class RavenServerLocatorTests
{
    [Fact]
    public void Provisions_a_directory_that_actually_contains_the_server()
    {
        var directory = RavenServerLocator.ServerDirectory;

        // Null would mean RavenDB falls back to AppContext.BaseDirectory, which no longer holds a
        // server — so every RavenDB test in the suite would fail. If this assertion fires, that is
        // the cause, not whichever test happens to report first.
        directory.Should().NotBeNull();
        Directory.Exists(directory).Should().BeTrue();
        File.Exists(Path.Combine(directory!, "Raven.Server.dll")).Should().BeTrue();
    }

    [Fact]
    public void The_server_is_not_copied_into_the_output_directory()
    {
        // Guards the actual defect: if a future change re-enables either copy route, bin/ grows by
        // 623 MB and every Nx cache upload for this project starts failing with a 499.
        //
        // Asserts on the payload rather than the directory. A working tree that predates this
        // change can be left with an empty RavenDBServer skeleton — measured here as 80 empty
        // directories and no files — which is harmless and which MSBuild now deletes anyway.
        // Failing on that would report residue as a regression.
        var copied = Path.Combine(AppContext.BaseDirectory, "RavenDBServer", "Raven.Server.dll");

        File.Exists(copied).Should().BeFalse();
    }

    [Fact]
    public void Resolution_is_stable_across_calls()
    {
        // Provisioning must be idempotent: the second call reuses the completed copy rather than
        // re-copying 623 MB.
        RavenServerLocator.ServerDirectory.Should().Be(RavenServerLocator.ServerDirectory);
    }

    [Fact]
    public void Leaves_no_lock_or_staging_directory_behind()
    {
        var directory = RavenServerLocator.ServerDirectory;
        directory.Should().NotBeNull();

        var parent = Path.GetDirectoryName(directory!)!;
        var name = Path.GetFileName(directory!);

        File.Exists($"{directory}.lock").Should().BeFalse();
        Directory.GetDirectories(parent, $"{name}.staging-*").Length.Should().Be(0);
    }

    [Fact]
    public void The_directory_is_writable_because_the_server_writes_next_to_its_binaries()
    {
        // Measured: the embedded server creates a Temp directory alongside its own binaries at
        // startup. That is why this cannot simply point at the restored NuGet package, which is a
        // content-addressed store shared by every solution on the machine.
        var directory = RavenServerLocator.ServerDirectory;
        directory.Should().NotBeNull();

        var probe = Path.Combine(directory!, $".write-probe-{Guid.NewGuid():N}");
        File.WriteAllText(probe, "probe");
        try
        {
            File.Exists(probe).Should().BeTrue();
        }
        finally
        {
            File.Delete(probe);
        }
    }
}
