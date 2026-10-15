using System.Text;

namespace SeoTester.Tests.Rendering;

/// <summary>Finds the files that hold a marker a test site served (the download proof, <see cref="BrowserStateTests"/>).</summary>
internal static class MarkerFiles
{
    /// <summary>Nothing a test page serves comes near this size: a bigger file is not read.</summary>
    private const long MaxRead = 1024 * 1024;

    /// <summary>
    /// The files in <paramref name="places"/> written since <paramref name="since"/> whose name or bytes hold
    /// <paramref name="marker"/>, hidden ones included.
    /// <para>
    /// Only regular files are opened (<c>docs/seo_analyzer_PRD.md</c> §12, "CI stall"). Opening a FIFO for reading waits
    /// for a writer, which may never come: on Linux every .NET process keeps two in the temp folder (CoreCLR's debugger
    /// pipes, <c>clr-debug-pipe-&lt;pid&gt;-…-in</c> and <c>-out</c>), and reading one stalled the SeoTester.Tests job until its
    /// 20-minute timeout. .NET has no API that tells a FIFO, a socket or a device from a regular file:
    /// <see cref="FileSystemInfo.Attributes"/> is <c>Normal</c> for all of them, <see cref="UnixFileMode"/> holds only the
    /// permission bits, and whatever looks closer opens the entry first, which is the call that waits. What .NET does
    /// report is the size, and those entries report 0. So an entry is opened only when it can hold the marker and is no
    /// link: a link reports the length of its target's path, and opening it opens the target, whatever that is.
    /// </para>
    /// </summary>
    public static List<string> Holding(string marker, DateTime since, IEnumerable<(string Dir, SearchOption Depth)> places)
    {
        var bytes = Encoding.UTF8.GetBytes(marker);
        var found = new List<string>();
        foreach (var (dir, depth) in places)
        {
            IEnumerable<string> candidates;
            try
            {
                // AttributesToSkip 0: the default skips hidden files, a dot-file on Linux.
                candidates = Directory.EnumerateFiles(dir, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = depth == SearchOption.AllDirectories,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0,
                });
            }
            catch (IOException)
            {
                continue;
            }
            foreach (var file in candidates)
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc < since && info.CreationTimeUtc < since)
                        continue;
                    if (info.Name.Contains(marker, StringComparison.Ordinal)
                        || (!info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.Length >= bytes.Length && info.Length <= MaxRead
                            && Holds(file, info.Length, bytes)))
                        found.Add(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use by another process, or gone already: not ours.
                }
            }
        }
        return found;
    }

    /// <summary>Reads <paramref name="file"/> without denying its writer anything: the browser may still be at work on it.</summary>
    private static bool Holds(string file, long length, byte[] marker)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[length];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return buffer.AsSpan(0, read).IndexOf(marker) >= 0;
    }
}
