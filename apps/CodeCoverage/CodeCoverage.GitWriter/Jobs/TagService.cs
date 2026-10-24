using System.Text;
using CodeCoverage.Git;
using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.GitWriter.Jobs;

/// <summary>A validated <c>POST /jobs/tags</c>.</summary>
public sealed record TagsJob(string Repo, Uri CloneUrl, GitCredential Credential, IReadOnlyList<string> Branches, GitIdentity Tagger, IReadOnlyList<TagToWrite> Tags);

/// <summary>
/// The server host's tag writer (#153 M5, PRD D16). The decisions are the engine's <see cref="TagWriter"/>, the same the
/// action's local host makes; this job only supplies the repository: an empty bare one into which the allowed branches
/// are fetched blob-less, so a commit on none of them is not even present and is refused as unreachable.
/// </summary>
public sealed class TagService(JobRunner jobs)
{
    public Task<TagsResponse> WriteAsync(TagsJob job) => jobs.RunAsync("tags", job.Repo, async context =>
    {
        await context.RequireAsync(["init", "--bare", "--quiet", "--template=", context.GitDirectory], inRepository: false);
        string[] fetch = ["fetch", "--quiet", "--no-tags", "--no-write-fetch-head", "--filter=blob:none", job.CloneUrl.AbsoluteUri,
            .. job.Branches.Select(b => $"+refs/heads/{b}:refs/heads/{b}")];
        await context.RequireAsync(fetch, job.Credential);
        context.EnsureWithinDisk();

        var writer = new TagWriter(new JobTagGit(context, job.Credential), job.CloneUrl.AbsoluteUri, job.Branches.Select(b => "refs/heads/" + b).ToList(),
            new Tagger(job.Tagger.Name, job.Tagger.Email));
        var results = await writer.WriteAsync(job.Tags, context.Deadline);
        return new TagsResponse(JobOutcomes.Tagged, null, results.Select(r => new TagResultWire(r.Tag.Name, Wire(r.Outcome), r.Detail)).ToList(), null,
            context.ElapsedMilliseconds);
    }, (outcome, reason, detail, ms) => new TagsResponse(outcome, reason, [], detail, ms));

    internal static string Wire(TagOutcome outcome) => outcome switch
    {
        TagOutcome.Created => "created",
        TagOutcome.AlreadyPresent => "already-present",
        TagOutcome.Conflict => "conflict",
        TagOutcome.Unreachable => "unreachable",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    /// <summary>The engine's git port over the job's hardened <see cref="GitRunner"/>: the credential only for the
    /// commands that reach the forge.</summary>
    internal sealed class JobTagGit(JobContext context, GitCredential credential) : ITagGit
    {
        public async Task<GitCli.Result> RunAsync(IReadOnlyList<string> arguments, string? input, bool authenticated, Tagger? tagger, CancellationToken cancellationToken)
        {
            var result = await context.GitAsync(arguments, authenticated ? credential : null, input: input,
                committer: tagger is null ? null : new GitIdentity(tagger.Name, tagger.Email));
            return new GitCli.Result(result.ExitCode, Encoding.UTF8.GetBytes(result.StandardOutput), result.StandardError);
        }
    }
}
