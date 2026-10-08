using System.Text.Json.Nodes;

namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// Runs the HR demo app on <see cref="SparkAppTestHost"/>. HR is a replication module, so it needs a
/// SparkModules database of its own. It stopped hosting the identity provider when SparkId took over
/// (<c>docs/identity_provider_platform_PRD.md</c> I0).
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

    protected override Task ConfigureAppSettings(JsonObject settings, SparkAppHostContext context)
    {
        var spark = settings["Spark"]!.AsObject();
        spark["Replication"] = new JsonObject
        {
            ["ModuleName"] = "HR",
            ["ModuleUrl"] = context.HttpsUrl,
            ["SparkModulesUrls"] = new JsonArray(context.RavenUrls[0]),
            ["SparkModulesDatabase"] = TestModulesDatabase,
        };

        return Task.CompletedTask;
    }
}
