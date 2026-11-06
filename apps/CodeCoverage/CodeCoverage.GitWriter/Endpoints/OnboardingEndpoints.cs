using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Jobs.Dependencies;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.GitWriter.Endpoints;

/// <summary>
/// <c>POST /jobs/onboarding</c>: the onboarding chooser's one commit (#571 D3). It may write what the versioning onboarding
/// writes (<see cref="GitWriterVersioningOnboardingEndpoint.IsPath"/>) and the dependency onboarding's closed set
/// (<see cref="DependencyFields.IsOnboardingPath"/>); the only file it deletes is Dependabot's, which the dependency updater replaces.
/// </summary>
internal sealed partial class GitWriterOnboardingEndpoint : IPostEndpoint<VersioningOnboardingWire>
{
    public static readonly string FilesRule = $"1 to {GitWriterVersioningOnboardingEndpoint.MaxFiles} distinct files of at most "
        + $"{GitWriterVersioningOnboardingEndpoint.MaxFileBytes / 1024} KiB text, each a versioning onboarding path or one of the dependency "
        + "onboarding's set; only .github/dependabot.yml or .yaml may be deleted";

    public static string Path => "/jobs/onboarding";

    [Inject] private readonly WriterService writer;
    [Inject] private readonly IOptions<GitWriterOptions> options;

    protected override async ValueTask<VersioningOnboardingWire?> BindRequestAsync(HttpContext context)
        => await Body.ReadAsync<VersioningOnboardingWire>(context.Request.Body, context.Request.ContentLength,
            GitWriterVersioningOnboardingEndpoint.MaxBodyBytes, context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(Refusal.Body(GitWriterVersioningOnboardingEndpoint.MaxBodyBytes));

    public override async Task<IResult> HandleAsync(VersioningOnboardingWire request, CancellationToken cancellationToken)
    {
        var refusal = new Refusal();
        if (DependencyFields.Job(refusal, request, options.Value.AllowInsecureHttp) is not { } job
            || DependencyFields.Commit(refusal, request) is not { } commit
            || refusal.Check(Files(request.Files), "files", FilesRule) is not { } files)
            return refusal.Result();
        return Results.Json(await writer.OnboardingAsync(job, commit, files), GitWriterHost.Json);
    }

    internal static List<(string Path, string? Content)>? Files(List<DependencyFileWire>? wires)
    {
        if (wires is not { Count: > 0 and <= GitWriterVersioningOnboardingEndpoint.MaxFiles }) return null;
        var list = new List<(string Path, string? Content)>();
        foreach (var wire in wires)
        {
            if (wire?.Path is not { } path || list.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                return null;
            if (wire.Delete)
            {
                if (wire.Content is not null || path is not (".github/dependabot.yml" or ".github/dependabot.yaml"))
                    return null;
                list.Add((path, null));
                continue;
            }
            if (wire.Content is not { Length: <= GitWriterVersioningOnboardingEndpoint.MaxFileBytes } content
                || !(GitWriterVersioningOnboardingEndpoint.IsPath(path) || DependencyFields.IsOnboardingPath(path)))
                return null;
            list.Add((path, content));
        }
        return list;
    }
}
