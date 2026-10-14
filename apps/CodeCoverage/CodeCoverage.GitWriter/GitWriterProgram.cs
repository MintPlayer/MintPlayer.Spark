namespace CodeCoverage.GitWriter;

/// <summary>The coverage-git-writer container's entry point (<see cref="GitWriterHost"/>).</summary>
public static class GitWriterProgram
{
    public static void Main(string[] args)
    {
        var app = GitWriterHost.Build(GitWriterHost.CreateBuilder(args));
        app.Run();
    }
}
