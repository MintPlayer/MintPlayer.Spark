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

        var installationOwners = await QueryGitHubInstallationOwnersAsync(accessToken, cancellationToken);
        var username = principal.FindFirstValue(ClaimTypes.Name);

        _cachedOwners = installationOwners
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

    private async Task<string[]> QueryGitHubInstallationOwnersAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SparkWebhooksDemo", "1.0"));

            var response = await httpClient.GetAsync(
                "https://api.github.com/user/installations",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub /user/installations query failed: {StatusCode}", response.StatusCode);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("installations", out var installations)) return [];

            var result = new List<string>();
            foreach (var installation in installations.EnumerateArray())
            {
                if (!installation.TryGetProperty("account", out var account)) continue;
                if (account.ValueKind == JsonValueKind.Null) continue;
                if (!account.TryGetProperty("login", out var loginNode)) continue;
                var login = loginNode.GetString();
                if (!string.IsNullOrEmpty(login))
                    result.Add(login);
            }
            return result.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query GitHub installations");
            return [];
        }
    }
}
