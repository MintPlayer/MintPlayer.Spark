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

        var act = () => args.EnsureParent("Company");

        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains("requires a parent") && e.Message.Contains("TestQuery"));
    }

    [Fact]
    public void EnsureParent_Throws_WhenParentTypeDoesNotMatch()
    {
        var args = CreateArgs(CreateParent(), "Person");

        var act = () => args.EnsureParent("Company");

        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains("expects parent of type 'Company'") && e.Message.Contains("got 'Person'"));
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

        var act = () => args.EnsureParent("Company", "Person");

        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains("Company, Person") && e.Message.Contains("got 'Car'"));
    }

    [Fact]
    public void EnsureParent_MultipleTypes_Throws_WhenParentIsNull()
    {
        var args = CreateArgs(parent: null);

        var act = () => args.EnsureParent("Company", "Person");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*requires a parent*");
    }
}
