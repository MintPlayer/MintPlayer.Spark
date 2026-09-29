using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Client.Authorization;
using MintPlayer.Spark.E2E.Tests._Infrastructure;
using static MintPlayer.Spark.E2E.Tests._Infrastructure.QnATestHost;

namespace MintPlayer.Spark.E2E.Tests.QnA;

/// <summary>
/// The account flows QnA runs with MailManager in pickup mode (#460 D6, D8, M8, M10): every mail is
/// an <c>.eml</c> file in the host's own folder (<see cref="SparkAppTestHost.PickedUpMails"/>).
/// </summary>
[Collection(QnAE2ECollection.Name)]
public partial class QnAAccountTests
{
    private readonly QnATestHost host;

    public QnAAccountTests(QnAE2ECollectionFixture fixture) => host = fixture.Host;

    /// <summary>
    /// D6: QnA sets <c>RequireConfirmedEmail</c>, so a new account cannot sign in until the link in its
    /// confirmation mail — pointing at the SPA's <c>confirm-email</c> page on the public base URL — is used.
    /// </summary>
    [Fact]
    public async Task Registration_mails_a_confirmation_link_and_only_a_confirmed_account_signs_in()
    {
        var email = $"newcomer-{Guid.NewGuid():N}@qna.example";
        var password = $"Aa1!{Guid.NewGuid():N}";
        using var client = host.NewClient();
        await client.RegisterAsync(email, password);

        var early = async () => await client.LoginAsync(email, password);
        await early.Should().ThrowAsync<SparkClientException>(); // an unconfirmed account does not sign in

        var mail = await host.WaitForMailAsync(email, "Confirm your email");
        var text = HttpUtility.HtmlDecode(mail.TextBody ?? mail.HtmlBody ?? string.Empty);
        var link = ConfirmLink().Match(text);
        link.Success.Should().BeTrue($"the mail carries a confirm-email link: {text}");
        link.Value.Should().StartWith(host.AppUrl, "links use Spark:Auth:PublicBaseUrl, never the request's Host");

        var query = HttpUtility.ParseQueryString(new Uri(link.Value).Query);
        var (confirmed, body) = await client.PostAsync("/spark/auth/confirm-email", new { userId = query["userId"], code = query["code"] });
        confirmed.Should().BeOneOf([200, 204], $"the mailed link confirms the account (answer: {body}; link: {link.Value})");

        await client.LoginAsync(email, password);
        (await client.GetCurrentUserAsync()).IsAuthenticated.Should().BeTrue(body.ToString());
    }

    /// <summary>
    /// D8: deleting an account (re-authenticated with the password) runs Moderation's handler — its votes
    /// are reversed and removed — and QnA's, which mails a goodbye from the app's own template. The posts
    /// it voted on stay; the account cannot sign in again.
    /// </summary>
    [Fact]
    public async Task Deleting_an_account_removes_its_votes_and_mails_a_goodbye()
    {
        using var author = await host.CreateUserAsync("gdpr-author");
        using var leaver = await host.CreateUserAsync("gdpr-leaver");
        var question = await author.Client.AskAsync("A question the leaver liked " + Guid.NewGuid().ToString("N"));

        var (voted, _) = await leaver.Client.VoteAsync(QuestionTypeId, question.Id!, +1);
        voted.Should().Be(200);
        (await author.Client.ScoreAsync(QuestionTypeId, question.Id!)).Should().Be(1);

        var (wrong, _) = await leaver.Client.SendJsonAsync(HttpMethod.Delete, "/spark/auth/manage/account", new { password = "not-the-password" });
        wrong.Should().Be(403, "deletion needs re-authentication");

        var (deleted, deleteBody) = await leaver.Client.SendJsonAsync(HttpMethod.Delete, "/spark/auth/manage/account", new { password = leaver.Password });
        deleted.Should().Be(204, deleteBody.ToString());

        (await author.Client.ScoreAsync(QuestionTypeId, question.Id!)).Should().Be(0, "the deleted account's votes are reversed");
        (await host.LoadAsync<StoredPost>(question.Id!))!.AuthorId.Should().Be(author.Id, "the content stays");

        var goodbye = await host.WaitForMailAsync(leaver.Email, "deleted");
        (goodbye.TextBody ?? goodbye.HtmlBody).Should().Contain("at your request");

        using var fresh = host.NewClient();
        var signIn = async () => await fresh.LoginAsync(leaver.Email, leaver.Password);
        (await signIn.Should().ThrowAsync<SparkClientException>()).Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // Parentheses excluded: the text part renders a link as "label (url)", and a captured ")" corrupts
    // the code (measured in M14: "Invalid token").
    [GeneratedRegex(@"https?://[^\s""'<>()]+/confirm-email\?[^\s""'<>()]+")]
    private static partial Regex ConfirmLink();
}
