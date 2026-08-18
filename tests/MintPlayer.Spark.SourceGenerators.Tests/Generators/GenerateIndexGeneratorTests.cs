using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

public class GenerateIndexGeneratorTests
{
    private const string GeneratorName = "GenerateIndexGenerator";

    /// <summary>
    /// The generated code references RavenDB types this test project does not reference, so these tests
    /// assert on emitted text rather than on a clean final compilation. Real-build fidelity is covered
    /// separately by a demo app that must actually compile against the generated output.
    /// </summary>
    private static GeneratorRunResult Run(string source, string rootNamespace = "TestApp")
        => GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(GenerateIndexAttribute)],
            rootNamespace: rootNamespace);

    private const string PlainCar = """
        using MintPlayer.Spark.Abstractions;

        namespace TestApp.Entities;

        [GenerateIndex]
        public class Car
        {
            public string? Id { get; set; }
            public string LicensePlate { get; set; } = string.Empty;
            public int Year { get; set; }
        }
        """;

    [Fact]
    public void Emits_an_index_and_an_index_entity()
    {
        var result = Run(PlainCar);

        result.GeneratedSources.Should().ContainSingle();
        var (hintName, generated) = result.GeneratedSources[0];

        hintName.Should().Be("SparkGeneratedIndexes.g.cs");
        generated.Should().Contain("public partial class VCar");
        generated.Should().Contain("public partial class Cars_Overview : global::Raven.Client.Documents.Indexes.AbstractIndexCreationTask<global::TestApp.Entities.Car>");
    }

    [Fact]
    public void Index_entity_is_linked_to_the_index_by_FromIndex()
    {
        var generated = Run(PlainCar).GeneratedSources[0].Source;

        generated.Should().Contain("[global::MintPlayer.Spark.Abstractions.FromIndexAttribute(typeof(Cars_Overview))]");
    }

    /// <summary>
    /// Both generated types belong to the application project, never to the assembly that declares the
    /// entity — that is what lets an entity library stay lean.
    /// </summary>
    [Fact]
    public void Generated_types_land_in_the_app_namespace_not_the_entity_namespace()
    {
        var generated = Run(PlainCar, rootNamespace: "MyApp").GeneratedSources[0].Source;

        generated.Should().Contain("namespace MyApp.Indexes");
        generated.Should().NotContain("namespace TestApp.Entities");
    }

    [Fact]
    public void Map_projects_every_indexable_property()
    {
        var generated = Run(PlainCar).GeneratedSources[0].Source;

        generated.Should().Contain("Map = cars => from car in cars");
        generated.Should().Contain("select new VCar()");
        generated.Should().Contain("LicensePlate = car.LicensePlate,");
        generated.Should().Contain("Year = car.Year,");
    }

    /// <summary>
    /// Without this a projection-only field returns null through <c>ProjectInto</c> while the index is
    /// provably correct — no error, no index fault. Measured; it is the likeliest way a generated index
    /// appears broken, so it gets its own test.
    /// </summary>
    [Fact]
    public void StoreAllFields_is_always_emitted()
    {
        var generated = Run(PlainCar).GeneratedSources[0].Source;

        generated.Should().Contain("StoreAllFields(global::Raven.Client.Documents.Indexes.FieldStorage.Yes);");
    }

    [Fact]
    public void Id_is_declared_on_the_index_entity_but_never_mapped()
    {
        var generated = Run(PlainCar).GeneratedSources[0].Source;

        generated.Should().Contain("public string? Id { get; set; }");
        generated.Should().NotContain("Id = car.Id");
    }

    [Fact]
    public void Emits_the_OnInitialize_extension_seam()
    {
        var generated = Run(PlainCar).GeneratedSources[0].Source;

        generated.Should().Contain("OnInitialize();");
        generated.Should().Contain("partial void OnInitialize();");
    }

    [Fact]
    public void No_source_without_the_Spark_reference()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            ["namespace TestApp; public class Foo { }"],
            referenceTypes: Array.Empty<Type>(),
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void No_source_for_an_entity_without_the_attribute()
    {
        var result = Run("""
            namespace TestApp.Entities;

            public class Car
            {
                public string LicensePlate { get; set; } = string.Empty;
            }
            """);

        result.GeneratedSources.Should().BeEmpty();
    }

    // --- property selection -------------------------------------------------------------------

    [Fact]
    public void IgnoreProperty_and_IgnoreForIndex_are_both_excluded()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Car
            {
                public string LicensePlate { get; set; } = string.Empty;
                [IgnoreProperty] public string? SyncEtag { get; set; }
                [IgnoreForIndex] public string? Notes { get; set; }
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("LicensePlate");
        generated.Should().NotContain("SyncEtag");
        generated.Should().NotContain("Notes");
    }

    /// <summary>
    /// Discovering only declared members silently drops inherited properties — a documented defect of the
    /// design this replaces, so the hierarchy walk gets an explicit test.
    /// </summary>
    [Fact]
    public void Inherited_properties_are_included()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            public abstract class AuditedEntity
            {
                public string? CreatedBy { get; set; }
            }

            [GenerateIndex]
            public class Car : AuditedEntity
            {
                public string LicensePlate { get; set; } = string.Empty;
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("CreatedBy = car.CreatedBy,");
        generated.Should().Contain("LicensePlate = car.LicensePlate,");
    }

    [Fact]
    public void Indexers_and_write_only_properties_are_excluded()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Car
            {
                public string LicensePlate { get; set; } = string.Empty;
                public string this[int i] { get => string.Empty; set { } }
                public string WriteOnly { set { } }
                private string Hidden { get; set; } = string.Empty;
                public static string Statik { get; set; } = string.Empty;
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("LicensePlate");
        generated.Should().NotContain("WriteOnly");
        generated.Should().NotContain("Hidden");
        generated.Should().NotContain("Statik");
        generated.Should().NotContain("this[");
    }

    /// <summary>
    /// A non-nullable reference type needs <c>= default!</c> or the generated file warns CS8618 in a
    /// nullable-enabled compilation.
    /// </summary>
    [Fact]
    public void Nullability_is_preserved_and_non_nullable_references_get_an_initializer()
    {
        var generated = Run(PlainCar).GeneratedSources[0].Source;

        generated.Should().Contain("public string LicensePlate { get; set; } = default!;");
        generated.Should().Contain("public int Year { get; set; }");
        generated.Should().NotContain("public int Year { get; set; } = default!;");
    }

    // --- naming overrides ---------------------------------------------------------------------

    [Fact]
    public void IndexName_and_IndexEntityName_can_be_overridden()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex(IndexName = "Vehicles_Search", IndexEntityName = "VehicleView")]
            public class Car
            {
                public string LicensePlate { get; set; } = string.Empty;
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("public partial class VehicleView");
        generated.Should().Contain("public partial class Vehicles_Search :");
        generated.Should().Contain("[global::MintPlayer.Spark.Abstractions.FromIndexAttribute(typeof(Vehicles_Search))]");
        generated.Should().NotContain("Cars_Overview");
    }

    [Fact]
    public void Description_is_emitted_on_the_index()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex(Description = "Cars for the overview grid")]
            public class Car
            {
                public string LicensePlate { get; set; } = string.Empty;
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("[global::System.ComponentModel.Description(\"Cars for the overview grid\")]");
    }

    /// <summary>
    /// The index name is the RavenDB index name, and renaming one re-indexes the database — so
    /// pluralization is pinned by tests rather than left to a general-purpose inflector.
    /// </summary>
    [Theory]
    [InlineData("Car", "Cars_Overview")]
    [InlineData("Person", "People_Overview")]
    [InlineData("Company", "Companies_Overview")]
    [InlineData("Address", "Addresses_Overview")]
    [InlineData("Child", "Children_Overview")]
    [InlineData("Day", "Days_Overview")]
    public void Index_names_are_pluralized_predictably(string entityName, string expectedIndexName)
    {
        var generated = Run($$"""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class {{entityName}}
            {
                public string Name { get; set; } = string.Empty;
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain($"public partial class {expectedIndexName} :");
    }

    // --- diagnostics --------------------------------------------------------------------------

    /// <summary>
    /// Every abort path reports a diagnostic. <c>Producer.Produce</c> discards exceptions, so a silent
    /// abort would emit nothing and leave a runtime auto-index in its place — the exact problem
    /// <c>[GenerateIndex]</c> exists to prevent.
    /// </summary>
    [Fact]
    public void Entity_with_no_indexable_properties_warns_and_emits_nothing()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Car
            {
                public string? Id { get; set; }
                [IgnoreProperty] public string? SyncEtag { get; set; }
            }
            """);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_003");
        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Two_entities_generating_the_same_index_name_report_a_duplicate()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex(IndexName = "Shared_Overview")]
            public class Car
            {
                public string Name { get; set; } = string.Empty;
            }

            [GenerateIndex(IndexName = "Shared_Overview")]
            public class Truck
            {
                public string Name { get; set; } = string.Empty;
            }
            """);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_002");
    }

    /// <summary>
    /// The duplicate is dropped rather than emitted twice, which would be a compile error in the
    /// generated file instead of a targeted diagnostic.
    /// </summary>
    [Fact]
    public void A_duplicate_index_name_is_emitted_only_once()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex(IndexName = "Shared_Overview")]
            public class Car
            {
                public string Name { get; set; } = string.Empty;
            }

            [GenerateIndex(IndexName = "Shared_Overview")]
            public class Truck
            {
                public string Name { get; set; } = string.Empty;
            }
            """).GeneratedSources[0].Source;

        CountOccurrences(generated, "public partial class Shared_Overview :").Should().Be(1);
    }

    [Fact]
    public void Multiple_entities_are_emitted_into_one_file()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Car
            {
                public string Name { get; set; } = string.Empty;
            }

            [GenerateIndex]
            public class Truck
            {
                public string Name { get; set; } = string.Empty;
            }
            """);

        result.GeneratedSources.Should().ContainSingle();
        var generated = result.GeneratedSources[0].Source;
        generated.Should().Contain("public partial class Cars_Overview :");
        generated.Should().Contain("public partial class Trucks_Overview :");
        generated.Should().Contain("public partial class VCar");
        generated.Should().Contain("public partial class VTruck");
    }

    // --- [Search] and sort companions ---------------------------------------------------------

    private const string SearchableCar = """
        using MintPlayer.Spark.Abstractions;

        namespace TestApp.Entities;

        [GenerateIndex]
        public class Car
        {
            [Search] public string Model { get; set; } = string.Empty;
            public int Year { get; set; }
        }
        """;

    [Fact]
    public void Search_declares_the_base_field_as_analyzed()
    {
        var generated = Run(SearchableCar).GeneratedSources[0].Source;

        generated.Should().Contain("Index(nameof(VCar.Model), global::Raven.Client.Documents.Indexes.FieldIndexing.Search);");
    }

    [Fact]
    public void Search_emits_a_sort_companion_with_no_separator()
    {
        var generated = Run(SearchableCar).GeneratedSources[0].Source;

        generated.Should().Contain("public string ModelSort { get; set; } = default!;");
        generated.Should().NotContain("Model_Sort");
    }

    /// <summary>
    /// Leaving the companion undeclared is what makes it sortable: it keeps RavenDB's default indexing,
    /// a single lower-cased un-tokenized term. Declaring Exact instead was measured as a regression on both
    /// ordering (case-sensitive ordinal) and equality (a case-mismatched == matches nothing).
    /// </summary>
    [Fact]
    public void The_sort_companion_is_never_declared_with_an_indexing_mode()
    {
        var generated = Run(SearchableCar).GeneratedSources[0].Source;

        generated.Should().NotContain("nameof(VCar.ModelSort)");
        generated.Should().NotContain("FieldIndexing.Exact");
    }

    [Fact]
    public void The_sort_companion_is_hidden_from_the_model()
    {
        var generated = Run(SearchableCar).GeneratedSources[0].Source;

        generated.Should().Contain("[global::MintPlayer.Spark.Abstractions.IgnorePropertyAttribute]");
    }

    /// <summary>
    /// A byte-identical copy of the base field's expression. Normalizing here (lower-casing, trimming)
    /// would make the sort order disagree with the value the user sees.
    /// </summary>
    [Fact]
    public void The_sort_companion_is_fed_the_same_expression_as_the_base_field()
    {
        var generated = Run(SearchableCar).GeneratedSources[0].Source;

        generated.Should().Contain("Model = car.Model,");
        generated.Should().Contain("ModelSort = car.Model,");
    }

    [Fact]
    public void A_property_without_Search_gets_neither_indexing_nor_a_companion()
    {
        var generated = Run(SearchableCar).GeneratedSources[0].Source;

        generated.Should().NotContain("YearSort");
        generated.Should().NotContain("nameof(VCar.Year)");
    }

    [Fact]
    public void Search_is_valid_on_a_collection_of_strings()
    {
        var generated = Run("""
            using System.Collections.Generic;
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Company
            {
                [Search] public string[] HistoricNames { get; set; } = [];
                [Search] public List<string> Clusters { get; set; } = new();
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("HistoricNamesSort");
        generated.Should().Contain("ClustersSort");
        generated.Should().Contain("Index(nameof(VCompany.HistoricNames), global::Raven.Client.Documents.Indexes.FieldIndexing.Search);");
    }

    /// <summary>
    /// <c>[IgnoreProperty]</c> keeps the property out of the index, so the <c>[Search]</c> beside it can
    /// never take effect. The reference implementation indexes such a field and merely hides it from its
    /// model; Spark's <c>[IgnoreProperty]</c> means "as if it did not exist", so the combination is a no-op
    /// — and a reported one, never a silent one.
    /// </summary>
    [Fact]
    public void Search_on_an_IgnoreProperty_property_is_a_reported_no_op()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Car
            {
                public string Name { get; set; } = string.Empty;
                [IgnoreProperty, Search] public string Hidden { get; set; } = string.Empty;
            }
            """);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_006");
        result.GeneratedSources[0].Source.Should().NotContain("Hidden");
    }

    /// <summary>
    /// The reference implementation happily applies FieldIndexing.Search to an object-typed field and gives
    /// it an object-typed sort companion. Both are meaningless, so Spark diagnoses instead.
    /// </summary>
    [Fact]
    public void Search_on_an_unsupported_type_is_an_error()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Car
            {
                public string Name { get; set; } = string.Empty;
                [Search] public int Year { get; set; }
            }
            """);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_005");
    }

    [Fact]
    public void Search_on_an_unsupported_type_does_not_produce_a_companion()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Car
            {
                public string Name { get; set; } = string.Empty;
                [Search] public int Year { get; set; }
            }
            """).GeneratedSources[0].Source;

        generated.Should().NotContain("YearSort");
        generated.Should().NotContain("nameof(VCar.Year)");
    }

    // --- DateTimeOffset ------------------------------------------------------------------------

    private const string DatedCar = """
        using System;
        using MintPlayer.Spark.Abstractions;

        namespace TestApp.Entities;

        [GenerateIndex]
        public class Car
        {
            public DateTimeOffset CreatedOn { get; set; }
            public DateTimeOffset? ArchivedOn { get; set; }
            public DateTime LegacyStamp { get; set; }
            public DateTime? LegacyNullable { get; set; }
            public DateOnly? RegisteredOn { get; set; }
        }
        """;

    [Fact]
    public void DateTimeOffset_is_indexed_Exact_with_no_attribute_needed()
    {
        var generated = Run(DatedCar).GeneratedSources[0].Source;

        generated.Should().Contain("Index(nameof(VCar.CreatedOn), global::Raven.Client.Documents.Indexes.FieldIndexing.Exact);");
        generated.Should().Contain("Index(nameof(VCar.ArchivedOn), global::Raven.Client.Documents.Indexes.FieldIndexing.Exact);");
    }

    [Fact]
    public void DateTimeOffset_gets_a_sort_companion_automatically()
    {
        var generated = Run(DatedCar).GeneratedSources[0].Source;

        generated.Should().Contain("CreatedOnSort = car.CreatedOn,");
        generated.Should().Contain("ArchivedOnSort = car.ArchivedOn,");
        generated.Should().Contain("public global::System.DateTimeOffset CreatedOnSort { get; set; }");
        generated.Should().Contain("public global::System.DateTimeOffset? ArchivedOnSort { get; set; }");
    }

    /// <summary>
    /// The asymmetry is deliberate, not an oversight: in the reference corpus all 15 DateTimeOffset properties
    /// get Exact plus a companion and all 22 DateTime properties get neither. Widening it would silently add
    /// fields to every existing index.
    /// </summary>
    [Theory]
    [InlineData("LegacyStamp")]
    [InlineData("LegacyNullable")]
    [InlineData("RegisteredOn")]
    public void Other_date_types_get_neither_indexing_nor_a_companion(string propertyName)
    {
        var generated = Run(DatedCar).GeneratedSources[0].Source;

        generated.Should().NotContain($"{propertyName}Sort");
        generated.Should().NotContain($"nameof(VCar.{propertyName})");
    }

    /// <summary>
    /// A date companion is still left undeclared — only the base field is Exact. Declaring the companion too
    /// would be the cargo-cult that R8 rules out.
    /// </summary>
    [Fact]
    public void The_date_sort_companion_is_not_itself_declared()
    {
        var generated = Run(DatedCar).GeneratedSources[0].Source;

        generated.Should().NotContain("nameof(VCar.CreatedOnSort)");
        generated.Should().NotContain("nameof(VCar.ArchivedOnSort)");
    }

    [Fact]
    public void Search_on_a_DateTimeOffset_is_reported_but_the_date_treatment_still_applies()
    {
        var result = Run("""
            using System;
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Car
            {
                [Search] public DateTimeOffset CreatedOn { get; set; }
            }
            """);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_005");

        var generated = result.GeneratedSources[0].Source;
        generated.Should().Contain("Index(nameof(VCar.CreatedOn), global::Raven.Client.Documents.Indexes.FieldIndexing.Exact);");
        generated.Should().Contain("CreatedOnSort = car.CreatedOn,");
    }

    // --- attribute carry-over -----------------------------------------------------------------

    /// <summary>
    /// SPARK002 is an ERROR when an index-entity property lacks a [Reference] its entity has, so carrying it
    /// over is not cosmetic. And it must NOT gain [IgnoreProperty] — that would strip the reference from the
    /// model and break breadcrumbs and .Include() resolution.
    /// </summary>
    [Fact]
    public void Reference_is_copied_verbatim_without_IgnoreProperty()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            public class Company { public string? Id { get; set; } }

            [GenerateIndex]
            public class Car
            {
                [Reference(typeof(Company))] public string? Owner { get; set; }
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("[global::MintPlayer.Spark.Abstractions.ReferenceAttribute(typeof(global::TestApp.Entities.Company))]");
        generated.Should().NotContain("[global::MintPlayer.Spark.Abstractions.IgnorePropertyAttribute]");
    }

    [Fact]
    public void Reference_optional_query_argument_is_preserved()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            public class Company { public string? Id { get; set; } }

            [GenerateIndex]
            public class Car
            {
                [Reference(typeof(Company), "GetCompanies")] public string? Owner { get; set; }
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("ReferenceAttribute(typeof(global::TestApp.Entities.Company), \"GetCompanies\")");
    }

    [Fact]
    public void LookupReference_is_copied()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            public class CarStatus { }

            [GenerateIndex]
            public class Car
            {
                [LookupReference(typeof(CarStatus))] public string? Status { get; set; }
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("[global::MintPlayer.Spark.Abstractions.LookupReferenceAttribute(typeof(global::TestApp.Entities.CarStatus))]");
    }

    /// <summary>
    /// Deny-list, not whitelist: an attribute the generator has never heard of still travels. The reference
    /// implementation whitelists, so anything outside its list is dropped with no indication.
    /// </summary>
    [Fact]
    public void An_unknown_attribute_is_still_copied()
    {
        var generated = Run("""
            using System;
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [AttributeUsage(AttributeTargets.Property)]
            public sealed class AuditedAttribute : Attribute
            {
                public AuditedAttribute(string reason, int level = 0) { }
                public bool Strict { get; set; }
            }

            [GenerateIndex]
            public class Car
            {
                [Audited("regulatory", 3, Strict = true)] public string? Model { get; set; }
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("[global::TestApp.Entities.AuditedAttribute(\"regulatory\", 3, Strict = true)]");
    }

    /// <summary>
    /// An attribute whose type does not resolve is an ERROR symbol: its name renders without a namespace and
    /// its arguments come back empty, so rendering it produced valid-looking but silently wrong source
    /// (<c>[MaxLength(250)]</c> became <c>[MaxLength]</c>). It is refused and reported instead.
    /// </summary>
    [Fact]
    public void An_unresolvable_attribute_is_reported_not_silently_mangled()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Car
            {
                [Nonexistent(250)] public string? Model { get; set; }
            }
            """);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_007");
        result.GeneratedSources[0].Source.Should().NotContain("Nonexistent");
    }

    /// <summary>
    /// An optional parameter left at its default should not be restated: an unadorned
    /// [Reference(typeof(Company))] must not render as [Reference(typeof(Company), null)].
    /// </summary>
    [Fact]
    public void Optional_arguments_left_at_their_default_are_omitted()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            public class Company { public string? Id { get; set; } }

            [GenerateIndex]
            public class Car
            {
                [Reference(typeof(Company))] public string? Owner { get; set; }
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("ReferenceAttribute(typeof(global::TestApp.Entities.Company))]");
        generated.Should().NotContain(", null)");
    }

    [Fact]
    public void Generator_directives_are_not_copied()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Car
            {
                [Search] public string? Model { get; set; }
            }
            """).GeneratedSources[0].Source;

        generated.Should().NotContain("Abstractions.SearchAttribute]");
        generated.Should().NotContain("Abstractions.SearchAttribute(");
    }

    /// <summary>
    /// A companion is a plain sort key. Copying the reference would declare a second reference to the same
    /// target that the model then has to resolve.
    /// </summary>
    [Fact]
    public void A_sort_companion_does_not_inherit_Reference_but_does_inherit_others()
    {
        var generated = Run("""
            using System;
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [AttributeUsage(AttributeTargets.Property)]
            public sealed class AuditedAttribute : Attribute { }

            public class Company { public string? Id { get; set; } }

            [GenerateIndex]
            public class Car
            {
                [Search, Audited, Reference(typeof(Company))] public string? Owner { get; set; }
            }
            """).GeneratedSources[0].Source;

        // One Reference (field only), two Audited (field + companion).
        CountOccurrences(generated, "Abstractions.ReferenceAttribute(typeof").Should().Be(1);
        CountOccurrences(generated, "AuditedAttribute]").Should().Be(2);
        generated.Should().Contain("OwnerSort");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
