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
/// ⚠️ No caller holds a right on <c>SparkUser</c>. A reference label is resolved through row security on
/// the target only, not through a type right, so the name renders for visitors while a user can be
/// neither loaded nor listed. A <c>Read/SparkUser</c> grant to <c>anonymous</c> would also be refused at
/// startup: <c>SparkUserActions</c> declares no row rule, so it would publish every user.
/// </para>
/// </summary>
[Collection(QnAE2ECollection.Name)]
public class QnAAuditFieldsTests
{
    /// <summary>SparkUser's model id (<c>apps/QnA/QnA/App_Data/Model/SparkUser.json</c>).</summary>
    private static readonly Guid SparkUserTypeId = Guid.Parse("4e13c0de-0000-4000-8000-000000000001");

    private readonly QnATestHost host;

    public QnAAuditFieldsTests(QnAE2ECollectionFixture fixture) => host = fixture.Host;

    [Fact]
    public async Task A_visitor_cannot_load_a_user()
    {
        using var user = await host.CreateUserAsync("audit-visible");
        using var anonymous = host.NewClient();

        (await LoadOrNullAsync(anonymous, user.Id)).Should().BeNull("nobody holds a right on SparkUser");
    }

    [Fact]
    public async Task A_signed_in_caller_cannot_load_another_user()
    {
        using var user = await host.CreateUserAsync("audit-target");
        using var other = await host.CreateUserAsync("audit-reader");

        (await LoadOrNullAsync(other.Client, user.Id)).Should().BeNull("nobody holds a right on SparkUser");
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
            // Registration sets UserName to the email address (SparkAccountEndpoints).
            attribute.Breadcrumb.Should().Be(author.Email, $"{name} is drawn by SparkUser's {{UserName}} breadcrumb");
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
