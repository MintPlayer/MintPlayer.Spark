using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Contributions;
using MintPlayer.Spark.History;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Moderation;
using MintPlayer.Spark.Replication;
using MintPlayer.Spark.SoftDelete;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Webhooks.GitHub.Extensions;

namespace MintPlayer.Spark.Tests.Endpoints;

/// <summary>
/// The Spark library's route table, per module composition, against a committed snapshot
/// (<c>docs/endpoints_generator_completion_plan.md</c> M0).
/// </summary>
/// <remarks>
/// The Endpoints-generator completion rewrites how nearly every route is declared and must change
/// none of them: not a path, a verb, an <c>[Authorize]</c>, an antiforgery flag. Each configuration
/// below boots a real <see cref="SparkEndpointFactory{TContext}"/> host and compares its
/// <see cref="Microsoft.AspNetCore.Routing.EndpointDataSource"/> with
/// <c>RouteSnapshots/{name}.txt</c>; see <see cref="RouteTableSnapshot"/> for what a line records.
/// <para>
/// To accept an intended change: set <c>SPARK_UPDATE_ROUTE_SNAPSHOT=1</c>, run this class, and review
/// the fixture diff like code. The applications' own route tables are snapshotted beside their hosts
/// (<c>apps/{App}/{App}.Tests</c>, one test project per application).
/// </para>
/// </remarks>
public class RouteTableSnapshotTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private const string FixtureDirectory = "tests/MintPlayer.Spark.Tests/Endpoints/RouteSnapshots";

    /// <summary>Every composition, by fixture name.</summary>
    public static TheoryData<string> Configurations => new(Compositions.Keys.Order(StringComparer.Ordinal));

    /// <remarks>
    /// The three <see cref="SparkLocalCredentials"/> modes each appear twice: with every
    /// authentication option at its default, and with every route-adding option on (passkeys,
    /// external-login linking, email change) plus the identity provider, whose <c>/connect</c>
    /// surface also depends on the mode. The modules each map their routes unconditionally, so one
    /// host carries all of them. The GitHub webhooks appear with and without
    /// <c>DevelopmentAppId</c>, the only switch for the dev WebSocket route.
    /// </remarks>
    private static readonly Dictionary<string, Action<ISparkBuilder>> Compositions = new()
    {
        ["core"] = _ => { },
        ["auth-full"] = spark => Authentication(spark, SparkLocalCredentials.Full, allOptions: false),
        ["auth-signinonly"] = spark => Authentication(spark, SparkLocalCredentials.SignInOnly, allOptions: false),
        ["auth-disabled"] = spark => Authentication(spark, SparkLocalCredentials.Disabled, allOptions: false),
        ["auth-full-all-options"] = spark => Authentication(spark, SparkLocalCredentials.Full, allOptions: true),
        ["auth-signinonly-all-options"] = spark => Authentication(spark, SparkLocalCredentials.SignInOnly, allOptions: true),
        ["auth-disabled-all-options"] = spark => Authentication(spark, SparkLocalCredentials.Disabled, allOptions: true),
        ["modules"] = spark =>
        {
            Authentication(spark, SparkLocalCredentials.Full, allOptions: false);
            spark.AddSoftDelete();
            spark.AddHistory();
            spark.AddContributions();
            spark.AddModeration<SparkUser>();
            spark.AddReplication(o => o.ModuleName = "RouteSnapshot");
        },
        ["webhooks"] = spark => spark.AddMessaging().AddGithubWebhooks(o => o.WebhookSecret = "route-snapshot"),
        ["webhooks-dev-app"] = spark => spark.AddMessaging().AddGithubWebhooks(o =>
        {
            o.WebhookSecret = "route-snapshot";
            o.DevelopmentAppId = 1;
        }),
    };

    private static void Authentication(ISparkBuilder spark, SparkLocalCredentials mode, bool allOptions)
    {
        // Mail needs a message bus; both are what every application that signs users in composes.
        spark.AddMessaging();
        spark.AddMailManager(o =>
        {
            o.From.Address = "noreply@route-snapshot.example";
            o.PickupFolder = Path.Combine(Path.GetTempPath(), "spark-route-snapshot-mail");
        });
        spark.AddAuthentication<SparkUser>(
            configure: auth =>
            {
                auth.LocalCredentials = mode;
                auth.AllowUnconfirmedRegistration = true;
                if (allOptions)
                {
                    auth.Passkeys = SparkPasskeys.Enabled;
                    auth.ExternalLoginLinking = SparkExternalLoginLinking.WhenSignedIn;
                    auth.EmailChange = SparkEmailChange.Enabled;
                }
            });
        // Disabled mode refuses to start without an external provider (nobody could sign in);
        // registered in every mode so the modes differ only in what the mode itself changes.
        spark.Services
            .AddAuthentication()
            .AddCookie("GitHub", "GitHub", _ => { });

        if (allOptions)
        {
            spark.AddIdentityProvider(options =>
            {
                options.Issuer = "https://idp.test";
                // Adds RequireCors to the /connect groups: CORS metadata is part of what the snapshot guards.
                options.EnableDynamicCors = true;
                options.SigningKeyPath = Path.Combine(Path.GetTempPath(), "spark-route-snapshot-" + Guid.NewGuid().ToString("N") + ".json");
            });
        }
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task Route_table_matches_the_committed_snapshot(string configuration)
    {
        // Development: the identity provider generates its own signing key only there.
        await using var factory = new SparkEndpointFactory<TestSparkContext>(
            Store,
            models: [],
            configureSpark: Compositions[configuration],
            environment: "Development");

        var snapshot = RouteTableSnapshot.Capture(factory.GetService<IServiceProvider>());

        RouteTableSnapshot.AssertMatchesFixture(snapshot, $"{FixtureDirectory}/{configuration}.txt");
    }
}
