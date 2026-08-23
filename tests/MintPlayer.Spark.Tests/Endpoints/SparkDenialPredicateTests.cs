using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Endpoints;

/// <summary>
/// The 401-vs-404 branch, as two propositions rather than as a side effect of other suites.
/// </summary>
/// <remarks>
/// Nothing asserted either of these before. The predicate used to be "is IAccessControl an
/// AllowAllAccessControl", which the deletion of that type made unwritable — and the obvious
/// replacement, "does the anonymous group hold any grant", would have been a silent regression:
/// Fleet and HR each grant anonymous exactly one right, so both would have flipped to 404 and
/// lost the sign-in redirect, which the client's interceptor drives off 401 alone.
/// </remarks>
public class SparkDenialPredicateTests : SparkTestDriver
{
    private static readonly Guid DocTypeId = Guid.Parse("5b5b0000-1111-2222-3333-444455556666");

    /// <summary>
    /// A host with no credential scheme and no identity user type. Nothing in the pipeline can
    /// turn a credential into a principal, so telling the caller to authenticate would be false.
    /// </summary>
    [Fact]
    public async Task A_host_with_no_way_to_sign_in_answers_404_to_everyone()
    {
        await using var factory = new SparkEndpointFactory<GuardedContext>(
            Store, [GuardedDocModel.For(DocTypeId)], security: SparkTestSecurity.Empty);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/spark/po/{DocTypeId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The same denial on a host that CAN authenticate: 401, so the client redirects to sign-in
    /// rather than showing the visitor a dead end.
    /// </summary>
    [Fact]
    public async Task A_host_that_can_authenticate_answers_401_to_an_anonymous_caller()
    {
        await using var factory = new SparkEndpointFactory<GuardedContext>(
            Store,
            [GuardedDocModel.For(DocTypeId)],
            configureServices: services => services
                .AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, NeverAuthenticatesHandler>("Test", _ => { }),
            configureSpark: spark => spark.AddCredentialScheme("Test"),
            security: SparkTestSecurity.Empty);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/spark/po/{DocTypeId}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Answers "no result" rather than failing, so the caller arrives anonymous through a pipeline
    /// that nonetheless HAS authentication in it — which is exactly the state the predicate is
    /// about.
    /// </summary>
    private sealed class NeverAuthenticatesHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.NoResult());
    }
}
