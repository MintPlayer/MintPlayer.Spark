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
}
