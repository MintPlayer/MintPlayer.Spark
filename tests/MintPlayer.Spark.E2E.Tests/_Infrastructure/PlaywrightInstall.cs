namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// Installs Chromium (and, on Linux, its system packages) once per test process.
/// </summary>
/// <remarks>
/// The Fleet and QnA collections initialise in parallel, and each used to run
/// <c>playwright install chromium --with-deps</c> itself. On a Linux runner both then call
/// <c>apt-get</c> at once and the second fails on <c>/var/lib/apt/lists/lock</c> (exit code 1),
/// failing every test of that collection at fixture start — measured on PR #461's first CI run.
/// One shared task serialises them: the second caller awaits the first install.
/// </remarks>
internal static class PlaywrightInstall
{
    private static readonly Lazy<Task<int>> Install = new(
        () => Task.Run(() => Microsoft.Playwright.Program.Main(["install", "chromium", "--with-deps"])),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static async Task EnsureAsync()
    {
        var exitCode = await Install.Value;
        if (exitCode != 0)
            throw new InvalidOperationException($"Playwright install failed with exit code {exitCode}");
    }
}
