using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>The writer's hardening (docs/coverage_merge_queue_PRD.md T8, T9, S8): every git process's configuration, the
/// token's path, request validation, and the job caps.</summary>
public class HardeningTests : WriterTestBase
{
    [Fact]
    public async Task Every_git_process_is_hardened_and_the_token_lives_only_in_the_environment()
    {
        var origin = Seed();
        await PreparedAsync(origin);

        Writer.Started.Should().NotBeEmpty();
        foreach (var psi in Writer.Started)
        {
            var args = string.Join('\n', psi.ArgumentList);
            foreach (var flag in new[] { "protocol.file.allow=never", "protocol.ext.allow=never", "transfer.fsckObjects=true", "attr.tree=4b825dc642cb6eb9a060e54bf8d69288fbee4904",
                         "core.fsmonitor=false", "submodule.recurse=false", $"core.hooksPath={Writer.Runner.HooksPath}", "credential.helper=", "pack.threads=1" })
                psi.ArgumentList.Should().Contain(flag, $"git {psi.ArgumentList.Last()} runs with {flag}");
            args.Should().NotContain(Token);
            psi.Environment["GIT_CONFIG_NOSYSTEM"].Should().Be("1");
        }
        var withCredential = Writer.Started.Where(p => p.Environment.ContainsKey("GIT_CONFIG_VALUE_0")).ToList();
        withCredential.Should().NotBeEmpty();
        withCredential.Select(p => p.Environment["GIT_CONFIG_KEY_0"]).Distinct().Should().Equal(["http.extraHeader"]);
        Writer.Logs.Lines.Should().NotContain(l => l.Contains(Token));
        // The clone persisted the hardening (no hooks, no info/ from a template), and nothing of the job is left.
        Writer.Started.First(p => p.ArgumentList.Contains("clone")).ArgumentList.Should().Contain("--template=");
        Directory.GetFileSystemEntries(Writer.Root).Should().BeEmpty();
    }

    public static TheoryData<string, string, object, string> BadRequests => new()
    {
        { "a queue branch outside mq/", "queue-branch", new { queueBranch = "main" }, "queueBranch" },
        { "an option-like base branch", "prepare", new { baseBranch = "-uattacker" }, "baseBranch" },
        { "a revision expression", "prepare", new { baseBranch = "main@{1}" }, "baseBranch" },
        { "a file remote", "prepare", new { cloneUrl = "file:///etc" }, "cloneUrl" },
        { "a remote with user info", "prepare", new { cloneUrl = "https://user:pass@example.invalid/x.git" }, "cloneUrl" },
        { "a short sha", "prepare", new { headSha = "abc123" }, "headSha" },
        { "an arbitrary head ref", "prepare", new { headRef = "refs/remotes/origin/x" }, "headRef" },
        { "no credential", "prepare", new { credential = (object?)null }, "credential" },
        { "an ident with a newline", "prepare", new { author = new { name = "a\nb", email = "a@example.invalid", date = AuthorDate } }, "author" },
        { "a key-less rule", "prepare", new { autoResolve = new object[] { new { path = "**/x.json", keys = Array.Empty<string>() } } }, "autoResolve" },
        // §10.3: the strategies and validators are a closed vocabulary; keys scope take-base only; take-base regenerates.
        { "an unknown strategy", "prepare", new { autoResolve = new object[] { new { path = "**/x.json", strategy = "merge-driver" } } }, "autoResolve" },
        { "keys on a keep-both rule", "prepare", new { autoResolve = new object[] { new { path = "**/x.json", strategy = "keep-both", keys = new[] { "a" } } } }, "autoResolve" },
        { "take-base that does not regenerate", "prepare", new { autoResolve = new object[] { new { path = "**/x.json", anyLine = true, regenerate = false } } }, "autoResolve" },
        { "an unknown validator", "prepare", new { autoResolve = new object[] { new { path = "**/x.json", strategy = "keep-both", validate = new[] { "yaml" } } } }, "autoResolve" },
        { "nine validators", "queue-branch", new { autoResolve = new object[] { new { path = "**/x.json", strategy = "keep-both", validate = Enumerable.Repeat("json", 9).ToArray() } } }, "autoResolve" },
        // O36: one patch per regenerate run, or the single patch of an older app, never both.
        { "a patch beside patches", "publish", new { patch = "ZGlmZg==", patches = new object[] { new { workflow = "regenerate.yml", patch = "" } } }, "patches" },
        { "a patch without its workflow", "publish", new { patches = new object[] { new { patch = "" } } }, "patches" },
        { "nine patches", "publish", new { patches = Enumerable.Range(0, 9).Select(i => (object)new { workflow = $"w{i}.yml", patch = "" }).ToArray() }, "patches" },
        // §0 D65: a date git would stamp itself makes the same request build another commit.
        { "an author without a date", "prepare", new { author = new { name = "a", email = "a@example.invalid" } }, "author" },
        { "a committer without a date", "publish", new { committer = new { name = "bot", email = "bot@example.invalid" } }, "committer" },
        { "a date before 1970", "queue-branch", new { committer = new { name = "bot", email = "bot@example.invalid", date = new DateTimeOffset(1969, 12, 31, 0, 0, 0, TimeSpan.Zero) } }, "committer" },
        // §0 D65: a rebuild is of the prepared commit, on the base it was prepared on.
        { "a queue-branch push without the base", "queue-branch", new { baseSha = (string?)null }, "baseSha" },
        { "a publish without the base", "publish", new { baseSha = (string?)null }, "baseSha" },
        { "a publish without the prepared commit", "publish", new { preparedSha = (string?)null }, "preparedSha" },
        // O25, §0 D67: the queue-branch push refuses a fork's .github change itself, so it must be told, never assume.
        { "a queue-branch push that does not say whether the head is a fork's", "queue-branch", new { headFromFork = (bool?)null }, "headFromFork" },
    };

    /// <summary>
    /// §0 D57: a refusal names the field and its rule, so the saga can end the entry with that reason at once instead of
    /// retrying a request that can never pass. It never echoes a value: the credential is one of them.
    /// </summary>
    [Theory]
    [MemberData(nameof(BadRequests))]
    public async Task A_request_that_does_not_validate_is_a_400_naming_the_field_and_starts_no_git(string because, string job, object overrides, string field)
    {
        var origin = Seed();
        var prepared = new Prepared(new string('b', 40), new string('a', 40), new string('c', 40));
        var valid = job switch
        {
            "queue-branch" => QueueBranchRequest(origin, prepared),
            "publish" => PublishRequest(origin, prepared),
            _ => PrepareRequest(origin, prepared.Head),
        };
        var body = Merge(valid, overrides);

        var response = await Writer.Client.PostAsJsonAsync($"/jobs/{job}", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because);
        var text = await response.Content.ReadAsStringAsync();
        using var refusal = JsonDocument.Parse(text);
        refusal.RootElement.GetProperty("outcome").GetString().Should().Be("refused");
        refusal.RootElement.GetProperty("field").GetString().Should().Be(field, because);
        refusal.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
        text.Should().NotContain(Token);
        Writer.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task An_oversized_or_malformed_body_is_a_400_naming_the_body()
    {
        var huge = new StringContent("{\"message\":\"" + new string('x', 300 * 1024) + "\"}", Encoding.UTF8, "application/json");

        foreach (var response in new[]
                 {
                     await Writer.Client.PostAsync("/jobs/prepare", huge),
                     await Writer.Client.PostAsync("/jobs/prepare", new StringContent("null", Encoding.UTF8, "application/json")),
                     await Writer.Client.PostAsync("/jobs/publish", new StringContent("{", Encoding.UTF8, "application/json")),
                 })
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            using var refusal = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            refusal.RootElement.GetProperty("field").GetString().Should().Be("body");
        }
        (await Writer.Client.GetAsync("/healthz")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void The_writers_compiled_references_hold_no_database_client_no_GitHub_App_and_no_Spark_runtime()
    {
        static bool Forbidden(string name)
            => name.StartsWith("Raven", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("Sparrow", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("Octokit", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("GitHubJwt", StringComparison.OrdinalIgnoreCase)
               || name.Equals("MintPlayer.Spark", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("MintPlayer.Spark.", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("CodeCoverage.", StringComparison.OrdinalIgnoreCase) && !name.Equals("CodeCoverage.GitWriter", StringComparison.OrdinalIgnoreCase) && !name.Equals("CodeCoverage.Git", StringComparison.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Assembly>([typeof(GitWriterHost).Assembly]);
        while (queue.TryDequeue(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is null || !seen.Add(reference.Name))
                    continue;
                try
                {
                    queue.Enqueue(Assembly.Load(reference));
                }
                catch (FileNotFoundException)
                {
                }
            }
        }
        seen.Where(Forbidden).Should().BeEmpty();
    }

    /// <summary>The body with some members replaced.</summary>
    private static Dictionary<string, object?> Merge(Dictionary<string, object?> body, object overrides)
        => With(body, overrides.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(overrides)));
}

/// <summary>The wall clock: a job that runs out of time has its git process tree killed and its directory removed.</summary>
public class JobTimeoutTests : WriterTestBase
{
    protected override IReadOnlyDictionary<string, string?>? WriterSettings => new Dictionary<string, string?> { ["GitWriter:JobTimeoutSeconds"] = "2" };

    [Fact]
    public async Task A_job_that_runs_out_of_time_is_killed_and_leaves_nothing()
    {
        var origin = Seed();
        var head = RollUpConflict(origin);
        Server.Delay = TimeSpan.FromSeconds(8);
        var clock = Stopwatch.StartNew();

        var response = await Writer.PrepareAsync(PrepareRequest(origin, head));

        response.Outcome.Should().Be("failed");
        response.Reason.Should().Be("timeout");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(7), "the process tree was killed, not waited for");
        Directory.GetFileSystemEntries(Writer.Root).Should().BeEmpty();
    }
}

/// <summary>The per-job disk cap (S8: 1 GB), here set to one byte.</summary>
public class JobDiskCapTests : WriterTestBase
{
    protected override IReadOnlyDictionary<string, string?>? WriterSettings => new Dictionary<string, string?> { ["GitWriter:MaxJobBytes"] = "1" };

    [Fact]
    public async Task A_job_over_its_disk_cap_is_stopped_and_leaves_nothing()
    {
        var origin = Seed();
        var head = RollUpConflict(origin);

        var response = await Writer.PrepareAsync(PrepareRequest(origin, head));

        response.Outcome.Should().Be("failed");
        response.Reason.Should().Be("too-large");
        origin.Ref($"refs/heads/{QueueBranch}").Should().BeNull();
        Directory.GetFileSystemEntries(Writer.Root).Should().BeEmpty();
    }
}

/// <summary>The host free-space floor: below it nothing starts.</summary>
public class JobDiskFloorTests : WriterTestBase
{
    protected override IReadOnlyDictionary<string, string?>? WriterSettings => new Dictionary<string, string?> { ["GitWriter:MinFreeBytes"] = long.MaxValue.ToString() };

    [Fact]
    public async Task Below_the_floor_a_job_is_refused_without_starting_git()
    {
        var origin = Seed();
        var head = RollUpConflict(origin);

        var response = await Writer.PrepareAsync(PrepareRequest(origin, head));

        response.Outcome.Should().Be("failed");
        response.Reason.Should().Be("disk-floor");
        Writer.Started.Should().BeEmpty();
    }
}

/// <summary>The slot limit: with one slot and no wait, a second job is answered busy.</summary>
public class JobSlotTests : WriterTestBase
{
    protected override IReadOnlyDictionary<string, string?>? WriterSettings => new Dictionary<string, string?>
    {
        ["GitWriter:MaxConcurrentJobs"] = "1",
        ["GitWriter:SlotWaitSeconds"] = "0",
    };

    [Fact]
    public async Task A_second_job_while_the_only_slot_is_taken_is_busy()
    {
        var origin = Seed();
        var head = RollUpConflict(origin);
        Server.Delay = TimeSpan.FromMilliseconds(500); // holds the first job inside its clone

        var first = Writer.PrepareAsync(PrepareRequest(origin, head));
        var deadline = Stopwatch.StartNew();
        while (!Writer.Started.Any(p => p.ArgumentList.Contains("clone")) && deadline.Elapsed < TimeSpan.FromSeconds(20))
            await Task.Delay(20);
        var second = await Writer.PrepareAsync(PrepareRequest(origin, head));

        second.Outcome.Should().Be("busy");
        (await first).Outcome.Should().Be("prepared");
    }

    [Fact]
    public async Task More_than_two_slots_are_refused_at_startup()
    {
        var act = async () => await WriterHost.StartAsync(new Dictionary<string, string?> { ["GitWriter:MaxConcurrentJobs"] = "3" });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
