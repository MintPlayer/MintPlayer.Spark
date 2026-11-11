using System.Net;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// The provider's error page (<c>GET /connect/error</c>, PRD D7). Anyone can link here with any text, so <c>error</c>
/// only picks one of the provider's own messages from an allow-list, and <c>error_description</c> is shown encoded and
/// cut to 300 characters, as a detail line below that message.
/// </summary>
public class OidcErrorPageTests(OidcSharedHost host) : OidcTestHost(host), IClassFixture<OidcSharedHost>
{
    private async Task<(HttpStatusCode Status, string Html)> GetAsync(string query)
    {
        using var response = await Client.GetAsync($"/connect/error{query}");
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_known_code_shows_its_own_message_and_code_with_400()
    {
        var (status, html) = await GetAsync("?error=invalid_scope");

        status.Should().Be(HttpStatusCode.BadRequest);
        html.Should().Contain("Something went wrong");
        html.Should().Contain("The application asked for a scope that is unknown or not allowed.");
        html.Should().Contain("Error code: invalid_scope");
        html.Should().NotContain("Details:");
    }

    [Theory]
    [InlineData("?error=server_error", HttpStatusCode.InternalServerError, "server_error")]
    [InlineData("?error=temporarily_unavailable", HttpStatusCode.ServiceUnavailable, "temporarily_unavailable")]
    [InlineData("?error=%3Cb%3Eyou%20won%3C/b%3E", HttpStatusCode.InternalServerError, "server_error")]
    [InlineData("", HttpStatusCode.InternalServerError, "server_error")]
    public async Task An_unknown_or_missing_code_is_a_server_error_and_unavailability_is_503(string query, HttpStatusCode expected, string code)
    {
        var (status, html) = await GetAsync(query);

        status.Should().Be(expected);
        html.Should().Contain($"Error code: {code}");
        html.Should().NotContain("you won");
    }

    [Fact]
    public async Task The_description_is_encoded_and_cut_to_300_characters()
    {
        var long_ = new string('a', 320);

        var (_, script) = await GetAsync("?error=access_denied&error_description=%3Cscript%3Ealert(1)%3C/script%3E");
        var (_, cut) = await GetAsync($"?error=access_denied&error_description={long_}");
        var (_, blank) = await GetAsync("?error=access_denied&error_description=%20%20");

        script.Should().NotContain("<script>alert(1)");
        script.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;");
        cut.Should().Contain($"Details: {new string('a', 300)}…").And.NotContain(new string('a', 301));
        blank.Should().NotContain("Details:");
    }
}
