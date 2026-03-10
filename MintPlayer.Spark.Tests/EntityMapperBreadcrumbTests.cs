using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests;

/// <summary>
/// Regression tests: ensures reference attributes on query results
/// are resolved to breadcrumbs (not raw document IDs).
/// </summary>
public class EntityMapperBreadcrumbTests
{
    private static readonly Guid PersonTypeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CompanyTypeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly IModelLoader _modelLoader;
    private readonly EntityMapper _mapper;

    public EntityMapperBreadcrumbTests()
    {
        _modelLoader = Substitute.For<IModelLoader>();

        var personTypeDef = new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "Person",
            ClrType = typeof(TestPerson).FullName!,
            DisplayAttribute = "LastName",
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
            DisplayAttribute = "Name",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string" },
            ]
        };

        _modelLoader.GetEntityType(PersonTypeId).Returns(personTypeDef);
        _modelLoader.GetEntityType(CompanyTypeId).Returns(companyTypeDef);
        _modelLoader.GetEntityTypeByClrType(typeof(TestPerson).FullName!).Returns(personTypeDef);
        _modelLoader.GetEntityTypeByClrType(typeof(TestCompany).FullName!).Returns(companyTypeDef);

        _mapper = new EntityMapper(_modelLoader);
    }

    [Fact]
    public void ToPersistentObject_WithIncludedDocuments_SetsBreadcrumbOnReferenceAttribute()
    {
        var person = new TestPerson
        {
            Id = "People/1",
            FirstName = "John",
            LastName = "Doe",
            Company = "Companies/abc-123",
        };

        var includedDocuments = new Dictionary<string, object>
        {
            ["Companies/abc-123"] = new TestCompany { Id = "Companies/abc-123", Name = "Acme Corp" }
        };

        var result = _mapper.ToPersistentObject(person, PersonTypeId, includedDocuments);

        var companyAttr = result.Attributes.Single(a => a.Name == "Company");
        Assert.Equal("Companies/abc-123", companyAttr.Value);
        Assert.Equal("Acme Corp", companyAttr.Breadcrumb);
    }

    [Fact]
    public void ToPersistentObject_WithoutIncludedDocuments_BreadcrumbIsNull()
    {
        var person = new TestPerson
        {
            Id = "People/1",
            FirstName = "John",
            LastName = "Doe",
            Company = "Companies/abc-123",
        };

        // This is the bug: when includedDocuments is null (as it was in QueryExecutor),
        // the breadcrumb is not set, and the raw ID is shown in the UI.
        var result = _mapper.ToPersistentObject(person, PersonTypeId);

        var companyAttr = result.Attributes.Single(a => a.Name == "Company");
        Assert.Equal("Companies/abc-123", companyAttr.Value);
        Assert.Null(companyAttr.Breadcrumb);
    }

    [Fact]
    public void ToPersistentObject_WithEmptyIncludedDocuments_BreadcrumbIsNull()
    {
        var person = new TestPerson
        {
            Id = "People/1",
            FirstName = "John",
            LastName = "Doe",
            Company = "Companies/abc-123",
        };

        var result = _mapper.ToPersistentObject(person, PersonTypeId, new Dictionary<string, object>());

        var companyAttr = result.Attributes.Single(a => a.Name == "Company");
        Assert.Null(companyAttr.Breadcrumb);
    }

    [Fact]
    public void ToPersistentObject_WithNullReference_NoBreadcrumb()
    {
        var person = new TestPerson
        {
            Id = "People/1",
            FirstName = "John",
            LastName = "Doe",
            Company = null,
        };

        var includedDocuments = new Dictionary<string, object>();

        var result = _mapper.ToPersistentObject(person, PersonTypeId, includedDocuments);

        var companyAttr = result.Attributes.Single(a => a.Name == "Company");
        Assert.Null(companyAttr.Value);
        Assert.Null(companyAttr.Breadcrumb);
    }
}
