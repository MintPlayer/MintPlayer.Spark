namespace CodeCoverage.GitWriter.Tests._Infrastructure;

/// <summary>
/// A local smart-HTTP forge requiring a token, a writer per test, and the Spark-shaped seed every merge-queue test starts
/// from: <c>main</c> with three model files, the hash file and a hand-authored <c>translations.json</c>; the pull request
/// branches from it as <c>feature</c> and is served as <c>refs/pull/1/head</c>.
/// </summary>
public abstract class WriterTestBase : IAsyncLifetime
{
    /// <summary>A fixture value, not a real token; the tests assert it never leaves the job's environment.</summary>
    protected static readonly string Token = "ghs_fixture" + Guid.NewGuid().ToString("N");

    protected const string Translations = "apps/DemoApp/DemoApp/App_Data/translations.json";
    protected const string QueueBranch = "mq/main/pr-1";
    protected static readonly DateTimeOffset AuthorDate = new(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(2));

    /// <summary>The committer's date the saga pins for a run (§0 D65): every commit of the run carries it.</summary>
    protected static readonly DateTimeOffset CommitterDate = new(2026, 10, 14, 10, 30, 0, TimeSpan.Zero);

    private readonly TempDir origins = new("origins");
    private readonly List<Origin> created = [];

    internal GitHttpServer Server { get; private set; } = null!;

    internal WriterHost Writer { get; private set; } = null!;

    protected virtual IReadOnlyDictionary<string, string?>? WriterSettings => null;

    public async Task InitializeAsync()
    {
        Server = await GitHttpServer.StartAsync(origins.Path);
        Server.RequireCredential = ("x-access-token", Token);
        Writer = await WriterHost.StartAsync(WriterSettings);
    }

    public async Task DisposeAsync()
    {
        await Writer.DisposeAsync();
        await Server.DisposeAsync();
        foreach (var origin in created)
            origin.Dispose();
        origins.Dispose();
    }

    /// <summary>The seed, pushed: <c>main</c> at the base commit and <c>feature</c> checked out at the same commit.</summary>
    internal Origin Seed(string name = "repo")
    {
        var origin = new Origin(Server, name);
        created.Add(origin);
        foreach (var entity in ModelHashes.BaseEntities)
            origin.Write($"apps/DemoApp/DemoApp/App_Data/Model/{entity}.json", $"{{\n  \"name\": \"{entity}\"\n}}\n");
        origin.Write(ModelHashes.Path, ModelHashes.Render("base", ModelHashes.BaseEntities));
        origin.Write(Translations, "{\n  \"Address\": { \"en\": \"Address\" }\n}\n");
        origin.Write("README.md", "seed\n");
        origin.Commit("base");
        origin.Push("main");
        origin.Checkout("feature", "main");
        return origin;
    }

    /// <summary>
    /// A contributor's fork on the same forge holding <paramref name="branch"/> as it is in <paramref name="origin"/>'s work
    /// tree (O3: a fork's pull request lives there; the base only mirrors its head as <c>refs/pull/N/head</c>).
    /// </summary>
    internal Origin Fork(Origin origin, string branch, string name = "fork")
    {
        var fork = new Origin(Server, name);
        created.Add(fork);
        FixtureGit.Run(origin.Work, "push", "--quiet", fork.Bare, $"{branch}:refs/heads/{branch}");
        return fork;
    }

    /// <summary>One side adds an entity the way synchronize does: a model file, two map lines and new roll-ups.</summary>
    internal static void AddEntity(Origin origin, string entity, string rollUp, IEnumerable<string>? existing = null)
    {
        origin.Write($"apps/DemoApp/DemoApp/App_Data/Model/{entity}.json", $"{{\n  \"name\": \"{entity}\"\n}}\n");
        origin.Write(ModelHashes.Path, ModelHashes.Render(rollUp, (existing ?? ModelHashes.BaseEntities).Append(entity)));
    }

    /// <summary>The roll-up conflict of S1: main adds Customer, the pull request adds Order. Returns the PR head.</summary>
    internal static string RollUpConflict(Origin origin)
    {
        AddEntity(origin, "Order", "with-order");
        var head = origin.Commit("Add Order");
        origin.Push("feature").PullRequest(1, "feature");
        origin.Checkout("main");
        AddEntity(origin, "Customer", "with-customer");
        origin.Commit("Add Customer");
        origin.Push("main");
        return head;
    }

    protected static object Credential(string password) => new { username = "x-access-token", password };

    /// <summary>
    /// The fields of a prepare, which the queue-branch push and the publish repeat to rebuild the same commit (§0 D65).
    /// A dictionary, so a test can replace or drop any of them.
    /// </summary>
    internal static Dictionary<string, object?> PrepareRequest(Origin origin, string headSha, string? baseSha = null,
        object? autoResolve = null, string? password = null, bool allowWorkflowChanges = false)
        => new()
        {
            ["provider"] = "github",
            ["repoId"] = "1001",
            ["cloneUrl"] = origin.Url.AbsoluteUri,
            ["credential"] = Credential(password ?? Token),
            ["baseBranch"] = "main",
            ["baseSha"] = baseSha,
            ["headRef"] = "refs/pull/1/head",
            ["headSha"] = headSha,
            ["autoResolve"] = autoResolve ?? new object[] { new { path = "**/App_Data/modelHashes.json", keys = new[] { "modelHash", "contextRoots", "modelFiles" } } },
            ["author"] = new { name = "Pull Request Author", email = "author@example.invalid", date = AuthorDate },
            ["committer"] = new { name = "coverage-bot", email = "bot@example.invalid", date = CommitterDate },
            ["message"] = "Add Order (#1)\n\nCo-authored-by: Someone <someone@example.invalid>\n",
            ["allowWorkflowChanges"] = allowWorkflowChanges,
        };

    /// <summary><c>POST /jobs/queue-branch</c> for <paramref name="prepared"/>: the prepare's fields, its answer, the lease.</summary>
    internal static Dictionary<string, object?> QueueBranchRequest(Origin origin, Prepared prepared, string? expectedQueueSha = null, bool allowWorkflowChanges = false)
        => With(PrepareRequest(origin, prepared.Head, prepared.Base, allowWorkflowChanges: allowWorkflowChanges), new()
        {
            ["preparedSha"] = prepared.Sha,
            ["queueBranch"] = QueueBranch,
            ["expectedQueueSha"] = expectedQueueSha,
        });

    /// <summary><c>POST /jobs/publish</c> for <paramref name="prepared"/>: the prepare's fields, its answer, the patch.</summary>
    internal static Dictionary<string, object?> PublishRequest(Origin origin, Prepared prepared, string? patch = null, string[]? regeneratedPaths = null,
        Uri? pushUrl = null)
        => With(PrepareRequest(origin, prepared.Head, prepared.Base), new()
        {
            ["pushUrl"] = pushUrl?.AbsoluteUri,
            ["preparedSha"] = prepared.Sha,
            ["prBranch"] = "feature",
            ["patch"] = patch is null ? null : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(patch)),
            ["regeneratedPaths"] = regeneratedPaths ?? ["**/App_Data/modelHashes.json", "**/App_Data/Model/*.json"],
        });

    /// <summary><paramref name="request"/> with <paramref name="changes"/> set (a null value is sent as null).</summary>
    internal static Dictionary<string, object?> With(Dictionary<string, object?> request, Dictionary<string, object?> changes)
    {
        var copy = new Dictionary<string, object?>(request);
        foreach (var (key, value) in changes)
            copy[key] = value;
        return copy;
    }

    /// <summary>The roll-up conflict, prepared: returns the prepared SHA, the PR head and the base it was built on.</summary>
    internal async Task<Prepared> PreparedAsync(Origin origin)
    {
        var head = RollUpConflict(origin);
        var prepared = await Writer.PrepareAsync(PrepareRequest(origin, head));
        prepared.Outcome.Should().Be("prepared", prepared.Detail);
        return new Prepared(prepared.PreparedSha!, head, prepared.BaseSha!);
    }

    /// <summary>The prepared commit, as a dispatch needs it: pushed to the queue branch (§0 D65), which only the
    /// queue-branch push does.</summary>
    internal async Task<Prepared> PushedAsync(Origin origin)
    {
        var prepared = await PreparedAsync(origin);
        var pushed = await Writer.PushQueueBranchAsync(QueueBranchRequest(origin, prepared));
        pushed.Outcome.Should().Be("pushed", pushed.Detail);
        return prepared;
    }
}

/// <summary>A prepare's answer: the commit, the pinned head and the base it was built on.</summary>
internal sealed record Prepared(string Sha, string Head, string Base);
