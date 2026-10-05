using Microsoft.CodeAnalysis;
using MintPlayer.Spark.History;
using MintPlayer.Spark.Moderation;
using MintPlayer.Spark.SoftDelete;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK039 (#271, D8): an application whose entities implement a framework-stamped interface, but which
/// does not reference the package that stamps it. Since the members are generated, nothing in the entity
/// reminds the author that the runtime half is a separate package.
/// </summary>
/// <remarks>
/// The fixture compilation references only the <c>.Abstractions</c> assemblies, which is exactly the
/// "runtime package missing" shape; the negative cases add the runtime assembly by type.
/// </remarks>
public class StampedContractRuntimeAnalyzerTests
{
    private const string AnalyzerName = "StampedContractRuntimeAnalyzer";

    private static readonly Type[] Abstractions = [typeof(IAuditable), typeof(ISoftDeletable), typeof(IModeratable)];

    private static Task<IReadOnlyList<Diagnostic>> Analyze(string source, OutputKind kind = OutputKind.ConsoleApplication, params Type[] extra)
        => GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], [.. Abstractions, .. extra], outputKind: kind);

    private const string AuditedApp = """
        using System;
        using MintPlayer.Spark.History;

        namespace Acme;

        public class Note : IAuditable
        {
            public string? CreatedBy { get; set; }
            public DateTimeOffset? CreatedAt { get; set; }
            public string? ModifiedBy { get; set; }
            public DateTimeOffset? ModifiedAt { get; set; }
        }

        public static class Program { public static void Main() { } }
        """;

    [Fact]
    public async Task An_audited_entity_without_History_is_reported()
    {
        var diagnostics = await Analyze(AuditedApp);

        var warning = diagnostics.Should().ContainSingle(d => d.Id == "SPARK039").Which;
        warning.Severity.Should().Be(DiagnosticSeverity.Warning);
        warning.GetMessage().Should().Contain("Acme.Note").And.Contain("MintPlayer.Spark.History").And.Contain("AddHistory");
    }

    [Fact]
    public async Task An_audited_entity_with_History_referenced_is_not_reported()
    {
        var diagnostics = await Analyze(AuditedApp, OutputKind.ConsoleApplication, typeof(MintPlayer.Spark.History.SparkHistoryExtensions));

        diagnostics.Should().NotContain(d => d.Id == "SPARK039");
    }

    /// <summary>A library is expected to reference the abstractions only; the application registers the package.</summary>
    [Fact]
    public async Task A_library_is_not_checked()
    {
        var diagnostics = await Analyze(AuditedApp, OutputKind.DynamicallyLinkedLibrary);

        diagnostics.Should().NotContain(d => d.Id == "SPARK039");
    }

    [Fact]
    public async Task A_soft_deletable_entity_without_SoftDelete_names_that_package()
    {
        var diagnostics = await Analyze("""
            using System;
            using MintPlayer.Spark.SoftDelete;

            namespace Acme;

            public class Post : ISoftDeletable
            {
                public bool IsDeleted { get; set; }
                public DateTimeOffset? DeletedAt { get; set; }
                public string? DeletedBy { get; set; }
                public string? DeleteReason { get; set; }
            }

            public static class Program { public static void Main() { } }
            """);

        diagnostics.Should().ContainSingle(d => d.Id == "SPARK039")
            .Which.GetMessage().Should().Contain("MintPlayer.Spark.SoftDelete").And.Contain("AddSoftDelete");
    }

    [Fact]
    public async Task An_app_with_no_stamped_entity_is_not_reported()
    {
        var diagnostics = await Analyze("""
            namespace Acme;

            public class Note { public string? Title { get; set; } }

            public static class Program { public static void Main() { } }
            """);

        diagnostics.Should().BeEmpty();
    }
}
