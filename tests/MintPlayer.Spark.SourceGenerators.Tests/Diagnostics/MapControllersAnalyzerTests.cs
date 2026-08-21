using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// #300 — SPARK010. Controllers mapped with ASP.NET Core's own <c>MapControllers()</c> sit outside
/// Spark: its antiforgery gate is scoped to paths it knows about, and an action cannot be authorized
/// against a <c>security.json</c> right. Nothing at runtime distinguishes such an endpoint from one
/// mounted through <c>spark.UseControllers()</c>, which is precisely why the rule is a compile-time
/// diagnostic — the mirror image of SPARK004, where the ordering being checked is invisible at
/// runtime instead.
/// <para>
/// As with SPARK004, the pipeline types are stubbed <em>in source</em> under their real names. The
/// negative cases depend on that: "a project that does not reference Spark" is expressed by omitting
/// the Spark stub, and "someone else's MapControllers" by declaring one in another type.
/// </para>
/// </summary>
public class MapControllersAnalyzerTests
{
    private const string AnalyzerName = "MapControllersAnalyzer";

    /// <summary>ASP.NET Core's routing surface, under the exact names the analyzer matches on.</summary>
    private const string AspNetStubs = """
        namespace Microsoft.AspNetCore.Routing
        {
            public interface IEndpointRouteBuilder { }
            public interface IEndpointConventionBuilder { }
        }

        namespace Microsoft.AspNetCore.Builder
        {
            using Microsoft.AspNetCore.Routing;

            public interface IApplicationBuilder { }

            public static class ControllerEndpointRouteBuilderExtensions
            {
                public static IEndpointConventionBuilder MapControllers(this IEndpointRouteBuilder endpoints) => null!;
            }

            public static class AuthorizationEndpointConventionBuilderExtensions
            {
                public static IEndpointConventionBuilder RequireAuthorization(this IEndpointConventionBuilder builder) => builder;
            }

            public static class EndpointRoutingApplicationBuilderExtensions
            {
                public static IApplicationBuilder UseEndpoints(
                    this IApplicationBuilder app, System.Action<IEndpointRouteBuilder> configure) => app;
            }
        }
        """;

    /// <summary>What makes a compilation "a Spark project" as far as the analyzer is concerned.</summary>
    private const string SparkStub = """
        namespace MintPlayer.Spark
        {
            public static class SparkExtensions { }
        }
        """;

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunAsync(params string[] sources)
        => GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [AspNetStubs, .. sources]);

    [Fact]
    public async Task A_bare_MapControllers_is_flagged()
    {
        var source = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;

            namespace TestApp;

            public static class Startup
            {
                public static void Map(IEndpointRouteBuilder endpoints) => endpoints.MapControllers();
            }
            """;

        var diagnostics = await RunAsync(SparkStub, source);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Id.Should().Be("SPARK010");
        diagnostics[0].GetMessage().Should().Contain("spark.UseControllers()");
    }

    [Fact]
    public async Task MapControllers_in_a_UseEndpoints_lambda_is_flagged()
    {
        // The classic-hosting shape. The call sits inside a lambda rather than at statement level,
        // which is where a syntax-shape-based check would quietly stop working.
        var source = """
            using Microsoft.AspNetCore.Builder;

            namespace TestApp;

            public static class Startup
            {
                public static void Configure(IApplicationBuilder app)
                    => app.UseEndpoints(endpoints => endpoints.MapControllers());
            }
            """;

        var diagnostics = await RunAsync(SparkStub, source);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Id.Should().Be("SPARK010");
    }

    [Fact]
    public async Task A_chained_MapControllers_RequireAuthorization_is_flagged()
    {
        // An app that added authorization to its controllers is the most likely one to believe it is
        // already covered — so this shape matters more than the bare one, not less.
        var source = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;

            namespace TestApp;

            public static class Startup
            {
                public static void Map(IEndpointRouteBuilder endpoints)
                    => endpoints.MapControllers().RequireAuthorization();
            }
            """;

        var diagnostics = await RunAsync(SparkStub, source);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Id.Should().Be("SPARK010");
    }

    [Fact]
    public async Task A_project_not_referencing_Spark_is_not_flagged()
    {
        var source = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;

            namespace TestApp;

            public static class Startup
            {
                public static void Map(IEndpointRouteBuilder endpoints) => endpoints.MapControllers();
            }
            """;

        var diagnostics = await RunAsync(source);

        diagnostics.Should().BeEmpty("a plain ASP.NET Core project has no Spark rules to bypass");
    }

    [Fact]
    public async Task Sparks_own_controllers_module_is_not_flagged()
    {
        // spark.UseControllers() calls MapControllers() itself. The exemption is keyed on the module
        // type being DECLARED in this compilation, so an app that merely references the package
        // cannot inherit it.
        var module = """
            namespace MintPlayer.Spark.Controllers
            {
                using Microsoft.AspNetCore.Builder;
                using Microsoft.AspNetCore.Routing;

                public static class SparkControllersExtensions
                {
                    public static void Mount(IEndpointRouteBuilder endpoints) => endpoints.MapControllers();
                }
            }
            """;

        var diagnostics = await RunAsync(SparkStub, module);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unrelated_MapControllers_extension_is_left_alone()
    {
        var source = """
            using Microsoft.AspNetCore.Routing;

            namespace TestApp;

            public static class MyRoutes
            {
                public static void MapControllers(this IEndpointRouteBuilder endpoints) { }

                public static void Map(IEndpointRouteBuilder endpoints) => endpoints.MapControllers();
            }
            """;

        var diagnostics = await RunAsync(SparkStub, source);

        diagnostics.Should().BeEmpty("the symbol resolves to the app's own method, not the framework's");
    }
}
