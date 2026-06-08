using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Services.Breadcrumb;
using NSubstitute;

namespace MintPlayer.Spark.Tests;

/// <summary>
/// EntityMapper no longer computes breadcrumbs — it copies the pre-resolved strings from a
/// <see cref="BreadcrumbResult"/> (keyed by id) onto the PO and its reference attributes.
/// Breadcrumb computation itself is covered by BreadcrumbResolverTests; these pin the copy.
/// </summary>
public class EntityMapperBreadcrumbTests
{
    private static readonly Guid PersonTypeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CompanyTypeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly EntityMapper _mapper;

    public EntityMapperBreadcrumbTests()
    {
        var modelLoader = Substitute.For<IModelLoader>();

        var personTypeDef = new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "Person",
            ClrType = typeof(TestPerson).FullName!,
            Breadcrumb = "{LastName}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "FirstName", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "LastName", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Company", DataType = "Reference", Query = "companies" },
            ]
        };

        var companyTypeDef = new EntityTypeDefinition
        {
            Id = CompanyTypeId,
            Name = "Company",
            ClrType = typeof(TestCompany).FullName!,
            Breadcrumb = "{Name}",
            Attributes = [new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string" }]
        };

        modelLoader.GetEntityType(PersonTypeId).Returns(personTypeDef);
        modelLoader.GetEntityType(CompanyTypeId).Returns(companyTypeDef);
        modelLoader.GetEntityTypeByClrType(typeof(TestPerson).FullName!).Returns(personTypeDef);
        modelLoader.GetEntityTypeByClrType(typeof(TestCompany).FullName!).Returns(companyTypeDef);

        _mapper = new EntityMapper(modelLoader);
    }

    private static TestPerson Person() => new()
    {
        Id = "People/1",
        FirstName = "John",
        LastName = "Doe",
        Company = "Companies/abc-123",
    };

    [Fact]
    public void Copies_reference_breadcrumb_from_the_result_by_id()
    {
        var breadcrumbs = new BreadcrumbResult(new Dictionary<string, string>
        {
            ["People/1"] = "Doe",
            ["Companies/abc-123"] = "Acme Corp",
        });

        var result = _mapper.ToPersistentObject(Person(), PersonTypeId, breadcrumbs);

        result.Breadcrumb.Should().Be("Doe", "the PO's own breadcrumb comes from the result by its id");
        var companyAttr = result.Attributes.Single(a => a.Name == "Company");
        companyAttr.Value.Should().Be("Companies/abc-123");
        companyAttr.Breadcrumb.Should().Be("Acme Corp");
    }

    [Fact]
    public void Without_a_result_reference_breadcrumb_is_null()
    {
        var result = _mapper.ToPersistentObject(Person(), PersonTypeId);

        var companyAttr = result.Attributes.Single(a => a.Name == "Company");
        companyAttr.Value.Should().Be("Companies/abc-123");
        companyAttr.Breadcrumb.Should().BeNull();
    }

    [Fact]
    public void Reference_id_absent_from_the_result_yields_null_breadcrumb()
    {
        var result = _mapper.ToPersistentObject(Person(), PersonTypeId, new BreadcrumbResult(new Dictionary<string, string>()));

        var companyAttr = result.Attributes.Single(a => a.Name == "Company");
        companyAttr.Breadcrumb.Should().BeNull();
    }

    [Fact]
    public void Null_reference_value_has_no_breadcrumb()
    {
        var person = Person();
        person.Company = null;

        var result = _mapper.ToPersistentObject(person, PersonTypeId, new BreadcrumbResult(new Dictionary<string, string>()));

        var companyAttr = result.Attributes.Single(a => a.Name == "Company");
        companyAttr.Value.Should().BeNull();
        companyAttr.Breadcrumb.Should().BeNull();
    }
}
