using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace MintPlayer.Spark.IdentityProvider.Services;

internal class OidcSigningKeyService
{
    private readonly RsaSecurityKey _signingKey;
    private readonly JsonWebKey _publicJwk;

    public OidcSigningKeyService(IHostEnvironment environment, string signingKeyPath)
    {
        var keyPath = Path.IsPathRooted(signingKeyPath)
            ? signingKeyPath
            : Path.Combine(environment.ContentRootPath, signingKeyPath);

        var rsa = RSA.Create();

        if (File.Exists(keyPath))
        {
            var json = File.ReadAllText(keyPath);
            var keyData = JsonSerializer.Deserialize<RsaKeyData>(json)!;
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = Base64UrlDecode(keyData.N),
                Exponent = Base64UrlDecode(keyData.E),
                D = Base64UrlDecode(keyData.D),
                P = Base64UrlDecode(keyData.P),
                Q = Base64UrlDecode(keyData.Q),
                DP = Base64UrlDecode(keyData.DP),
                DQ = Base64UrlDecode(keyData.DQ),
                InverseQ = Base64UrlDecode(keyData.QI),
            });
        }
        else
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    $"OIDC signing key not found at '{keyPath}'. " +
                    "In Production, provide a signing key. Auto-generation is only available in Development.");
            }

            rsa = RSA.Create(2048);

            var parameters = rsa.ExportParameters(true);
            var keyData = new RsaKeyData
            {
                N = Base64UrlEncode(parameters.Modulus!),
                E = Base64UrlEncode(parameters.Exponent!),
                D = Base64UrlEncode(parameters.D!),
                P = Base64UrlEncode(parameters.P!),
                Q = Base64UrlEncode(parameters.Q!),
                DP = Base64UrlEncode(parameters.DP!),
                DQ = Base64UrlEncode(parameters.DQ!),
                QI = Base64UrlEncode(parameters.InverseQ!),
            };

            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            File.WriteAllText(keyPath, JsonSerializer.Serialize(keyData, new JsonSerializerOptions { WriteIndented = true }));
        }

        _signingKey = new RsaSecurityKey(rsa) { KeyId = "spark-oidc-key-1" };

        // Build public JWK for the JWKS endpoint
        var pubParams = rsa.ExportParameters(false);
        _publicJwk = new JsonWebKey
        {
            Kty = "RSA",
            Use = "sig",
            Kid = _signingKey.KeyId,
            Alg = SecurityAlgorithms.RsaSha256,
            N = Base64UrlEncode(pubParams.Modulus!),
            E = Base64UrlEncode(pubParams.Exponent!),
        };
    }

    public RsaSecurityKey GetSigningKey() => _signingKey;

    public JsonWebKey GetPublicJwk() => _publicJwk;

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private sealed class RsaKeyData
    {
        public string N { get; set; } = "";
        public string E { get; set; } = "";
        public string D { get; set; } = "";
        public string P { get; set; } = "";
        public string Q { get; set; } = "";
        public string DP { get; set; } = "";
        public string DQ { get; set; } = "";
        public string QI { get; set; } = "";
    }
}
