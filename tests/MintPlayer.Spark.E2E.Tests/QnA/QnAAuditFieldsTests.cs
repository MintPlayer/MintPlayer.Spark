using System.Net;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.E2E.Tests._Infrastructure;
using static MintPlayer.Spark.E2E.Tests._Infrastructure.QnATestHost;

namespace MintPlayer.Spark.E2E.Tests.QnA;

/// <summary>
/// QnA shows who wrote and last changed a post (#264, G-Q14/G-Q15): <c>CreatedBy</c> and
/// <c>ModifiedBy</c> are <c>SparkUser</c> references, drawn by the user's <c>{UserName}</c> breadcrumb.
/// <para>
/// ⚠️ Since MCP PRD D19 (owner decision 2026-10-12) a reference is shown only to a caller holding
/// <c>Read</c> on its target type, so visitors and signed-in users hold <c>Read/SparkUser</c>, behind
/// QnA's <c>SparkUserActions</c> row rule: a user is readable only while they author a published
/// question or a live answer, and only <c>UserName</c> is in the model. A user who wrote nothing stays
/// unloadable.
/// </para>
/// </summary>
[Collection(QnAE2ECollection.Name)]
public class QnAAuditFieldsTests
{
    private const string NoPosts = "the SparkUser row rule only admits authors of a visible post";

    /// <summary>
    /// SparkUser's model id, which MintPlayer.Spark.Authorization ships: UUIDv5 of
    /// <c>authorization:SparkUser</c> (composition D5). QnA's <c>App_Data/Model/SparkUser.json</c> is only a delta.
    /// </summary>
    private static readonly Guid SparkUserTypeId = Guid.Parse("0d3faefa-624c-5135-bca7-652aa9053e0e");

    private readonly QnATestHost host;

    public QnAAuditFieldsTests(QnAE2ECollectionFixture fixture) => host = fixture.Host;

    [Fact]
    public async Task A_visitor_cannot_load_a_user()
    {
        using var user = await host.CreateUserAsync("audit-visible");
        using var anonymous = host.NewClient();

        (await LoadOrNullAsync(anonymous, user.Id)).Should().BeNull(NoPosts);
    }

    [Fact]
    public async Task A_visitor_can_load_an_author_and_sees_only_the_user_name()
    {
        using var author = await host.CreateUserAsync("audit-loadable");
        using var anonymous = host.NewClient();
        await author.Client.AskAsync("Whose name is this? " + Guid.NewGuid().ToString("N"));

        var loaded = await LoadOrNullAsync(anonymous, author.Id);

        loaded.Should().NotBeNull("an author of a published question is readable");
        loaded!.Attributes.Select(a => a.Name).Should().Equal("UserName");
    }

    [Fact]
    public async Task A_signed_in_caller_cannot_load_another_user()
    {
        using var user = await host.CreateUserAsync("audit-target");
        using var other = await host.CreateUserAsync("audit-reader");

        (await LoadOrNullAsync(other.Client, user.Id)).Should().BeNull(NoPosts);
    }

    [Fact]
    public async Task A_questions_author_and_last_editor_render_as_the_users_name()
    {
        using var author = await host.CreateUserAsync("audit-author");
        using var anonymous = host.NewClient();
        var question = await author.Client.AskAsync("Who wrote this? " + Guid.NewGuid().ToString("N"));

        var loaded = (await anonymous.GetPersistentObjectAsync(QuestionTypeId, question.Id!))!;

        foreach (var name in new[] { "CreatedBy", "ModifiedBy" })
        {
            var attribute = loaded[name];
            attribute.Value?.ToString().Should().Be(author.Id, $"{name} stores the user's id");
            attribute.Breadcrumb.Should().Be(author.UserName, $"{name} is drawn by SparkUser's {{UserName}} breadcrumb");
            // G-Q22: the user name is a public handle, so another user's email never reaches the page.
            attribute.Breadcrumb.Should().NotContain("@");
            attribute.Breadcrumb.Should().NotBe(author.Email);
            attribute.IsReadOnly.Should().BeTrue();
        }
    }

    /// <summary>
    /// A refused load: null for a 404, and the 401/403 the type gate answers otherwise. Any other failure
    /// is rethrown, so a broken endpoint cannot pass as a refusal.
    /// </summary>
    private static async Task<PersistentObject?> LoadOrNullAsync(SparkClient client, string id)
    {
        try
        {
            return await client.GetPersistentObjectAsync(SparkUserTypeId, id);
        }
        catch (SparkClientException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return null;
        }
    }
}
