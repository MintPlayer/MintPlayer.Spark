using System.Net;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Tests._Infrastructure;
using MintPlayer.Spark.Logging;
using MintPlayer.Spark.Logging.Models;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// Who reads the bot's logs on this server (#660 G14–G16), through the real composition root: the library's rule (a log
/// opens exactly when its related record does) under CodeCoverage's <c>security.json</c> (<c>Read/Log</c> for everyone,
/// <c>ListAll/Log</c> for the Administrators) and its own <c>LogActions</c> (the list for administrators, an exception's
/// text for the repository's managers).
/// </summary>
[Collection(CoverageWebHostCollection.Name)]
public class LogAccessEndToEndTests(OwnerPrivacyHostFixture host) : IClassFixture<OwnerPrivacyHostFixture>
{
    private const string LogTypeId = "571341c2-942f-509d-81d8-27892ffc5d3d";
    private const string LogsQueryId = "30f8c4ed-00e4-5d28-b513-badfa52cac8e";
    private const string ExceptionMarker = "log-access-marker-exception";
    private const string TextMarker = "log-access-marker-text";

    /// <summary>A command on acme's private repository, and its log.</summary>
    private static readonly string PrivateCommandId = CommandInvocation.DocumentId(EForgeProvider.GitHub, 4602, 91);
    private static readonly string PrivateLogId = LogIds.ForKey(CommandInvocation.LogKeyOf(PrivateCommandId));

    /// <summary>A backport's log on acme's public repository, related to the repository itself.</summary>
    private static readonly string PublicLogId = LogIds.ForKey("backports/github/4601/7/1");

    private async Task SeedAsync()
    {
        using var session = host.Store.OpenAsyncSession();
        await session.StoreAsync(new CommandInvocation
        {
            Repository = host.PrivateRepositoryId, Number = 3, CommentId = 91, Command = "/queue", Actor = "privacy-owner",
            ReceivedAtUtc = DateTime.UtcNow, Outcome = ECommandOutcome.Failed, LogId = PrivateLogId,
        }, PrivateCommandId);
        await session.StoreAsync(Log(PrivateCommandId), PrivateLogId);
        await session.StoreAsync(Log(host.PublicRepositoryId), PublicLogId);
        await session.SaveChangesAsync();
    }

    private static Log Log(string related) => new()
    {
        RelatedPersistentObjectId = related,
        Entries = [new LogEntry { At = DateTime.UtcNow, Level = "Error", Text = TextMarker, Exception = $"System.InvalidOperationException: {ExceptionMarker}" }],
    };

    private async Task<(HttpStatusCode Status, string Body)> LoadAsync(string id, string? user)
    {
        await SeedAsync();
        return await host.PostAsync("/spark/po/load", new { objectTypeId = LogTypeId, id }, user);
    }

    [Fact]
    public async Task A_visitor_opening_a_private_repositorys_log_is_asked_to_sign_in()
        => (await LoadAsync(PrivateLogId, null)).Status.Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task A_signed_in_stranger_finds_no_private_repositorys_log()
        => (await LoadAsync(PrivateLogId, OwnerPrivacyHostFixture.StrangerUserId)).Status.Should().Be(HttpStatusCode.NotFound);

    [Theory]
    [InlineData(null)]
    [InlineData(OwnerPrivacyHostFixture.StrangerUserId)]
    public async Task A_public_repositorys_log_opens_for_everyone_without_its_exception(string? user)
    {
        var (status, body) = await LoadAsync(PublicLogId, user);

        status.Should().Be(HttpStatusCode.OK);
        body.Should().Contain(TextMarker);
        body.Should().NotContain(ExceptionMarker, "an exception's text is for the repository's managers");
    }

    [Fact]
    public async Task A_member_who_sees_the_private_repository_reads_its_log_without_the_exception()
    {
        var (status, body) = await LoadAsync(PrivateLogId, OwnerPrivacyHostFixture.MemberUserId);

        status.Should().Be(HttpStatusCode.OK);
        body.Should().Contain(TextMarker);
        body.Should().NotContain(ExceptionMarker);
    }

    [Theory]
    [InlineData("private")]
    [InlineData("public")]
    public async Task The_accounts_manager_reads_the_exception(string which)
    {
        var (status, body) = await LoadAsync(which == "private" ? PrivateLogId : PublicLogId, OwnerPrivacyHostFixture.OwnerUserId);

        status.Should().Be(HttpStatusCode.OK);
        body.Should().Contain(ExceptionMarker);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(OwnerPrivacyHostFixture.StrangerUserId)]
    [InlineData(OwnerPrivacyHostFixture.OwnerUserId)]
    public async Task The_list_of_logs_is_withheld_from_everyone_but_the_administrators(string? user)
    {
        await SeedAsync();

        var (status, body) = await host.PostAsync("/spark/queries/execute", new { queryId = LogsQueryId }, user);

        status.Should().NotBe(HttpStatusCode.OK, "Read/Log implies Query, so CodeCoverage's LogActions withholds the list (G14)");
        body.Should().NotContain(TextMarker);
    }

    [Fact]
    public async Task An_administrator_lists_the_logs()
    {
        await SeedAsync();

        var (status, body) = await host.PostAsync("/spark/queries/execute", new { queryId = LogsQueryId }, OwnerPrivacyHostFixture.AdminUserId);

        status.Should().Be(HttpStatusCode.OK);
        body.Should().Contain(PublicLogId);
    }
}
