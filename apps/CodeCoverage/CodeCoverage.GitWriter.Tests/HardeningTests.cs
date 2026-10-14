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

    public static TheoryData<string, object, string> BadRequests => new()
    {
        { "a queue branch outside mq/", new { queueBranch = "main" }, "queueBranch" },
        { "an option-like base branch", new { baseBranch = "-uattacker" }, "baseBranch" },
        { "a revision expression", new { baseBranch = "main@{1}" }, "baseBranch" },
        { "a file remote", new { cloneUrl = "file:///etc" }, "cloneUrl" },
        { "a remote with user info", new { cloneUrl = "https://user:pass@example.invalid/x.git" }, "cloneUrl" },
        { "a short sha", new { headSha = "abc123" }, "headSha" },
        { "an arbitrary head ref", new { headRef = "refs/remotes/origin/x" }, "headRef" },
        { "no credential", new { credential = (object?)null }, "credential" },
        { "an ident with a newline", new { author = new { name = "a\nb", email = "a@example.invalid" } }, "author" },
        { "a key-less rule", new { autoResolve = new object[] { new { path = "**/x.json", keys = Array.Empty<string>() } } }, "autoResolve" },
    };

    /// <summary>
    /// §0 D57: a refusal names the field and its rule, so the saga can end the entry with that reason at once instead of
    /// retrying a request that can never pass. It never echoes a value: the credential is one of them.
    /// </summary>
    [Theory]
    [MemberData(nameof(BadRequests))]
    public async Task A_request_that_does_not_validate_is_a_400_naming_the_field_and_starts_no_git(string because, object overrides, string field)
    {
        var origin = Seed();
        var body = Merge(PrepareRequest(origin, new string('a', 40)), overrides);

        var response = await Writer.Client.PostAsJsonAsync("/jobs/prepare", body);

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

    /// <summary>The anonymous body with some members replaced.</summary>
    private static Dictionary<string, object?> Merge(object body, object overrides)
    {
        var merged = body.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(body));
        foreach (var property in overrides.GetType().GetProperties())
            merged[property.Name] = property.GetValue(overrides);
        return merged;
    }
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
