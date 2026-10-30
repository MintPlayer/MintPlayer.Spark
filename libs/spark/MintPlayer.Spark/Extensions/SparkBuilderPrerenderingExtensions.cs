using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.AspNetCore.SpaServices.Abstractions;
using MintPlayer.AspNetCore.SpaServices.Prerendering;
using MintPlayer.AspNetCore.SpaServices.Prerendering.Services;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Prerendering;
using MintPlayer.Spark.Prerendering;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Server-side rendering of the Angular app with its data (docs/spark_ssr_PRD.md). Opt-in, inert until
/// both calls are made (D5):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark => spark
///     .AddPrerendering(o => o.DefaultTimeZone = "Europe/Brussels"));
///
/// app.UseSpaImproved(spa =>
/// {
///     spa.Options.SourcePath = "ClientApp";
///     spa.UseSparkPrerendering();
///     if (app.Environment.IsDevelopment()) spa.UseAngularCliServer(npmScript: "start");
/// });
/// </code>
/// </summary>
public static class SparkBuilderPrerenderingExtensions
{
    /// <summary>The <c>ApplicationBuilder.Properties</c> key UseRouting reads the endpoint route builder from.</summary>
    private const string GlobalEndpointRouteBuilderKey = "__GlobalEndpointRouteBuilder";

    /// <summary>Registers the supplier. Idempotent; libraries register their own contributors.</summary>
    public static ISparkBuilder AddPrerendering(this ISparkBuilder builder, Action<SparkPrerenderingOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.AddOptions<SparkPrerenderingOptions>();
        builder.Services.TryAddSingleton<SparkPrerenderDispatcher>();
        builder.Services.TryAddScoped<ISpaPrerenderingService, SparkPrerenderingService>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<ISparkPrerenderContributor, SparkRoutesPrerenderContributor>());
        return builder;
    }

    /// <summary>
    /// Server-renders every SPA page. Call inside <c>UseSpaImproved</c>, before <c>UseAngularCliServer</c>.
    /// <para>
    /// The boot module defaults to <c>{SourcePath}/dist/ClientApp/server/main.server.mjs</c>, the Angular
    /// build's server output, and the page template to <c>/index.csr.html</c>: an SSR build renames the
    /// browser's <c>index.html</c> (PRD §7 SP1).
    /// </para>
    /// </summary>
    public static ISpaBuilder UseSparkPrerendering(this ISpaBuilder spa, Action<SpaPrerenderingOptions>? configure = null)
    {
        var app = spa.ApplicationBuilder;
        var dispatcher = app.ApplicationServices.GetService<SparkPrerenderDispatcher>()
            ?? throw new InvalidOperationException("UseSparkPrerendering() needs AddSpark(..., spark => spark.AddPrerendering()).");

        // The page's requests run through the application's own endpoints. UseRouting finds them through
        // the endpoint route builder under this key, but WebApplication.New() removes it from every branch
        // (UseWhen, UseSpaImproved), and without it UseRouting starts an empty table: every request 404s.
        // The application's endpoints are also the DI EndpointDataSource, which UseEndpoints fills.
        var branch = app.New();
        branch.Properties[GlobalEndpointRouteBuilderKey] = new ApplicationEndpoints(app);
        SparkPrerenderDispatcher.Configure(branch);
        dispatcher.Pipeline = branch.Build();

        spa.Options.DefaultPage = "/index.csr.html";
        spa.UseSpaPrerendering(options =>
        {
            options.BootModulePath = $"{spa.Options.SourcePath}/dist/ClientApp/server/main.server.mjs";
            options.ExcludeUrls = ["/sockjs-node", "/ng-cli-ws", "/@vite", "/@fs"];
            configure?.Invoke(options);
        });
        return spa;
    }

    /// <summary>Every endpoint the application mapped, as the DI <see cref="EndpointDataSource"/> composes them.</summary>
    private sealed class ApplicationEndpoints(IApplicationBuilder app) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider => app.ApplicationServices;
        public ICollection<EndpointDataSource> DataSources { get; } = [app.ApplicationServices.GetRequiredService<EndpointDataSource>()];
        public IApplicationBuilder CreateApplicationBuilder() => app.New();
    }
}
