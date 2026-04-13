using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Services.Internal;
using Newtonsoft.Json;
using Octokit;
using Octokit.Internal;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using GraphQLConnection = Octokit.GraphQL.Connection;
using GraphQLProductHeaderValue = Octokit.GraphQL.ProductHeaderValue;

namespace MintPlayer.Spark.Webhooks.GitHub.Services;

[Register(typeof(IGitHubInstallationService), ServiceLifetime.Singleton)]
internal partial class GitHubInstallationService : IGitHubInstallationService, IDisposable
{
    private static readonly ProductHeaderValue ProductHeader = new("SparkWebhooks", "1.0");
    private static readonly GraphQLProductHeaderValue GraphQLProductHeader = new("SparkWebhooks", "1.0");

    [Options] private readonly IOptions<GitHubWebhooksOptions> _options;

    private readonly ConcurrentDictionary<long, AccessToken> _installationTokens = new();
    private readonly ConcurrentDictionary<long, IGitHubClient> _installationClients = new();
    private readonly ConcurrentDictionary<long, GraphQLConnection> _installationGraphQLConnections = new();
    private readonly List<IDisposable> _ownedDisposables = new();
    private readonly IHttpClient _sharedRestHttpClient = new HttpClientAdapter(HttpMessageHandlerFactory.CreateDefault);
    private readonly SemaphoreSlim _refreshGate = new(initialCount: 1, maxCount: 1);
    private bool _disposed;

    /// <summary>
    /// Returns a cached installation token if it has &gt; 60s remaining, otherwise mints a fresh one
    /// (under <see cref="_refreshGate"/> to ensure only one concurrent refresh per installation).
    /// </summary>
    internal async Task<AccessToken> GetOrCreateInstallationTokenAsync(long installationId, CancellationToken cancellationToken)
    {
        // Fast path — lock-free read of a still-fresh token
        if (_installationTokens.TryGetValue(installationId, out var cached)
            && DateTimeOffset.UtcNow.AddSeconds(60) < cached.ExpiresAt)
        {
            return cached;
        }

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            // Re-check inside the gate (another caller may have refreshed)
            if (_installationTokens.TryGetValue(installationId, out cached)
                && DateTimeOffset.UtcNow.AddSeconds(60) < cached.ExpiresAt)
            {
                return cached;
            }

            var appClient = await CreateAppClientAsync();
            var fresh = await appClient.GitHubApps.CreateInstallationToken(installationId);
            _installationTokens[installationId] = fresh;
            return fresh;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Removes the cached token for the given installation. Called by the 401-retry interceptors
    /// when a request returns Unauthorized, forcing the next call to mint a fresh token.
    /// </summary>
    internal void InvalidateInstallation(long installationId)
        => _installationTokens.TryRemove(installationId, out _);

    public async Task<IGitHubClient> CreateAppClientAsync()
    {
        var opts = _options.Value;
        var privateKey = await ResolvePrivateKeyAsync(opts);
        var jwt = CreateJwt(opts.ClientId!, privateKey);

        return new GitHubClient(ProductHeader)
        {
            Credentials = new Credentials(jwt, AuthenticationType.Bearer),
        };
    }

    public Task<IGitHubClient> CreateInstallationClientAsync(long installationId)
        => Task.FromResult(_installationClients.GetOrAdd(installationId, BuildInstallationClient));

    private IGitHubClient BuildInstallationClient(long installationId)
    {
        var refreshing = new TokenRefreshingHttpClient(_sharedRestHttpClient, installationId, this);
        var connection = new Connection(
            ProductHeader,
            GitHubClient.GitHubApiUrl,
            new DynamicInstallationCredentialStore(installationId, this),
            refreshing,
            new SimpleJsonSerializer());
        return new GitHubClient(connection);
    }

    public async Task<GraphQLConnection> CreateGraphQLConnectionAsync(long installationId, EClientType clientType)
    {
        switch (clientType)
        {
            case EClientType.App:
                // App-mode GraphQL is rare and never cached — mint a fresh JWT-bearing connection per call.
                var appClient = await CreateAppClientAsync();
                var appToken = appClient.Connection.Credentials.Password;
                return new GraphQLConnection(GraphQLProductHeader, appToken);
            case EClientType.Installation:
                return _installationGraphQLConnections.GetOrAdd(installationId, BuildInstallationGraphQLConnection);
            default:
                throw new ArgumentOutOfRangeException(nameof(clientType), clientType, null);
        }
    }

    private GraphQLConnection BuildInstallationGraphQLConnection(long installationId)
    {
        var handler = new TokenRefreshingHandler(installationId, this) { InnerHandler = new HttpClientHandler() };
        var httpClient = new HttpClient(handler);
        lock (_ownedDisposables) { _ownedDisposables.Add(httpClient); }

        return new GraphQLConnection(
            GraphQLProductHeader,
            new DynamicInstallationGraphQLCredentialStore(installationId, this),
            httpClient);
    }

    private static async Task<string> ResolvePrivateKeyAsync(GitHubWebhooksOptions opts)
    {
        var privateKey = opts.PrivateKeyPem;
        if (string.IsNullOrEmpty(privateKey))
        {
            if (string.IsNullOrEmpty(opts.PrivateKeyPath))
                throw new InvalidOperationException(
                    "GitHub App authentication requires either PrivateKeyPem or PrivateKeyPath to be configured.");

            var absolutePath = Path.IsPathRooted(opts.PrivateKeyPath)
                ? opts.PrivateKeyPath
                : Path.Combine(Directory.GetCurrentDirectory(), opts.PrivateKeyPath);
            privateKey = await File.ReadAllTextAsync(absolutePath);
        }

        if (string.IsNullOrEmpty(opts.ClientId))
            throw new InvalidOperationException(
                "GitHub App authentication requires ClientId to be configured.");

        return privateKey;
    }

    private static string CreateJwt(string clientId, string privateKey)
    {
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
        {
            alg = "RS256",
            typ = "JWT"
        })));

        var iat = DateTimeOffset.UtcNow.AddSeconds(-60);
        var exp = iat.AddMinutes(10);

        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
        {
            iat = iat.ToUnixTimeSeconds(),
            exp = exp.ToUnixTimeSeconds(),
            iss = clientId
        })));

        var rsa = RSA.Create();
        rsa.ImportFromPem(privateKey);

        var signature = Base64UrlEncode(
            rsa.SignData(Encoding.UTF8.GetBytes($"{header}.{payload}"),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        return $"{header}.{payload}.{signature}";
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _refreshGate.Dispose();
        _sharedRestHttpClient.Dispose();

        lock (_ownedDisposables)
        {
            foreach (var d in _ownedDisposables)
            {
                try { d.Dispose(); }
                catch { /* swallow — best-effort cleanup at shutdown */ }
            }
            _ownedDisposables.Clear();
        }

        _installationClients.Clear();
        _installationGraphQLConnections.Clear();
        _installationTokens.Clear();
    }
}
