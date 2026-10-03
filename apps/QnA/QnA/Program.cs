using System.Text.RegularExpressions;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Contributions;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.History;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Moderation;
using MintPlayer.Spark.SoftDelete;
using QnA;
using QnA.Interceptors;
using QnA.Security;
using QnA.Services;
using QnA.Testing;

var builder = WebApplication.CreateBuilder(args);

// QnA is the #460 demo: every framework feature the MintPlayer migration needs, on a small
// question-and-answer site. Nothing below configures Data Protection or forwarded headers — Spark
// core does both (D5, D15): a local key folder and loopback/private-range proxies in Development,
// and outside it the host's configuration (Spark:DataProtection, Spark:ForwardedHeaders). The base
// appsettings.json sets neither, by owner decision: a key store in the base file would follow the
// app into every environment.
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<QnAContext>();
    spark.AddActions();
    spark.AddHooks();
    spark.AddCustomActions();

    // Password accounts, and every account must confirm its email before it can sign in (D6). The
    // confirmation link points at the SPA's confirm-email page (withAccount(), D16).
    spark.AddAuthentication<SparkUser>(auth =>
    {
        auth.LocalCredentials = SparkLocalCredentials.Full;
        auth.RequireConfirmedEmail = true;
    });

    // Mail is queued through Messaging and written as .eml files into Spark:Mail:PickupFolder
    // (appsettings.json) — the demo sends nothing (D6, M8). Moderation's jobs run on Cron, which
    // AddModeration registers itself.
    spark.AddMessaging();
    spark.AddMailManager();

    // M6: deleting a question or an answer marks it deleted; moderators restore and purge.
    spark.AddSoftDelete();
    // M7: revisions from the model ("revisions" in Question.json / Answer.json), audit stamping,
    // revert. Names in revision lists come from the accounts, at read time.
    spark.AddHistory();
    spark.AddHistoryUserNameResolver<QnAUserNames>();
    // M12: votes, reputation, earned privileges (App_Data/moderation.json), flags, the review queue,
    // locks, suspensions. After SoftDelete and History: it observes their operations.
    spark.AddModeration<SparkUser>();
    // Contributions M6: Question.Translations — every signed-in user writes their own version of each
    // language; the latest is shown, every version is in the history. Its interceptor is ordered after
    // SoftDelete, History and Moderation whatever the registration order; registered last to read so.
    // Translator names come from QnAUserNames through AddHistoryUserNameResolver above.
    spark.AddContributions();

    // M2: QnA's own rules through the core seams — a row policy and three hooks (AddHooks above
    // registers them too; listed here so the setup reads in one place).
    spark.AddSparkRowPolicy<DraftQuestionPolicy>();
    spark.AddHook<QuestionTagsInterceptor>();
    spark.AddHook<ClosedQuestionInterceptor>();
    // A translator who is not the question's author sees its own attributes read-only (a UI hint;
    // QuestionActions.GetProtectedAttributesAsync is the enforcement).
    spark.AddHook<QuestionTranslatorFormInterceptor>();
});

builder.Services.AddScoped<QnAAccess>();
builder.Services.AddScoped<QuestionStateChanges>();
// An after-deletion handler: the goodbye mail is queued only once the store deleted the account
// (Moderation's deletion handler removed the votes and flags before that).
builder.Services.AddScoped<ISparkAccountDeletedHandler<SparkUser>, QnAAccountDeletionHandler>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SparkAuth.QnA";
});

builder.Services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist/ClientApp/browser";
});

// Model synchronization is a build step, not a run mode: it writes App_Data/Model/*.json from the
// entity classes and needs no database, so it runs here and the process returns before Build().
if (builder.SynchronizeSparkModelsIfRequested(args))
    return;

// Writes a starting App_Data/security.json for an application that has none. Never overwrites.
if (builder.InitializeSparkSecurityIfRequested(args))
    return;

// --spark-init-moderation prints the security.json rights each privilege in moderation.json needs.
// It writes nothing; QnA's security.json was reviewed against its output.
if (builder.InitializeSparkModerationIfRequested(args))
    return;

// --spark-verify-security fails the build when the set of rights reachable WITHOUT signing in
// has changed (App_Data/securityPosture.txt).
if (builder.VerifySparkSecurityIfRequested(args))
    return;

var testSeams = QnATestSeams.IsEnabled(builder.Configuration, builder.Environment);

var app = builder.Build();

if (builder.Configuration.GetValue("Spark:HttpsRedirection", true))
    app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSpaStaticFilesImproved();

app.UseRouting();
app.UseSpark();

app.UseEndpoints(endpoints =>
{
    endpoints.MapSpark();
    if (testSeams)
        QnATestSeams.Map(endpoints);
});

app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/spark"),
    appBuilder =>
    {
        appBuilder.UseSpaImproved(spa =>
        {
            spa.Options.SourcePath = "ClientApp";

            if (app.Environment.IsDevelopment())
            {
                spa.UseAngularCliServer(npmScript: "start", cliRegexes: [openBrowserRegex()]);
            }
        });
    });

app.Run();

partial class Program
{
    [GeneratedRegex(@"Local\:\s+(?<openbrowser>https?\:\/\/(.+))")]
    private static partial Regex openBrowserRegex();
}
