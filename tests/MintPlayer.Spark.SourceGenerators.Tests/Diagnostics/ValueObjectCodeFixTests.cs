using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// The code fixes for SPARK016 and SPARK017, both of which live in
/// <c>MintPlayer.Spark.LibraryGenerators</c> alongside the generator that raises SPARK016.
/// </summary>
/// <remarks>
/// These fixes are an <b>IDE affordance and nothing else</b>. Under <c>dotnet build</c> a referenced
/// project arrives as a metadata .dll, SPARK017's offender has no source location, and no fix can be
/// offered — the error severity remains the enforcement. What these tests pin is the loaded-solution
/// case: a <c>CompilationReference</c>, where the library's source locations survive.
/// <para>
/// The two-project fixtures are the point. A single-project fixture would pass while proving nothing
/// about the arrangement the fix actually meets in practice — the type in an entity library, the
/// diagnostic raised from the application's compilation.
/// </para>
/// </remarks>
public class ValueObjectCodeFixTests
{
    private const string LibraryGeneratorsAssembly = "MintPlayer.Spark.LibraryGenerators";

    private static IEnumerable<Type> Refs =>
    [
        typeof(SparkContext),
        typeof(ValueObjectAttribute),
        typeof(ReferenceAttribute),
        typeof(Raven.Client.Documents.Linq.IRavenQueryable<>),
    ];

    /// <summary>The application half: a context whose one query roots a document with a collection.</summary>
    private const string AppSource = """
        using System.Collections.Generic;
        using MintPlayer.Spark;
        using Raven.Client.Documents.Linq;
        using TestLib;

        namespace TestApp;

        public class AppContext : SparkContext
        {
            public IRavenQueryable<Person> People { get; set; } = null!;
        }
        """;

    private const string PersonSource = """
        using System.Collections.Generic;

        namespace TestLib;

        public class Person
        {
            public string? Id { get; set; }
            public List<PhoneNumber> Phones { get; set; } = new();
        }
        """;

    private static Task<CodeFixResult> RunCompletenessFixAsync(params FixtureProject[] projects)
        => CodeFixHarness.RunAnalyzerFixAsync(
            analyzerTypeName: "ValueObjectCompletenessAnalyzer",
            codeFixTypeName: "ValueObjectCompletenessCodeFixProvider",
            diagnosticId: "SPARK017",
            projects: projects,
            referenceTypes: Refs,
            codeFixAssemblyName: LibraryGeneratorsAssembly);

    private static Task<CodeFixResult> RunPartialFixAsync(params FixtureProject[] projects)
        => CodeFixHarness.RunGeneratorFixAsync(
            generatorTypeName: "ValueObjectKeyGenerator",
            codeFixTypeName: "ValueObjectPartialCodeFixProvider",
            diagnosticId: "SPARK016",
            projects: projects,
            referenceTypes: Refs,
            generatorAssemblyName: LibraryGeneratorsAssembly,
            codeFixAssemblyName: LibraryGeneratorsAssembly);

    // ---------------------------------------------------------------- SPARK016

    [Fact]
    public async Task SPARK016_adds_the_partial_keyword()
    {
        var result = await RunPartialFixAsync(
            FixtureProject.Of("TestLib", ("PhoneNumber.cs", """
                using MintPlayer.Spark.Abstractions;

                namespace TestLib;

                [ValueObject]
                public class PhoneNumber
                {
                    public string? Number { get; set; }
                }
                """)));

        result.Document("PhoneNumber.cs").Should().Contain("public partial class PhoneNumber");
    }

    /// <summary>
    /// ⚠️ The modifier must land adjacent to the type keyword. <c>partial public class</c> is
    /// CS0267, so a fix that prepended instead of appended would produce code that does not compile
    /// — and the assertion above alone would not catch it.
    /// </summary>
    [Fact]
    public async Task SPARK016_puts_partial_last_so_the_result_compiles()
    {
        var result = await RunPartialFixAsync(
            FixtureProject.Of("TestLib", ("PhoneNumber.cs", """
                using MintPlayer.Spark.Abstractions;

                namespace TestLib;

                [ValueObject]
                internal sealed class PhoneNumber
                {
                    public string? Number { get; set; }
                }
                """)));

        result.Document("PhoneNumber.cs").Should().Contain("internal sealed partial class PhoneNumber");
    }

    // ---------------------------------------------------------------- SPARK017

    /// <summary>
    /// The headline: one invocation, one edit, all three changes. Adding only the attribute would
    /// clear SPARK017 and raise SPARK016 on the next build.
    /// </summary>
    [Fact]
    public async Task SPARK017_adds_the_attribute_the_partial_keyword_and_the_using_together()
    {
        var result = await RunCompletenessFixAsync(
            FixtureProject.Of("TestLib",
                ("Person.cs", PersonSource),
                ("PhoneNumber.cs", """
                    namespace TestLib;

                    public class PhoneNumber
                    {
                        public string? Number { get; set; }
                    }
                    """)),
            FixtureProject.Of("TestApp", ("AppContext.cs", AppSource)));

        var fixedSource = result.Document("PhoneNumber.cs");

        fixedSource.Should().Contain("using MintPlayer.Spark.Abstractions;");
        fixedSource.Should().Contain("[ValueObject]");
        fixedSource.Should().Contain("public partial class PhoneNumber");
    }

    /// <summary>
    /// The cross-project case, stated explicitly: the offending type is in the library, the
    /// diagnostic comes from the application's compilation, and the edit lands in the library's file
    /// while the application's file is untouched.
    /// </summary>
    [Fact]
    public async Task SPARK017_fixes_a_type_in_another_project()
    {
        var result = await RunCompletenessFixAsync(
            FixtureProject.Of("TestLib",
                ("Person.cs", PersonSource),
                ("PhoneNumber.cs", """
                    namespace TestLib;

                    public class PhoneNumber
                    {
                        public string? Number { get; set; }
                    }
                    """)),
            FixtureProject.Of("TestApp", ("AppContext.cs", AppSource)));

        result.Diagnostics.Should().ContainSingle();
        result.Document("PhoneNumber.cs").Should().Contain("[ValueObject]");
        result.Document("AppContext.cs").Should().Be(AppSource.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// A type that is already <c>partial</c> gets the attribute only — no <c>partial partial</c>.
    /// </summary>
    [Fact]
    public async Task SPARK017_does_not_duplicate_an_existing_partial()
    {
        var result = await RunCompletenessFixAsync(
            FixtureProject.Of("TestLib",
                ("Person.cs", PersonSource),
                ("PhoneNumber.cs", """
                    namespace TestLib;

                    public partial class PhoneNumber
                    {
                        public string? Number { get; set; }
                    }
                    """)),
            FixtureProject.Of("TestApp", ("AppContext.cs", AppSource)));

        var fixedSource = result.Document("PhoneNumber.cs");

        fixedSource.Should().Contain("public partial class PhoneNumber");
        fixedSource.Should().NotContain("partial partial");
    }

    /// <summary>
    /// A file that already imports the namespace does not get a second using.
    /// </summary>
    [Fact]
    public async Task SPARK017_does_not_duplicate_an_existing_using()
    {
        var result = await RunCompletenessFixAsync(
            FixtureProject.Of("TestLib",
                ("Person.cs", PersonSource),
                ("PhoneNumber.cs", """
                    using MintPlayer.Spark.Abstractions;

                    namespace TestLib;

                    public class PhoneNumber
                    {
                        public string? Number { get; set; }
                    }
                    """)),
            FixtureProject.Of("TestApp", ("AppContext.cs", AppSource)));

        var occurrences = result.Document("PhoneNumber.cs")
            .Split("using MintPlayer.Spark.Abstractions;").Length - 1;

        occurrences.Should().Be(1);
    }

    /// <summary>
    /// The declaration keeps its doc comment and its indentation: the attribute takes over the
    /// leading trivia and the type keeps only its indent.
    /// </summary>
    [Fact]
    public async Task SPARK017_preserves_the_doc_comment_above_the_type()
    {
        var result = await RunCompletenessFixAsync(
            FixtureProject.Of("TestLib",
                ("Person.cs", PersonSource),
                ("PhoneNumber.cs", """
                    namespace TestLib;

                    /// <summary>A phone number.</summary>
                    public class PhoneNumber
                    {
                        public string? Number { get; set; }
                    }
                    """)),
            FixtureProject.Of("TestApp", ("AppContext.cs", AppSource)));

        var fixedSource = result.Document("PhoneNumber.cs");

        fixedSource.Should().Contain("/// <summary>A phone number.</summary>");
        fixedSource.Should().Contain("[ValueObject]");
        // The doc comment stays above the attribute, not orphaned below it.
        fixedSource.IndexOf("<summary>", StringComparison.Ordinal)
            .Should().BeLessThan(fixedSource.IndexOf("[ValueObject]", StringComparison.Ordinal));
    }

    /// <summary>
    /// ⚠️ The boundary the whole feature stops at. When the entity library arrives as a metadata
    /// reference — a NuGet-packaged library, or any <c>dotnet build</c> — no document declares the
    /// offending type, so no edit is possible. The diagnostic must still fire, and it must now land
    /// on the <c>SparkContext</c> property that reaches the type: the one location in this
    /// compilation that can carry it.
    /// </summary>
    /// <remarks>
    /// This also pins an improvement over the previous behaviour, where the same case produced a
    /// bare <c>CSC : error SPARK017</c> with no file, line or column at all.
    /// </remarks>
    [Fact]
    public async Task SPARK017_reports_on_the_context_property_for_a_metadata_type()
    {
        var libraryReference = GeneratorHarness.CompileToMetadataReference(
            "TestLib",
            [PersonSource, """
                namespace TestLib;

                public class PhoneNumber
                {
                    public string? Number { get; set; }
                }
                """],
            Refs);

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(
            "ValueObjectCompletenessAnalyzer",
            [AppSource],
            Refs,
            additionalReferences: [libraryReference]);

        var reported = diagnostics.Should().ContainSingle().Which;
        reported.Id.Should().Be("SPARK017");

        // Reported inside the application's own file — the library has no syntax tree here.
        var span = reported.Location.SourceSpan;
        var appText = (await reported.Location.SourceTree!.GetTextAsync()).ToString();
        appText.Substring(span.Start, span.Length).Should().Contain("People");

        // And the fix still knows which type it would have to change, had a document existed.
        reported.Properties["SparkOffendingType"].Should().Be("TestLib.PhoneNumber");
    }
}
