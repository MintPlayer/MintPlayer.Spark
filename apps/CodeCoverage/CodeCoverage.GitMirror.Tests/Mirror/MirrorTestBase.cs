using System.Text;
using CodeCoverage.GitMirror.Tests._Infrastructure;

namespace CodeCoverage.GitMirror.Tests.Mirror;

/// <summary>A local smart-HTTP origin server and a sidecar per test, both torn down after it.</summary>
public abstract class MirrorTestBase : IAsyncLifetime
{
    private readonly TempDir origins = new("origins");

    internal GitHttpServer Server { get; private set; } = null!;

    internal SidecarHost Sidecar { get; private set; } = null!;

    /// <summary>Settings for the sidecar of this test class.</summary>
    protected virtual IReadOnlyDictionary<string, string?>? SidecarSettings => null;

    public async Task InitializeAsync()
    {
        Server = await GitHttpServer.StartAsync(origins.Path);
        Sidecar = await SidecarHost.StartAsync(SidecarSettings);
    }

    public async Task DisposeAsync()
    {
        await Sidecar.DisposeAsync();
        await Server.DisposeAsync();
        origins.Dispose();
    }

    /// <summary>A small origin: main (3 commits), a feature branch, an annotated and a lightweight tag, and a
    /// <c>refs/pull/1/head</c> that must never reach a mirror (fork isolation, §5.5).</summary>
    internal Dictionary<string, string> CreateOrigin(string name)
    {
        var path = Server.PathOf(name);
        var shas = new RepoBuilder()
            .Commit("a", 1).Commit("b", 2, "a").Commit("c", 3, "b").Commit("d", 4, "b")
            .Branch("main", "c").Branch("feature", "d").Tag("v1", "b", annotated: true).Tag("light", "a")
            .WriteTo(path);
        FixtureGit.Run(path, "update-ref", "refs/pull/1/head", shas["d"]);
        return shas;
    }

    /// <summary>Adds a commit on top of <paramref name="parentSha"/> and moves <paramref name="branch"/> to it (a push).</summary>
    internal string Push(string origin, string branch, string subject, long minutes, string parentSha)
        => new RepoBuilder().Commit(subject, minutes, parentSha).Branch(branch, subject).WriteTo(Server.PathOf(origin))[subject];

    internal static bool AnyFileContains(string directory, params string[] secrets)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            foreach (var secret in secrets)
            {
                if (bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) >= 0)
                    return true;
            }
        }
        return false;
    }

    internal static string[] Entries(string directory)
        => Directory.Exists(directory) ? Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories) : [];
}
