using System.Text.RegularExpressions;
using HR;
using Microsoft.AspNetCore.HttpOverrides;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Replication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddControllers();
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<HRContext>();
    spark.AddActions();
    spark.AddMigrations(); // generated: discovers ISparkMigration classes, runs them once at startup

    spark.AddAuthorization();
    spark.AddAuthentication<SparkUser>();

    // HR doubles as the identity provider: it serves /connect/* and administers its own clients
    // and scopes through the PersistentObject screens (see HRContext). Issuer is pinned rather
    // than derived from the Host header — outside Development the provider requires it, because a
    // caller-controlled issuer is a caller-controlled token audience.
    spark.AddIdentityProvider(options =>
    {
        options.Issuer = builder.Configuration["SparkIdentityProvider:Issuer"]
            ?? "https://localhost:5002";
    });

    spark.AddMessaging();

    // Everything else comes from the `Spark:Replication` section, bound by AddReplication.
    // Assemblies are the one setting configuration cannot express.
    spark.AddReplication(opt => opt.AssembliesToScan = [typeof(HR.Replicated.Car).Assembly]);
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SparkAuth.HR";
});

builder.Services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist/ClientApp/browser";
});

var app = builder.Build();

app.UseForwardedHeaders();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSpaStaticFilesImproved();

app.UseRouting();
app.UseSpark(o => o.SynchronizeModelsIfRequested<HRContext>(args));

app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapSpark();
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