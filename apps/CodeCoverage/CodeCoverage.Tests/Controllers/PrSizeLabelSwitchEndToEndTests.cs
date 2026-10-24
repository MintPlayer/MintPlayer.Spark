using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.PrSize;
using CodeCoverage.Tests._Infrastructure;
using MintPlayer.Spark.Messaging.Models;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// Switching the PR size labeller off asks whether to remove the labels already written (docs/coverage_pr_size_labels_PRD.md
/// D4.3), over the real <c>/spark/po/update</c> of the real composition root: the save answers 449 with the question and
/// stores nothing; "Remove labels" stores and queues a removal per inheriting repository, "Keep labels" stores and queues
/// none, and Cancel stores nothing. A changed scale queues a reconcile (D4.2).
/// </summary>
[Collection(CoverageWebHostCollection.Name)]
public class PrSizeLabelSwitchEndToEndTests(OwnerPrivacyHostFixture host) : IClassFixture<OwnerPrivacyHostFixture>
{
    private const string AccountTypeId = "a6c9a501-43a6-44f2-acf1-bafcfb2698cb";
    private const string Owner = OwnerPrivacyHostFixture.OwnerUserId;

    [Fact]
    public async Task Switching_off_asks_first_and_stores_nothing_before_the_answer()
    {
        await ArrangeAsync();
        try
        {
            var (status, body) = await SaveAsync(po => Set(po, "PrSizeLabels", false), retry: null);

            ((int)status).Should().Be(449, body);
            var retry = Retry(body);
            var options = retry.GetProperty("options").EnumerateArray().Select(o => o.GetString()).ToList();
            options.Should().Contain(PrSizeLabelSwitch.RemoveOption);
            options.Should().Contain(PrSizeLabelSwitch.KeepOption);
            retry.GetRawText().Should().Contain("the 1 repository that inherit");
            (await LoadAccountAsync()).PrSizeLabels.Should().BeTrue();
            (await MessagesAsync<RemovePrSizeLabelsMessage>(0)).Should().Be(0);
        }
        finally { await RestoreAsync(); }
    }

    [Theory]
    [InlineData(PrSizeLabelSwitch.RemoveOption, true, 1)]
    [InlineData(PrSizeLabelSwitch.KeepOption, true, 0)]
    [InlineData("Cancel", false, 0)]
    public async Task The_answer_decides_what_is_stored_and_queued(string answer, bool stored, int removals)
    {
        await ArrangeAsync();
        try
        {
            var (status, body) = await SaveAsync(po => Set(po, "PrSizeLabels", false), retry: null);
            var step = Retry(body).GetProperty("step").GetInt32();

            (status, body) = await SaveAsync(po => Set(po, "PrSizeLabels", false), retry: (step, answer));

            if (stored) status.Should().Be(HttpStatusCode.OK, body);
            (await LoadAccountAsync()).PrSizeLabels.Should().Be(!stored);
            (await MessagesAsync<RemovePrSizeLabelsMessage>(removals)).Should().Be(removals);
        }
        finally { await RestoreAsync(); }
    }

    [Fact]
    public async Task A_changed_scale_queues_a_reconcile_without_asking()
    {
        await ArrangeAsync();
        try
        {
            var (status, body) = await SaveAsync(po =>
            {
                var scale = po["attributes"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == "PrSizeLabelScale")!.AsObject();
                // An array AsDetail carries its rows as nested persistent objects in "objects"; its "value" is null.
                var rows = scale["objects"]!.AsArray();
                var m = rows.Single(r => Field(r!, "Name") == "size/M")!;
                SetField(m, "Name", "size/medium");
                scale["isValueChanged"] = true;
            }, retry: null);

            status.Should().Be(HttpStatusCode.OK, body);
            (await MessagesAsync<ReconcilePrSizeLabelsMessage>(1)).Should().Be(1);
        }
        finally { await RestoreAsync(); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The labeller on for acme, and the public repository (which inherits) carrying one label it wrote.</summary>
    private async Task ArrangeAsync()
    {
        using var session = host.Store.OpenAsyncSession();
        var account = await session.LoadAsync<Account>(host.AccountId);
        account.PrSizeLabels = true;
        if (account.PrSizeLabelScale.Count == 0) account.PrSizeLabelScale = PrSizeDefaults.Scale();
        var repository = await session.LoadAsync<Repository>(host.PublicRepositoryId);
        repository.PrSizeLabels = LookupReferences.EPrSizeLabelPolicy.Inherit;
        repository.PrSizeLabelState = [new ManagedLabel { RowId = "m", Name = "size/M", Color = "eebb00", Description = "30 to 99 lines" }];
        foreach (var message in await session.Query<SparkMessage>().Where(m => m.QueueName == CoverageQueues.PrSizeLabels).ToListAsync())
            session.Delete(message);
        await session.SaveChangesAsync();
        // The save's question counts the repositories through the overview index; let it see this state first.
        await session.Query<Repository, Indexes.Repositories_Overview>().Customize(c => c.WaitForNonStaleResults()).Take(1).ToListAsync();
    }

    /// <summary>The class shares one host: put back what the other tests assert on.</summary>
    private async Task RestoreAsync()
    {
        using var session = host.Store.OpenAsyncSession();
        var account = await session.LoadAsync<Account>(host.AccountId);
        account.PrSizeLabels = false;
        account.PrSizeLabelScale = PrSizeDefaults.Scale();
        (await session.LoadAsync<Repository>(host.PublicRepositoryId)).PrSizeLabelState = [];
        await session.SaveChangesAsync();
    }

    private async Task<Account> LoadAccountAsync()
    {
        using var session = host.Store.OpenAsyncSession();
        return await session.LoadAsync<Account>(host.AccountId);
    }

    /// <summary>
    /// The lane's messages of type <typeparamref name="T"/> for the public repository. The fan-out runs in the after-commit
    /// hook, which Spark delivers through its outbox after the response, so this waits up to 10 s for
    /// <paramref name="expected"/> of them. The bound is a failure bound: the count is returned as soon as it is reached.
    /// </summary>
    private async Task<int> MessagesAsync<T>(int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            using var session = host.Store.OpenAsyncSession();
            var messages = await session.Query<SparkMessage>().Customize(c => c.WaitForNonStaleResults())
                .Where(m => m.QueueName == CoverageQueues.PrSizeLabels).ToListAsync();
            var count = messages.Count(m => m.MessageType.Contains(typeof(T).Name, StringComparison.Ordinal)
                && m.PayloadJson.Contains(host.PublicRepositoryId, StringComparison.Ordinal));
            if (count >= expected || DateTime.UtcNow > deadline) return count;
            await Task.Delay(100);
        }
    }

    private static JsonElement Retry(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("operations").EnumerateArray()
            .Single(o => o.GetProperty("type").GetString() == "retry").Clone();
    }

    private static void Set(JsonObject po, string attribute, object value)
    {
        var target = po["attributes"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == attribute)!.AsObject();
        target["value"] = JsonValue.Create(value);
        target["isValueChanged"] = true;
    }

    private static string? Field(JsonNode row, string name)
        => row is JsonObject o && o["attributes"] is JsonArray attributes
            ? attributes.SingleOrDefault(a => a!["name"]!.GetValue<string>() == name)?["value"]?.GetValue<string>()
            : row[name]?.GetValue<string>();

    private static void SetField(JsonNode row, string name, string value)
    {
        if (row is JsonObject o && o["attributes"] is JsonArray attributes)
        {
            var attribute = attributes.Single(a => a!["name"]!.GetValue<string>() == name)!.AsObject();
            attribute["value"] = value;
            attribute["isValueChanged"] = true;
        }
        else row[name] = value;
    }

    /// <summary>Loads acme as the owner, edits it and posts it to <c>/spark/po/update</c>, with the prompt's answer if any.</summary>
    private async Task<(HttpStatusCode Status, string Body)> SaveAsync(Action<JsonObject> edit, (int Step, string Option)? retry)
    {
        var (status, body) = await host.PostAsync("/spark/po/load", new { objectTypeId = AccountTypeId, id = host.AccountId }, Owner);
        status.Should().Be(HttpStatusCode.OK, body);
        var po = JsonNode.Parse(body)!.AsObject();
        edit(po);
        var request = new JsonObject
        {
            ["objectTypeId"] = AccountTypeId,
            ["id"] = host.AccountId,
            ["persistentObject"] = po,
        };
        if (retry is { } answer)
            request["retryResults"] = new JsonArray(new JsonObject { ["step"] = answer.Step, ["option"] = answer.Option });
        return await host.PostRawAsync("/spark/po/update", request.ToJsonString(), Owner);
    }
}
