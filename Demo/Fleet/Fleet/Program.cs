using System.Text.RegularExpressions;
using Fleet;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using MintPlayer.Spark.Replication.Authentication;
using MintPlayer.AspNetCore.SpaServices.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Fleet owns data other modules replicate, so it has to be able to recognise them. Asking for a
// client certificate is not the same as requiring one: AllowCertificate keeps every ordinary
// browser and anonymous request working exactly as before, and simply makes the certificate
// available on the connections that do present one.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ConfigureHttpsDefaults(https =>
    {
        https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;

        // Kestrel validates the client certificate's chain during the TLS handshake, before any
        // authentication handler runs. Module certificates come from an operator-created CA that no
        // machine trusts by default, so the handshake would fail and the request would never reach
        // Spark — the scheme would look misconfigured rather than refused.
        //
        // Deferring here is not "trust anything": it moves the decision to the thumbprint pinned in
        // SparkModules, which is strictly narrower than chain validation. A certificate from the
        // right CA still fails unless it is *the* certificate that module registered.
        https.AllowAnyClientCertificate();
    });
});

builder.Services.AddControllers();
builder.Services.AddSparkFull(builder.Configuration, options =>
{
    // Everything else comes from the `Spark:Replication` section, bound by AddReplication.
    // Assemblies are the one setting configuration cannot express.
    options.Replication = opt => opt.AssembliesToScan = [typeof(Fleet.Replicated.Person).Assembly];
    options.RateLimiter = _ => { };

    options.Configure = spark =>
    {
        // A module presenting its registered certificate becomes an ordinary Spark caller: the CN
        // names the module, the handler emits `group = "Module:{CN}"`, and security.json governs it
        // exactly like a person. Nothing here is replication-specific — the credential works on
        // every Spark endpoint, which is the point of M9's composite scheme.
        spark.AddModuleCertificateAuthentication();
    };
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SparkAuth.Fleet";
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
app.UseSparkFull(args);

app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapSparkFull();
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