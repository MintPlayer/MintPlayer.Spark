namespace CodeCoverage.GitMirror;

/// <summary>The coverage-git container's entry point (<see cref="GitMirrorHost"/>).</summary>
public static class GitMirrorProgram
{
    public static void Main(string[] args)
    {
        var app = GitMirrorHost.Build(GitMirrorHost.CreateBuilder(args));
        app.Run();
    }
}
