namespace CodeCoverage.Versioning.Tests.MSBuild;

/// <summary>
/// #565 (docs/docker_build_versioning_PRD.md): an image's build context has no <c>.git</c> (<c>.dockerignore</c>), so the
/// package versioning targets, imported for every project from <c>Directory.Build.targets</c>, could only fail
/// <c>git tag</c> and warn MPV003 once per library, on restore and on publish. Every .NET Dockerfile therefore sets
/// <c>ENV MintPlayerVersioningSkip=true</c> in each stage that runs <c>dotnet restore/build/publish</c> (a stage built
/// <c>FROM</c> another stage inherits its environment). This guard keeps a new Dockerfile from bringing the warnings back.
/// </summary>
public class DockerfileVersioningSkipTests
{
    private const string Skip = "MintPlayerVersioningSkip=true";

    [Fact]
    public void Every_dotnet_stage_of_every_Dockerfile_skips_package_versioning()
    {
        var root = RepositoryRoot();
        var dockerfiles = Dockerfiles(Path.Combine(root, "apps")).ToList();
        var offenders = dockerfiles
            .SelectMany(path => StagesMissingSkip(File.ReadAllText(path))
                .Select(stage => $"{Path.GetRelativePath(root, path).Replace('\\', '/')}: stage '{stage}'"))
            .ToList();

        offenders.Should().BeEmpty();
        // The scan must actually have seen the .NET images (CodeCoverage x3, DemoApp, MintPlayer, SeoTester x2), or an
        // empty offender list proves nothing.
        dockerfiles.Count(path => File.ReadAllText(path).Contains(Skip)).Should().Be(7);
    }

    [Fact]
    public void The_check_flags_a_stage_without_the_skip_and_follows_stage_inheritance()
    {
        StagesMissingSkip("""
            FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
            RUN dotnet restore "./App.csproj"
            """).Should().Equal("build");

        StagesMissingSkip("""
            FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
            ENV MintPlayerVersioningSkip=true
            RUN dotnet restore "./App.csproj"
            FROM build AS publish
            RUN dotnet publish "./App.csproj" -c Release
            FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
            """).Should().BeEmpty();

        // A fresh base image does not inherit the earlier stage's ENV.
        StagesMissingSkip("""
            FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
            ENV MintPlayerVersioningSkip=true
            FROM mcr.microsoft.com/dotnet/sdk:11.0 AS other
            RUN dotnet build
            """).Should().Equal("other");
    }

    /// <summary>The names of the stages that run the dotnet CLI's restore, build or publish without the skip in effect.</summary>
    private static List<string> StagesMissingSkip(string dockerfile)
    {
        var skipping = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        string? stage = null;
        var skip = false;
        var flagged = false;
        var index = 0;
        foreach (var raw in dockerfile.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("FROM ", StringComparison.OrdinalIgnoreCase))
            {
                if (stage != null) skipping[stage] = skip;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var image = parts.Length > 1 ? parts[1] : "";
                stage = parts.Length > 3 && parts[2].Equals("AS", StringComparison.OrdinalIgnoreCase) ? parts[3] : $"#{index}";
                index++;
                skip = skipping.TryGetValue(image, out var inherited) && inherited;
                flagged = false;
            }
            else if (line.StartsWith("ENV ", StringComparison.OrdinalIgnoreCase) && line.Contains(Skip))
                skip = true;
            else if (!skip && !flagged && stage != null
                     && System.Text.RegularExpressions.Regex.IsMatch(line, @"\bdotnet\s+(restore|build|publish)\b"))
            {
                missing.Add(stage);
                flagged = true;
            }
        }
        return missing;
    }

    /// <summary>The real images' Dockerfiles: test fixtures (copies of Dockerfiles parsed as data) are not built.</summary>
    private static IEnumerable<string> Dockerfiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "Dockerfile"))
            yield return file;
        foreach (var sub in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(sub);
            if (name is "node_modules" or "bin" or "obj" or "dist" or ".angular"
                || name.Equals("fixtures", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".Tests", StringComparison.Ordinal))
                continue;
            foreach (var file in Dockerfiles(sub))
                yield return file;
        }
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "nx.json")) && File.Exists(Path.Combine(dir.FullName, "Directory.Build.targets")))
                return dir.FullName;
        throw new InvalidOperationException("The repository root (nx.json + Directory.Build.targets) is not above " + AppContext.BaseDirectory);
    }
}
