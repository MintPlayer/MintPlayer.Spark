using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using Newtonsoft.Json;
using Octokit;
using System.Security.Cryptography;
using System.Text;

namespace MintPlayer.Spark.Webhooks.GitHub.Services;

[Register(typeof(IGitHubInstallationService), ServiceLifetime.Scoped)]
internal partial class GitHubInstallationService : IGitHubInstallationService
{
    [Options] private readonly IOptions<GitHubWebhooksOptions> _options;

    public async Task<IGitHubClient> CreateClientAsync(long installationId)
    {
        var opts = _options.Value;
        var privateKey = opts.PrivateKeyPem;
        if (string.IsNullOrEmpty(privateKey))
        {
            if (string.IsNullOrEmpty(opts.PrivateKeyPath))
                throw new InvalidOperationException(
                    "GitHub App authentication requires either PrivateKeyPem or PrivateKeyPath to be configured.");

            var absolutePath = Path.IsPathRooted(opts.PrivateKeyPath)
                ? opts.PrivateKeyPath
                : Path.Combine(Directory.GetCurrentDirectory(), opts.PrivateKeyPath);
            privateKey = File.ReadAllText(absolutePath);
        }

        if (string.IsNullOrEmpty(opts.ClientId))
            throw new InvalidOperationException(
                "GitHub App authentication requires ClientId to be configured.");

        var jwt = CreateJwt(opts.ClientId, privateKey);

        var header = new ProductHeaderValue("SparkWebhooks", "1.0");
        var appClient = new GitHubClient(header)
        {
            Credentials = new Credentials(jwt, AuthenticationType.Bearer)
        };

        var response = await appClient.GitHubApps.CreateInstallationToken(installationId);
        return new GitHubClient(header)
        {
            Credentials = new Credentials(response.Token)
        };
    }

    private static string CreateJwt(string clientId, string privateKey)
    {
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
        {
            alg = "RS256",
            typ = "JWT"
        })));

        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
        {
            iat = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds(),
            exp = DateTimeOffset.UtcNow.AddMinutes(9).ToUnixTimeSeconds(),
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
}
