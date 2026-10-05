using System.Text.Json.Nodes;

namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// Runs the HR demo app on <see cref="SparkAppTestHost"/>. HR is both a replication module and an OIDC
/// issuer, so it needs what Fleet needs for those two: a SparkModules database of its own, a pinned
/// issuer, and a signing key (the provider generates one only in Development).
/// </summary>
/// <remarks>
/// Added for #264: HR's <c>Person.LastName</c> was hidden by <c>isVisible: false</c> while it was required,
/// so a person could not be created from the SPA at all. Only an E2E test against the real model file and the
/// real client reaches that.
/// </remarks>
public sealed class HRTestHost : SparkAppTestHost
{
    public static readonly SparkAppDescriptor HR = new(
        AppName: "HR",
        ProjectDirectory: Path.Combine("apps", "HR", "HR"),
        ProjectFileName: "HR.csproj",
        DatabasePrefix: "SparkHRE2E",
        CoverageSlug: "hr")
    {
        UsesMailPickup = true,
    };

    /// <summary>The <c>ObjectTypeId</c> of <c>Person</c> in <c>apps/HR/HR/App_Data/Model/Person.json</c>.</summary>
    public static readonly Guid PersonTypeId = Guid.Parse("2a34a658-1768-4c82-8724-884b2f99f4e0");

    public HRTestHost() : base(HR) { }

    private string TestModulesDatabase => $"SparkModulesHRE2E-{Suffix}";

    protected override IEnumerable<string> ExtraDatabases => [TestModulesDatabase];

    protected override async Task ConfigureAppSettings(JsonObject settings, SparkAppHostContext context)
    {
        // Same reasoning as FleetTestHost: tests are not Development, so the key is supplied.
        var signingKeyFileName = $"oidc-signing-key.{EnvironmentName}.json";
        var signingKeyFile = Path.Combine(context.ProjectDirectory, signingKeyFileName);
        RegisterTemporaryFile(signingKeyFile);
        await File.WriteAllTextAsync(signingKeyFile, NewSigningKeyJson());

        var spark = settings["Spark"]!.AsObject();
        spark["Replication"] = new JsonObject
        {
            ["ModuleName"] = "HR",
            ["ModuleUrl"] = context.HttpsUrl,
            ["SparkModulesUrls"] = new JsonArray(context.RavenUrls[0]),
            ["SparkModulesDatabase"] = TestModulesDatabase,
        };

        settings["SparkIdentityProvider"] = new JsonObject
        {
            ["Issuer"] = $"http://localhost:{context.HttpPort}",
            ["SigningKeyPath"] = signingKeyFileName,
        };
    }

    /// <summary>An RSA key in the shape <c>OidcSigningKeyService</c> reads (base64url RSA parameters).</summary>
    private static string NewSigningKeyJson()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var p = rsa.ExportParameters(true);
        static string B64(byte[] data) =>
            Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            N = B64(p.Modulus!),
            E = B64(p.Exponent!),
            D = B64(p.D!),
            P = B64(p.P!),
            Q = B64(p.Q!),
            DP = B64(p.DP!),
            DQ = B64(p.DQ!),
            QI = B64(p.InverseQ!),
        });
    }
}
