using System.Net;
using CodeCoverage.ApiTokens;
using CodeCoverage.Tests.Mcp._Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodeCoverage.Tests.Mcp;

/// <summary>
/// MCP PRD D23: CodeCoverage's upload credentials are not agent credentials. A <c>covt_</c> API token and a GitHub Actions
/// workflow token both authenticate their own endpoints, and both are refused on <c>/mcp</c> with a 401 — never served as
/// an anonymous call, which would hand an agent a working session on a token meant for CI.
/// </summary>
[Collection(CoverageMcpHostCollection.Name)]
public class CoverageMcpCredentialTests(CoverageMcpHostFixture fixture)
{
    [Fact]
    public async Task Upload_credentials_the_app_accepts_are_refused_on_mcp_and_never_run_as_anonymous()
    {
        var upload = fixture.Credentials.UploadToken;
        var workflow = fixture.WorkflowToken();

        // Both are valid where they belong: otherwise a refusal below would prove nothing.
        (await AuthenticateAsync(ApiTokenAuthenticationHandler.SchemeName, upload)).Succeeded.Should().BeTrue("the covt_ token is a live upload credential");
        (await AuthenticateAsync(GitHubOidc.SchemeName, workflow)).Succeeded.Should().BeTrue("the workflow token is one the upload endpoint accepts");

        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // The same call with no credential at all succeeds: the repository is public.
        var anonymous = await McpWire.CallAsync(client, "codecoverage_summary", new { repository = "github/acme/open" });
        anonymous.Status.Should().Be(HttpStatusCode.OK);
        anonymous.IsToolError.Should().BeFalse(anonymous.ToolText);

        foreach (var (name, token) in new[] { ("covt_", upload), ("GitHub Actions", workflow) })
        {
            var reply = await McpWire.CallAsync(client, "codecoverage_summary", new { repository = "github/acme/open" }, McpWire.Bearer(token));

            reply.Status.Should().Be(HttpStatusCode.Unauthorized, $"a {name} token is not an agent credential (D23)");
            reply.Response.Headers.WwwAuthenticate.ToString().Should().Contain("error=\"invalid_token\"");
            reply.ErrorMessage.Should().StartWith("The token was not accepted.");
            reply.Text.Should().NotContain("github/acme/open", "nothing of the repository is served to a refused credential");
        }
    }

    [Fact]
    public async Task A_personal_access_token_opens_mcp_and_not_the_upload_api()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var reply = await McpWire.CallAsync(client, "codecoverage_summary", new { repository = "github/acme/secret" }, McpWire.Bearer(fixture.Credentials.DevToken));
        reply.IsToolError.Should().BeFalse(reply.ToolText);

        // And the other way round: the agent token is no upload credential either.
        (await AuthenticateAsync(ApiTokenAuthenticationHandler.SchemeName, fixture.Credentials.DevToken)).Succeeded.Should().BeFalse();
        (await AuthenticateAsync(GitHubOidc.SchemeName, fixture.Credentials.DevToken)).Succeeded.Should().BeFalse();
    }

    /// <summary>Runs one authentication scheme of the real host against a bearer header.</summary>
    private async Task<AuthenticateResult> AuthenticateAsync(string scheme, string token)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Authorization = "Bearer " + token;
        return await context.AuthenticateAsync(scheme);
    }
}
