using System.Text.RegularExpressions;
using Festival;
using Festival.Seed;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Challenges;
using MintPlayer.Spark.Challenges.Turnstile;
using MintPlayer.Spark.Commerce;
using MintPlayer.Spark.Commerce.Invoicing;
using MintPlayer.Spark.Commerce.Invoicing.Peppol.Test;
using MintPlayer.Spark.Commerce.Payments.Test;
using MintPlayer.Spark.Commerce.Promotions;
using MintPlayer.Spark.Commerce.Shipping;
using MintPlayer.Spark.Commerce.Shipping.Test;
using MintPlayer.Spark.Commerce.Shop;
using MintPlayer.Spark.Commerce.Ticketing.Tickets;
using MintPlayer.Spark.Commerce.WaitingRoom;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.WaitingRoom;

var builder = WebApplication.CreateBuilder(args);

// apps/Festival (commerce #521 M18, PRD §3.2, §4.16, D22): the festival scenario with both systems hooked up. A ticket
// channel and a shop channel in ConnectedSellers mode, one basket and one payment (Basket:MixChannels, D40), the test
// payment provider, the test carrier and the test Peppol access point. Local and E2E only: https 5015 / http 5016,
// database SparkFestival on localhost:8080. The channels, fees, shipping methods and VAT rates are in appsettings.json.
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<FestivalContext>();
    spark.AddSparkDiscovered();

    // Buyers register with a password; the tickets channel requires sign-in (D4), the shop sells to guests.
    spark.AddAuthentication<SparkUser>(auth => auth.LocalCredentials = SparkLocalCredentials.Full);

    // Payment and tracking webhooks are handled from the spark-webhooks queue; mail goes to the pickup folder.
    spark.AddMessaging();
    spark.AddMailManager();

    // GA9 (X21): the consent banner and records. Turnstile needs it; a visitor who refuses gets ALTCHA instead.
    spark.AddConsent();
    // The challenge at the waiting room's join (Spark:Challenges:Points). Turnstile runs with Cloudflare's published
    // always-pass test keys (appsettings.json), so nothing real is verified and no secret is involved.
    spark.AddChallenges(c => c.UseAltcha().UseTurnstile());

    // The reference-data client (addresses, commerce §4.18): pointed at apps/ReferenceData in Development
    // (appsettings.Development.json), free text everywhere else.
    spark.AddReferenceDataClient();

    // The commerce suite (PRD §3.3). One line per package; the adapters are the .Test ones (K7, D37).
    spark.AddCommerce();                                        // Spark:Commerce: channels, sellers, fees, legal documents
    spark.AddTestPayments();                                    // the test provider (instant Connect onboarding, disputes)
    spark.AddCommerceShipping().UseTestCarrier();               // methods and zones: Spark:Commerce:Shipping
    spark.AddCommerceShop();                                    // products, variants, delivery, returns; adds Checkout
    spark.AddCommerceInvoicing().UseTestPeppol();               // invoices and credit notes; Peppol to the in-memory access point
    spark.AddCommercePromotions();                              // sale prices with the prior price (GA3), back-in-stock
    spark.AddTicketing();                                       // Ticketing.Events + Venues + Tickets (scanner)
    spark.AddWaitingRoom();                                     // rooms, modes, admission tokens
    spark.AddCommerceWaitingRoom();                             // one room per queued sale phase
});

builder.Services.AddHealthChecks();
builder.Services.AddHostedService<FestivalSeeder>();
builder.Services.AddHostedService<FestivalAdminSeeder>();
builder.Services.AddScoped<Festival.Testing.FestivalTestEvents>();

builder.Services.ConfigureApplicationCookie(options => options.Cookie.Name = ".SparkAuth.Festival");

builder.Services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist/ClientApp/browser";
});

// The --spark-* commands are build steps: they need no database and return before Build().
if (builder.SynchronizeSparkModelsIfRequested(args))
    return;
if (builder.InitializeSparkSecurityIfRequested(args))
    return;
if (builder.VerifySparkSecurityIfRequested(args))
    return;

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
    endpoints.MapHealthChecks("/health").AllowAnonymous();
    // The E2E test seams (/festival-test/*): mapped only when Festival:TestSeams:Enabled, refused in Production.
    endpoints.MapFestivalEndpoints();
});

// /spark, /festival-test and /health are never the SPA's index.
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/spark")
        && !context.Request.Path.StartsWithSegments("/festival-test")
        && !context.Request.Path.StartsWithSegments("/health"),
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

public partial class Program
{
    [GeneratedRegex(@"Local\:\s+(?<openbrowser>https?\:\/\/(.+))")]
    private static partial Regex openBrowserRegex();
}
