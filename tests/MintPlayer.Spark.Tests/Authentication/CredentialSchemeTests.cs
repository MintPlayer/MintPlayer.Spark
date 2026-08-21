using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Endpoints.PersistentObject;

namespace MintPlayer.Spark.Tests.Authentication;

/// <summary>
/// M9 — the scheme plumbing, end to end through a real host.
/// <para>
/// Both defects this closes were silent. Spark's endpoints carry no <c>[Authorize]</c> and name no
/// scheme, so ASP.NET ran only the default authenticate scheme and an app could register a
/// certificate or bearer handler that never executed — the caller arrived anonymous with
/// the anonymous group's rights, no error, no log (F7). And antiforgery was demanded of every mutating
/// request whatever authenticated it, so an external caller with no cookie to echo got a bare 400
/// with no body (F8).
/// </para>
/// <para>
/// These are asserted through HTTP rather than against the handler in isolation, because both
/// failures were in the wiring — a handler that authenticates correctly and is never asked proves
/// nothing.
/// </para>
/// </summary>
public class CredentialSchemeTests : MintPlayer.Spark.Testing.SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("55555555-dddd-dddd-dddd-555555555555");

    private const string StubScheme = "Stub";
    private const string StubHeader = "X-Stub-Credential";

    /// <summary>
    /// Stands in for a non-ambient credential — a bearer token or a client certificate. It
    /// authenticates on an explicit header the caller had to construct, which is exactly the
    /// property that makes it immune to CSRF: a cross-site page cannot make a browser send it.
    /// </summary>
    private sealed class StubCredentialHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(StubHeader, out var presented))
                return Task.FromResult(AuthenticateResult.NoResult());

            if (presented != "valid")
                return Task.FromResult(AuthenticateResult.Fail("Unrecognised stub credential."));

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "machine")], StubScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), StubScheme)));
        }
    }

    private SparkEndpointFactory NewFactory() => new(
        Store,
        [TestModels.Person(PersonTypeId)],
        configureSpark: spark => spark.AddCredentialScheme<AuthenticationSchemeOptions, StubCredentialHandler>(StubScheme));

    private static HttpRequestMessage CreatePersonRequest(string? credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/spark/po/{PersonTypeId}")
        {
            Content = JsonContent.Create(new
            {
                persistentObject = new
                {
                    name = "Person",
                    objectTypeId = PersonTypeId,
                    attributes = new[]
                    {
                        new { name = "FirstName", value = (object)"Alice" },
                        new { name = "LastName", value = (object)"Smith" },
                    }
                }
            })
        };

        if (credential is not null)
            request.Headers.Add(StubHeader, credential);

        return request;
    }

    /// <summary>
    /// F8. The caller proved who it is with a credential no browser attaches on its own, so there
    /// is no ambient authority to forge and nothing for an antiforgery token to protect. Before
    /// this, a CI job posting a coverage report could not get past the gate at all.
    /// </summary>
    [Fact]
    public async Task A_non_ambient_credential_is_exempt_from_antiforgery()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(CreatePersonRequest("valid"));

        response.StatusCode.Should().NotBe(HttpStatusCode.BadRequest,
            "a bearer/certificate caller has no XSRF-TOKEN cookie to echo, and cannot be made to "
            + "send one by a cross-site page — so demanding it protects nothing and blocks everything");
    }

    /// <summary>
    /// The gate must key on what authenticated the request, not on request shape. If it keyed on
    /// "was an Authorization-ish header present", an attacker could disable CSRF protection for a
    /// cookie-authenticated victim by attaching a junk header. A junk header authenticates nothing.
    /// </summary>
    [Fact]
    public async Task An_unrecognised_credential_does_not_suppress_the_antiforgery_gate()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(CreatePersonRequest("forged"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the credential was refused, so nothing authenticated this request and the gate stands");
    }

    [Fact]
    public async Task An_anonymous_request_is_still_gated()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(CreatePersonRequest(credential: null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "registering a credential scheme must not relax the default for callers that present none");
    }

    /// <summary>
    /// F7 proper. The scheme has to actually run — before the composite, a registered handler was
    /// never consulted on a Spark endpoint, because nothing named it.
    /// </summary>
    [Fact]
    public async Task A_registered_scheme_authenticates_the_request()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(CreatePersonRequest("valid"));

        // Reaching the handler at all is the proof: the exemption above is only granted when the
        // composite recorded a successful, non-ambient authentication for this request.
        response.StatusCode.Should().NotBe(HttpStatusCode.BadRequest);
        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
    }
}
