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
        dispatcher.Attach(app);

        spa.Options.DefaultPage = "/index.csr.html";
        spa.UseSpaPrerendering(options =>
        {
            options.BootModulePath = $"{spa.Options.SourcePath}/dist/ClientApp/server/main.server.mjs";
            options.ExcludeUrls = ["/sockjs-node", "/ng-cli-ws", "/@vite", "/@fs"];
            configure?.Invoke(options);
        });
        return spa;
    }

    /// <summary>
    /// The development server bundle (PRD I12, M4): runs <paramref name="npmScript"/>, an Angular build of the server
    /// entry in watch mode, and renders once its first build completes. Each rebuild replaces the boot module, so a
    /// change to server-side code needs no production build. Set it in <c>UseSparkPrerendering</c>'s
    /// <c>configure</c>, in Development only:
    /// <code>
    /// spa.UseSparkPrerendering(o => o.BootModuleBuilder = app.Environment.IsDevelopment() ? SparkBuilderPrerenderingExtensions.WatchBuild("build:ssr:watch") : null);
    /// </code>
    /// The script must keep its Angular cache apart from the dev server's (<c>tools/ssr-watch.mjs</c> sets <c>CI=1</c>),
    /// since two builds writing one <c>.angular/cache</c> can wedge the dev server's watcher.
    /// </summary>
    public static ISpaPrerendererBuilder WatchBuild(string npmScript)
        => new AngularPrerendererBuilder(npmScript, @"Application bundle generation complete\.", 1);

}
