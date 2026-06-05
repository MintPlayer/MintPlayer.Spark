using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests;

/// <summary>
/// PRD §2 — first-class <see cref="PersistentObjectAttributeAsDetail"/>: the forward path
/// scaffolds nested POs per detail child, the inverse path instantiates the right CLR type
/// and recurses, and the polymorphic JSON converter round-trips the Object / Objects fields
/// through the wire.
/// </summary>
public class EntityMapperAsDetailTests
{
    private static readonly Guid PersonTypeId        = Guid.Parse("aaaaaaaa-3333-3333-3333-333333333333");
    private static readonly Guid AddressTypeId       = Guid.Parse("bbbbbbbb-3333-3333-3333-333333333333");
    private static readonly Guid JobTypeId           = Guid.Parse("cccccccc-3333-3333-3333-333333333333");
    private static readonly Guid CertificationTypeId = Guid.Parse("dddddddd-3333-3333-3333-333333333333");

    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();
    private readonly EntityMapper _mapper;

    public EntityMapperAsDetailTests()
    {
        var addressDef = new EntityTypeDefinition
        {
            Id = AddressTypeId,
            Name = "Address",
            ClrType = typeof(TestAddress).FullName!,
            DisplayAttribute = "Street",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Street", DataType = "string", Order = 1 },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "City",   DataType = "string", Order = 2 },
            ],
        };

        var certificationDef = new EntityTypeDefinition
        {
            Id = CertificationTypeId,
            Name = "Certification",
            ClrType = typeof(TestCertification).FullName!,
            DisplayAttribute = "Name",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name",   DataType = "string", Order = 1 },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Issuer", DataType = "string", Order = 2 },
            ],
        };

        var jobDef = new EntityTypeDefinition
        {
            Id = JobTypeId,
            Name = "Job",
            ClrType = typeof(TestJob).FullName!,
            DisplayAttribute = "Title",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title", DataType = "string", Order = 1 },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Year",  DataType = "number", Order = 2 },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = "Certifications",
                    DataType = "AsDetail",
                    AsDetailType = typeof(TestCertification).FullName,
                    IsArray = true,
                    Order = 3,
                },
            ],
        };

        var personDef = new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "Person",
            ClrType = typeof(TestPerson).FullName!,
            DisplayAttribute = "FirstName",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "FirstName", DataType = "string", Order = 1 },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = "Address",
                    DataType = "AsDetail",
                    AsDetailType = typeof(TestAddress).FullName,
                    IsArray = false,
                    Order = 2,
                },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = "Jobs",
                    DataType = "AsDetail",
                    AsDetailType = typeof(TestJob).FullName,
                    IsArray = true,
                    Order = 3,
                },
            ],
        };

        _modelLoader.GetEntityType(PersonTypeId).Returns(personDef);
        _modelLoader.GetEntityTypeByName("Person").Returns(personDef);
        _modelLoader.GetEntityTypeByClrType(typeof(TestPerson).FullName!).Returns(personDef);
        _modelLoader.GetEntityType(AddressTypeId).Returns(addressDef);
        _modelLoader.GetEntityTypeByClrType(typeof(TestAddress).FullName!).Returns(addressDef);
        _modelLoader.GetEntityType(JobTypeId).Returns(jobDef);
        _modelLoader.GetEntityTypeByClrType(typeof(TestJob).FullName!).Returns(jobDef);
        _modelLoader.GetEntityType(CertificationTypeId).Returns(certificationDef);
        _modelLoader.GetEntityTypeByClrType(typeof(TestCertification).FullName!).Returns(certificationDef);

        _mapper = new EntityMapper(_modelLoader);
    }

    // --- Scaffold -------------------------------------------------------

    [Fact]
    public void GetPersistentObject_SingleAsDetail_PreScaffoldsNestedPO()
    {
        var po = _mapper.GetPersistentObject<TestPerson>();

        var addressAttr = po["Address"];
        addressAttr.Should().BeOfType<PersistentObjectAttributeAsDetail>();
        var asDetail = (PersistentObjectAttributeAsDetail)addressAttr;
        asDetail.IsArray.Should().BeFalse();
        asDetail.Object.Should().NotBeNull(
            "single AsDetail gets a pre-scaffolded empty child so UIs open a structured form");
        asDetail.Object!.ObjectTypeId.Should().Be(AddressTypeId);
        asDetail.Object.Attributes.Select(a => a.Name).Should().Contain(["Street", "City"]);
        asDetail.AsDetailType.Should().Be(typeof(TestAddress).FullName);
    }

    [Fact]
    public void GetPersistentObject_ArrayAsDetail_StartsWithEmptyList()
    {
        var po = _mapper.GetPersistentObject<TestPerson>();

        var jobsAttr = (PersistentObjectAttributeAsDetail)po["Jobs"];
        jobsAttr.IsArray.Should().BeTrue();
        jobsAttr.Objects.Should().NotBeNull().And.BeEmpty(
            "array AsDetail populates on forward pass, not at scaffold");
        jobsAttr.Object.Should().BeNull();
    }

    // --- Forward populate ----------------------------------------------

    [Fact]
    public void PopulateAttributeValues_SingleAsDetail_FillsNestedPO()
    {
        var po = _mapper.GetPersistentObject<TestPerson>();
        var person = new TestPerson
        {
            FirstName = "Alice",
            Address = new TestAddress { Street = "Main 1", City = "Brussels" },
        };

        _mapper.PopulateAttributeValues(po, person);

        var address = ((PersistentObjectAttributeAsDetail)po["Address"]).Object!;
        address["Street"].Value.Should().Be("Main 1");
        address["City"].Value.Should().Be("Brussels");
    }

    [Fact]
    public void PopulateAttributeValues_ArrayAsDetail_ScaffoldsOneChildPerItem()
    {
        var po = _mapper.GetPersistentObject<TestPerson>();
        var person = new TestPerson
        {
            FirstName = "Alice",
            Jobs =
            [
                new TestJob { Title = "Intern", Year = 2020 },
                new TestJob { Title = "Dev",    Year = 2024 },
            ],
        };

        _mapper.PopulateAttributeValues(po, person);

        var jobs = (PersistentObjectAttributeAsDetail)po["Jobs"];
        jobs.Objects.Should().HaveCount(2);
        jobs.Objects![0]["Title"].Value.Should().Be("Intern");
        jobs.Objects[0]["Year"].Value.Should().Be(2020);
        jobs.Objects[1]["Title"].Value.Should().Be("Dev");
        jobs.Objects[1]["Year"].Value.Should().Be(2024);
    }

    [Fact]
    public void PopulateAttributeValues_NullSingleAsDetail_ClearsObject()
    {
        var po = _mapper.GetPersistentObject<TestPerson>();
        var person = new TestPerson { FirstName = "Alice", Address = null };

        _mapper.PopulateAttributeValues(po, person);

        ((PersistentObjectAttributeAsDetail)po["Address"]).Object.Should().BeNull();
    }

    [Fact]
    public void PopulateAttributeValues_EmptyArrayAsDetail_YieldsEmptyObjects()
    {
        var po = _mapper.GetPersistentObject<TestPerson>();
        var person = new TestPerson { FirstName = "Alice", Jobs = [] };

        _mapper.PopulateAttributeValues(po, person);

        ((PersistentObjectAttributeAsDetail)po["Jobs"]).Objects.Should().BeEmpty();
    }

    // --- Inverse populate ----------------------------------------------

    [Fact]
    public void PopulateObjectValues_SingleAsDetail_InstantiatesAndFillsEntity()
    {
        var po = _mapper.GetPersistentObject<TestPerson>();
        ((PersistentObjectAttributeAsDetail)po["Address"]).Object!["Street"].Value = "Rue 42";
        ((PersistentObjectAttributeAsDetail)po["Address"]).Object!["City"].Value = "Ghent";
        po["FirstName"].Value = "Alice";

        var person = new TestPerson();
        _mapper.PopulateObjectValues(po, person);

        person.FirstName.Should().Be("Alice");
        person.Address.Should().NotBeNull();
        person.Address!.Street.Should().Be("Rue 42");
        person.Address.City.Should().Be("Ghent");
    }

    [Fact]
    public void PopulateObjectValues_ArrayAsDetail_BuildsListOfInstantiatedChildren()
    {
        var po = _mapper.GetPersistentObject<TestPerson>();
        po["FirstName"].Value = "Alice";

        var job1 = _mapper.GetPersistentObject<TestJob>();
        job1["Title"].Value = "Intern"; job1["Year"].Value = 2020;
        var job2 = _mapper.GetPersistentObject<TestJob>();
        job2["Title"].Value = "Dev";    job2["Year"].Value = 2024;
        ((PersistentObjectAttributeAsDetail)po["Jobs"]).Objects = [job1, job2];

        var person = new TestPerson();
        _mapper.PopulateObjectValues(po, person);

        person.Jobs.Should().HaveCount(2);
        person.Jobs[0].Title.Should().Be("Intern");
        person.Jobs[0].Year.Should().Be(2020);
        person.Jobs[1].Title.Should().Be("Dev");
        person.Jobs[1].Year.Should().Be(2024);
    }

    [Fact]
    public void PopulateObjectValues_NullSingleAsDetail_SetsPropertyNull()
    {
        var po = _mapper.GetPersistentObject<TestPerson>();
        ((PersistentObjectAttributeAsDetail)po["Address"]).Object = null;

        var person = new TestPerson { Address = new TestAddress { Street = "stale" } };
        _mapper.PopulateObjectValues(po, person);

        person.Address.Should().BeNull();
    }

    // --- Round-trip (forward + inverse) --------------------------------

    [Fact]
    public void RoundTrip_SingleAndArrayAsDetail_PreservesValuesDeepEqual()
    {
        var original = new TestPerson
        {
            FirstName = "Alice",
            Address = new TestAddress { Street = "Main 1", City = "Brussels" },
            Jobs =
            [
                new TestJob { Title = "Intern", Year = 2020 },
                new TestJob { Title = "Dev",    Year = 2024 },
            ],
        };

        var po = _mapper.ToPersistentObject(original);
        var rebuilt = _mapper.ToEntity<TestPerson>(po);

        rebuilt.FirstName.Should().Be(original.FirstName);
        rebuilt.Address!.Street.Should().Be(original.Address!.Street);
        rebuilt.Address.City.Should().Be(original.Address.City);
        rebuilt.Jobs.Should().HaveCount(2);
        rebuilt.Jobs[0].Title.Should().Be("Intern");
        rebuilt.Jobs[0].Year.Should().Be(2020);
        rebuilt.Jobs[1].Title.Should().Be("Dev");
        rebuilt.Jobs[1].Year.Should().Be(2024);
    }

    // --- Multi-level nesting (AsDetail-in-AsDetail) --------------------

    [Fact]
    public void RoundTrip_MultiLevelAsDetail_PreservesNestedInNestedThroughJson()
    {
        var original = new TestPerson
        {
            FirstName = "Alice",
            Jobs =
            [
                new TestJob
                {
                    Title = "Dev",
                    Year = 2024,
                    Certifications =
                    [
                        new TestCertification { Name = "Azure Fundamentals", Issuer = "Microsoft" },
                        new TestCertification { Name = "AWS CP",             Issuer = "Amazon" },
                    ],
                },
            ],
        };

        var po = _mapper.ToPersistentObject(original);

        // Forward recursion produced the nested-in-nested structure.
        var jobs = (PersistentObjectAttributeAsDetail)po["Jobs"];
        var firstJob = jobs.Objects!.Single();
        var certs = (PersistentObjectAttributeAsDetail)firstJob["Certifications"];
        certs.Objects.Should().HaveCount(2, "array AsDetail inside array AsDetail must be populated recursively");
        certs.Objects![0]["Name"].Value.Should().Be("Azure Fundamentals");

        // Serialize + deserialize exercises the polymorphic converter at every level.
        var json = JsonSerializer.Serialize(po);
        var rehydrated = JsonSerializer.Deserialize<PersistentObject>(json)!;

        // Inverse recursion reconstitutes the two-level CLR graph.
        var rebuilt = _mapper.ToEntity<TestPerson>(rehydrated);
        rebuilt.Jobs.Should().HaveCount(1);
        rebuilt.Jobs[0].Certifications.Should().HaveCount(2);
        rebuilt.Jobs[0].Certifications[0].Name.Should().Be("Azure Fundamentals");
        rebuilt.Jobs[0].Certifications[0].Issuer.Should().Be("Microsoft");
        rebuilt.Jobs[0].Certifications[1].Name.Should().Be("AWS CP");
        rebuilt.Jobs[0].Certifications[1].Issuer.Should().Be("Amazon");
    }

    // --- Polymorphic JSON converter ------------------------------------

    [Fact]
    public void JsonConverter_SerializesAsDetailSubclassFields()
    {
        var po = _mapper.GetPersistentObject<TestPerson>();
        ((PersistentObjectAttributeAsDetail)po["Address"]).Object!["Street"].Value = "Main 1";

        var json = JsonSerializer.Serialize(po);

        json.Should().Contain("\"Street\"", "wire must carry nested PO's attributes");
        json.Should().Contain("\"Object\"", "subclass's nested PO field must be emitted (STJ default is PascalCase)");
    }

    [Fact]
    public void JsonConverter_DeserializesAsDetailBasedOnDataType()
    {
        var po = _mapper.GetPersistentObject<TestPerson>();
        ((PersistentObjectAttributeAsDetail)po["Address"]).Object!["Street"].Value = "Main 1";
        ((PersistentObjectAttributeAsDetail)po["Address"]).Object!["City"].Value = "Brussels";
        var json = JsonSerializer.Serialize(po);

        var roundtrip = JsonSerializer.Deserialize<PersistentObject>(json)!;
        var addressAttr = roundtrip.Attributes.Single(a => a.Name == "Address");
        addressAttr.Should().BeOfType<PersistentObjectAttributeAsDetail>(
            "the polymorphic converter must instantiate the subclass when dataType == AsDetail");
        ((PersistentObjectAttributeAsDetail)addressAttr).Object!["Street"].Value!.ToString().Should().Be("Main 1");
    }

    [Fact]
    public void JsonConverter_NonAsDetailAttributeUsesBaseClass()
    {
        var po = _mapper.GetPersistentObject<TestPerson>();
        po["FirstName"].Value = "Alice";
        var json = JsonSerializer.Serialize(po);

        var roundtrip = JsonSerializer.Deserialize<PersistentObject>(json)!;
        var firstNameAttr = roundtrip.Attributes.Single(a => a.Name == "FirstName");
        firstNameAttr.Should().NotBeOfType<PersistentObjectAttributeAsDetail>();
        firstNameAttr.GetType().Should().Be(typeof(PersistentObjectAttribute));
    }

    // --- fixtures -------------------------------------------------------

    private sealed class TestPerson
    {
        public string? Id { get; set; }
        public string? FirstName { get; set; }
        public TestAddress? Address { get; set; }
        public TestJob[] Jobs { get; set; } = [];
    }

    private sealed class TestAddress
    {
        public string? Id { get; set; }
        public string? Street { get; set; }
        public string? City { get; set; }
    }

    private sealed class TestJob
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public int Year { get; set; }
        public List<TestCertification> Certifications { get; set; } = [];
    }

    private sealed class TestCertification
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Issuer { get; set; }
    }
}
