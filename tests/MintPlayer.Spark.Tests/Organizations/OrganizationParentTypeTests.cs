using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Organizations.Actions;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Organizations;

/// <summary>
/// D28: the members and invitations sub-queries scope to their parent organization by the parent's type, matched by
/// id against the model type whose <c>clrType</c> is <see cref="Organization"/>. They compared <c>ParentType</c> (the
/// type's name in the application) to <c>nameof(Organization)</c> (<c>OrganizationActions.cs:47</c>), so an
/// application that renamed the type got empty member lists.
/// </summary>
public class OrganizationParentTypeTests
{
    private static readonly Guid OrganizationTypeId = Guid.Parse("0a9a0000-0000-4000-8000-000000000001");

    private static IServiceProvider Services()
    {
        var modelLoader = Substitute.For<IModelLoader>();
        modelLoader.GetEntityTypeByClrType(typeof(Organization).FullName!).Returns(new EntityTypeDefinition
        {
            Id = OrganizationTypeId,
            Name = "Company",
            ClrType = typeof(Organization).FullName,
        });
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IModelLoader)).Returns(modelLoader);
        return services;
    }

    private static CustomQueryArgs Args(Guid parentTypeId, string parentTypeName) => new()
    {
        Parent = new PersistentObject { Id = "Organizations/1", Name = parentTypeName, ObjectTypeId = parentTypeId },
        ParentType = parentTypeName,
        Query = new SparkQuery { Id = Guid.NewGuid(), Name = "OrganizationMembers", Source = "Custom.OrganizationMembers" },
    };

    [Fact]
    public void A_renamed_organization_type_still_scopes_its_sub_queries()
        => OrganizationMembershipActions.ParentId(Args(OrganizationTypeId, "Company"), Services()).Should().Be("Organizations/1");

    [Fact]
    public void A_parent_of_another_type_named_Organization_scopes_nothing()
        => OrganizationMembershipActions.ParentId(Args(Guid.NewGuid(), nameof(Organization)), Services()).Should().BeNull();
}
