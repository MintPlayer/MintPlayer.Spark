using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
using System.Text.Json;

namespace WebhooksDemo.Services;

[Register(typeof(IOrganizationAccessService), ServiceLifetime.Scoped)]
public partial class OrganizationAccessService : IOrganizationAccessService
{
    [Inject] private readonly IHttpContextAccessor _httpContextAccessor;
    [Inject] private readonly UserManager<SparkUser> _userManager;
    [Inject] private readonly ILogger<OrganizationAccessService> _logger;

    private const string GitHubLoginProvider = "GitHub";
    private const string AccessTokenName = "access_token";

    private string[]? _cachedOwners;

    public async Task<string[]> GetAllowedOwnersAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedOwners is not null) return _cachedOwners;

        var principal = _httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
            return _cachedOwners = [];

        var user = await _userManager.GetUserAsync(principal);
        if (user is null)
            return _cachedOwners = [];

        var accessToken = await _userManager.GetAuthenticationTokenAsync(
            user, GitHubLoginProvider, AccessTokenName);
        if (string.IsNullOrEmpty(accessToken))
        {
            _logger.LogWarning("No GitHub access token found for user {UserId}", user.Id);
            return _cachedOwners = [];
        }

        var orgs = await QueryGitHubOrgsAsync(accessToken, cancellationToken);
        var username = principal.FindFirstValue(ClaimTypes.Name);

        _cachedOwners = orgs
            .Concat(username is not null ? [username] : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return _cachedOwners;
    }

    public async Task<bool> IsOwnerAllowedAsync(string ownerLogin, CancellationToken cancellationToken = default)
    {
        var owners = await GetAllowedOwnersAsync(cancellationToken);
        return owners.Contains(ownerLogin, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string[]> QueryGitHubOrgsAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SparkWebhooksDemo", "1.0"));

            const string query = "{ viewer { organizations(first: 100) { nodes { login } } } }";
            var response = await httpClient.PostAsJsonAsync(
                "https://api.github.com/graphql",
                new { query },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub GraphQL org query failed: {StatusCode}", response.StatusCode);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data)) return [];
            if (!data.TryGetProperty("viewer", out var viewer)) return [];
            if (!viewer.TryGetProperty("organizations", out var orgsNode)) return [];
            if (!orgsNode.TryGetProperty("nodes", out var nodes)) return [];

            var result = new List<string>();
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind == JsonValueKind.Null) continue;
                var login = node.GetProperty("login").GetString();
                if (!string.IsNullOrEmpty(login))
                    result.Add(login);
            }
            return result.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query GitHub organizations");
            return [];
        }
    }
}
