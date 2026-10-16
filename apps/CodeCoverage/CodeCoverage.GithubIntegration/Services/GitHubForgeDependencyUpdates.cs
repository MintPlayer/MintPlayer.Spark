using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Raven.Client.Documents.Session;
using Account = CodeCoverage.Entities.Account;
using Repository = CodeCoverage.Entities.Repository;

namespace CodeCoverage.Services;

/// <summary>
/// GitHub's <see cref="IForgeDependencyUpdates"/> (dependency-updates PRD N5–N7, R15, R16), as the App installation of the
/// repository's account, over raw REST like <see cref="GitHubForgeMergeQueue"/>.
/// </summary>
/// <remarks>
/// ⚠️ The response shapes follow docs.github.com/rest (pulls, reactions, check-runs, git refs and tags, contents, artifacts);
/// they are <b>not live-captured yet</b>: S18 (end to end on SparkTest) captures them and replaces the test bodies.
/// </remarks>
[Register(typeof(IForgeDependencyUpdates), ServiceLifetime.Scoped)]
public partial class GitHubForgeDependencyUpdates : IForgeDependencyUpdates
{
    [Inject] private readonly IGitHubInstallationService installationService;
    [Inject] private readonly IHttpClientFactory httpClientFactory;
    [Inject] private readonly IAsyncDocumentSession session;

    private static readonly Uri ApiBase = new("https://api.github.com/");
    private const string ApiVersion = "2022-11-28";
    private const int MaxFileBytes = 1024 * 1024;
    private const int MaxSummaryLength = 65535;

    public EForgeProvider Provider => EForgeProvider.GitHub;

    // ── Artifacts ────────────────────────────────────────────────────────────────────────────────

    public async Task<ForgeArtifactFiles> DownloadArtifactFilesAsync(Repository repository, long runId, string artifactName, IReadOnlyCollection<string> fileNames,
        long maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        using var listing = await SendAsync(repository, HttpMethod.Get,
            $"{RepoPath(repository)}/actions/runs/{runId.ToString(CultureInfo.InvariantCulture)}/artifacts?name={Uri.EscapeDataString(artifactName)}&per_page=100", null, cancellationToken);
        EnsureSuccess(listing);
        var artifacts = Items(listing.Json!.RootElement, "artifacts").Where(a => Str(a, "name") == artifactName).ToList();
        if (artifacts.Count == 0)
            return ForgeArtifactFiles.Failed(EForgeArtifactOutcome.NotFound, $"The run has no artifact named '{artifactName}'.");
        var artifact = artifacts.FirstOrDefault(a => !Flag(a, "expired"));
        if (artifact.ValueKind == JsonValueKind.Undefined)
            return ForgeArtifactFiles.Failed(EForgeArtifactOutcome.Expired, $"The artifact '{artifactName}' has expired.");
        if (Num(artifact, "size_in_bytes") is { } declared && declared > maxBytes)
            return ForgeArtifactFiles.Failed(EForgeArtifactOutcome.TooLarge, $"The artifact is {declared} bytes; the limit is {maxBytes}.");

        var archive = await DownloadArchiveAsync(repository, Num(artifact, "id") ?? 0, maxBytes, cancellationToken);
        return archive is null
            ? ForgeArtifactFiles.Failed(EForgeArtifactOutcome.TooLarge, $"The archive exceeds {maxBytes} bytes.")
            : ReadNamedFiles(archive, fileNames, maxBytes);
    }

    /// <summary>The named files of the archive, each and all together held to <paramref name="maxBytes"/>; other files are
    /// ignored (a runner may leave a log beside them), directories are skipped, a name seen twice is invalid.</summary>
    internal static ForgeArtifactFiles ReadNamedFiles(byte[] archive, IReadOnlyCollection<string> fileNames, long maxBytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long total = 0;
            foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                if (!fileNames.Contains(entry.FullName))
                    continue;
                if (files.ContainsKey(entry.FullName))
                    return ForgeArtifactFiles.Failed(EForgeArtifactOutcome.Invalid, $"The artifact holds '{entry.FullName}' twice.");
                using var stream = entry.Open();
                using var content = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                        return ForgeArtifactFiles.Failed(EForgeArtifactOutcome.TooLarge, $"The files inflate past {maxBytes} bytes.");
                    content.Write(chunk, 0, read);
                }
                files[entry.FullName] = content.ToArray();
            }
            return new ForgeArtifactFiles(EForgeArtifactOutcome.Ok, files, null);
        }
        catch (InvalidDataException ex)
        {
            return ForgeArtifactFiles.Failed(EForgeArtifactOutcome.Invalid, $"The artifact is not a readable zip: {ex.Message}");
        }
    }

    private async Task<byte[]?> DownloadArchiveAsync(Repository repository, long artifactId, long maxBytes, CancellationToken cancellationToken)
    {
        var token = await TokenAsync(repository, cancellationToken);
        var http = httpClientFactory.CreateClient(nameof(GitHubForgeMergeQueue));
        using var request = NewRequest(HttpMethod.Get, new Uri(ApiBase, $"{RepoPath(repository)}/actions/artifacts/{artifactId.ToString(CultureInfo.InvariantCulture)}/zip"), token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
        {
            // Signed blob storage: no credential goes there.
            using var blobRequest = new HttpRequestMessage(HttpMethod.Get, location.IsAbsoluteUri ? location : new Uri(ApiBase, location));
            using var blob = await http.SendAsync(blobRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return await ReadCappedAsync(blob, maxBytes, cancellationToken);
        }
        return await ReadCappedAsync(response, maxBytes, cancellationToken);
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new ForgeAccessDeniedException("GitHub refused the artifact download.", "actions=read");
        if (!response.IsSuccessStatusCode)
            throw new ForgeRequestException($"GitHub answered {(int)response.StatusCode} for the artifact download.", (int)response.StatusCode);
        if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // ── The pull request ─────────────────────────────────────────────────────────────────────────

    public async Task<ForgeBotPullRequest?> FindPullRequestAsync(Repository repository, string headBranch, string baseBranch, CancellationToken cancellationToken = default)
    {
        RequireBranch(headBranch);
        RequireBranch(baseBranch);
        using var reply = await SendAsync(repository, HttpMethod.Get,
            $"{RepoPath(repository)}/pulls?state=open&head={Uri.EscapeDataString($"{repository.OwnerLogin}:{headBranch}")}&base={Uri.EscapeDataString(baseBranch)}&per_page=10",
            null, cancellationToken);
        EnsureSuccess(reply);
        var root = reply.Json!.RootElement;
        return root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? ReadPull(root[0]) : null;
    }

    public async Task<ForgeBotPullRequest?> GetPullRequestAsync(Repository repository, int number, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        using var reply = await SendAsync(repository, HttpMethod.Get, $"{RepoPath(repository)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken);
        if (reply.Status == HttpStatusCode.NotFound) return null;
        EnsureSuccess(reply);
        return ReadPull(reply.Json!.RootElement);
    }

    public async Task<ForgeBotPullRequest> CreatePullRequestAsync(Repository repository, string headBranch, string baseBranch, string title, string body, bool draft,
        CancellationToken cancellationToken = default)
    {
        RequireBranch(headBranch);
        RequireBranch(baseBranch);
        using var reply = await SendAsync(repository, HttpMethod.Post, $"{RepoPath(repository)}/pulls", new Dictionary<string, object?>
        {
            ["title"] = title, ["head"] = headBranch, ["base"] = baseBranch, ["body"] = body, ["draft"] = draft, ["maintainer_can_modify"] = false,
        }, cancellationToken);
        EnsureSuccess(reply, "pull_requests=write");
        return ReadPull(reply.Json!.RootElement);
    }

    public async Task UpdatePullRequestAsync(Repository repository, int number, string title, string body, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        using var reply = await SendAsync(repository, HttpMethod.Patch, $"{RepoPath(repository)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}",
            new Dictionary<string, object?> { ["title"] = title, ["body"] = body }, cancellationToken);
        EnsureSuccess(reply, "pull_requests=write");
    }

    /// <remarks>docs: <c>PATCH /repos/{owner}/{repo}/pulls/{n}</c> with <c>state: closed</c>; closing a closed one answers 200.</remarks>
    public async Task ClosePullRequestAsync(Repository repository, int number, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        using var reply = await SendAsync(repository, HttpMethod.Patch, $"{RepoPath(repository)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}",
            new Dictionary<string, object?> { ["state"] = "closed" }, cancellationToken);
        if (reply.Status == HttpStatusCode.NotFound) return;
        EnsureSuccess(reply, "pull_requests=write");
    }

    internal static ForgeBotPullRequest ReadPull(JsonElement pull)
        => new((int)(Num(pull, "number") ?? 0), Str(Child(pull, "head"), "ref") ?? string.Empty, Str(Child(pull, "head"), "sha") ?? string.Empty,
            Str(Child(pull, "base"), "ref") ?? string.Empty, Str(pull, "state") == "open", Flag(pull, "merged") || Str(pull, "merged_at") is not null,
            Flag(pull, "draft"), Str(pull, "html_url"));

    public async Task AddCommentReactionAsync(Repository repository, long commentId, EForgeReaction reaction, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commentId);
        using var reply = await SendAsync(repository, HttpMethod.Post, $"{RepoPath(repository)}/issues/comments/{commentId.ToString(CultureInfo.InvariantCulture)}/reactions",
            new Dictionary<string, object?>
            {
                ["content"] = reaction switch { EForgeReaction.ThumbsUp => "+1", EForgeReaction.ThumbsDown => "-1", _ => "eyes" },
            }, cancellationToken);
        EnsureSuccess(reply, "issues=write");
    }

    // ── The check run ───────────────────────────────────────────────────────────────────────────

    public async Task<long> PublishCheckAsync(Repository repository, string sha, ForgeQueueCheck check, long? existingId, CancellationToken cancellationToken = default)
    {
        if (!CommitSha().IsMatch(sha))
            throw new ArgumentException($"'{sha}' is not a commit sha.", nameof(sha));
        var completed = check.State is not (EForgeQueueCheckState.Queued or EForgeQueueCheckState.InProgress);
        var body = new Dictionary<string, object?>
        {
            ["name"] = IForgeDependencyUpdates.DependencyCheckName,
            ["status"] = completed ? "completed" : check.State == EForgeQueueCheckState.Queued ? "queued" : "in_progress",
            ["output"] = new Dictionary<string, object?>
            {
                ["title"] = check.Title,
                ["summary"] = check.Summary.Length <= MaxSummaryLength ? check.Summary : check.Summary[..(MaxSummaryLength - 1)] + "…",
            },
        };
        if (completed)
            body["conclusion"] = check.State switch
            {
                EForgeQueueCheckState.Success => "success",
                EForgeQueueCheckState.Failure => "failure",
                EForgeQueueCheckState.Cancelled => "cancelled",
                _ => "neutral",
            };
        if (check.DetailsUrl is { } details)
            body["details_url"] = details;
        if (check.PullRequestNumber is { } number)
            body["external_id"] = number.ToString(CultureInfo.InvariantCulture);

        using var reply = existingId is { } id
            ? await SendAsync(repository, HttpMethod.Patch, $"{RepoPath(repository)}/check-runs/{id.ToString(CultureInfo.InvariantCulture)}", body, cancellationToken)
            : await SendAsync(repository, HttpMethod.Post, $"{RepoPath(repository)}/check-runs", new Dictionary<string, object?>(body) { ["head_sha"] = sha }, cancellationToken);
        EnsureSuccess(reply, "checks=write");
        return Num(reply.Json!.RootElement, "id") ?? throw new ForgeRequestException("GitHub answered the check run without an id.", (int)reply.Status);
    }

    // ── Static reads of another repository (gap C, R15) ──────────────────────────────────────────

    /// <remarks>
    /// <c>GET /repos/{owner}/{repo}/git/ref/tags/{tag}</c>: a lightweight tag's ref points at the commit; an annotated one at a
    /// tag object, read with <c>GET /git/tags/{sha}</c> until a commit (at most three levels: a tag of a tag is rare, a
    /// longer chain is refused).
    /// </remarks>
    public async Task<string?> ResolveTagCommitAsync(Repository repository, string ownerRepo, string tag, CancellationToken cancellationToken = default)
    {
        if (!GitHubForgeClient.GitHubFullName().IsMatch(ownerRepo) || !GitRef().IsMatch(tag))
            return null;
        using var reply = await SendAsync(repository, HttpMethod.Get, $"repos/{ownerRepo}/git/ref/tags/{EscapePath(tag)}", null, cancellationToken);
        if (reply.Status == HttpStatusCode.NotFound) return null;
        EnsureSuccess(reply);
        var target = Child(reply.Json!.RootElement, "object");
        var (type, sha) = (Str(target, "type"), Str(target, "sha"));
        for (var depth = 0; depth < 3 && type == "tag" && sha is not null && CommitSha().IsMatch(sha); depth++)
        {
            using var tagObject = await SendAsync(repository, HttpMethod.Get, $"repos/{ownerRepo}/git/tags/{sha}", null, cancellationToken);
            if (tagObject.Status == HttpStatusCode.NotFound) return null;
            EnsureSuccess(tagObject);
            var next = Child(tagObject.Json!.RootElement, "object");
            (type, sha) = (Str(next, "type"), Str(next, "sha"));
        }
        return type == "commit" && sha is not null && CommitSha().IsMatch(sha) ? sha.ToLowerInvariant() : null;
    }

    public async Task<string?> GetRepositoryFileAsync(Repository repository, string ownerRepo, string gitRef, string path, CancellationToken cancellationToken = default)
    {
        if (!GitHubForgeClient.GitHubFullName().IsMatch(ownerRepo) || !GitRef().IsMatch(gitRef))
            return null;
        var segments = path.Split('/');
        if (path.Length == 0 || segments.Any(s => s is "" or "." or ".."))
            return null;
        using var reply = await SendAsync(repository, HttpMethod.Get,
            $"repos/{ownerRepo}/contents/{string.Join('/', segments.Select(Uri.EscapeDataString))}?ref={Uri.EscapeDataString(gitRef)}", null, cancellationToken);
        if (reply.Status == HttpStatusCode.NotFound) return null;
        EnsureSuccess(reply);
        var root = reply.Json!.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Str(root, "type") != "file" || Str(root, "encoding") != "base64" || Str(root, "content") is not { } content
            || Num(root, "size") is not { } size || size > MaxFileBytes)
            return null;
        try
        {
            var text = StrictUtf8.GetString(Convert.FromBase64String(content.Replace("\n", "", StringComparison.Ordinal)));
            return text.StartsWith('﻿') ? text[1..] : text;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────

    private sealed class Reply(HttpStatusCode status, JsonDocument? json, string? acceptedPermissions, string method, string path) : IDisposable
    {
        public HttpStatusCode Status { get; } = status;
        public JsonDocument? Json { get; } = json;
        public string? AcceptedPermissions { get; } = acceptedPermissions;
        public string Method { get; } = method;
        public string Path { get; } = path;
        public bool IsSuccess => (int)Status is >= 200 and < 300;
        public string? Message => Json?.RootElement is { ValueKind: JsonValueKind.Object } root ? Str(root, "message") : null;
        public void Dispose() => Json?.Dispose();
    }

    private async Task<Reply> SendAsync(Repository repository, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var token = await TokenAsync(repository, cancellationToken);
        var http = httpClientFactory.CreateClient(nameof(GitHubForgeMergeQueue));
        using var request = NewRequest(method, new Uri(ApiBase, path), token);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        JsonDocument? json = null;
        if (bytes.Length > 0)
        {
            try { json = JsonDocument.Parse(bytes); }
            catch (JsonException) { /* not JSON: an error page from a proxy; the status decides */ }
        }
        // P16: a rate limit is "not now", never a refusal (a secondary limit is a 403 too) and never an absence.
        var message = json?.RootElement is { ValueKind: JsonValueKind.Object } errorRoot ? Str(errorRoot, "message") : null;
        if (GitHubRateLimits.Classify(response, message, DateTimeOffset.UtcNow, $"{method} {path}") is { } limited)
        {
            json?.Dispose();
            throw new ForgeRequestException($"GitHub rate limit reached on {method} {path}.", 429, limited);
        }
        var accepted = response.Headers.TryGetValues("X-Accepted-GitHub-Permissions", out var values) ? values.FirstOrDefault() : null;
        return new Reply(response.StatusCode, json, accepted, method.Method, path);
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, Uri uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodeCoverage", "1.0"));
        return request;
    }

    private static void EnsureSuccess(Reply reply, string? forbiddenNeeds = null)
    {
        if (reply.IsSuccess) return;
        var what = $"GitHub answered {(int)reply.Status} to {reply.Method} {reply.Path}: {reply.Message ?? "no message"}";
        throw reply.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? new ForgeAccessDeniedException(what, reply.AcceptedPermissions ?? (reply.Status == HttpStatusCode.Forbidden ? forbiddenNeeds : null))
            : new ForgeRequestException(what, (int)reply.Status);
    }

    private async Task<string> TokenAsync(Repository repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var installationId = repository.Account is null ? null : (await session.LoadAsync<Account>(repository.Account, cancellationToken))?.InstallationId;
        if (installationId is null)
            throw new ForgeAccessDeniedException($"No GitHub App installation for {repository.FullName}.");
        return await installationService.GetInstallationTokenAsync(installationId.Value, cancellationToken);
    }

    private static string RepoPath(Repository repository)
        => $"repos/{Uri.EscapeDataString(repository.OwnerLogin)}/{Uri.EscapeDataString(repository.Name)}";

    private static string EscapePath(string value) => string.Join('/', value.Split('/').Select(Uri.EscapeDataString));

    private static void RequireBranch(string branch)
    {
        if (string.IsNullOrEmpty(branch) || !GitRef().IsMatch(branch))
            throw new ArgumentException($"'{branch}' is not a branch name.", nameof(branch));
    }

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Num(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number : null;

    private static bool Flag(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static JsonElement Child(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? value : default;

    private static IEnumerable<JsonElement> Items(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex CommitSha();

    /// <summary>A tag, branch or sha as a ref: no whitespace, no <c>..</c>, no control or URL-special characters.</summary>
    [GeneratedRegex(@"^(?!.*\.\.)(?!/)(?!.*/$)[A-Za-z0-9._+/\-]{1,200}$")]
    private static partial Regex GitRef();
}
