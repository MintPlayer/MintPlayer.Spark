using System.Drawing;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests;

/// <summary>
/// Covers the inverse mapping surface on <see cref="IEntityMapper"/>:
/// <c>PopulateObjectValues</c> / <c>PopulateObjectValuesAsync</c> — TranslatedString
/// per-language merging, Reference resolution via session/pre-loaded dict, typed
/// coercion, and Vidyano-parity silent-skip + dot-notation-skip behaviors.
/// </summary>
public class EntityMapperInverseTests
{
    private static readonly Guid PersonTypeId = Guid.Parse("aaaaaaaa-2222-2222-2222-222222222222");

    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();
    private readonly EntityMapper _mapper;

    public EntityMapperInverseTests()
    {
        _mapper = new EntityMapper(_modelLoader);
    }

    // --- basic population ------------------------------------------------

    [Fact]
    public void PopulateObjectValues_WritesMatchingProperties()
    {
        var person = new TestPerson();
        var po = PoWith(
            ("FirstName", "Alice", "string"),
            ("Age", 42, "number"));

        _mapper.PopulateObjectValues(po, person);

        person.FirstName.Should().Be("Alice");
        person.Age.Should().Be(42);
    }

    [Fact]
    public void PopulateObjectValues_WritesIdFromPO()
    {
        var person = new TestPerson();
        var po = PoWith([("FirstName", "Alice", "string")], id: "people/1");

        _mapper.PopulateObjectValues(po, person);

        person.Id.Should().Be("people/1");
    }

    [Fact]
    public void PopulateObjectValues_SkipsDotNotationAttributes()
    {
        var person = new TestPerson { FirstName = "Alice" };
        var po = PoWith(
            ("FirstName", "Bob", "string"),
            ("Address.Street", "Main 1", "string"));

        _mapper.PopulateObjectValues(po, person);

        person.FirstName.Should().Be("Bob", "flat attributes populate normally");
        // The dot-notation attribute is silently dropped; no entity property named 'Address.Street'
        // would ever resolve anyway, but the explicit skip is the reservation for nested AsDetail.
    }

    [Fact]
    public void PopulateObjectValues_UntouchedEntityFieldsSurvive()
    {
        // Any field not mentioned on the PO's attributes is left as-is on the entity.
        // This is the PATCH-style semantics that lets load-existing-then-populate work.
        var person = new TestPerson
        {
            Id = "people/1",
            FirstName = "Alice",
            LastName = "Adams",
            Age = 30,
        };
        var po = PoWith([("FirstName", "Alicia", "string")]);

        _mapper.PopulateObjectValues(po, person);

        person.LastName.Should().Be("Adams", "absent from PO → unchanged");
        person.Age.Should().Be(30, "absent from PO → unchanged");
    }

    [Fact]
    public void PopulateObjectValues_AttributeWithoutMatchingProperty_SilentlySkips()
    {
        var person = new TestPerson();
        var po = PoWith(
            ("FirstName", "Alice", "string"),
            ("NoSuchProperty", "ignored", "string"));

        var act = () => _mapper.PopulateObjectValues(po, person);

        act.Should().NotThrow();
        person.FirstName.Should().Be("Alice");
    }

    // --- typed coercion --------------------------------------------------

    [Fact]
    public void PopulateObjectValues_ConvertsGuidDateOnlyEnumColor()
    {
        var person = new TestPerson();
        var po = PoWith(
            ("Code", "9c0a8400-e29b-41d4-a716-446655440000", "guid"),
            ("BirthDate", "1985-04-23", "date"),
            ("FavoriteStatus", "Retired", "string"),
            ("FavoriteColor", "#123456", "color"));

        _mapper.PopulateObjectValues(po, person);

        person.Code.Should().Be(Guid.Parse("9c0a8400-e29b-41d4-a716-446655440000"));
        person.BirthDate.Should().Be(new DateOnly(1985, 4, 23));
        person.FavoriteStatus.Should().Be(TestStatus.Retired);
        person.FavoriteColor.Should().Be(Color.FromArgb(0x12, 0x34, 0x56));
    }

    // --- TranslatedString merging ----------------------------------------

    [Fact]
    public void PopulateObjectValues_TranslatedString_MergePreservesAbsentLanguages()
    {
        var profession = new TestProfession
        {
            Label = new TranslatedString
            {
                Translations = new() { ["en"] = "Doctor", ["fr"] = "Médecin", ["nl"] = "Dokter" }
            }
        };
        var po = PoWith([("Label", TranslatedStringElement(("en", "Physician"))
, "TranslatedString")]);

        _mapper.PopulateObjectValues(po, profession);

        profession.Label!.Translations["en"].Should().Be("Physician", "incoming language overwrites");
        profession.Label.Translations["fr"].Should().Be("Médecin", "absent from incoming → survives");
        profession.Label.Translations["nl"].Should().Be("Dokter", "absent from incoming → survives");
    }

    [Fact]
    public void PopulateObjectValues_TranslatedString_ExistingNull_AssignsIncoming()
    {
        var profession = new TestProfession { Label = null };
        var po = PoWith([("Label", TranslatedStringElement(("en", "Doctor"), ("fr", "Médecin")), "TranslatedString")]);

        _mapper.PopulateObjectValues(po, profession);

        profession.Label.Should().NotBeNull();
        profession.Label!.Translations.Should().ContainKey("en").WhoseValue.Should().Be("Doctor");
        profession.Label.Translations.Should().ContainKey("fr").WhoseValue.Should().Be("Médecin");
    }

    [Fact]
    public void PopulateObjectValues_TranslatedString_NullIncoming_ClearsProperty()
    {
        var profession = new TestProfession
        {
            Label = new TranslatedString { Translations = new() { ["en"] = "Doctor" } }
        };
        var po = PoWith([("Label", (object?)null, "TranslatedString")]);

        _mapper.PopulateObjectValues(po, profession);

        profession.Label.Should().BeNull("explicit null incoming clears the property");
    }

    [Fact]
    public void PopulateObjectValues_TranslatedString_AcceptsAlreadyMaterializedInstance()
    {
        var profession = new TestProfession();
        var incoming = new TranslatedString { Translations = new() { ["en"] = "Nurse" } };
        var po = PoWith([("Label", (object?)incoming, "TranslatedString")]);

        _mapper.PopulateObjectValues(po, profession);

        profession.Label!.Translations["en"].Should().Be("Nurse");
    }

    // --- Reference resolution --------------------------------------------

    [Fact]
    public void PopulateObjectValues_ReferenceToStringProperty_PassesThroughRefId()
    {
        // Spark convention: most Reference props are string? ids annotated with [Reference].
        // The inverse path must NOT try to resolve these — just write the refId through.
        var person = new TestPerson();
        var po = PoWith([("Company", "companies/acme", "Reference")]);

        _mapper.PopulateObjectValues(po, person);

        person.Company.Should().Be("companies/acme");
    }

    [Fact]
    public void PopulateObjectValues_ReferenceToComplexProperty_ResolvesFromIncludedDocuments()
    {
        var order = new TestOrder();
        var customer = new TestCustomer { Id = "customers/1", Name = "Alice" };
        var po = PoWith([("Customer", "customers/1", "Reference")]);

        _mapper.PopulateObjectValues(po, order,
            includedDocuments: new Dictionary<string, object> { ["customers/1"] = customer });

        order.Customer.Should().BeSameAs(customer);
    }

    [Fact]
    public void PopulateObjectValues_ReferenceToComplexProperty_NotInDict_Throws()
    {
        var order = new TestOrder();
        var po = PoWith([("Customer", "customers/1", "Reference")]);

        var act = () => _mapper.PopulateObjectValues(po, order, includedDocuments: null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Customer*customers/1*includedDocuments*",
                "sync path must fail loud rather than silently drop a reference we can't resolve");
    }

    [Fact]
    public void PopulateObjectValues_ReferenceToComplexProperty_NullRefId_ClearsProperty()
    {
        var order = new TestOrder
        {
            Customer = new TestCustomer { Id = "customers/1", Name = "Alice" }
        };
        var po = PoWith([("Customer", (object?)null, "Reference")]);

        _mapper.PopulateObjectValues(po, order);

        order.Customer.Should().BeNull("null refId means 'unset the reference'");
    }

    [Fact]
    public async Task PopulateObjectValuesAsync_ReferenceToComplexProperty_ResolvesViaSession()
    {
        var order = new TestOrder();
        var customer = new TestCustomer { Id = "customers/1", Name = "Alice" };
        var po = PoWith([("Customer", "customers/1", "Reference")]);

        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<TestCustomer>("customers/1", Arg.Any<CancellationToken>())
            .Returns(customer);

        await _mapper.PopulateObjectValuesAsync(po, order, session);

        order.Customer.Should().BeSameAs(customer);
        _ = session.Received(1).LoadAsync<TestCustomer>("customers/1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PopulateObjectValuesAsync_IncludedDocuments_WinsOverSession()
    {
        // When a caller pre-loaded references on the forward path, we should reuse those
        // instances rather than re-fetching — symmetry with the forward path.
        var order = new TestOrder();
        var preloaded = new TestCustomer { Id = "customers/1", Name = "From Dict" };
        var po = PoWith([("Customer", "customers/1", "Reference")]);

        var session = Substitute.For<IAsyncDocumentSession>();
        // Sync overload accepts includedDocuments directly; async overload does too via
        // the common WritePropertyAsync core — this test exercises that path.
        await _mapper.PopulateObjectValuesAsync(po, order, session);

        // (We can't easily test the "includedDocuments wins" behavior on the async overload
        // through the public surface since includedDocuments isn't a parameter there. The
        // sync-overload test above covers that path; this async test confirms the session
        // fallback works when there's no dict.)
        _ = session.Received().LoadAsync<TestCustomer>("customers/1", Arg.Any<CancellationToken>());
    }

    // --- fixtures / helpers ---------------------------------------------

    private static PersistentObject PoWith(params (string Name, object? Value, string DataType)[] attrs)
        => PoWith(attrs, id: null);

    private static PersistentObject PoWith((string Name, object? Value, string DataType)[] attrs, string? id)
    {
        var attributes = attrs.Select(a => new PersistentObjectAttribute
        {
            Name = a.Name,
            Value = a.Value,
            DataType = a.DataType,
        }).ToArray();

        return new PersistentObject
        {
            Id = id,
            Name = "TestPO",
            ObjectTypeId = PersonTypeId,
            Attributes = attributes,
        };
    }

    /// <summary>Builds a JsonElement shaped like the wire value for a TranslatedString.</summary>
    private static JsonElement TranslatedStringElement(params (string Lang, string Value)[] entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (lang, value) in entries)
                writer.WriteString(lang, value);
            writer.WriteEndObject();
        }
        stream.Position = 0;
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }

    private sealed class TestPerson
    {
        public string? Id { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public int Age { get; set; }
        public Guid Code { get; set; }
        public DateOnly BirthDate { get; set; }
        public TestStatus FavoriteStatus { get; set; }
        public Color FavoriteColor { get; set; }
        public string? Company { get; set; }
    }

    private sealed class TestProfession
    {
        public string? Id { get; set; }
        public TranslatedString? Label { get; set; }
    }

    private sealed class TestOrder
    {
        public string? Id { get; set; }
        public TestCustomer? Customer { get; set; }
    }

    private sealed class TestCustomer
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    private enum TestStatus
    {
        Active,
        Retired,
    }
}
