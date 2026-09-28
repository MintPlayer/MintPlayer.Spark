using System.Net;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.E2E.Tests._Infrastructure;
using static MintPlayer.Spark.E2E.Tests._Infrastructure.QnATestHost;

namespace MintPlayer.Spark.E2E.Tests.QnA;

/// <summary>
/// QnA's content rules end to end: SoftDelete (M6), History (M7), the row policy and interceptors
/// QnA adds through the core seam (M2), and <c>OnDisableActionsAsync</c> (M3).
/// </summary>
[Collection(QnAE2ECollection.Name)]
public class QnAContentTests
{
    private readonly QnATestHost host;

    public QnAContentTests(QnAE2ECollectionFixture fixture) => host = fixture.Host;

    // ---------- SoftDelete (M6) ----------

    /// <summary>
    /// A deleted answer disappears from every read path — detail, the list, the question's answers
    /// sub-query — for everyone, its author included, until a moderator restores it from the recycle bin.
    /// </summary>
    [Fact]
    public async Task A_deleted_answer_is_hidden_everywhere_until_a_moderator_restores_it()
    {
        using var asker = await host.CreateUserAsync("sd-asker");
        using var answerer = await host.CreateUserAsync("sd-answerer");
        using var moderator = await host.ModeratorAsync();
        using var anonymous = host.NewClient();
        var question = await asker.Client.AskAsync("Where did my answer go? " + Guid.NewGuid().ToString("N"));
        var answer = await answerer.Client.AnswerAsync(question.Id!);

        await answerer.Client.DeletePersistentObjectAsync(AnswerTypeId, answer.Id!);
        (await host.LoadAsync<StoredPost>(answer.Id!))!.IsDeleted.Should().BeTrue("a delete marks the document, it does not remove it");

        await host.WaitForIndexingAsync();
        (await anonymous.GetPersistentObjectAsync(AnswerTypeId, answer.Id!)).Should().BeNull();
        (await answerer.Client.GetPersistentObjectAsync(AnswerTypeId, answer.Id!)).Should().BeNull("its author does not see it either");
        (await anonymous.ExecuteQueryAsync(QuestionAnswersQueryId, take: 100, parentId: question.Id, parentType: "Question"))
            .Items.Select(i => i.Id).Should().NotContain(answer.Id!);
        (await asker.Client.ExecuteQueryAsync(AnswersQueryId, take: 200)).Items.Select(i => i.Id).Should().NotContain(answer.Id!);

        // The recycle bin: ViewDeleted holders ask for it; everyone else's flag is ignored.
        (await moderator.ExecuteQueryAsync(AnswersQueryId, take: 200, deleted: SparkDeletedFilter.Only))
            .Items.Select(i => i.Id).Should().Contain(answer.Id!);
        (await asker.Client.ExecuteQueryAsync(AnswersQueryId, take: 200, deleted: SparkDeletedFilter.Only))
            .Items.Select(i => i.Id).Should().NotContain(answer.Id!);
        (await moderator.GetPersistentObjectAsync(AnswerTypeId, answer.Id!, deleted: SparkDeletedFilter.Include)).Should().NotBeNull();

        var (authorRestore, _) = await answerer.Client.PostAsync("/spark/po/restore", new { objectTypeId = AnswerTypeId.ToString(), id = answer.Id });
        authorRestore.Should().NotBe(200, "Restore is a moderator's right");

        await moderator.PostJsonAsync("/spark/po/restore", new { objectTypeId = AnswerTypeId.ToString(), id = answer.Id });
        (await anonymous.GetPersistentObjectAsync(AnswerTypeId, answer.Id!)).Should().NotBeNull();
        await host.WaitForIndexingAsync();
        (await anonymous.ExecuteQueryAsync(QuestionAnswersQueryId, take: 100, parentId: question.Id, parentType: "Question"))
            .Items.Select(i => i.Id).Should().Contain(answer.Id!);
    }

    /// <summary>A purge (GDPR) removes a deleted answer and every revision of it; it is refused for a live row.</summary>
    [Fact]
    public async Task Purge_removes_a_deleted_answer_and_its_revisions_for_good()
    {
        using var asker = await host.CreateUserAsync("purge-asker");
        using var moderator = await host.ModeratorAsync();
        var question = await asker.Client.AskAsync("A question with a regrettable answer " + Guid.NewGuid().ToString("N"));
        var answer = await asker.Client.AnswerAsync(question.Id!, "Something that has to go.");
        await asker.Client.EditAsync(AnswerTypeId, answer.Id!, "Body", "Something that has to go, edited.");
        (await host.RevisionCountAsync(answer.Id!)).Should().BeGreaterThan(0, "revisions are enabled from the model");

        var (live, _) = await moderator.PostAsync("/spark/po/purge", new { objectTypeId = AnswerTypeId.ToString(), id = answer.Id });
        live.Should().NotBe(200, "only a deleted row can be purged");

        await moderator.DeletePersistentObjectAsync(AnswerTypeId, answer.Id!);
        await moderator.PostJsonAsync("/spark/po/purge", new { objectTypeId = AnswerTypeId.ToString(), id = answer.Id });

        (await host.LoadAsync<StoredPost>(answer.Id!)).Should().BeNull();
        (await host.RevisionCountAsync(answer.Id!)).Should().Be(0, "a purge deletes the revisions too");
    }

    // ---------- History (M7) ----------

    /// <summary>
    /// Revisions list newest first with the editor's name (resolved at read time from the stored id),
    /// an old revision reads back, and a moderator's revert goes through the save pipeline: the old
    /// title returns, the author stays the author, the reverter is stamped as the modifier.
    /// </summary>
    [Fact]
    public async Task History_lists_revisions_with_names_and_a_moderator_reverts_through_the_pipeline()
    {
        using var author = await host.CreateUserAsync("history-author");
        using var moderator = await host.ModeratorAsync();
        var question = await author.Client.AskAsync("Title v1");
        await author.Client.EditAsync(QuestionTypeId, question.Id!, "Title", "Title v2");

        var request = new { objectTypeId = QuestionTypeId.ToString(), id = question.Id };
        var revisions = (await author.Client.PostJsonAsync("/spark/po/revisions", request)).Result().EnumerateArray().ToList();
        revisions.Count.Should().BeGreaterThanOrEqualTo(2);
        var oldest = revisions[^1];
        oldest.GetProperty("userId").GetString().Should().Be(author.Id);
        oldest.GetProperty("userName").GetString().Should().NotBeNullOrEmpty("QnAUserNames resolves the id to the account's name");
        var changeVector = oldest.GetProperty("changeVector").GetString()!;

        var old = (await author.Client.PostJsonAsync("/spark/po/revision", new { objectTypeId = QuestionTypeId.ToString(), id = question.Id, changeVector })).Result();
        AttributeValue(old, "Title").Should().Be("Title v1");

        var revert = new { objectTypeId = QuestionTypeId.ToString(), id = question.Id, changeVector };
        var (authorRevert, _) = await author.Client.PostAsync("/spark/po/revert", revert);
        authorRevert.Should().NotBe(200, "Revert is a moderator's right");

        await moderator.PostJsonAsync("/spark/po/revert", revert);
        var stored = (await host.LoadAsync<StoredPost>(question.Id!))!;
        stored.Title.Should().Be("Title v1");
        stored.CreatedBy.Should().Be(author.Id);
        stored.AuthorId.Should().Be(author.Id, "Moderation keeps the author whatever the revision says");
        stored.ModifiedBy.Should().NotBe(author.Id, "the revert is stamped with the moderator");
    }

    // ---------- the M2 seam ----------

    /// <summary>QnA's row filter policy: a draft is visible to its author only, on every read path.</summary>
    [Fact]
    public async Task A_draft_question_is_visible_to_its_author_only()
    {
        using var author = await host.CreateUserAsync("draft-author");
        using var other = await host.CreateUserAsync("draft-other");
        using var anonymous = host.NewClient();
        var draft = await author.Client.AskAsync("Half-written thoughts " + Guid.NewGuid().ToString("N"), draft: true);

        (await author.Client.GetPersistentObjectAsync(QuestionTypeId, draft.Id!)).Should().NotBeNull();
        (await other.Client.GetPersistentObjectAsync(QuestionTypeId, draft.Id!)).Should().BeNull();
        (await anonymous.GetPersistentObjectAsync(QuestionTypeId, draft.Id!)).Should().BeNull();

        await host.WaitForIndexingAsync();
        (await author.Client.ExecuteQueryAsync(QuestionsQueryId, take: 200)).Items.Select(i => i.Id).Should().Contain(draft.Id!);
        (await other.Client.ExecuteQueryAsync(QuestionsQueryId, take: 200)).Items.Select(i => i.Id).Should().NotContain(draft.Id!);

        var (voted, _) = await other.Client.VoteAsync(QuestionTypeId, draft.Id!, +1);
        voted.Should().Be(404, "a vote on a post the caller cannot see is refused like a missing one (#453)");

        var answer = async () => await other.Client.AnswerAsync(draft.Id!);
        (await answer.Should().ThrowAsync<SparkClientException>()).Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>QnA's tags interceptor normalises on every save.</summary>
    [Fact]
    public async Task Tags_are_normalised_on_save()
    {
        using var author = await host.CreateUserAsync("tags-author");
        var question = await author.Client.AskAsync("Tagged question " + Guid.NewGuid().ToString("N"), tags: "C#, c# ,Spark;RavenDB");

        (await host.LoadAsync<StoredPost>(question.Id!))!.Tags.Should().Be("c#, spark, ravendb");

        var tooMany = async () => await author.Client.EditAsync(QuestionTypeId, question.Id!, "Tags", "a, b, c, d, e, f");
        (await tooMany.Should().ThrowAsync<SparkClientException>()).Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---------- OnDisableActionsAsync (M3) + the closed-question interceptor (M2) ----------

    /// <summary>
    /// Close / Reopen are offered to the author only (and moderators), the one that applies; a closed
    /// question refuses new answers (an interceptor, so on every write path); reopening accepts them again.
    /// </summary>
    [Fact]
    public async Task A_closed_question_refuses_new_answers_and_only_its_author_or_a_moderator_closes_it()
    {
        using var author = await host.CreateUserAsync("close-author");
        using var other = await host.CreateUserAsync("close-other");
        var question = await author.Client.AskAsync("Is this still open? " + Guid.NewGuid().ToString("N"));

        var (_, forOther) = await other.Client.LoadRawAsync(QuestionTypeId, question.Id!);
        forOther.DisabledActions().Should().Contain("CloseQuestion").And.Contain("ReopenQuestion");
        var (_, forAuthor) = await author.Client.LoadRawAsync(QuestionTypeId, question.Id!);
        forAuthor.DisabledActions().Should().Contain("ReopenQuestion").And.NotContain("CloseQuestion");

        var otherParent = await other.Client.GetPersistentObjectAsync(QuestionTypeId, question.Id!);
        var otherClose = async () => await other.Client.ExecuteActionAsync(QuestionTypeId, "CloseQuestion", parent: otherParent);
        (await otherClose.Should().ThrowAsync<SparkClientException>()).Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var authorParent = await author.Client.GetPersistentObjectAsync(QuestionTypeId, question.Id!);
        await author.Client.ExecuteActionAsync(QuestionTypeId, "CloseQuestion", parent: authorParent);
        (await host.LoadAsync<StoredPost>(question.Id!))!.IsClosed.Should().BeTrue();

        var answerClosed = async () => await other.Client.AnswerAsync(question.Id!);
        (await answerClosed.Should().ThrowAsync<SparkClientException>()).Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var closedParent = await author.Client.GetPersistentObjectAsync(QuestionTypeId, question.Id!);
        await author.Client.ExecuteActionAsync(QuestionTypeId, "ReopenQuestion", parent: closedParent);
        (await host.LoadAsync<StoredPost>(question.Id!))!.IsClosed.Should().BeFalse();
        (await other.Client.AnswerAsync(question.Id!)).Id.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// The author cannot delete a question that has answers: Delete is withheld at load and refused with
    /// 403 at submit. Once the answer is gone (soft-deleted), the question can be deleted.
    /// </summary>
    [Fact]
    public async Task The_author_cannot_delete_a_question_that_has_answers()
    {
        using var author = await host.CreateUserAsync("keep-author");
        using var answerer = await host.CreateUserAsync("keep-answerer");
        var question = await author.Client.AskAsync("Worth keeping? " + Guid.NewGuid().ToString("N"));
        var answer = await answerer.Client.AnswerAsync(question.Id!);
        await host.WaitForIndexingAsync();

        var (_, loaded) = await author.Client.LoadRawAsync(QuestionTypeId, question.Id!);
        loaded.DisabledActions().Should().Contain("Delete");
        var delete = async () => await author.Client.DeletePersistentObjectAsync(QuestionTypeId, question.Id!);
        (await delete.Should().ThrowAsync<SparkClientException>()).Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await answerer.Client.DeletePersistentObjectAsync(AnswerTypeId, answer.Id!);
        await host.WaitForIndexingAsync();
        var (_, reloaded) = await author.Client.LoadRawAsync(QuestionTypeId, question.Id!);
        reloaded.DisabledActions().Should().NotContain("Delete", "a deleted answer does not count");
        await author.Client.DeletePersistentObjectAsync(QuestionTypeId, question.Id!);
        (await host.LoadAsync<StoredPost>(question.Id!))!.IsDeleted.Should().BeTrue();
    }

    private static string? AttributeValue(JsonElement persistentObject, string name)
        => persistentObject.GetProperty("attributes").EnumerateArray()
            .First(a => a.GetProperty("name").GetString() == name)
            .GetProperty("value").ToString();
}
