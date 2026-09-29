using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Client.Authorization;

namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// Runs the QnA demo (<c>apps/QnA</c>, #460 M13) on <see cref="SparkAppTestHost"/>. Adds what only QnA
/// needs: the public base URL its account mails link to, the test seams that run Moderation's jobs on
/// request, and Moderation thresholds a fresh test account can meet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the thresholds move.</b> Every account here is minutes old, so the shipped gates (a voter must
/// be 7 days old with 50 reputation to give any, diversity over 3 voters, new accounts throttled to
/// 5 posts a day) would make every vote worth nothing. The overrides go through configuration — the
/// same layering an operator uses (<c>moderation.json</c> is the lowest source, D14) — so they also
/// prove that layering. What stays real: the vote write path, the ledger, the privilege provider,
/// the crediting and detector code, the caps other than the new-account throttle.
/// </para>
/// <para>
/// <b>Why the jobs never run on their own.</b> Their schedules are set to a date that never comes, and
/// the tests run them through <c>/qna-test/moderation/*</c> (<see cref="RunCreditingAsync"/>,
/// <see cref="RunFraudDetectorAsync"/>, <see cref="RecomputeAsync"/>). A test that waited for the
/// five-minute crediting job would be slow and still racy; one that the job could overtake would not
/// know what it measured. The crediting delay is 0 hours, so "delayed" here means "counted only once
/// the job has credited it" — the 48-hour figure itself is pinned by the unit tests on a controllable
/// clock (<c>ModerationFraudTests</c>).
/// </para>
/// </remarks>
public sealed class QnATestHost : SparkAppTestHost
{
    public static readonly SparkAppDescriptor QnA = new(
        AppName: "QnA",
        ProjectDirectory: Path.Combine("apps", "QnA", "QnA"),
        ProjectFileName: "QnA.csproj",
        DatabasePrefix: "SparkQnAE2E",
        CoverageSlug: "qna")
    {
        UsesMailPickup = true,
    };

    /// <summary>The <c>ObjectTypeId</c>s in <c>apps/QnA/QnA/App_Data/Model</c>.</summary>
    public static readonly Guid QuestionTypeId = Guid.Parse("40b97e47-51cc-4b9e-bb72-f288dbdaea7b");
    public static readonly Guid AnswerTypeId = Guid.Parse("39342297-cb55-4adb-b132-3c5e9b9fddcf");

    /// <summary>The queries: <c>GetQuestions</c>, <c>GetAnswers</c>, and the answers sub-query of a question.</summary>
    public static readonly Guid QuestionsQueryId = Guid.Parse("e893707a-c3a0-4cf8-8099-87a337f1f666");
    public static readonly Guid AnswersQueryId = Guid.Parse("e4a0db0c-7ce3-4344-b273-cadccf285b10");
    public static readonly Guid QuestionAnswersQueryId = Guid.Parse("4e13a000-0000-4000-8000-000000000001");

    /// <summary>The privilege the tests earn: <c>Downvote</c> at 10 reputation (one credited up-vote).</summary>
    public const int DownvoteRep = 10;

    public QnATestHost() : base(QnA) { }

    /// <summary>The seeded admin is QnA's moderator: the <c>Moderators</c> group holds every never-earnable right.</summary>
    protected override string AdminGroup => "Moderators";

    protected override Task ConfigureAppSettings(JsonObject settings, SparkAppHostContext context)
    {
        var spark = settings["Spark"]!.AsObject();

        // Outside Development an account mail is refused without it (#460 M5): a link built from the
        // request's Host header is password-reset poisoning.
        spark["Auth"] = new JsonObject { ["PublicBaseUrl"] = context.HttpsUrl };

        const string never = "0 0 31 2 *"; // 31 February: the jobs run only when a test asks.
        spark["Moderation"] = new JsonObject
        {
            ["Privileges"] = new JsonObject
            {
                // GroupId and Grants stay moderation.json's: configuration layers key by key.
                ["Upvote"] = Gates(rep: 0),
                ["Flag"] = Gates(rep: 0),
                ["Downvote"] = Gates(rep: DownvoteRep),
            },
            ["Fraud"] = new JsonObject
            {
                ["CreditDelayHours"] = 0,
                ["EligibleVoterMinAgeDays"] = 0,
                ["EligibleVoterMinReputation"] = 0,
                ["DiversityMinVoters"] = 1,
            },
            ["NewAccounts"] = new JsonObject { ["AccountAgeDays"] = 0 },
            ["Jobs"] = new JsonObject { ["CreditingSchedule"] = never, ["DetectorSchedule"] = never },
        };

        settings["QnA"] = new JsonObject { ["TestSeams"] = new JsonObject { ["Enabled"] = true } };
        return Task.CompletedTask;

        static JsonObject Gates(int rep) => new() { ["Rep"] = rep, ["MinAccountAgeDays"] = 0, ["MinActiveDays"] = 0 };
    }

    // ---------- users ----------

    /// <summary>
    /// A confirmed account with no groups, signed in. Its email is on the fictional <c>qna.example</c>
    /// domain; <paramref name="name"/> keeps accounts apart within a run.
    /// </summary>
    public async Task<QnAUser> CreateUserAsync(string name)
    {
        var email = $"{name}-{Suffix}@qna.example";
        var password = $"Aa1!{Guid.NewGuid():N}";
        var id = await SeedUserAsync(email, password, groupName: null);
        var client = NewClient();
        await client.LoginAsync(email, password);
        return new QnAUser(id, email, password, client);
    }

    /// <summary>The seeded admin (a moderator), signed in.</summary>
    public async Task<SparkClient> ModeratorAsync()
    {
        var client = NewClient();
        await client.LoginAsync(AdminEmailAddress, AdminPass);
        return client;
    }

    /// <summary>A client with no session; the caller disposes it.</summary>
    public SparkClient NewClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        return new SparkClient(new HttpClient(handler) { BaseAddress = new Uri(AppUrl) }, ownsClient: true);
    }

    // ---------- test seams (moderator only) ----------

    /// <summary>Runs Moderation's crediting job now; returns how many ledger entries it credited.</summary>
    public async Task<int> RunCreditingAsync()
    {
        using var moderator = await ModeratorAsync();
        var body = await moderator.PostJsonAsync("/qna-test/moderation/credit", new { });
        return body.GetProperty("credited").GetInt32();
    }

    /// <summary>Runs the nightly fraud detector now; returns its report.</summary>
    public async Task<JsonElement> RunFraudDetectorAsync()
    {
        using var moderator = await ModeratorAsync();
        return await moderator.PostJsonAsync("/qna-test/moderation/detect", new { });
    }

    /// <summary>Recomputes the stored reputation summaries of <paramref name="userIds"/> (what the badge and the privileges read).</summary>
    public async Task RecomputeAsync(params string[] userIds)
    {
        using var moderator = await ModeratorAsync();
        await moderator.PostJsonAsync("/qna-test/moderation/recompute", new { userIds });
    }

    // ---------- the database, read directly ----------

    /// <summary>How many RavenDB revisions <paramref name="documentId"/> has (a purge must leave 0).</summary>
    public async Task<int> RevisionCountAsync(string documentId)
    {
        using var store = OpenAppStore();
        using var session = store.OpenAsyncSession();
        var revisions = await session.Advanced.Revisions.GetMetadataForAsync(documentId, start: 0, pageSize: 1024);
        return revisions.Count;
    }

    // ---------- mail ----------

    /// <summary>
    /// Waits for a picked-up mail to <paramref name="to"/> whose subject contains <paramref name="subjectPart"/>.
    /// Mail is queued through Messaging, so it lands a moment after the request that caused it; the
    /// timeout is a failure bound, never an expected duration.
    /// </summary>
    public async Task<MimeKit.MimeMessage> WaitForMailAsync(string to, string subjectPart, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            foreach (var path in PickedUpMails())
            {
                MimeKit.MimeMessage message;
                try { message = await MimeKit.MimeMessage.LoadAsync(path); }
                catch (IOException) { continue; } // still being written
                if (message.To.Mailboxes.Any(m => string.Equals(m.Address, to, StringComparison.OrdinalIgnoreCase))
                    && message.Subject?.Contains(subjectPart, StringComparison.OrdinalIgnoreCase) == true)
                    return message;
            }

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"No mail to {to} with '{subjectPart}' in its subject was picked up. Mails: "
                    + string.Join(", ", PickedUpMails().Select(Path.GetFileName)) + $"\n{RecentLog(40)}");
            await Task.Delay(250);
        }
    }
}

/// <summary>
/// A stored question or answer as RavenDB holds it, for "what was written" assertions that must not go
/// through the API under test. The E2E project does not reference QnA's library, so this mirrors the
/// fields the tests read.
/// </summary>
public sealed class StoredPost
{
    public string? Title { get; set; }
    public string? Body { get; set; }
    public string? Tags { get; set; }
    public string? QuestionId { get; set; }
    public bool IsDraft { get; set; }
    public bool IsClosed { get; set; }
    public bool IsDeleted { get; set; }
    public string? AuthorId { get; set; }
    public string? CreatedBy { get; set; }
    public string? ModifiedBy { get; set; }
}

/// <summary>A signed-in QnA account. Disposing it disposes its client.</summary>
public sealed record QnAUser(string Id, string Email, string Password, SparkClient Client) : IDisposable
{
    public void Dispose() => Client.Dispose();
}

/// <summary>
/// The QnA protocol calls the typed client has no method for — Moderation's, SoftDelete's and
/// History's add-on endpoints, a raw <c>/spark/po/load</c> — over <see cref="SparkClient.SendAsync"/>,
/// which attaches the session and the antiforgery token like a browser holding the app would.
/// </summary>
public static class QnAProtocol
{
    /// <summary>POSTs JSON and returns the status and the parsed body (<c>default</c> for an empty one). Never throws on status.</summary>
    public static async Task<(int Status, JsonElement Body)> PostAsync(this SparkClient client, string url, object payload)
        => await SendJsonAsync(client, HttpMethod.Post, url, payload);

    /// <summary>Sends JSON with any method and returns the status and the parsed body. Never throws on status.</summary>
    public static async Task<(int Status, JsonElement Body)> SendJsonAsync(this SparkClient client, HttpMethod method, string url, object? payload)
    {
        using var response = await client.SendAsync(method, url, payload is null ? null : JsonContent.Create(payload), requiresAntiforgery: true);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>POSTs JSON and returns the body, or throws naming the status and body.</summary>
    public static async Task<JsonElement> PostJsonAsync(this SparkClient client, string url, object payload)
    {
        var (status, body) = await client.PostAsync(url, payload);
        if (status is < 200 or >= 300)
            throw new InvalidOperationException($"POST {url} answered {status}: {body}");
        return body;
    }

    /// <summary>The envelope's <c>result</c> (Spark's add-on endpoints answer <c>{ result, operations }</c>).</summary>
    public static JsonElement Result(this JsonElement envelope)
        => envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("result", out var result) ? result : default;

    // ---------- persistent objects ----------

    public static Task<MintPlayer.Spark.Abstractions.PersistentObject> AskAsync(this SparkClient client, string title, string body = "What is the idiomatic way to do this?", string? tags = null, bool draft = false)
        => client.CreatePersistentObjectAsync(new MintPlayer.Spark.Abstractions.PersistentObject
        {
            Name = "Question",
            ObjectTypeId = QnATestHost.QuestionTypeId,
            Attributes =
            [
                new MintPlayer.Spark.Abstractions.PersistentObjectAttribute { Name = "Title", Value = title, IsValueChanged = true },
                new MintPlayer.Spark.Abstractions.PersistentObjectAttribute { Name = "Body", Value = body, IsValueChanged = true },
                new MintPlayer.Spark.Abstractions.PersistentObjectAttribute { Name = "Tags", Value = tags, IsValueChanged = true },
                new MintPlayer.Spark.Abstractions.PersistentObjectAttribute { Name = "IsDraft", Value = draft, IsValueChanged = true },
            ],
        });

    public static Task<MintPlayer.Spark.Abstractions.PersistentObject> AnswerAsync(this SparkClient client, string questionId, string body = "Use the built-in one; it already handles the edge cases.")
        => client.CreatePersistentObjectAsync(new MintPlayer.Spark.Abstractions.PersistentObject
        {
            Name = "Answer",
            ObjectTypeId = QnATestHost.AnswerTypeId,
            Attributes =
            [
                new MintPlayer.Spark.Abstractions.PersistentObjectAttribute { Name = "QuestionId", Value = questionId, IsValueChanged = true },
                new MintPlayer.Spark.Abstractions.PersistentObjectAttribute { Name = "Body", Value = body, IsValueChanged = true },
            ],
        });

    /// <summary>Changes one attribute of a stored object through <c>/spark/po/update</c>, as the edit form does.</summary>
    public static async Task<MintPlayer.Spark.Abstractions.PersistentObject> EditAsync(this SparkClient client, Guid typeId, string id, string attribute, object? value)
    {
        var po = await client.GetPersistentObjectAsync(typeId, id)
            ?? throw new InvalidOperationException($"{id} is not visible to this caller.");
        po[attribute].Value = value;
        po[attribute].IsValueChanged = true;
        return await client.UpdatePersistentObjectAsync(po);
    }

    /// <summary><c>POST /spark/po/load</c> as JSON — the typed client does not surface <c>disabledActions</c>.</summary>
    public static Task<(int Status, JsonElement Body)> LoadRawAsync(this SparkClient client, Guid typeId, string id, string? deleted = null)
        => client.PostAsync("/spark/po/load", deleted is null
            ? new { objectTypeId = typeId.ToString(), id }
            : new { objectTypeId = typeId.ToString(), id, deleted });

    /// <summary>The <c>disabledActions</c> of a loaded object (empty when the field is absent).</summary>
    public static IReadOnlyList<string> DisabledActions(this JsonElement loadBody)
    {
        var po = loadBody.ValueKind == JsonValueKind.Object && loadBody.TryGetProperty("result", out var r) ? r : loadBody;
        return po.ValueKind == JsonValueKind.Object && po.TryGetProperty("disabledActions", out var list)
            ? list.EnumerateArray().Select(e => e.GetString()!).ToList()
            : [];
    }

    // ---------- moderation ----------

    public static Task<(int Status, JsonElement Body)> VoteAsync(this SparkClient client, Guid typeId, string id, int direction)
        => client.PostAsync("/spark/moderation/vote", new { objectTypeId = typeId.ToString(), id, direction });

    /// <summary>The score of <paramref name="id"/> as the caller sees it (null when the target is invisible).</summary>
    public static async Task<int?> ScoreAsync(this SparkClient client, Guid typeId, string id)
    {
        var body = await client.PostJsonAsync("/spark/moderation/votes", new { objectTypeId = typeId.ToString(), ids = new[] { id } });
        var state = body.Result().EnumerateArray().FirstOrDefault();
        return state.ValueKind == JsonValueKind.Object ? state.GetProperty("score").GetInt32() : null;
    }

    public static Task<(int Status, JsonElement Body)> FlagAsync(this SparkClient client, Guid typeId, string id, string reason)
        => client.PostAsync("/spark/moderation/flag", new { objectTypeId = typeId.ToString(), id, reason });

    /// <summary>The caller's own reputation: total, pending, earned privileges, suspended.</summary>
    public static async Task<(int Total, int Pending, IReadOnlyList<string> Privileges)> ReputationAsync(this SparkClient client)
    {
        var r = (await client.PostJsonAsync("/spark/moderation/reputation", new { })).Result();
        return (r.GetProperty("total").GetInt32(), r.GetProperty("pending").GetInt32(),
            r.GetProperty("privileges").EnumerateArray().Select(p => p.GetString()!).ToList());
    }
}
