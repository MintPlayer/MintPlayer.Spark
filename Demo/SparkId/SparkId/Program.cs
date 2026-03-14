using System.Text.RegularExpressions;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Extensions;
using SparkId;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<SparkIdContext>();
    spark.AddActions();

    spark.AddAuthorization();
    spark.AddAuthentication<SparkUser>();

    // Social login providers using the same AddOidcLogin() that HR/Fleet use
    // (commented out until AddOidcLogin is implemented)
    // spark.AddOidcLogin("google", opts => { ... });
    // spark.AddOidcLogin("facebook", opts => { ... });

    // OIDC Identity Provider
    spark.AddIdentityProvider();
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SparkAuth.SparkId";
});

builder.Services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist/ClientApp/browser";
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSpaStaticFilesImproved();

app.UseRouting();
app.UseSpark();
app.SynchronizeSparkModelsIfRequested<SparkIdContext>(args);

// Seed default OIDC scopes and development client registrations
if (app.Environment.IsDevelopment())
{
    var store = app.Services.GetRequiredService<Raven.Client.Documents.IDocumentStore>();
    await OidcSeedData.SeedAsync(store);
}

app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapSpark();
});

app.MapWhen(
    context => !context.Request.Path.StartsWithSegments("/spark")
            && !context.Request.Path.StartsWithSegments("/connect")
            && !context.Request.Path.StartsWithSegments("/.well-known"),
    appBuilder =>
    {
        appBuilder.UseSpaImproved(spa =>
        {
            spa.Options.SourcePath = "ClientApp";

            if (app.Environment.IsDevelopment())
            {
                spa.UseAngularCliServer(npmScript: "start", cliRegexes: [new Regex(@"Local\:\s+(?<openbrowser>https?\:\/\/(.+))")]);
            }
        });
    });

app.Run();
