using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using NSubstitute;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests;

public class CustomQueryArgsTests
{
    private static CustomQueryArgs CreateArgs(PersistentObject? parent = null, string? parentType = null)
    {
        return new CustomQueryArgs
        {
            Query = new SparkQuery
            {
                Id = Guid.NewGuid(),
                Name = "TestQuery",
                Source = "Custom.Test"
            },
            Session = Substitute.For<IAsyncDocumentSession>(),
            Parent = parent,
            ParentType = parentType
        };
    }

    private static PersistentObject CreateParent(string name = "Company") => new()
    {
        Id = "Companies/1",
        Name = name,
        ObjectTypeId = Guid.NewGuid()
    };

    [Fact]
    public void EnsureParent_Succeeds_WhenParentMatchesType()
    {
        var args = CreateArgs(CreateParent(), "Company");

        args.EnsureParent("Company");
    }

    [Fact]
    public void EnsureParent_IsCaseInsensitive()
    {
        var args = CreateArgs(CreateParent(), "company");

        args.EnsureParent("Company");
    }

    [Fact]
    public void EnsureParent_Throws_WhenParentIsNull()
    {
        var args = CreateArgs(parent: null);

        var ex = Assert.Throws<InvalidOperationException>(() => args.EnsureParent("Company"));
        Assert.Contains("requires a parent", ex.Message);
        Assert.Contains("TestQuery", ex.Message);
    }

    [Fact]
    public void EnsureParent_Throws_WhenParentTypeDoesNotMatch()
    {
        var args = CreateArgs(CreateParent(), "Person");

        var ex = Assert.Throws<InvalidOperationException>(() => args.EnsureParent("Company"));
        Assert.Contains("expects parent of type 'Company'", ex.Message);
        Assert.Contains("got 'Person'", ex.Message);
    }

    [Fact]
    public void EnsureParent_MultipleTypes_Succeeds_WhenAnyMatch()
    {
        var args = CreateArgs(CreateParent(), "Person");

        args.EnsureParent("Company", "Person", "Department");
    }

    [Fact]
    public void EnsureParent_MultipleTypes_IsCaseInsensitive()
    {
        var args = CreateArgs(CreateParent(), "person");

        args.EnsureParent("Company", "Person");
    }

    [Fact]
    public void EnsureParent_MultipleTypes_Throws_WhenNoneMatch()
    {
        var args = CreateArgs(CreateParent(), "Car");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            args.EnsureParent("Company", "Person"));
        Assert.Contains("Company, Person", ex.Message);
        Assert.Contains("got 'Car'", ex.Message);
    }

    [Fact]
    public void EnsureParent_MultipleTypes_Throws_WhenParentIsNull()
    {
        var args = CreateArgs(parent: null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            args.EnsureParent("Company", "Person"));
        Assert.Contains("requires a parent", ex.Message);
    }
}
