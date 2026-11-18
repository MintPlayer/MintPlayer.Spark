using MintPlayer.Spark.Cli;

namespace MintPlayer.Spark.Cli.Tests;

public class ContextEditorTests
{
    private const string Context = """
        using MintPlayer.Spark;
        using SparkApp.Library.Entities;

        namespace SparkApp;

        /// <summary>The application's entity roots.</summary>
        public partial class SparkAppContext : SparkContext
        {
            public IQueryable<Person> People => Session.Query<Person>();
        }

        """;

    [Fact]
    public void AddRoot_adds_the_queryable_after_the_last_one()
    {
        ContextEditor.AddRoot(Context, "Invoice", "Invoices").Should().Be("""
            using MintPlayer.Spark;
            using SparkApp.Library.Entities;

            namespace SparkApp;

            /// <summary>The application's entity roots.</summary>
            public partial class SparkAppContext : SparkContext
            {
                public IQueryable<Person> People => Session.Query<Person>();
                public IQueryable<Invoice> Invoices => Session.Query<Invoice>();
            }

            """);
    }

    [Fact]
    public void AddRoot_fills_a_context_without_roots()
    {
        const string empty = "public class AppContext : MintPlayer.Spark.SparkContext\n{\n}\n";

        ContextEditor.AddRoot(empty, "Invoice", "Invoices")
            .Should().Be("public class AppContext : MintPlayer.Spark.SparkContext\n{\n    public IQueryable<Invoice> Invoices => Session.Query<Invoice>();\n}\n");
    }

    [Fact]
    public void AddRoot_refuses_a_property_name_that_is_taken()
    {
        var act = () => ContextEditor.AddRoot(Context, "Human", "People");

        act.Should().Throw<InvalidOperationException>().WithMessage("*People*");
    }

    [Fact]
    public void AddRoot_refuses_a_file_without_a_SparkContext_class()
    {
        var act = () => ContextEditor.AddRoot("public class Other { }", "Invoice", "Invoices");

        act.Should().Throw<InvalidOperationException>().WithMessage("*SparkContext*");
    }

    [Fact]
    public void IsContext_recognizes_the_class_declaration()
    {
        ContextEditor.IsContext(Context).Should().BeTrue();
        ContextEditor.IsContext("// derives from SparkContext\npublic class X { }").Should().BeFalse();
    }
}
