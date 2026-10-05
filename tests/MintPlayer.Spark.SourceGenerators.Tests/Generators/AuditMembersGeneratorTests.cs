using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.History;
using MintPlayer.Spark.Moderation;
using MintPlayer.Spark.SoftDelete;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using GeneratorRunResult = MintPlayer.Spark.SourceGenerators.Tests._Infrastructure.GeneratorRunResult;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// The audit-members generator (#271): a <c>partial</c> type implementing <c>IAuditCreated</c>,
/// <c>IAuditModified</c> (so <c>IAuditable</c>), <c>ISoftDeletable</c> or <c>IModeratable</c> gets the
/// members it does not declare.
/// </summary>
/// <remarks>
/// Every test asserts on generator <em>and</em> compiler errors together: a generated half that does not
/// compile (a wrong header, a duplicate member) shows up only in the second set.
/// </remarks>
public class AuditMembersGeneratorTests
{
    private const string GeneratorName = "AuditMembersGenerator";
    private const string GeneratorAssembly = "MintPlayer.Spark.LibraryGenerators";

    private static readonly Type[] WithoutSparkUser =
        [typeof(ReferenceAttribute), typeof(IAuditable), typeof(ISoftDeletable), typeof(IModeratable)];

    private static readonly Type[] WithSparkUser = [.. WithoutSparkUser, typeof(SparkUser)];

    private static GeneratorRunResult Run(string source, bool sparkUser = true)
        => GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: sparkUser ? WithSparkUser : WithoutSparkUser,
            rootNamespace: "TestLib",
            generatorAssemblyName: GeneratorAssembly);

    private static IEnumerable<Diagnostic> Errors(GeneratorRunResult result)
        => result.GeneratorDiagnostics
            .Concat(result.FinalCompilationDiagnostics)
            .Where(d => d.Severity == DiagnosticSeverity.Error);

    private static void ShouldHaveNoErrors(GeneratorRunResult result)
        => Errors(result).Should().BeEmpty();

    private static string Combined(GeneratorRunResult result)
        => string.Join("\n", result.GeneratedSources.Select(s => s.Source));

    private const string SparkUserReference =
        "[global::MintPlayer.Spark.Abstractions.ReferenceAttribute(typeof(global::MintPlayer.Spark.Authorization.Identity.SparkUser))]";

    [Fact]
    public void IAuditable_gets_all_four_members_read_only()
    {
        var result = Run("""
            using MintPlayer.Spark.History;

            namespace TestLib;

            public partial class Note : IAuditable
            {
                public string? Id { get; set; }
            }
            """);

        ShouldHaveNoErrors(result);
        var combined = Combined(result);
        combined.Should().Contain("partial class Note");
        combined.Should().Contain("public string? CreatedBy { get; set; }");
        combined.Should().Contain("public global::System.DateTimeOffset? CreatedAt { get; set; }");
        combined.Should().Contain("public string? ModifiedBy { get; set; }");
        combined.Should().Contain("public global::System.DateTimeOffset? ModifiedAt { get; set; }");
        // One [ReadOnly(true)] per emitted member: the synchronizer creates them read-only.
        CountOf(combined, "[global::System.ComponentModel.ReadOnly(true)]").Should().Be(4);
    }

    [Fact]
    public void User_id_members_are_references_to_SparkUser_when_it_resolves()
    {
        var result = Run("""
            using MintPlayer.Spark.History;
            using MintPlayer.Spark.Moderation;
            using MintPlayer.Spark.SoftDelete;

            namespace TestLib;

            public partial class Post : IAuditable, ISoftDeletable, IModeratable { }
            """);

        ShouldHaveNoErrors(result);
        var combined = Combined(result);
        // CreatedBy, ModifiedBy, DeletedBy, AuthorId — and no other member.
        CountOf(combined, SparkUserReference).Should().Be(4);
        result.GeneratorDiagnostics.Should().NotContain(d => d.Id == "SPARK040");
    }

    [Fact]
    public void Without_SparkUser_user_ids_are_plain_strings_and_SPARK040_says_why()
    {
        var result = Run("""
            using MintPlayer.Spark.History;

            namespace TestLib;

            public partial class Note : IAuditable { }
            """, sparkUser: false);

        ShouldHaveNoErrors(result);
        Combined(result).Should().NotContain("ReferenceAttribute");
        Combined(result).Should().Contain("public string? CreatedBy { get; set; }");

        var info = result.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "SPARK040").Which;
        info.Severity.Should().Be(DiagnosticSeverity.Info);
        info.GetMessage().Should().Contain("CreatedBy, ModifiedBy");
    }

    [Fact]
    public void A_half_gets_only_its_own_pair()
    {
        var result = Run("""
            using MintPlayer.Spark.History;

            namespace TestLib;

            public partial class LogEntry : IAuditCreated { }
            """);

        ShouldHaveNoErrors(result);
        var combined = Combined(result);
        combined.Should().Contain("CreatedBy");
        combined.Should().Contain("CreatedAt");
        combined.Should().NotContain("ModifiedBy");
        combined.Should().NotContain("ModifiedAt");
    }

    [Fact]
    public void Soft_delete_and_moderation_members_are_generated()
    {
        var result = Run("""
            using MintPlayer.Spark.Moderation;
            using MintPlayer.Spark.SoftDelete;

            namespace TestLib;

            public partial class Post : ISoftDeletable, IModeratable { }
            """);

        ShouldHaveNoErrors(result);
        var combined = Combined(result);
        combined.Should().Contain("public bool IsDeleted { get; set; }");
        combined.Should().Contain("public global::System.DateTimeOffset? DeletedAt { get; set; }");
        combined.Should().Contain("public string? DeletedBy { get; set; }");
        combined.Should().Contain("public string? DeleteReason { get; set; }");
        combined.Should().Contain("public string? AuthorId { get; set; }");
        combined.Should().Contain("public global::System.DateTimeOffset? PostedAt { get; set; }");
    }

    /// <summary>A member the author wrote — to add an attribute, a doc comment — is theirs; the rest is generated.</summary>
    [Fact]
    public void A_declared_member_is_left_alone()
    {
        var result = Run("""
            using MintPlayer.Spark.History;

            namespace TestLib;

            public partial class Note : IAuditable
            {
                public string? CreatedBy { get; set; }
            }
            """);

        ShouldHaveNoErrors(result);
        var combined = Combined(result);
        combined.Should().NotContain("CreatedBy");
        combined.Should().Contain("CreatedAt");
        combined.Should().Contain("ModifiedBy");
    }

    /// <summary>QnA's contribution type implements IModeratable explicitly as read-only views: nothing to generate.</summary>
    [Fact]
    public void An_explicit_implementation_counts_as_declared()
    {
        var result = Run("""
            using System;
            using MintPlayer.Spark.Moderation;

            namespace TestLib;

            public partial class Version : IModeratable
            {
                string? IModeratable.AuthorId { get => null; set { } }
                DateTimeOffset? IModeratable.PostedAt { get => null; set { } }
            }
            """);

        ShouldHaveNoErrors(result);
        result.GeneratedSources.Should().BeEmpty();
    }

    /// <summary>A fully hand-written entity (QnA before #271) gets no output and no diagnostic, partial or not.</summary>
    [Fact]
    public void A_complete_non_partial_type_is_left_alone()
    {
        var result = Run("""
            using System;
            using MintPlayer.Spark.History;

            namespace TestLib;

            public class Note : IAuditable
            {
                public string? CreatedBy { get; set; }
                public DateTimeOffset? CreatedAt { get; set; }
                public string? ModifiedBy { get; set; }
                public DateTimeOffset? ModifiedAt { get; set; }
            }
            """);

        ShouldHaveNoErrors(result);
        result.GeneratedSources.Should().BeEmpty();
        result.GeneratorDiagnostics.Should().BeEmpty();
    }

    /// <summary>Re-emitting what a base type declares would hide it (CS0108) and split the stored value.</summary>
    [Fact]
    public void A_contract_the_base_type_implements_is_not_generated_again()
    {
        var result = Run("""
            using MintPlayer.Spark.History;

            namespace TestLib;

            public partial class Entry : IAuditable { }

            public partial class SpecialEntry : Entry, IAuditable { }
            """);

        ShouldHaveNoErrors(result);
        var combined = Combined(result);
        combined.Should().Contain("partial class Entry");
        combined.Should().NotContain("partial class SpecialEntry");
        result.FinalCompilationDiagnostics.Should().NotContain(d => d.Id == "CS0108");
    }

    [Fact]
    public void A_type_that_is_not_partial_is_reported_as_SPARK038()
    {
        var result = Run("""
            using MintPlayer.Spark.History;

            namespace TestLib;

            public class Note : IAuditable { }
            """);

        var error = result.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "SPARK038").Which;
        error.Severity.Should().Be(DiagnosticSeverity.Error);
        error.GetMessage().Should().Contain("CreatedBy, CreatedAt, ModifiedBy, ModifiedAt");
        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void A_containing_type_that_is_not_partial_is_reported_as_SPARK038()
    {
        var result = Run("""
            using MintPlayer.Spark.History;

            namespace TestLib;

            public class Outer
            {
                public partial class Note : IAuditable { }
            }
            """);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK038");
    }

    [Fact]
    public void A_record_is_reopened_as_a_record()
    {
        var result = Run("""
            using MintPlayer.Spark.History;

            namespace TestLib;

            public partial record Note(string Title) : IAuditCreated;
            """);

        ShouldHaveNoErrors(result);
        Combined(result).Should().Contain("partial record Note");
    }

    [Fact]
    public void A_generic_type_is_reopened_with_its_type_parameters()
    {
        var result = Run("""
            using MintPlayer.Spark.History;

            namespace TestLib;

            public partial class Envelope<TPayload> : IAuditCreated where TPayload : class
            {
                public TPayload? Payload { get; set; }
            }
            """);

        ShouldHaveNoErrors(result);
        Combined(result).Should().Contain("partial class Envelope<TPayload>");
    }

    [Fact]
    public void A_nested_type_is_reopened_inside_its_containing_type()
    {
        var result = Run("""
            using MintPlayer.Spark.History;

            namespace TestLib;

            public partial class Outer
            {
                public partial class Note : IAuditCreated { }
            }
            """);

        ShouldHaveNoErrors(result);
        Combined(result).Should().Contain("partial class Outer");
        Combined(result).Should().Contain("partial class Note");
    }

    /// <summary>Matched by the framework interface's full name: a look-alike interface of the app's own is not one.</summary>
    [Fact]
    public void A_type_with_no_framework_contract_gets_nothing()
    {
        var result = Run("""
            namespace TestLib;

            public interface IMine { string? CreatedBy { get; set; } }

            public partial class Note : IMine
            {
                public string? CreatedBy { get; set; }
            }
            """);

        ShouldHaveNoErrors(result);
        result.GeneratedSources.Should().BeEmpty();
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
