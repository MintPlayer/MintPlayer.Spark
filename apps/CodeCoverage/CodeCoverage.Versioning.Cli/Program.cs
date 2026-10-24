using System.Text.Json;
using CodeCoverage.Versioning.Versions;
using CodeCoverage.Versioning.Api;
using CodeCoverage.Versioning.Build;
using CodeCoverage.Versioning.Cli;
using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Releases;

try
{
	return args switch
	{
		["api-list", var path] => ApiList(path),
		["manifest", .. var rest] => await Manifest(Options.Parse(rest)),
		["run", .. var rest] => await Run(Options.Parse(rest)),
		_ => Usage(),
	};
}
catch (OptionsException e)
{
	Console.Error.WriteLine(e.Message);
	return Usage();
}

static int ApiList(string path)
{
	using var stream = File.OpenRead(path);
	var lines = DotnetApiListing.List(stream);
	foreach (var line in lines) Console.WriteLine(line);
	Console.WriteLine(DotnetApiListing.Hash(lines));
	return 0;
}

// The action's build-side half (D5, D6): the manifest of the checkout's HEAD, written to --out with the notes for the log.
static async Task<int> Manifest(Options options)
{
	var repo = Path.GetFullPath(options.Require("repo"));
	var git = new GitCli(GitDirectory(repo));
	var commit = (await git.RunAsync(["rev-parse", "--verify", "HEAD^{commit}"])).EnsureSuccess("rev-parse").Text.Trim();
	using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
	http.DefaultRequestHeaders.UserAgent.ParseAdd("codecoverage-versioning");
	var config = options.Get("config") is { } configPath
		? JsonSerializer.Deserialize<VersioningConfig>(await File.ReadAllTextAsync(configPath), Wire.Json) ?? new VersioningConfig()
		: new VersioningConfig();
	if (config.Validate() is { Count: > 0 } configErrors) throw new OptionsException("The versioning configuration is invalid: " + string.Join(" ", configErrors));
	// The line whose last tag's hash a registry lookup completes: a release branch's major, else the latest line.
	int? branchMajor = null;
	if (options.Get("branch") is { } branch && options.Get("default-branch") is { } defaultBranch && branch != defaultBranch)
		ReleaseBranches.IsMatch(config.EffectiveReleaseBranches, branch, out branchMajor);
	var result = await BuildManifest.CollectAsync(git, new BuildManifestRequest(repo, commit, options.Get("configuration") ?? "Release", branchMajor,
		options.Has("no-registry") ? null : new PublicPackageRegistry(http), config.ToSettings().Ignore, await DotnetSdk.VersionAsync(repo)));
	await File.WriteAllTextAsync(options.Require("out"), JsonSerializer.Serialize(new { result.Manifest, result.Notes }, Wire.Json));
	return 0;
}

// Local mode (D2): the same ReleaseRun the server's git writer runs, against the checkout, pushing with the checkout's
// credentials or CODECOVERAGE_VERSIONING_TOKEN. The result has the server's response shape, so the action reads one format.
static async Task<int> Run(Options options)
{
	var repo = Path.GetFullPath(options.Require("repo"));
	var output = options.Require("out");
	var branch = options.Require("branch");
	var defaultBranch = options.Require("default-branch");
	var remote = options.Get("remote") ?? "origin";

	async Task<int> Write(LocalRunResponse response)
	{
		await File.WriteAllTextAsync(output, JsonSerializer.Serialize(response, Wire.Json));
		return 0;
	}

	ReleaseManifest manifest;
	VersioningConfig config;
	try
	{
		manifest = JsonSerializer.Deserialize<ReleaseManifest>(await File.ReadAllTextAsync(options.Require("manifest")), Wire.Json)
			?? throw new JsonException("The manifest is empty.");
		config = JsonSerializer.Deserialize<VersioningConfig>(await File.ReadAllTextAsync(options.Require("config")), Wire.Json) ?? new VersioningConfig();
	}
	catch (JsonException e)
	{
		return await Write(LocalRunResponse.Refusal("invalid-input", e.Message));
	}
	if (manifest.Validate() is { Count: > 0 } manifestErrors) return await Write(LocalRunResponse.Refusal("invalid-manifest", null, manifestErrors));
	if (config.Validate() is { Count: > 0 } configErrors) return await Write(LocalRunResponse.Refusal("invalid-config", null, configErrors));

	int? branchMajor = null;
	if (branch != defaultBranch && !ReleaseBranches.IsMatch(config.EffectiveReleaseBranches, branch, out branchMajor))
		return await Write(LocalRunResponse.Refusal("branch-not-versioned", $"{branch} is neither the default branch nor a release branch."));

	var git = new GitCli(GitDirectory(repo), TimeSpan.FromMinutes(5));
	var writer = new TagWriter(new CliTagGit(git, Environment.GetEnvironmentVariable("CODECOVERAGE_VERSIONING_TOKEN")), remote,
		[$"refs/remotes/{remote}/{branch}"], new Tagger("github-actions[bot]", "41898282+github-actions[bot]@users.noreply.github.com"));
	try
	{
		var result = await ReleaseRun.RunAsync(git, writer, new ReleaseRunRequest(manifest, config.ToSettings(), branch == defaultBranch, branchMajor));
		return await Write(LocalRunResponse.From(result));
	}
	catch (Exception e) when (e is ReleaseRunException or GraphException or VersioningException)
	{
		return await Write(LocalRunResponse.Refusal("unplannable", e.Message));
	}
	catch (GitException e)
	{
		return await Write(LocalRunResponse.Refusal("error", e.Message));
	}
}

// A checkout's .git is a directory, or a file pointing at one (worktrees, submodules).
static string GitDirectory(string repo)
{
	var dotGit = Path.Combine(repo, ".git");
	if (File.Exists(dotGit) && File.ReadAllText(dotGit).Trim() is var pointer && pointer.StartsWith("gitdir:", StringComparison.Ordinal))
		return Path.GetFullPath(pointer["gitdir:".Length..].Trim(), repo);
	return dotGit;
}

static int Usage()
{
	Console.Error.WriteLine("""
		usage: codecoverage-versioning api-list <assembly.dll>
		       codecoverage-versioning manifest --repo <dir> --out <file> [--config <file>] [--configuration Release] [--branch <name> --default-branch <name>] [--no-registry]
		       codecoverage-versioning run --repo <dir> --manifest <file> --config <file> --branch <name> --default-branch <name> --out <file> [--remote origin]
		""");
	return 2;
}
