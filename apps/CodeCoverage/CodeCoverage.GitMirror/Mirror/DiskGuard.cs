using Microsoft.Extensions.Options;

namespace CodeCoverage.GitMirror.Mirror;

/// <summary>
/// The host free-space floor (D8, D20): hard, enforced here before every clone and fetch. The total mirror budget is soft
/// and the app's job (D20), because a clone's size is unknown until it finishes.
/// The volume is a named Docker volume on the root disk, so the numbers are the host's (statfs through DriveInfo, §9.2).
/// </summary>
public sealed class DiskGuard(IOptions<GitMirrorOptions> options, MirrorPaths paths)
{
    public long MinFreeBytes => options.Value.MinFreeBytes;

    public long FreeBytes => Drive().AvailableFreeSpace;

    public long TotalBytes => Drive().TotalSize;

    public bool BelowFloor() => FreeBytes < MinFreeBytes;

    private DriveInfo Drive()
    {
        Directory.CreateDirectory(paths.Root);
        return new DriveInfo(paths.Root);
    }
}
