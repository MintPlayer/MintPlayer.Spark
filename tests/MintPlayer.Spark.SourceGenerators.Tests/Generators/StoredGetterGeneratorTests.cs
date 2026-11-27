using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using GeneratorRunResult = MintPlayer.Spark.SourceGenerators.Tests._Infrastructure.GeneratorRunResult;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// The generator that gives a stored type's get-only members a private backing field, so the SQL providers can store
/// them as RavenDB does (#536 O13), and records each getter's formula hash.
/// </summary>
public class StoredGetterGeneratorTests
{
    // The harness compiles without a nullable context; entities are written with one.
    private static GeneratorRunResult Run(params string[] sources)
        => GeneratorHarness.Run(
            "StoredGetterGenerator",
            [.. sources.Select(static s => "#nullable enable\n" + s)],
            referenceTypes: [typeof(Id<>)],
            rootNamespace: "TestApp",
            generatorAssemblyName: "MintPlayer.Spark.LibraryGenerators");

    private static IEnumerable<Diagnostic> Problems(GeneratorRunResult result)
        => result.GeneratorDiagnostics
            .Concat(result.FinalCompilationDiagnostics)
            .Where(d => d.Severity >= DiagnosticSeverity.Warning);

    private static string Combined(GeneratorRunResult result)
        => string.Join("\n", result.GeneratedSources.Select(s => s.Source));

    private static string FormulaOf(GeneratorRunResult result, string property)
    {
        var marker = $"SparkStoredGetter(\"{property}\", \"";
        var combined = Combined(result);
        var start = combined.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"the generated source names {property}");
        start += marker.Length;
        return combined.Substring(start, combined.IndexOf('"', start) - start);
    }

    private const string Person = """
        using MintPlayer.Spark.Abstractions;

        namespace TestApp;

        public partial class Person
        {
            public Id<Person> Id { get; set; }
            public string FirstName { get; set; } = "";
            public string LastName { get; set; } = "";
            public string FullName => $"{FirstName} {LastName}";
            public int? Age { get { return FirstName.Length; } }
            public string? Nickname { get; set; }
        }
        """;

    [Fact]
    public void A_get_only_member_of_an_entity_gets_a_private_backing_field_and_its_formula()
    {
        var result = Run(Person);

        Problems(result).Should().BeEmpty();
        var combined = Combined(result);
        combined.Should().Contain("partial class Person");
        combined.Should().Contain($"private string {SparkStoredGetterAttribute.FieldPrefix}FullName;");
        combined.Should().Contain($"private int? {SparkStoredGetterAttribute.FieldPrefix}Age;");
        combined.Should().NotContain("__stored_FirstName").And.NotContain("__stored_Nickname").And.NotContain("__stored_Id");
        FormulaOf(result, "FullName").Should().MatchRegex("^[0-9a-f]{64}$");
        FormulaOf(result, "Age").Should().NotBe(FormulaOf(result, "FullName"));
    }

    [Fact]
    public void The_formula_ignores_formatting_and_comments_but_not_the_body()
    {
        var original = FormulaOf(Run(Person), "FullName");
        var reformatted = FormulaOf(Run(Person.Replace(
            "public string FullName => $\"{FirstName} {LastName}\";",
            "public string FullName\n        // the display name\n        =>   $\"{FirstName} {LastName}\" ;")), "FullName");
        var changed = FormulaOf(Run(Person.Replace(
            "public string FullName => $\"{FirstName} {LastName}\";",
            "public string FullName => $\"{LastName}, {FirstName}\";")), "FullName");

        reformatted.Should().Be(original);
        changed.Should().NotBe(original);
    }

    [Fact]
    public void A_type_deriving_from_an_entity_and_a_value_object_are_stored_too()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            public abstract partial class Subject
            {
                public Id<Subject> Id { get; set; }
            }

            public partial class Artist : Subject
            {
                public string Name { get; set; } = "";
                public string Text => Name.ToUpperInvariant();
            }

            [ValueObject]
            public partial class Scope
            {
                public string Name { get; set; } = "";
                public string? Audience => Name.Length > 0 ? Name : null;
            }
            """);

        Problems(result).Should().BeEmpty();
        var combined = Combined(result);
        combined.Should().Contain("private string __stored_Text;");
        combined.Should().Contain("private string __stored_Audience;");
    }

    [Fact]
    public void A_setter_a_static_a_JsonIgnore_or_a_type_Spark_does_not_store_gets_nothing()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            public partial class Person
            {
                public Id<Person> Id { get; set; }
                public string Name { get; set; } = "";
                public string Init { get; init; } = "";
                public static string Static => "x";
                [System.Text.Json.Serialization.JsonIgnore] public string Ignored => Name;
                internal string Internal => Name;
                public string this[int i] => Name;
            }

            public partial class NotStored
            {
                public string Name { get; set; } = "";
                public string Upper => Name.ToUpperInvariant();
            }
            """);

        Problems(result).Should().BeEmpty();
        Combined(result).Should().NotContain("__stored_");
    }

    [Fact]
    public void A_non_partial_derived_type_is_reported()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            public abstract partial class Subject
            {
                public Id<Subject> Id { get; set; }
            }

            public class Artist : Subject
            {
                public string Name { get; set; } = "";
                public string Text => Name.ToUpperInvariant();
            }
            """);

        result.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "SPARK067")
            .Which.GetMessage().Should().Contain("Artist.Text").And.Contain("partial");
        Combined(result).Should().NotContain("__stored_");
    }

    [Fact]
    public void A_getter_that_reads_the_clock_or_another_entity_is_reported()
    {
        var result = Run("""
            using System;
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            public partial class Company
            {
                public Id<Company> Id { get; set; }
                public string Name { get; set; } = "";
            }

            public partial class Person
            {
                public Id<Person> Id { get; set; }
                public DateTime Born { get; set; }
                public Company? Employer { get; set; }
                public int Age => (int)((DateTime.UtcNow - Born).TotalDays / 365.25);
                public string EmployerName => Employer?.Name ?? "";
                public bool Expired(TimeProvider clock) => clock.GetUtcNow() > Born;
                public string Plain => Born.ToString("O");
            }
            """);

        var reported = result.GeneratorDiagnostics.Where(d => d.Id == "SPARK068").Select(d => d.GetMessage()).ToList();
        reported.Should().HaveCount(2);
        reported.Should().Contain(m => m.Contains("Person.Age") && m.Contains("DateTime.UtcNow"));
        reported.Should().Contain(m => m.Contains("Person.EmployerName") && m.Contains("Company"));
        Combined(result).Should().Contain("__stored_Age").And.Contain("__stored_EmployerName").And.Contain("__stored_Plain");
    }

    [Theory]
    [InlineData("DateTimeOffset.Now.Ticks", "DateTimeOffset.Now")]
    [InlineData("Environment.TickCount64", "Environment.TickCount64")]
    [InlineData("Stopwatch.GetTimestamp()", "Stopwatch.GetTimestamp")]
    [InlineData("Clock.GetUtcNow().Ticks", "TimeProvider.GetUtcNow")]
    public void Every_clock_is_reported(string getter, string clock)
    {
        var result = Run($$"""
            using System;
            using System.Diagnostics;
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            public partial class Ticket
            {
                private static readonly TimeProvider Clock = TimeProvider.System;
                public Id<Ticket> Id { get; set; }
                public long Stamp => {{getter}};
            }
            """);

        result.FinalCompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        result.GeneratorDiagnostics.Where(d => d.Id == "SPARK068").Select(d => d.GetMessage()).Should()
            .ContainSingle().Which.Should().Contain("Ticket.Stamp").And.Contain(clock);
    }

    [Fact]
    public void A_getter_that_reads_its_own_type_is_not_reported()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            public partial class Person
            {
                public Id<Person> Id { get; set; }
                public string Name { get; set; } = "";
            }

            public partial class Employee : Person
            {
                public string Label => this.Name + ((Person)this).Name;
            }
            """);

        result.GeneratorDiagnostics.Where(d => d.Id == "SPARK068").Should().BeEmpty("the type itself and its base are not another entity");
        Combined(result).Should().Contain("__stored_Label");
    }

    [Fact]
    public void A_record_is_reported_because_its_equality_would_include_the_field()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            [ValueObject]
            public partial record Scope
            {
                public string Name { get; set; } = "";
                public string Upper => Name.ToUpperInvariant();
            }
            """);

        result.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "SPARK067")
            .Which.GetMessage().Should().Contain("Scope.Upper").And.Contain("record");
        Combined(result).Should().NotContain("__stored_");
    }
}
