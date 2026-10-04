using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// The <c>OnCreatingTicket</c> event <see cref="GitHubAuthenticationExtensions.AddGitHub(Microsoft.AspNetCore.Identity.IdentityBuilder, Action{OAuthOptions})"/>
/// installs: it fetches <c>/user</c> for the claims and <c>/user/emails</c> for the
/// <c>email_verified</c> attestation auto-provisioning depends on. Driven by hand with a scripted
/// backchannel, since a real OAuth callback needs GitHub.
/// </summary>
public class GitHubOnCreatingTicketTests
{
    private const string VerifiedPrimary = """
        [ { "email": "old@example.test", "primary": false, "verified": true },
          { "email": "dev@example.test", "primary": true, "verified": true } ]
        """;

    private const string User = """{ "id": 4242, "login": "octo-dev", "email": "dev@example.test" }""";

    [Fact]
    public async Task A_verified_primary_email_is_attested_and_the_user_claims_are_mapped()
    {
        var backchannel = new ScriptedBackchannel()
            .Respond("https://api.github.com/user", HttpStatusCode.OK, User)
            .Respond("https://api.github.com/user/emails", HttpStatusCode.OK, VerifiedPrimary);

        var identity = await RunAsync(backchannel);

        (identity.FindFirst("email_verified")?.Value).Should().Be("true");
        (identity.FindFirst(ClaimTypes.NameIdentifier)?.Value).Should().Be("4242");
        (identity.FindFirst(ClaimTypes.Name)?.Value).Should().Be("octo-dev");
        (identity.FindFirst(ClaimTypes.Email)?.Value).Should().Be("dev@example.test");
        backchannel.Requests.Should().AllSatisfy(r =>
        {
            (r.Headers.Authorization?.ToString()).Should().Be("Bearer access-token");
            r.Headers.UserAgent.ToString().Should().Contain("SparkAuth");
        });
    }

    [Fact]
    public async Task A_GitHub_Enterprise_host_is_asked_for_its_own_emails()
    {
        // The emails URL used to be a hard-coded api.github.com, so a GHE deployment sent its GHE
        // access token to public GitHub and never received an attestation.
        var backchannel = new ScriptedBackchannel()
            .Respond("https://ghe.example.test/api/v3/user", HttpStatusCode.OK, User)
            .Respond("https://ghe.example.test/api/v3/user/emails", HttpStatusCode.OK, VerifiedPrimary);

        var identity = await RunAsync(backchannel,
            options => options.UserInformationEndpoint = "https://ghe.example.test/api/v3/user");

        backchannel.Requests.Select(r => r.RequestUri!.Host).Should().OnlyContain(host => host == "ghe.example.test",
            "the token belongs to the Enterprise host and must never be sent anywhere else");
        (identity.FindFirst("email_verified")?.Value).Should().Be("true");
    }

    [Theory]
    [InlineData("""[ { "email": "dev@example.test", "primary": true, "verified": false } ]""")]
    [InlineData("""[ { "email": "dev@example.test", "primary": false, "verified": true } ]""")]
    [InlineData("[]")]
    [InlineData("""{ "message": "not an array" }""")]
    [InlineData("""[ "dev@example.test", { "primary": "yes", "verified": "yes" } ]""")]
    public async Task No_attestation_without_a_verified_primary_email(string emails)
    {
        // Including bodies of an unexpected shape: those mean "not attested", not a failed sign-in.
        var backchannel = new ScriptedBackchannel()
            .Respond("https://api.github.com/user", HttpStatusCode.OK, User)
            .Respond("https://api.github.com/user/emails", HttpStatusCode.OK, emails);

        var identity = await RunAsync(backchannel);

        identity.FindFirst("email_verified").Should().BeNull();
        (identity.FindFirst(ClaimTypes.Name)?.Value).Should().Be("octo-dev", "the sign-in itself still succeeds");
    }

    [Fact]
    public async Task A_refused_emails_lookup_issues_no_attestation_but_does_not_fail_the_sign_in()
    {
        // A missing user:email scope: 403, logged, and signing in to an already-linked account works.
        var backchannel = new ScriptedBackchannel()
            .Respond("https://api.github.com/user", HttpStatusCode.OK, User)
            .Respond("https://api.github.com/user/emails", HttpStatusCode.Forbidden, """{ "message": "no" }""");

        var identity = await RunAsync(backchannel);

        identity.FindFirst("email_verified").Should().BeNull();
        (identity.FindFirst(ClaimTypes.NameIdentifier)?.Value).Should().Be("4242");
    }

    [Fact]
    public async Task A_failing_user_lookup_fails_the_sign_in()
    {
        var backchannel = new ScriptedBackchannel()
            .Respond("https://api.github.com/user", HttpStatusCode.InternalServerError, "{}");

        var act = () => RunAsync(backchannel);

        await act.Should().ThrowAsync<HttpRequestException>();
        backchannel.Requests.Should().ContainSingle("nothing is attested for a user that could not be read");
    }

    private static async Task<ClaimsIdentity> RunAsync(ScriptedBackchannel backchannel, Action<OAuthOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSparkAuthentication<SparkUser>().AddGitHub(options =>
        {
            options.ClientId = "test-client";
            options.ClientSecret = "test-secret";
            configure?.Invoke(options);
        });

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get("GitHub");

        var identity = new ClaimsIdentity("GitHub");
        using var tokens = JsonDocument.Parse("""{ "access_token": "access-token", "token_type": "bearer" }""");
        using var user = JsonDocument.Parse("{}");
        var context = new OAuthCreatingTicketContext(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties(),
            new DefaultHttpContext { RequestServices = provider },
            new AuthenticationScheme("GitHub", "GitHub", typeof(OAuthHandler<OAuthOptions>)),
            options,
            new HttpClient(backchannel),
            OAuthTokenResponse.Success(tokens),
            user.RootElement);

        await options.Events.OnCreatingTicket(context);
        return identity;
    }

    private sealed class ScriptedBackchannel : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = new(StringComparer.Ordinal);

        public List<HttpRequestMessage> Requests { get; } = [];

        public ScriptedBackchannel Respond(string url, HttpStatusCode status, string body)
        {
            _responses[url] = (status, body);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var (status, body) = _responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var scripted)
                ? scripted
                : (HttpStatusCode.NotFound, "{}");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
