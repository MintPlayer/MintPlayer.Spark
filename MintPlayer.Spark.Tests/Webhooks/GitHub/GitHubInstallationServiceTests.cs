using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit;

namespace MintPlayer.Spark.Tests.Webhooks.GitHub;

public class GitHubInstallationServiceTests
{
    private const long InstallationId = 12345L;

    private static GitHubInstallationService NewService(GitHubWebhooksOptions? options = null)
    {
        var opts = Options.Create(options ?? new GitHubWebhooksOptions());
        // Source generator writes the [Options] field via the public ctor
        return new GitHubInstallationService(opts);
    }

    private static ConcurrentDictionary<long, AccessToken> GetTokenCache(GitHubInstallationService service)
    {
        var field = typeof(GitHubInstallationService)
            .GetField("_installationTokens", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (ConcurrentDictionary<long, AccessToken>)field.GetValue(service)!;
    }

    private static void SeedToken(GitHubInstallationService service, long id, DateTimeOffset expiresAt)
    {
        GetTokenCache(service)[id] = new AccessToken("seeded-" + id, expiresAt);
    }

    [Fact]
    public async Task Fast_path_returns_the_cached_token_when_it_has_more_than_60s_remaining()
    {
        using var service = NewService();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10);
        SeedToken(service, InstallationId, expiry);

        var token = await service.GetOrCreateInstallationTokenAsync(InstallationId, CancellationToken.None);

        token.Token.Should().Be("seeded-12345");
        token.ExpiresAt.Should().Be(expiry);
    }

    [Fact]
    public async Task Token_expiring_within_60s_is_treated_as_stale_and_triggers_a_refresh()
    {
        using var service = NewService(); // no ClientId / no private key — refresh must throw
        SeedToken(service, InstallationId, DateTimeOffset.UtcNow.AddSeconds(30));

        // Because PEM/ClientId are unconfigured, CreateAppClientAsync throws inside the refresh path.
        // The exception is the signal that the stale check decided to refresh.
        var act = async () => await service.GetOrCreateInstallationTokenAsync(InstallationId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*PrivateKeyPem*");
    }

    [Fact]
    public async Task Token_expiring_just_past_the_60s_window_is_still_considered_fresh()
    {
        using var service = NewService();
        var expiry = DateTimeOffset.UtcNow.AddSeconds(75);
        SeedToken(service, InstallationId, expiry);

        var token = await service.GetOrCreateInstallationTokenAsync(InstallationId, CancellationToken.None);

        token.ExpiresAt.Should().Be(expiry);
    }

    [Fact]
    public void InvalidateInstallation_removes_the_cached_entry()
    {
        using var service = NewService();
        SeedToken(service, InstallationId, DateTimeOffset.UtcNow.AddHours(1));
        GetTokenCache(service).ContainsKey(InstallationId).Should().BeTrue();

        service.InvalidateInstallation(InstallationId);

        GetTokenCache(service).ContainsKey(InstallationId).Should().BeFalse();
    }

    [Fact]
    public async Task One_thousand_concurrent_fast_path_calls_all_return_the_same_cached_AccessToken()
    {
        using var service = NewService();
        SeedToken(service, InstallationId, DateTimeOffset.UtcNow.AddHours(1));
        var original = GetTokenCache(service)[InstallationId];

        var tasks = Enumerable.Range(0, 1000).Select(_ =>
            service.GetOrCreateInstallationTokenAsync(InstallationId, CancellationToken.None)).ToArray();
        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(t => ReferenceEquals(t, original));
    }

    [Fact]
    public async Task CreateAppClientAsync_throws_when_ClientId_is_not_configured()
    {
        using var service = NewService(new GitHubWebhooksOptions
        {
            PrivateKeyPem = GenerateRsaPrivateKeyPem(),
            // ClientId is null
        });

        var act = async () => await service.CreateAppClientAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ClientId*");
    }

    [Fact]
    public async Task CreateAppClientAsync_throws_when_neither_PrivateKeyPem_nor_PrivateKeyPath_is_set()
    {
        using var service = NewService(new GitHubWebhooksOptions
        {
            ClientId = "Iv23liFAKEAPPID",
        });

        var act = async () => await service.CreateAppClientAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*PrivateKeyPem*");
    }

    [Fact]
    public async Task CreateAppClientAsync_produces_a_GitHubClient_with_Bearer_credentials_and_a_three_part_JWT()
    {
        using var service = NewService(new GitHubWebhooksOptions
        {
            ClientId = "Iv23liFAKEAPPID",
            PrivateKeyPem = GenerateRsaPrivateKeyPem(),
        });

        var client = await service.CreateAppClientAsync();

        client.Connection.Credentials.AuthenticationType.Should().Be(AuthenticationType.Bearer);
        var jwt = client.Connection.Credentials.Password;
        jwt.Split('.').Should().HaveCount(3, "JWT format is header.payload.signature");
    }

    [Fact]
    public async Task CreateInstallationClientAsync_caches_the_client_per_installationId()
    {
        using var service = NewService();

        var a = await service.CreateInstallationClientAsync(InstallationId);
        var b = await service.CreateInstallationClientAsync(InstallationId);
        var c = await service.CreateInstallationClientAsync(InstallationId + 1);

        a.Should().BeSameAs(b, "same id → cached");
        a.Should().NotBeSameAs(c, "different id → different client");
    }

    private static string GenerateRsaPrivateKeyPem()
    {
        using var rsa = RSA.Create(2048);
        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        var base64 = Convert.ToBase64String(pkcs8, Base64FormattingOptions.InsertLineBreaks);
        return $"-----BEGIN PRIVATE KEY-----\n{base64}\n-----END PRIVATE KEY-----";
    }
}
