using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.E2E.Tests._Infrastructure;
using static MintPlayer.Spark.E2E.Tests._Infrastructure.QnATestHost;

using PO = MintPlayer.Spark.Abstractions.PersistentObject;

namespace MintPlayer.Spark.E2E.Tests.QnA;

/// <summary>
/// Contributions M6 over the API, against QnA's real wiring: <c>Question.Translations</c> is a
/// <c>[Contribution]</c>, QnA's security.json grants the rights, <c>QuestionActions</c> lets every
/// signed-in user edit a question for its translations only, and Moderation sees a translator's version
/// as a post. The library's own behaviour (hydration, latest-wins, the concurrency pin, rebuild) is
/// pinned by <c>ContributionsRuntimeTests</c>/<c>ContributionsSurfaceTests</c>; what is here is what
/// only the app's rights and rules decide.
/// </summary>
[Collection(QnAE2ECollection.Name)]
public class QnAContributionsTests
{
    private readonly QnATestHost host;

    public QnAContributionsTests(QnAE2ECollectionFixture fixture) => host = fixture.Host;

    private static string CurrentId(string questionId, string language, string script) => $"{questionId}/Translations/{language}/{script}";

    /// <summary>
    /// A member who is not the author saves a translation of the question; the title they also posted is
    /// dropped (QnA's per-row rule), and the question itself is not rewritten — no new modifier, no new
    /// revision — because only the contribution documents changed.
    /// </summary>
    [Fact]
    public async Task A_member_translates_someone_elses_question_but_cannot_change_its_title()
    {
        using var author = await host.CreateUserAsync("tr-author");
        using var translator = await host.CreateUserAsync("tr-translator");
        var question = await author.Client.AskAsync("How do I translate this? " + Guid.NewGuid().ToString("N"));
        var revisionsBefore = await host.RevisionCountAsync(question.Id!);

        await translator.Client.SaveTranslationsAsync(question.Id!, po =>
        {
            po["Title"].Value = "Hijacked title";
            po["Title"].IsValueChanged = true;
            po.AddTranslation("nl", "Latn", "Hoe vertaal ik dit?", "Een vertaalde vraag.");
        });

        var stored = (await host.LoadAsync<StoredPost>(question.Id!))!;
        stored.Title.Should().StartWith("How do I translate this?", "only the author or a moderator writes the title");
        stored.ModifiedBy.Should().Be(author.Id, "a translation does not make the translator the question's modifier");
        (await host.RevisionCountAsync(question.Id!)).Should().Be(revisionsBefore, "the question document was not written");

        var current = (await host.LoadAsync<StoredTranslation>(CurrentId(question.Id!, "nl", "Latn")))!;
        current.ContributorId.Should().Be(translator.Id);
        current.Title.Should().Be("Hoe vertaal ik dit?");

        // The row everyone reads names the translator (resolved at read time) and counts the versions.
        using var anonymous = host.NewClient();
        var row = (await anonymous.GetPersistentObjectAsync(QuestionTypeId, question.Id!))!.Translations().Single();
        row.Id.Should().Be("nl/Latn");
        (row["ContributorName"].Value?.ToString()).Should().Be(translator.Email, "QnAUserNames resolves the id to the account's name");
        Convert.ToInt32(row["ContributionCount"].Value?.ToString()).Should().Be(1);

        // And the history is readable without signing in, like the rest of the site.
        var history = await anonymous.ExecuteQueryAsync(TranslationHistoryQueryId, take: 50, parentId: question.Id, parentType: "Question");
        history.Items.Select(i => i.Id).Should().Equal(current.ContributionId);
    }

    /// <summary>The author edits the title and translates in one save: both land.</summary>
    [Fact]
    public async Task The_author_changes_the_title_and_adds_a_translation_in_one_save()
    {
        using var author = await host.CreateUserAsync("tr-own");
        var question = await author.Client.AskAsync("Own title " + Guid.NewGuid().ToString("N"));

        await author.Client.SaveTranslationsAsync(question.Id!, po =>
        {
            po["Title"].Value = "Own title, edited";
            po["Title"].IsValueChanged = true;
            po.AddTranslation("fr", "Latn", "Titre", "Corps");
        });

        (await host.LoadAsync<StoredPost>(question.Id!))!.Title.Should().Be("Own title, edited");
        (await host.LoadAsync<StoredTranslation>(CurrentId(question.Id!, "fr", "Latn")))!.ContributorId.Should().Be(author.Id);
    }

    /// <summary>
    /// Two translators write the same language; the latest is shown. Revert is a moderator's right
    /// (<c>RevertContribution</c>): a member is refused, the moderator's revert hides the newer version,
    /// and removing the whole version (<c>Delete</c> on the current type) is the moderator's alone too.
    /// </summary>
    [Fact]
    public async Task A_moderator_reverts_a_translation_and_removes_the_whole_version()
    {
        using var author = await host.CreateUserAsync("tr-rv-author");
        using var first = await host.CreateUserAsync("tr-rv-first");
        using var second = await host.CreateUserAsync("tr-rv-second");
        using var moderator = await host.ModeratorAsync();
        var question = await author.Client.AskAsync("Revert me " + Guid.NewGuid().ToString("N"));
        var currentId = CurrentId(question.Id!, "de", "Latn");

        await first.Client.SaveTranslationsAsync(question.Id!, po => po.AddTranslation("de", "Latn", "Erste", "Die erste Fassung."));
        var firstVersion = (await host.LoadAsync<StoredTranslation>(currentId))!.ContributionId!;
        await second.Client.SaveTranslationsAsync(question.Id!, po => po.EditTranslation("de/Latn", "Zweite", "Die zweite Fassung."));
        (await host.LoadAsync<StoredTranslation>(currentId))!.ContributorId.Should().Be(second.Id, "latest wins");

        var revert = new { objectTypeId = TranslationContributionTypeId.ToString(), id = firstVersion };
        var (memberRevert, _) = await second.Client.PostAsync("/spark/po/revert-contribution", revert);
        memberRevert.Should().NotBe(200, "RevertContribution is a moderator's right");

        await moderator.PostJsonAsync("/spark/po/revert-contribution", revert);
        var reverted = (await host.LoadAsync<StoredTranslation>(currentId))!;
        reverted.ContributorId.Should().Be(first.Id);
        reverted.Title.Should().Be("Erste", "the reverted-to version keeps its text and its author");

        var (memberRemove, _) = await first.Client.PostAsync("/spark/po/delete", new { objectTypeId = TranslationCurrentTypeId.ToString(), id = currentId, etag = await host.EtagAsync(currentId) });
        memberRemove.Should().NotBe(200, "removing a whole version is a moderator's right");
        (await host.LoadAsync<StoredTranslation>(currentId)).Should().NotBeNull();

        await moderator.PostJsonAsync("/spark/po/delete", new { objectTypeId = TranslationCurrentTypeId.ToString(), id = currentId, etag = await host.EtagAsync(currentId) });
        (await host.LoadAsync<StoredTranslation>(currentId)).Should().BeNull("every visible version of de/Latn was hidden");
        (await host.LoadAsync<StoredTranslationVersion>(firstVersion))!.IsDeleted.Should().BeTrue("hidden, not destroyed: a moderator can restore it");
    }

    /// <summary>
    /// A translator's version is a post for Moderation (the generated type is <c>IModeratable</c> in
    /// QnA): a flag on it opens a review case, with the translator as its author.
    /// </summary>
    [Fact]
    public async Task A_flagged_translation_opens_a_review_case()
    {
        using var author = await host.CreateUserAsync("tr-flag-author");
        using var translator = await host.CreateUserAsync("tr-flag-translator");
        using var flagger = await host.CreateUserAsync("tr-flag-flagger");
        using var moderator = await host.ModeratorAsync();
        var question = await author.Client.AskAsync("Flag a translation " + Guid.NewGuid().ToString("N"));
        await translator.Client.SaveTranslationsAsync(question.Id!, po => po.AddTranslation("es", "Latn", "Compra relojes", "Spam."));
        var version = (await host.LoadAsync<StoredTranslation>(CurrentId(question.Id!, "es", "Latn")))!.ContributionId!;

        var (flagged, body) = await flagger.Client.FlagAsync(TranslationContributionTypeId, version, "Spam: this is an advertisement.");
        flagged.Should().BeOneOf([200, 204], body.ToString());

        await host.WaitForIndexingAsync();
        var queue = (await moderator.PostJsonAsync("/spark/moderation/cases", new { take = 200 })).Result();
        queue.EnumerateArray().Select(c => c.GetProperty("id").GetString()).Should().Contain($"ModerationCases/flag/{version}");
    }

    /// <summary>Anonymous visitors read translations but cannot write one: the save is refused, nothing is stored.</summary>
    [Fact]
    public async Task An_anonymous_visitor_cannot_translate()
    {
        using var author = await host.CreateUserAsync("tr-anon-author");
        using var anonymous = host.NewClient();
        var question = await author.Client.AskAsync("Anonymous translation " + Guid.NewGuid().ToString("N"));

        var save = async () => await anonymous.SaveTranslationsAsync(question.Id!, po => po.AddTranslation("it", "Latn", "Titolo", "Corpo"));

        await save.Should().ThrowAsync<SparkClientException>();
        (await host.LoadAsync<StoredTranslation>(CurrentId(question.Id!, "it", "Latn"))).Should().BeNull();
    }
}

/// <summary>A <c>QuestionTranslationsCurrent</c> document as RavenDB holds it.</summary>
public sealed class StoredTranslation
{
    public string? TargetId { get; set; }
    public string? ContributorId { get; set; }
    public string? ContributionId { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
    public int ContributionCount { get; set; }
}

/// <summary>A <c>QuestionTranslationsContribution</c> document (one translator's version) as RavenDB holds it.</summary>
public sealed class StoredTranslationVersion
{
    public string? ContributorId { get; set; }
    public string? Title { get; set; }
    public bool IsDeleted { get; set; }
    public string? DeleteReason { get; set; }
}

/// <summary>Editing <c>Question.Translations</c> the way the edit form does: load, change the rows, update.</summary>
public static class QnATranslations
{
    public static async Task SaveTranslationsAsync(this SparkClient client, string questionId, Action<PO> edit)
    {
        var po = await client.GetPersistentObjectAsync(QuestionTypeId, questionId)
            ?? throw new InvalidOperationException($"{questionId} is not visible to this caller.");
        edit(po);
        await client.UpdatePersistentObjectAsync(po);
    }

    public static IReadOnlyList<PO> Translations(this PO question)
        => ((PersistentObjectAttributeAsDetail)question["Translations"]).Objects ?? [];

    public static void AddTranslation(this PO question, string language, string script, string title, string body)
    {
        var rows = (PersistentObjectAttributeAsDetail)question["Translations"];
        rows.Objects = [.. rows.Objects ?? [], new PO
        {
            Name = "QuestionTranslation",
            ObjectTypeId = TranslationTypeId,
            Attributes =
            [
                new PersistentObjectAttribute { Name = "Language", DataType = "string", Value = language, IsValueChanged = true },
                new PersistentObjectAttribute { Name = "Script", DataType = "string", Value = script, IsValueChanged = true },
                new PersistentObjectAttribute { Name = "Title", DataType = "string", Value = title, IsValueChanged = true },
                new PersistentObjectAttribute { Name = "Body", DataType = "MultiLineString", Value = body, IsValueChanged = true },
            ],
        }];
        rows.IsValueChanged = true;
    }

    public static void EditTranslation(this PO question, string key, string title, string body)
    {
        var rows = (PersistentObjectAttributeAsDetail)question["Translations"];
        var row = (rows.Objects ?? []).Single(r => r.Id == key);
        row["Title"].Value = title;
        row["Title"].IsValueChanged = true;
        row["Body"].Value = body;
        row["Body"].IsValueChanged = true;
        rows.IsValueChanged = true;
    }
}
