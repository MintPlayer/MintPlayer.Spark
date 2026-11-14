using System.Net;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Logging;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Organizations;

namespace MintPlayer.Spark.Tests.Logging;

/// <summary>
/// Who reads a log (#660 G15/G16): one that belongs to a record opens exactly for that record's readers, through the
/// record's own gates (here the organizations package's row rule on <see cref="OgNote"/>), and fails closed when the
/// record is gone. Rights come from the application: the library grants nothing.
/// </summary>
public class LogAccessTests : OrganizationsTestHost
{
    // The library's stamped ids (UUIDv5 over 'logging:…', App_Data/Model/*.json).
    internal const string LogTypeId = "571341c2-942f-509d-81d8-27892ffc5d3d";
    internal const string LogsQueryId = "30f8c4ed-00e4-5d28-b513-badfa52cac8e";

    public LogAccessTests()
    {
        ExtraSpark = spark => spark.AddLogs();
        // What an application opts into: detail loads for every signed-in user, gated per log by LogActions.
        ExtraRights.Add(new() { Key = "logs", Resource = "Read/Log", GroupId = SparkTestSecurity.AuthenticatedGroupId });
    }

    private async Task<string> WriteLogAsync(string key, string? relatedPersistentObjectId, string text)
    {
        await Factory.GetService<ILoggerScopeProvider>().LogAsync(key, LogLevel.Information, text, relatedPersistentObjectId: relatedPersistentObjectId);
        return LogIds.ForKey(key);
    }

    private Task<(HttpStatusCode Status, System.Text.Json.JsonElement Body)> LoadLogAsync(string id, string? userId)
        => SendAsync("/spark/po/load", Wire.Typed(LogTypeId, id: id), userId);

    [Fact]
    public async Task A_log_of_a_record_opens_for_its_readers_only()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        var organization = await SeedOrganizationAsync("Acme", ann);
        var note = await SeedNoteAsync("Plan", organization.Id);
        var id = await WriteLogAsync("tests/note-log", note.Id, "Merged the plan");

        var (status, body) = await LoadLogAsync(id, ann.Id);
        status.Should().Be(HttpStatusCode.OK);
        body.GetRawText().Should().Contain("Merged the plan");

        (await LoadLogAsync(id, bob.Id)).Status.Should().Be(HttpStatusCode.NotFound, "bob may not read the note, so not its log");
        (await LoadLogAsync(id, null)).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_log_whose_record_is_gone_opens_for_nobody()
    {
        var ann = await SeedUserAsync("ann");
        var id = await WriteLogAsync("tests/orphan", "OgNotes/does-not-exist", "Orphaned");

        (await LoadLogAsync(id, ann.Id)).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_log_of_no_record_follows_the_Read_right_alone()
    {
        var ann = await SeedUserAsync("ann");
        var id = await WriteLogAsync("tests/standalone", null, "Standalone");

        (await LoadLogAsync(id, ann.Id)).Status.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// <c>Read</c> implies <c>Query</c> in Spark, so the <c>Read/Log</c> grant opens the list too. Its rows pass the
    /// same gate as a detail load, or the list would hand out every log a detail load refuses.
    /// </summary>
    [Fact]
    public async Task The_list_holds_only_the_logs_the_caller_may_open()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        var organization = await SeedOrganizationAsync("Acme", ann);
        var note = await SeedNoteAsync("Plan", organization.Id);
        var noteLog = await WriteLogAsync("tests/note-log", note.Id, "Merged the plan");
        await WriteLogAsync("tests/orphan", "OgNotes/does-not-exist", "Orphaned");
        var standalone = await WriteLogAsync("tests/standalone", null, "Standalone");

        (await QueryIdsAsync(Wire.Query(LogsQueryId), ann.Id!)).Should().BeEquivalentTo([noteLog, standalone]);
        (await QueryIdsAsync(Wire.Query(LogsQueryId), bob.Id!)).Should().BeEquivalentTo([standalone]);
    }
}
