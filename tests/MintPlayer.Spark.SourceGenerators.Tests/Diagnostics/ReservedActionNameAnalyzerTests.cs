using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK023 (#460 contributions, Q2): a custom action named like a reserved action verb — read from
/// the compilation's and its references' <c>[assembly: SparkReservedActions(typeof(X))]</c>.
/// </summary>
public class ReservedActionNameAnalyzerTests
{
    private const string AnalyzerName = "ReservedActionNameAnalyzer";

    /// <summary>A package that is not Spark itself, declaring one verb and one non-verb constant.</summary>
    internal const string StubPackageSource = """
        [assembly: MintPlayer.Spark.Abstractions.Authorization.SparkReservedActions(typeof(Acme.Package.AcmeRights))]

        namespace Acme.Package
        {
            public static class AcmeRights
            {
                public const string Frobnicate = "Frobnicate";

                [MintPlayer.Spark.Abstractions.Authorization.SparkNotAnAction]
                public const string Target = "AcmeTarget";

                internal const string Hidden = "Hidden";
                public static readonly string NotConst = "NotConst";
            }
        }
        """;

    private static string ActionClass(string className) => $$"""
        using System.Threading;
        using System.Threading.Tasks;
        using MintPlayer.Spark.Abstractions.Actions;

        public sealed class {{className}} : ICustomAction
        {
            public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
        """;

    private static readonly Type[] SparkReferences = [typeof(ICustomAction), typeof(SparkReservedActionsAttribute)];

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunAsync(string source, bool withPackage = false)
        => GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName,
            [source],
            referenceTypes: SparkReferences,
            additionalReferences: withPackage
                ? [GeneratorHarness.CompileToMetadataReference("Acme.Spark.Package", [StubPackageSource], [typeof(SparkReservedActionsAttribute)])]
                : null);

    [Theory]
    [InlineData("EditAction", "Edit")]
    [InlineData("Delete", "Delete")]
    [InlineData("QueryReadAction", "QueryRead")]
    public async Task A_custom_action_named_like_a_core_verb_is_an_error(string className, string verb)
    {
        var diagnostics = await RunAsync(ActionClass(className));

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK023");
        diagnostic.Severity.Should().Be(Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        diagnostic.GetMessage().Should().Contain($"'{verb}'");
        diagnostic.GetMessage().Should().Contain("MintPlayer.Spark.Abstractions");
    }

    [Theory]
    [InlineData("editAction")]
    [InlineData("EDITAction")]
    [InlineData("replicate")]
    public async Task The_comparison_is_case_insensitive_like_rights(string className)
    {
        (await RunAsync(ActionClass(className))).Should().ContainSingle().Which.Id.Should().Be("SPARK023");
    }

    [Fact]
    public async Task A_verb_declared_by_a_referenced_assembly_is_reserved()
    {
        var diagnostics = await RunAsync(ActionClass("FrobnicateAction"), withPackage: true);

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK023");
        diagnostic.GetMessage().Should().Contain("Acme.Package.AcmeRights");
        diagnostic.GetMessage().Should().Contain("Acme.Spark.Package");
    }

    [Theory]
    [InlineData("FrobnicateAction")]  // the package is not referenced here
    [InlineData("CarCopyAction")]
    [InlineData("EditorAction")]      // "Editor", not "Edit": whole-name comparison, never a prefix
    [InlineData("NewDeleteAttachmentAction")]
    public async Task A_name_that_is_not_a_reserved_verb_is_not_reported(string className)
    {
        (await RunAsync(ActionClass(className))).Should().BeEmpty();
    }

    [Theory]
    [InlineData("AcmeTargetAction")] // [SparkNotAnAction]
    [InlineData("HiddenAction")]     // not public
    [InlineData("NotConstAction")]   // not a constant
    public async Task Only_public_constants_not_marked_as_non_actions_are_verbs(string className)
    {
        (await RunAsync(ActionClass(className), withPackage: true)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_class_that_is_not_a_custom_action_is_not_reported()
    {
        (await RunAsync("public sealed class EditAction { }")).Should().BeEmpty();
    }

    [Fact]
    public async Task An_abstract_custom_action_is_not_reported()
    {
        const string Source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Abstractions.Actions;

            public abstract class EditAction : ICustomAction
            {
                public abstract Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default);
            }
            """;

        (await RunAsync(Source)).Should().BeEmpty();
    }
}
