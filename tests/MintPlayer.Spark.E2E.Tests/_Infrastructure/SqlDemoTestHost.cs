using System.Text.Json.Nodes;

namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// Runs the relational providers' demo (<c>apps/SqlDemo</c>, #536) on <see cref="SparkAppTestHost"/>, on a database of
/// its own on the PostgreSQL test server: <c>SPARK_TEST_POSTGRESQL</c>'s, or the embedded instance. The app applies its
/// schema at startup (the framework's migrations, then <c>Migrations/PostgreSql</c>), as it does in production.
/// </summary>
public sealed class SqlDemoTestHost : SparkAppTestHost
{
    public static readonly SparkAppDescriptor SqlDemo = new(
        AppName: "SqlDemo",
        ProjectDirectory: Path.Combine("apps", "SqlDemo", "SqlDemo"),
        ProjectFileName: "SqlDemo.csproj",
        // A PostgreSQL database name: lower case, so it needs no quoting in psql.
        DatabasePrefix: "sqldemo_e2e",
        CoverageSlug: "sqldemo")
    {
        Store = SparkAppStore.PostgreSql,
        UsesMailPickup = true,
    };

    public SqlDemoTestHost() : base(SqlDemo) { }

    /// <summary>The <c>ObjectTypeId</c>s and query ids in <c>apps/SqlDemo/SqlDemo/App_Data/Model</c>.</summary>
    public static readonly Guid AuthorTypeId = Guid.Parse("7f2cb8e2-e488-4343-975d-ba64859d4ae1");
    public static readonly Guid BookTypeId = Guid.Parse("2cea3221-e367-48c1-973f-b5a946c13d9b");
    public static readonly Guid GetBooksQueryId = Guid.Parse("0a9cbe71-912e-466c-a994-d48b11e933b9");
    public static readonly Guid GetAuthorsQueryId = Guid.Parse("3c39ca26-23e6-4c96-bd87-cf70d8f1fed8");

    protected override Task ConfigureAppSettings(JsonObject settings, SparkAppHostContext context)
    {
        // Outside Development an account mail is refused without it (#460 M5): registration sends a confirmation.
        settings["Spark"]!["Auth"] = new JsonObject { ["PublicBaseUrl"] = context.HttpsUrl };
        return Task.CompletedTask;
    }
}
