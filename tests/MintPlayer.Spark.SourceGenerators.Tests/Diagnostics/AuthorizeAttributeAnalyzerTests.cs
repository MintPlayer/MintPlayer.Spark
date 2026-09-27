using Microsoft.AspNetCore.Authorization;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK020: <c>[Authorize]</c> with a policy or roles — a policy Spark never registers throws at
/// request time, and roles cannot see Spark's <c>group</c> claims.
/// </summary>
public class AuthorizeAttributeAnalyzerTests
{
    private const string AnalyzerName = "AuthorizeAttributeAnalyzer";

    /// <summary>The real attribute types, so the analyzer sees exactly what an app's compiler does.</summary>
    private static readonly Type[] SparkRefs =
        [typeof(AuthorizeAttribute), typeof(IAuthorizeData), typeof(SparkAuthorizeAttribute)];

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunAsync(string source, Type[]? refs = null)
        => GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], refs ?? SparkRefs);

    private static string Controller(string attributes) => $$"""
        using Microsoft.AspNetCore.Authorization;
        using MintPlayer.Spark.Services;

        namespace TestApp;

        public class Upload { }

        {{attributes}}
        public class UploadsController
        {
            public void Create() { }
        }
        """;

    private static string Action(string attributes) => $$"""
        using Microsoft.AspNetCore.Authorization;
        using MintPlayer.Spark.Services;

        namespace TestApp;

        public class Upload { }

        public class UploadsController
        {
            {{attributes}}
            public void Create() { }
        }
        """;

    [Theory]
    [InlineData("[Authorize(\"Administrators\")]", "Policy")]
    [InlineData("[Authorize(policy: \"Administrators\")]", "Policy")]
    [InlineData("[Authorize(Policy = \"Administrators\")]", "Policy")]
    [InlineData("[Authorize(Roles = \"Administrators\")]", "Roles")]
    [InlineData("[Authorize(AuthenticationSchemes = \"Bearer\", Roles = \"Administrators\")]", "Roles")]
    [InlineData("[Authorize(\"Administrators\", Roles = \"Administrators\")]", "Policy and Roles")]
    [InlineData("[AuthorizeAttribute(Roles = \"Administrators\")]", "Roles")]
    public async Task Policy_or_roles_on_a_class_is_flagged(string attribute, string named)
    {
        var diagnostics = await RunAsync(Controller(attribute));

        diagnostics.Where(d => d.Id == "SPARK020").Should().HaveCount(1,
            "a policy Spark never registers throws at request time, and roles cannot see group claims");
        diagnostics.Single(d => d.Id == "SPARK020").GetMessage().Should().StartWith($"[Authorize] with {named} ",
            "the message must name what was written so the developer knows which argument to drop");
    }

    [Theory]
    [InlineData("[Authorize(\"Administrators\")]")]
    [InlineData("[Authorize(Policy = \"Administrators\")]")]
    [InlineData("[Authorize(Roles = \"Administrators\")]")]
    [InlineData("[Authorize(AuthenticationSchemes = \"Bearer\", Roles = \"Administrators\")]")]
    public async Task Policy_or_roles_on_a_method_is_flagged(string attribute)
    {
        var diagnostics = await RunAsync(Action(attribute));

        diagnostics.Where(d => d.Id == "SPARK020").Should().HaveCount(1);
    }

    [Fact]
    public async Task Policy_or_roles_on_a_minimal_api_lambda_is_flagged()
    {
        var diagnostics = await RunAsync("""
            using System;
            using Microsoft.AspNetCore.Authorization;
            using MintPlayer.Spark.Services;

            namespace TestApp;

            public static class Endpoints
            {
                public static Delegate Handler = [Authorize(Roles = "Administrators")] () => "ok";
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK020").Should().HaveCount(1,
            "an attribute on a route handler lambda becomes endpoint metadata exactly like one on an action");
    }

    [Fact]
    public async Task The_message_recommends_SparkAuthorize()
    {
        var diagnostics = await RunAsync(Controller("[Authorize(Roles = \"Administrators\")]"));

        diagnostics.Single(d => d.Id == "SPARK020").GetMessage().Should().Contain("[SparkAuthorize(\"Action\", nameof(Target))]",
            "the message is all a developer gets at `dotnet build`, so it has to carry the replacement");
    }

    [Theory]
    [InlineData("[Authorize]")]
    [InlineData("[Authorize()]")]
    [InlineData("[Authorize(AuthenticationSchemes = \"Bearer\")]")]
    [InlineData("[SparkAuthorize(\"New\", nameof(Upload))]")]
    [InlineData("[SparkAuthorize]")]
    [InlineData("[AllowAnonymous]")]
    public async Task Sign_in_only_schemes_only_and_Spark_attributes_are_not_flagged(string attribute)
    {
        var onClass = await RunAsync(Controller(attribute));
        var onMethod = await RunAsync(Action(attribute));

        onClass.Where(d => d.Id == "SPARK020").Should().BeEmpty(
            "none of these name a policy or roles; SparkAuthorize sets its policy itself and is the recommended form");
        onMethod.Where(d => d.Id == "SPARK020").Should().BeEmpty();
    }

    /// <summary>
    /// A subclass is its author's decision — <c>SparkAuthorizeAttribute</c> is exactly such a subclass —
    /// so only the exact ASP.NET Core type is judged.
    /// </summary>
    [Fact]
    public async Task A_user_defined_subclass_of_AuthorizeAttribute_is_not_flagged()
    {
        var diagnostics = await RunAsync("""
            using Microsoft.AspNetCore.Authorization;

            namespace TestApp;

            public class AdminOnlyAttribute : AuthorizeAttribute
            {
                public AdminOnlyAttribute() : base("Administrators") { }
            }

            [AdminOnly(Roles = "Administrators")]
            public class UploadsController { }
            """);

        diagnostics.Where(d => d.Id == "SPARK020").Should().BeEmpty();
    }

    [Fact]
    public async Task A_compilation_without_the_ASP_NET_authorization_reference_is_not_flagged()
    {
        var diagnostics = await RunAsync("""
            using System;

            namespace TestApp;

            public class AuthorizeAttribute : Attribute
            {
                public string? Roles { get; set; }
            }

            [Authorize(Roles = "Administrators")]
            public class UploadsController { }
            """, refs: []);

        diagnostics.Where(d => d.Id == "SPARK020").Should().BeEmpty(
            "an unrelated attribute that happens to be called Authorize is not ASP.NET Core's");
    }

    /// <summary>
    /// The premise — Spark owns the policies and the group claims — does not hold where Spark is not
    /// referenced, and the recommended replacement would not compile there.
    /// </summary>
    [Fact]
    public async Task A_compilation_that_cannot_see_Spark_is_not_flagged()
    {
        var diagnostics = await RunAsync("""
            using Microsoft.AspNetCore.Authorization;

            namespace TestApp;

            [Authorize(Roles = "Administrators")]
            public class UploadsController { }
            """, refs: [typeof(AuthorizeAttribute), typeof(IAuthorizeData)]);

        diagnostics.Where(d => d.Id == "SPARK020").Should().BeEmpty();
    }

    // ---- The endpoint-convention form: .RequireAuthorization(…) ----

    /// <summary>
    /// The real minimal-API surface: <c>MapGet</c> / <c>MapGroup</c> (Routing), the convention builders
    /// (Http.Abstractions), <c>RequireAuthorization</c> (Authorization.Policy), the policy builder
    /// (Authorization) and Spark's attribute.
    /// </summary>
    private static readonly Type[] EndpointRefs =
    [
        typeof(AuthorizeAttribute),
        typeof(IAuthorizeData),
        typeof(AuthorizationPolicyBuilder),
        typeof(SparkAuthorizeAttribute),
        typeof(Microsoft.AspNetCore.Builder.AuthorizationEndpointConventionBuilderExtensions),
        typeof(Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions),
        typeof(Microsoft.AspNetCore.Builder.IEndpointConventionBuilder),
        typeof(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder),
        typeof(Microsoft.AspNetCore.Routing.RouteGroupBuilder),
        typeof(Microsoft.AspNetCore.Http.RequestDelegate),
        typeof(Microsoft.AspNetCore.Http.RequestDelegateFactoryOptions),
        typeof(Microsoft.AspNetCore.Routing.Patterns.RoutePattern),
        typeof(Microsoft.AspNetCore.Routing.RouteData),
    ];

    private static string OnMapGet(string call) => Endpoints($$"""app.MapGet("/uploads", () => "ok"){{call}};""");

    private static string OnGroup(string call) => Endpoints($$"""
        var group = app.MapGroup("/api"){{call}};
                group.MapGet("/uploads", () => "ok");
        """);

    private static string Endpoints(string body) => $$"""
        using System;
        using Microsoft.AspNetCore.Authorization;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Routing;
        using MintPlayer.Spark.Services;

        namespace TestApp;

        public class Upload { }

        public static class Endpoints
        {
            public const string AdminPolicy = "Administrators";

            public static string[] Policies() => ["Administrators"];

            public static void Map(IEndpointRouteBuilder app)
            {
                {{body}}
            }
        }
        """;

    /// <summary>
    /// A fixture that does not compile makes every "not flagged" case pass for the wrong reason — an
    /// unbound call has no method to judge — so each endpoint source is compiled first.
    /// </summary>
    private static async Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunEndpointAsync(string source)
    {
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "TestInput",
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source)],
            GeneratorHarness.BuildReferences(EndpointRefs),
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        compilation.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .Should().BeEmpty("the fixture must bind to the real ASP.NET Core methods for the result to mean anything");

        return await GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], EndpointRefs);
    }

    [Theory]
    [InlineData(".RequireAuthorization(\"Administrators\")", "RequireAuthorization with a policy name ")]
    [InlineData(".RequireAuthorization(AdminPolicy)", "RequireAuthorization with a policy name ")]
    [InlineData(".RequireAuthorization(\"Administrators\", \"Operators\")", "RequireAuthorization with a policy name ")]
    [InlineData(".RequireAuthorization(new[] { \"Administrators\" })", "RequireAuthorization with a policy name ")]
    [InlineData(".RequireAuthorization(Policies())", "RequireAuthorization with a policy name ")]
    [InlineData(".RequireAuthorization(new AuthorizeAttribute(\"Administrators\"))", "RequireAuthorization(new AuthorizeAttribute(…)) with Policy ")]
    [InlineData(".RequireAuthorization(new AuthorizeAttribute { Policy = \"Administrators\" })", "RequireAuthorization(new AuthorizeAttribute(…)) with Policy ")]
    [InlineData(".RequireAuthorization(new AuthorizeAttribute { Roles = \"Administrators\" })", "RequireAuthorization(new AuthorizeAttribute(…)) with Roles ")]
    [InlineData(".RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = \"Bearer\", Roles = \"Administrators\" })", "RequireAuthorization(new AuthorizeAttribute(…)) with Roles ")]
    [InlineData(".RequireAuthorization(new AuthorizeAttribute(\"Administrators\") { Roles = \"Administrators\" })", "RequireAuthorization(new AuthorizeAttribute(…)) with Policy and Roles ")]
    [InlineData(".RequireAuthorization(new SparkAuthorizeAttribute(\"New\", nameof(Upload)), new AuthorizeAttribute { Roles = \"Administrators\" })", "RequireAuthorization(new AuthorizeAttribute(…)) with Roles ")]
    public async Task RequireAuthorization_with_a_policy_name_or_roles_is_flagged(string call, string messageStart)
    {
        foreach (var source in new[] { OnMapGet(call), OnGroup(call) })
        {
            var diagnostics = await RunEndpointAsync(source);

            diagnostics.Where(d => d.Id == "SPARK020").Should().HaveCount(1,
                "a policy name is looked up in a registry Spark leaves empty, and roles cannot see group claims — on a route and on a group alike");
            var message = diagnostics.Single(d => d.Id == "SPARK020").GetMessage();
            message.Should().StartWith(messageStart, "the message must name what was written");
            message.Should().Contain(".RequireAuthorization(new SparkAuthorizeAttribute(\"Action\", nameof(Target)))",
                "the replacement is spelled in the form the developer wrote");
        }
    }

    [Theory]
    [InlineData(".RequireAuthorization()")]
    [InlineData(".RequireAuthorization(new string[0])")]
    [InlineData(".RequireAuthorization(new SparkAuthorizeAttribute(\"New\", nameof(Upload)))")]
    [InlineData(".RequireAuthorization(new SparkAuthorizeAttribute { Group = \"Administrators\" })")]
    [InlineData(".RequireAuthorization(new AuthorizeAttribute())")]
    [InlineData(".RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = \"Bearer\" })")]
    [InlineData(".RequireAuthorization(policy => policy.RequireAuthenticatedUser())")]
    [InlineData(".RequireAuthorization(policy => policy.RequireRole(\"Administrators\"))")]
    [InlineData(".RequireAuthorization(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())")]
    [InlineData(".AllowAnonymous()")]
    public async Task RequireAuthorization_sign_in_only_Spark_and_explicit_policy_objects_are_not_flagged(string call)
    {
        foreach (var source in new[] { OnMapGet(call), OnGroup(call) })
        {
            var diagnostics = await RunEndpointAsync(source);

            diagnostics.Where(d => d.Id == "SPARK020").Should().BeEmpty(
                "none of these looks a policy up by name or carries Roles; a policy object is an explicit decision");
        }
    }

    [Fact]
    public async Task The_diagnostic_is_on_the_RequireAuthorization_name_not_the_whole_chain()
    {
        var source = OnMapGet(".RequireAuthorization(\"Administrators\")");
        var diagnostics = await RunEndpointAsync(source);

        var span = diagnostics.Single(d => d.Id == "SPARK020").Location.SourceSpan;
        source.Substring(span.Start, span.Length).Should().Be("RequireAuthorization");
    }

    [Fact]
    public async Task The_offending_AuthorizeAttribute_argument_is_what_gets_underlined()
    {
        var source = OnMapGet(".RequireAuthorization(new SparkAuthorizeAttribute(\"New\", nameof(Upload)), new AuthorizeAttribute { Roles = \"Administrators\" })");
        var diagnostics = await RunEndpointAsync(source);

        var span = diagnostics.Single(d => d.Id == "SPARK020").Location.SourceSpan;
        source.Substring(span.Start, span.Length).Should().Be("new AuthorizeAttribute { Roles = \"Administrators\" }");
    }

    /// <summary>
    /// Matched on the containing type, not the name: a project's own <c>RequireAuthorization</c>
    /// (here found first, because extension lookup starts in the innermost namespace) is its own.
    /// </summary>
    [Fact]
    public async Task A_user_defined_RequireAuthorization_extension_is_not_flagged()
    {
        var diagnostics = await RunEndpointAsync("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;

            namespace TestApp;

            public static class MyAuthorizationExtensions
            {
                public static TBuilder RequireAuthorization<TBuilder>(this TBuilder builder, params string[] policyNames)
                    where TBuilder : IEndpointConventionBuilder => builder;
            }

            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet("/uploads", () => "ok").RequireAuthorization("Administrators");
                    app.MapGroup("/api").RequireAuthorization("Administrators");
                }
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK020").Should().BeEmpty(
            "a method that merely shares the name is not ASP.NET Core's policy lookup");
    }

    [Fact]
    public async Task RequireAuthorization_in_a_compilation_that_cannot_see_Spark_is_not_flagged()
    {
        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(AnalyzerName, ["""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;

            namespace TestApp;

            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                    => app.MapGet("/uploads", () => "ok").RequireAuthorization("Administrators");
            }
            """], EndpointRefs.Where(t => t != typeof(SparkAuthorizeAttribute)).ToArray());

        diagnostics.Where(d => d.Id == "SPARK020").Should().BeEmpty();
    }
}
