using System.Net;
using System.Net.Http.Json;
using SeoTester.Tests._Infrastructure;

namespace SeoTester.Tests;

/// <summary>
/// SeoTester runs ASP.NET's rate-limiting middleware (owner decision 2026-10-12), so the identity provider's endpoint budgets
/// apply: its machine endpoints, client registration included (where anonymous agents register, MCP PRD D5), refuse a caller
/// over the budget with 429 before the endpoint runs. Without the middleware those policies are inert and every request
/// reaches the endpoint. An empty registration is refused for its missing redirect URIs before the provider's always-on
/// per-address registration budget is spent, so only the policy can answer 429 here.
/// </summary>
[Collection(RateLimitedHostCollection.Name)]
public class RateLimitTests(RateLimitedHostFixture host)
{
    [Fact]
    public async Task Client_registration_answers_429_once_the_identity_providers_budget_is_spent()
    {
        using var client = host.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i <= RateLimitedHostFixture.Budget; i++)
        {
            using var response = await client.PostAsync("/connect/register", JsonContent.Create(new { }));
            statuses.Add(response.StatusCode);
        }

        statuses.Take(RateLimitedHostFixture.Budget).Should().NotContain(HttpStatusCode.TooManyRequests, "within the budget the endpoint answers itself");
        statuses[^1].Should().Be(HttpStatusCode.TooManyRequests, "one request over the identity provider's budget");
    }
}

/// <summary>A SeoTester host whose identity provider allows <see cref="Budget"/> machine-endpoint requests per address and minute.</summary>
public sealed class RateLimitedHostFixture : SeoTesterHostFixture
{
    public const int Budget = 3;

    protected override IReadOnlyDictionary<string, string?>? HostSettings => new Dictionary<string, string?>
    {
        ["Spark:IdentityProvider:RateLimits:PermitLimit"] = Budget.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RateLimitedHostCollection : ICollectionFixture<RateLimitedHostFixture>
{
    public const string Name = "SeoTester rate-limited host";
}
