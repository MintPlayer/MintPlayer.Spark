using MintPlayer.Spark.E2E.Tests._Infrastructure;
using static MintPlayer.Spark.E2E.Tests._Infrastructure.QnATestHost;

namespace MintPlayer.Spark.E2E.Tests.QnA;

/// <summary>
/// QnA shows who wrote and last changed a post (#264, G-Q14/G-Q15): <c>CreatedBy</c> and
/// <c>ModifiedBy</c> are <c>SparkUser</c> references, drawn by the user's <c>{UserName}</c> breadcrumb.
/// Visitors may read <c>SparkUser</c> for that, and <c>security.json</c> denies them — and signed-in
/// users — every user attribute but <c>UserName</c>.
/// </summary>
[Collection(QnAE2ECollection.Name)]
public class QnAAuditFieldsTests
{
    /// <summary>SparkUser's model id (<c>apps/QnA/QnA/App_Data/Model/SparkUser.json</c>).</summary>
    private static readonly Guid SparkUserTypeId = Guid.Parse("4e13c0de-0000-4000-8000-000000000001");

    private readonly QnATestHost host;

    public QnAAuditFieldsTests(QnAE2ECollectionFixture fixture) => host = fixture.Host;

    [Fact]
    public async Task A_visitor_loading_a_user_gets_the_user_name_and_nothing_else()
    {
        using var user = await host.CreateUserAsync("audit-visible");
        using var anonymous = host.NewClient();

        var loaded = await anonymous.GetPersistentObjectAsync(SparkUserTypeId, user.Id);

        loaded.Should().NotBeNull("anonymous holds Read/SparkUser so that author names resolve");
        loaded!.Attributes.Select(a => a.Name).Should().Equal("UserName");
        loaded.Attributes[0].Value?.ToString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_signed_in_caller_loading_another_user_gets_the_user_name_and_nothing_else()
    {
        using var user = await host.CreateUserAsync("audit-target");
        using var other = await host.CreateUserAsync("audit-reader");

        var loaded = await other.Client.GetPersistentObjectAsync(SparkUserTypeId, user.Id);

        loaded!.Attributes.Select(a => a.Name).Should().Equal("UserName");
    }

    [Fact]
    public async Task A_questions_author_and_last_editor_render_as_the_users_name()
    {
        using var author = await host.CreateUserAsync("audit-author");
        using var anonymous = host.NewClient();
        var question = await author.Client.AskAsync("Who wrote this? " + Guid.NewGuid().ToString("N"));

        var userName = (await anonymous.GetPersistentObjectAsync(SparkUserTypeId, author.Id))!["UserName"].Value?.ToString();
        var loaded = (await anonymous.GetPersistentObjectAsync(QuestionTypeId, question.Id!))!;

        foreach (var name in new[] { "CreatedBy", "ModifiedBy" })
        {
            var attribute = loaded[name];
            attribute.Value?.ToString().Should().Be(author.Id, $"{name} stores the user's id");
            attribute.Breadcrumb.Should().Be(userName, $"{name} is drawn by SparkUser's {{UserName}} breadcrumb");
            attribute.IsReadOnly.Should().BeTrue();
        }
        userName.Should().NotBeNullOrEmpty();
    }
}
