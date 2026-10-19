using System.Text.Json.Nodes;
using MintPlayer.Spark.Moderation.Documents;
using MintPlayer.Spark.Moderation.Migrations;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Migrations;

/// <summary>
/// <see cref="M_202610191545_ModerationTargetTypesById"/> (D28, commerce X9, S22) rewrites the <c>TargetType</c> of
/// every moderation document from the type's name to its id. The seeds are the shapes the moderation code wrote before
/// D28 (<c>SparkModeration</c>: <c>TargetType = Definition.Name</c>; the satellite audit sink: the CLR simple name),
/// stored through the document classes themselves, so the migration runs against what a database holds.
/// </summary>
public class ModerationTargetTypesByIdMigrationTests : SparkTestDriver
{
    private const string QuestionId = "8b1c0d2e-3f40-4a51-8b62-7c83d94ea5f6";
    private const string AnswerId = "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d";

    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["Question"] = QuestionId,
        ["Answer"] = AnswerId,
    };

    // The satellite sink stored the CLR simple name of a contribution document type.
    private static readonly Dictionary<string, string> ClrNames = new(StringComparer.Ordinal) { ["AnswerContribution"] = AnswerId };

    private async Task SeedLegacyAsync()
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new ModerationVote { VoterId = "users/1", TargetId = "questions/1", TargetType = "Question", Direction = 1, Day = "2026-10-01" }, "ModerationVotes/users/1/questions/1");
        await session.StoreAsync(new ReputationEvent { UserId = "users/2", Kind = "upvoted", Points = 10, TargetId = "questions/1", TargetType = "Question", Day = "2026-10-01" }, "ReputationEvents/1");
        await session.StoreAsync(new ReputationEvent { UserId = "users/2", Kind = "suspended", Points = 0 }, "ReputationEvents/2");
        await session.StoreAsync(new ModerationFlag { FlaggerId = "users/3", TargetId = "answers/1", TargetType = "Answer", Reason = "spam", CaseId = "ReviewCases/1" }, "ModerationFlags/users/3/answers/1");
        await session.StoreAsync(new ReviewCase { Kind = "flag", TargetId = "answers/1", TargetType = "Answer", FlagCount = 1 }, "ReviewCases/1");
        await session.StoreAsync(new ReviewCase { Kind = "reciprocal", AccountIds = ["users/1", "users/2"] }, "ReviewCases/2");
        await session.StoreAsync(new ModerationLock { TargetId = "questions/1", TargetType = "Question", LockedBy = "users/9" }, "ModerationLocks/questions/1");
        await session.StoreAsync(new ModerationAuditEntry { Action = "lock", TargetId = "questions/1", TargetType = "Question" }, "ModerationAuditEntries/1");
        await session.StoreAsync(new ModerationAuditEntry { Action = "remove-version", TargetId = "contributions/1", TargetType = "AnswerContribution" }, "ModerationAuditEntries/2");
        await session.StoreAsync(new ModerationAuditEntry { Action = "lock", TargetId = "gone/1", TargetType = "RemovedType" }, "ModerationAuditEntries/3");
        await session.SaveChangesAsync();
    }

    private async Task<string?> TargetTypeAsync(string id)
    {
        using var reader = Store.OpenAsyncSession();
        var command = new Raven.Client.Documents.Commands.GetDocumentsCommand(Store.Conventions, id, includes: null, metadataOnly: false);
        await reader.Advanced.RequestExecutor.ExecuteAsync(command, reader.Advanced.Context);
        return JsonNode.Parse(command.Result.Results[0]!.ToString()!)!["TargetType"]?.GetValue<string>();
    }

    [Fact]
    public async Task Before_the_migration_the_documents_hold_names()
    {
        await SeedLegacyAsync();

        (await TargetTypeAsync("ModerationVotes/users/1/questions/1")).Should().Be("Question", "the seed is the pre-D28 shape");
        Guid.TryParse(await TargetTypeAsync("ReviewCases/1"), out _).Should().BeFalse();
    }

    [Fact]
    public async Task Every_stored_name_becomes_the_type_id()
    {
        await SeedLegacyAsync();

        var unknown = await ModerationTargetTypeUpgrade.RunAsync(Store, Names, ClrNames);

        (await TargetTypeAsync("ModerationVotes/users/1/questions/1")).Should().Be(QuestionId);
        (await TargetTypeAsync("ReputationEvents/1")).Should().Be(QuestionId);
        (await TargetTypeAsync("ReputationEvents/2")).Should().BeNull("an entry without a target stays without one");
        (await TargetTypeAsync("ModerationFlags/users/3/answers/1")).Should().Be(AnswerId);
        (await TargetTypeAsync("ReviewCases/1")).Should().Be(AnswerId);
        (await TargetTypeAsync("ReviewCases/2")).Should().BeNull();
        (await TargetTypeAsync("ModerationLocks/questions/1")).Should().Be(QuestionId);
        (await TargetTypeAsync("ModerationAuditEntries/1")).Should().Be(QuestionId);
        (await TargetTypeAsync("ModerationAuditEntries/2")).Should().Be(AnswerId, "the satellite sink's CLR simple name maps too, in the audit log");
        (await TargetTypeAsync("ModerationAuditEntries/3")).Should().Be("RemovedType", "a name no type has is left as it is");
        unknown.Should().ContainSingle().Which.Value.Should().Be("RemovedType");
    }

    [Fact]
    public async Task A_clr_simple_name_maps_only_in_the_audit_log()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new ModerationVote { VoterId = "users/1", TargetId = "x/1", TargetType = "AnswerContribution" }, "ModerationVotes/users/1/x/1");
            await session.SaveChangesAsync();
        }

        await ModerationTargetTypeUpgrade.RunAsync(Store, Names, ClrNames);

        (await TargetTypeAsync("ModerationVotes/users/1/x/1")).Should().Be("AnswerContribution", "only the audit sink ever wrote CLR names");
    }

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        await SeedLegacyAsync();
        await ModerationTargetTypeUpgrade.RunAsync(Store, Names, ClrNames);
        var once = await TargetTypeAsync("ReviewCases/1");
        string? changeVector;
        using (var session = Store.OpenAsyncSession())
            changeVector = session.Advanced.GetChangeVectorFor(await session.LoadAsync<ReviewCase>("ReviewCases/1"));

        var unknown = await ModerationTargetTypeUpgrade.RunAsync(Store, Names, ClrNames);

        (await TargetTypeAsync("ReviewCases/1")).Should().Be(once);
        using (var session = Store.OpenAsyncSession())
            session.Advanced.GetChangeVectorFor(await session.LoadAsync<ReviewCase>("ReviewCases/1")).Should().Be(changeVector, "no document is rewritten");
        unknown.Should().ContainSingle().Which.Value.Should().Be("RemovedType");
    }

    [Fact]
    public void The_maps_come_from_the_model_and_skip_ambiguous_clr_names()
    {
        var types = new[]
        {
            new Abstractions.EntityTypeDefinition { Id = Guid.Parse(QuestionId), Name = "WebshopQuestion", ClrType = "Qna.Question" },
            new Abstractions.EntityTypeDefinition { Id = Guid.Parse(AnswerId), Name = "Answer", ClrType = "Qna.Answer" },
            new Abstractions.EntityTypeDefinition { Id = Guid.NewGuid(), Name = "Other", ClrType = "Lib.Answer" },
        };

        ((object)ModerationTargetTypeUpgrade.NameMap(types)).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["WebshopQuestion"] = QuestionId, ["Answer"] = AnswerId, ["Other"] = types[2].Id.ToString("D"),
        });
        ((object)ModerationTargetTypeUpgrade.ClrSimpleNameMap(types)).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["Question"] = QuestionId,
        }); // 'Answer' is a type's name and two classes are called Answer
    }
}
