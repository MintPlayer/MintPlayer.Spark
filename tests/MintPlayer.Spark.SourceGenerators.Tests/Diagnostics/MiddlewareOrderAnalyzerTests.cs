using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// #265 — SPARK004. <c>UseSpark</c> adds middleware that reads endpoint metadata (the rate limiter's
/// <c>[EnableRateLimiting]</c> / <c>[DisableRateLimiting]</c>, and <c>UseAuthorization</c>'s
/// <c>[Authorize]</c>). Called before <c>UseRouting</c>, no endpoint is selected and all of it is
/// silently ignored — nothing throws and nothing is logged, which is why the check exists at all.
/// <para>
/// It is a compile-time diagnostic rather than a runtime guard because ordering is a property of the
/// code, following ASP.NET Core's own <c>ASP0001</c>. A runtime version was written and removed; see
/// <c>docs/issue_265_PRD.md</c> D7.
/// </para>
/// <para>
/// The pipeline types are stubbed <em>in source</em> under their real names rather than referenced.
/// The harness supplies BCL references only, and stubs make the symbol-resolution paths — including
/// the negative case where an unrelated <c>UseSpark</c> must be left alone — expressible without
/// dragging the ASP.NET shared framework into the compilation.
/// </para>
/// </summary>
public class MiddlewareOrderAnalyzerTests
{
    private const string AnalyzerName = "MiddlewareOrderAnalyzer";

    /// <summary>
    /// Minimal stand-ins for the real pipeline surface, under the exact names the analyzer matches on:
    /// <c>MintPlayer.Spark.SparkExtensions</c> and <c>Microsoft.AspNetCore.Builder</c>.
    /// </summary>
    private const string Stubs = """
        namespace Microsoft.AspNetCore.Builder
        {
            public interface IApplicationBuilder { }

            public static class EndpointRoutingApplicationBuilderExtensions
            {
                public static IApplicationBuilder UseRouting(this IApplicationBuilder app) => app;
            }

            public static class AuthAppBuilderExtensions
            {
                public static IApplicationBuilder UseAuthorization(this IApplicationBuilder app) => app;
            }
        }

        namespace MintPlayer.Spark
        {
            using Microsoft.AspNetCore.Builder;

            public static class SparkExtensions
            {
                public static IApplicationBuilder UseSpark(this IApplicationBuilder app) => app;
            }
        }

        namespace MintPlayer.Spark.AllFeatures
        {
            using Microsoft.AspNetCore.Builder;

            public static class SparkFullExtensions
            {
                public static IApplicationBuilder UseSparkFull(this IApplicationBuilder app) => app;
            }
        }
        """;

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunAsync(string source)
        => GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [Stubs, source]);

    [Fact]
    public async Task UseSpark_before_UseRouting_raises_SPARK004()
    {
        var source = """
            using Microsoft.AspNetCore.Builder;
            using MintPlayer.Spark;

            namespace TestApp;

            public static class Startup
            {
                public static void Configure(IApplicationBuilder app)
                {
                    app.UseSpark();
                    app.UseRouting();
                }
            }
            """;

        var diagnostics = await RunAsync(source);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Id.Should().Be("SPARK004");
        diagnostics[0].GetMessage().Should().Contain("UseSpark");
    }

    [Fact]
    public async Task UseRouting_before_UseSpark_is_clean()
    {
        var source = """
            using Microsoft.AspNetCore.Builder;
            using MintPlayer.Spark;

            namespace TestApp;

            public static class Startup
            {
                public static void Configure(IApplicationBuilder app)
                {
                    app.UseRouting();
                    app.UseSpark();
                }
            }
            """;

        var diagnostics = await RunAsync(source);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task No_UseRouting_in_the_body_is_clean()
    {
        // The minimal-hosting shape: WebApplication inserts routing at the front of the pipeline
        // itself, so an app that never calls UseRouting is correctly ordered. Also covers routing
        // configured in a helper this analyzer cannot see — silence beats guessing either way.
        var source = """
            using Microsoft.AspNetCore.Builder;
            using MintPlayer.Spark;

            namespace TestApp;

            public static class Startup
            {
                public static void Configure(IApplicationBuilder app)
                {
                    app.UseSpark();
                }
            }
            """;

        var diagnostics = await RunAsync(source);

        diagnostics.Should().BeEmpty("minimal hosting routes ahead of UseSpark without an explicit call");
    }

    [Fact]
    public async Task A_chained_call_is_ordered_left_to_right()
    {
        // Every invocation node in `app.UseSpark().UseRouting()` starts at `app`, so invocation spans
        // cannot order them. The analyzer compares the method NAME tokens, which run left to right —
        // the same order the runtime composes the middleware in.
        var source = """
            using Microsoft.AspNetCore.Builder;
            using MintPlayer.Spark;

            namespace TestApp;

            public static class Startup
            {
                public static void Configure(IApplicationBuilder app)
                    => app.UseAuthorization().UseSpark().UseRouting();
            }
            """;

        var diagnostics = await RunAsync(source);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Id.Should().Be("SPARK004");
    }

    [Fact]
    public async Task A_correctly_ordered_chain_is_clean()
    {
        var source = """
            using Microsoft.AspNetCore.Builder;
            using MintPlayer.Spark;

            namespace TestApp;

            public static class Startup
            {
                public static void Configure(IApplicationBuilder app)
                    => app.UseRouting().UseSpark();
            }
            """;

        var diagnostics = await RunAsync(source);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task UseSparkFull_is_covered_too()
    {
        var source = """
            using Microsoft.AspNetCore.Builder;
            using MintPlayer.Spark.AllFeatures;

            namespace TestApp;

            public static class Startup
            {
                public static void Configure(IApplicationBuilder app)
                {
                    app.UseSparkFull();
                    app.UseRouting();
                }
            }
            """;

        var diagnostics = await RunAsync(source);

        diagnostics.Should().ContainSingle();
        diagnostics[0].GetMessage().Should().Contain("UseSparkFull");
    }

    [Fact]
    public async Task An_unrelated_UseSpark_extension_is_left_alone()
    {
        // Someone else's method that happens to share the name. The symbol resolves to a type that is
        // not Spark's, so it is rejected rather than reported — this is the case that makes name
        // matching alone insufficient.
        var source = """
            using Microsoft.AspNetCore.Builder;

            namespace TestApp;

            public static class MyOwnExtensions
            {
                public static IApplicationBuilder UseSpark(this IApplicationBuilder app) => app;
            }

            public static class Startup
            {
                public static void Configure(IApplicationBuilder app)
                {
                    MyOwnExtensions.UseSpark(app);
                    app.UseRouting();
                }
            }
            """;

        var diagnostics = await RunAsync(source);

        diagnostics.Should().BeEmpty("only Spark's own UseSpark carries this requirement");
    }

    [Fact]
    public async Task Top_level_statements_are_analyzed()
    {
        // A minimal-hosting Program.cs has no enclosing method — the scope is the compilation unit.
        // Ordering still applies once UseRouting is called explicitly.
        var source = """
            using Microsoft.AspNetCore.Builder;
            using MintPlayer.Spark;

            IApplicationBuilder app = null!;
            app.UseSpark();
            app.UseRouting();
            """;

        var diagnostics = await RunAsync(source);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Id.Should().Be("SPARK004");
    }

    [Fact]
    public async Task Ordering_inside_a_Configure_lambda_is_analyzed()
    {
        // webBuilder.Configure(app => ...) puts a whole pipeline inside one lambda, which is why a
        // lambda is treated as its own scope rather than folded into the enclosing method.
        var source = """
            using System;
            using Microsoft.AspNetCore.Builder;
            using MintPlayer.Spark;

            namespace TestApp;

            public static class Startup
            {
                public static void Build(Action<IApplicationBuilder> configure)
                    => configure(app =>
                    {
                        app.UseSpark();
                        app.UseRouting();
                    });
            }
            """;

        var diagnostics = await RunAsync(source);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Id.Should().Be("SPARK004");
    }
}
