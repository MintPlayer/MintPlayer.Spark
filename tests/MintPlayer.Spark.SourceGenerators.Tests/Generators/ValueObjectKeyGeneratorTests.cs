using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using GeneratorRunResult = MintPlayer.Spark.SourceGenerators.Tests._Infrastructure.GeneratorRunResult;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// Tests for the value-object key generator, which gives every <c>[ValueObject]</c> type a stable
/// row key and registers the set with the runtime.
/// </summary>
/// <remarks>
/// Lives in the third generator assembly, loaded via <c>generatorAssemblyName</c> the same way
/// <see cref="SparkFullGeneratorTests"/> loads the AllFeatures one.
/// </remarks>
public class ValueObjectKeyGeneratorTests
{
    private const string GeneratorName = "ValueObjectKeyGenerator";
    private const string GeneratorAssembly = "MintPlayer.Spark.LibraryGenerators";

    /// <summary>
    /// ⚠️ Asserts on the <em>union</em> of both diagnostic sets, and that is the point of the helper.
    /// A generator emitting uncompilable C# reports no diagnostics of its own, so a test that checks
    /// only <c>GeneratorDiagnostics</c> passes green while every consumer's build breaks.
    /// </summary>
    private static GeneratorRunResult Run(params string[] sources)
        => GeneratorHarness.Run(
            GeneratorName,
            sources,
            referenceTypes: [typeof(ValueObjectAttribute), typeof(SparkValueObjects)],
            rootNamespace: "TestApp",
            generatorAssemblyName: GeneratorAssembly);

    private static void ShouldHaveNoErrors(GeneratorRunResult result)
        => result.GeneratorDiagnostics
            .Concat(result.FinalCompilationDiagnostics)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

    private static string Combined(GeneratorRunResult result)
        => string.Join("\n", result.GeneratedSources.Select(s => s.Source));

    [Fact]
    public void Decorated_partial_type_gets_a_key_and_a_registration()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            [ValueObject]
            public partial class PhoneNumber
            {
                public string Number { get; set; } = string.Empty;
            }
            """);

        ShouldHaveNoErrors(result);

        var combined = Combined(result);
        combined.Should().Contain("partial class PhoneNumber");
        combined.Should().Contain("public string Id { get; set; } = global::System.Guid.NewGuid().ToString(\"N\");");

        // The generated key is marked too, so a type reads the same whether its key was generated or
        // written by hand -- and anything reasoning about keys has exactly one thing to look for.
        combined.Should().Contain("[global::MintPlayer.Spark.Abstractions.ValueKey]");
        combined.Should().Contain("Register(typeof(global::TestApp.PhoneNumber)");
    }

    /// <summary>
    /// The key is minted by a field initializer rather than a constructor, because the framework
    /// builds these through <c>Activator.CreateInstance</c> on every save.
    /// </summary>
    [Fact]
    public void Generated_key_is_initialized_at_the_declaration()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            [ValueObject]
            public partial class Address
            {
                public string Street { get; set; } = string.Empty;
            }
            """);

        ShouldHaveNoErrors(result);
        Combined(result).Should().Contain("= global::System.Guid.NewGuid()");
    }

    /// <summary>
    /// The case <c>[ValueKey]</c> exists for. Registered, so the save-time diff can judge its
    /// collection — but nothing emitted, because the type already has a key that carries meaning.
    /// </summary>
    [Fact]
    public void ValueKey_type_is_registered_but_nothing_is_emitted_for_it()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            [ValueObject]
            public partial class ProjectColumn
            {
                [ValueKey]
                public string Id { get; set; } = string.Empty;
                public string Name { get; set; } = string.Empty;
            }
            """);

        ShouldHaveNoErrors(result);

        var combined = Combined(result);
        combined.Should().Contain("Register(typeof(global::TestApp.ProjectColumn)");
        combined.Should().NotContain("Guid.NewGuid()");
        combined.Should().NotContain("partial class ProjectColumn");
    }

    /// <summary>
    /// A key need not be called <c>Id</c>, and the registered accessor must read whichever property
    /// was marked rather than assuming the generated name.
    /// </summary>
    [Fact]
    public void ValueKey_on_a_differently_named_property_is_the_one_registered()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            [ValueObject]
            public partial class BuildSession
            {
                [ValueKey]
                public string SessionId { get; set; } = string.Empty;
            }
            """);

        ShouldHaveNoErrors(result);
        Combined(result).Should().Contain(").SessionId");
    }

    /// <summary>
    /// Reported, never skipped. A value object absent from the registry is not neutral — under the
    /// fail-closed diff its collection becomes unjudgeable, and the symptom surfaces far from here.
    /// </summary>
    [Fact]
    public void Decorated_type_that_is_not_partial_reports_SPARK016()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            [ValueObject]
            public class PhoneNumber
            {
                public string Number { get; set; } = string.Empty;
            }
            """);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK016");
    }

    /// <summary>
    /// <c>partial</c> buys nothing for a type that already has a key, so it is not demanded.
    /// </summary>
    [Fact]
    public void Non_partial_type_with_a_ValueKey_is_accepted()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            [ValueObject]
            public class EventColumnMapping
            {
                [ValueKey]
                public string Id { get; set; } = string.Empty;
            }
            """);

        result.GeneratorDiagnostics.Should().NotContain(d => d.Id == "SPARK016");
        Combined(result).Should().Contain("Register(typeof(global::TestApp.EventColumnMapping)");
    }

    /// <summary>
    /// The regression that motivated the whole marker design. These four shapes are what the
    /// abandoned reachability walk keyed by mistake — including a type that is one row per line of
    /// source code. Undecorated means untouched, whatever the shape.
    /// </summary>
    [Fact]
    public void Undecorated_collection_elements_are_left_alone()
    {
        var result = Run("""
            namespace TestApp;

            public class LineCoverage
            {
                public int Line { get; set; }
            }

            public class TreeFileSummary
            {
                public string Path { get; set; } = string.Empty;
            }

            public class Report
            {
                public System.Collections.Generic.List<LineCoverage> Lines { get; set; } = [];
                public System.Collections.Generic.List<TreeFileSummary> Files { get; set; } = [];
            }
            """);

        ShouldHaveNoErrors(result);
        result.GeneratedSources.Should().BeEmpty();
    }

    /// <summary>
    /// Containing types come from the symbol, never the file path — namespace does not follow folder
    /// in most of these apps, and several value objects share a source file.
    /// </summary>
    [Fact]
    public void Nested_type_is_emitted_inside_its_containing_type()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Deeply.Nested;

            public partial class Outer
            {
                [ValueObject]
                public partial class Inner
                {
                    public string Value { get; set; } = string.Empty;
                }
            }
            """);

        ShouldHaveNoErrors(result);

        var combined = Combined(result);
        combined.Should().Contain("namespace TestApp.Deeply.Nested");
        combined.Should().Contain("partial class Outer");
        combined.Should().Contain("partial class Inner");
        combined.Should().Contain("Register(typeof(global::TestApp.Deeply.Nested.Outer.Inner)");
    }

    /// <summary>
    /// The registration is a module initializer, which is what closes the cross-assembly gap: the
    /// runtime sees the union of every loaded assembly's set without either side knowing the other.
    /// </summary>
    [Fact]
    public void Registration_runs_as_a_module_initializer_in_the_root_namespace()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            [ValueObject]
            public partial class Address
            {
                public string Street { get; set; } = string.Empty;
            }
            """);

        ShouldHaveNoErrors(result);

        var combined = Combined(result);
        combined.Should().Contain("namespace TestApp");
        combined.Should().Contain("[global::System.Runtime.CompilerServices.ModuleInitializer]");
    }

    [Fact]
    public void Nothing_is_emitted_when_no_type_is_decorated()
    {
        var result = Run("""
            namespace TestApp;

            public class Foo { }
            """);

        result.GeneratedSources.Should().BeEmpty();
    }
}
