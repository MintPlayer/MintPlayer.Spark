using System.Diagnostics;

namespace SeoTester.Tests.Rendering;

/// <summary>
/// The download proof's scan (<see cref="MarkerFiles"/>) opens regular files only (<c>docs/seo_analyzer_PRD.md</c> §12,
/// "CI stall"). On Linux every .NET process keeps FIFOs in the temp folder, and opening one for reading waits for a
/// writer that never comes: the scan read such a FIFO and stalled the SeoTester.Tests job until its 20-minute timeout.
/// </summary>
public sealed class MarkerFilesTests
{
    /// <summary>
    /// A FIFO named like CoreCLR's debugger pipe and a link to it sit beside a regular file that holds the marker: the scan
    /// finishes and finds that file only. Red before the fix: the scan opened the FIFO and never returned.
    /// </summary>
    [LinuxFact]
    public async Task The_scan_opens_no_fifo_and_no_link()
    {
        var marker = "scan-marker-" + Guid.NewGuid().ToString("N");
        var started = DateTime.UtcNow.AddSeconds(-2);
        var dir = Directory.CreateTempSubdirectory("seo-scan-");
        try
        {
            // The FIFO's path is longer than the marker: the link to it passes the size rule, so only the link rule stops it.
            var fifo = Path.Combine(dir.FullName, $"clr-debug-pipe-{Environment.ProcessId}-1234567890123-in");
            using (var mkfifo = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { fifo } })!)
            {
                await mkfifo.WaitForExitAsync();
                mkfifo.ExitCode.Should().Be(0);
            }
            File.CreateSymbolicLink(Path.Combine(dir.FullName, "linked-pipe"), fifo);
            var saved = Path.Combine(dir.FullName, "saved.bin");
            await File.WriteAllTextAsync(saved, marker + " body");

            var scan = Task.Run(() => MarkerFiles.Holding(marker, started, [(dir.FullName, SearchOption.TopDirectoryOnly)]));
            // A failure bound only: the scan takes milliseconds. Before the fix it never returned.
            (await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(10))) == scan).Should().BeTrue("the scan opens no FIFO, which waits for a writer");
            (await scan).Should().Equal(saved);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private sealed class LinuxFactAttribute : FactAttribute
    {
        public LinuxFactAttribute()
        {
            if (!OperatingSystem.IsLinux())
                Skip = "Linux only: there every .NET process keeps FIFOs in the temp folder (CoreCLR's debugger pipes). Windows has no FIFO in its file system.";
        }
    }
}
