using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using MintPlayer.Spark.Authorization.OneShotOAuth;

namespace CodeCoverage.MergeQueue;

/// <summary>
/// #554: the dedicated GitHub OAuth app whose one-shot token sets up a repository's merge-queue rulesets. The GitHub Apps
/// hold no Administration (GitHub has no narrower permission for rulesets), so the signed-in admin authorizes this app in
/// a popup, the "Set up rulesets" action uses the token, and revokes it when done.
/// </summary>
/// <remarks>
/// <para>
/// Config: <c>GitHub:{Development|Production}:RulesetsOAuth:ClientId</c> and <c>:ClientSecret</c>; the app's callback URL
/// is <c>{Coverage:BaseUrl}/spark/auth/oauth-callback</c>. The client is registered even without them, so the callback
/// route is the same everywhere; the action refuses with <see cref="IsConfigured"/> false.
/// </para>
/// <para>
/// ⚠️ An OAuth app token never expires on its own: <see cref="RevokeAsync"/> ends it with
/// <c>DELETE /applications/{client_id}/token</c> (Basic client id and secret, 204). <c>…/grant</c> would instead remove every
/// token the person has for the app.
/// </para>
/// </remarks>
public static class GitHubRulesetsOAuth
{
    /// <summary>The name the client is registered under.</summary>
    public const string ClientName = "GitHubRulesets";

    /// <summary>"Full control of private repositories": the only classic scope that covers rulesets.</summary>
    public const string Scope = "repo";

    internal const string ApiBase = "https://api.github.com";

    public static void Configure(SparkOneShotOAuthClient client, string? clientId, string? clientSecret)
    {
        client.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
        client.TokenEndpoint = "https://github.com/login/oauth/access_token";
        client.ClientId = clientId ?? string.Empty;
        client.ClientSecret = clientSecret ?? string.Empty;
        client.Revoke = RevokeAsync;
    }

    public static bool IsConfigured(SparkOneShotOAuthOptions options)
        => options.Clients.TryGetValue(ClientName, out var client)
           && !string.IsNullOrEmpty(client.ClientId) && !string.IsNullOrEmpty(client.ClientSecret);

    internal static async Task RevokeAsync(SparkOneShotOAuthRevocation revocation, CancellationToken cancellationToken)
    {
        var client = revocation.Client;
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{ApiBase}/applications/{Uri.EscapeDataString(client.ClientId)}/token")
        {
            Content = JsonContent.Create(new { access_token = revocation.AccessToken }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{client.ClientId}:{client.ClientSecret}")));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("CodeCoverage");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await revocation.Http.SendAsync(request, cancellationToken);
        // 404: GitHub no longer knows the token (revoked already); nothing is left to end.
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            throw new HttpRequestException($"GitHub refused to revoke the rulesets token: {(int)response.StatusCode}.", null, response.StatusCode);
    }
}
