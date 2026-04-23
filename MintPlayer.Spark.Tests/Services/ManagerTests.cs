using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

public class ManagerTests
{
    private readonly IRetryAccessor _retry = Substitute.For<IRetryAccessor>();
    private readonly ITranslationsLoader _translations = Substitute.For<ITranslationsLoader>();
    private readonly IRequestCultureResolver _culture = Substitute.For<IRequestCultureResolver>();

    private Manager CreateManager() => new(_retry, _translations, _culture);

    [Fact]
    public void NewPersistentObject_Synthetic_ReturnsPoWithSuppliedAttributes()
    {
        var manager = CreateManager();
        var plate = new PersistentObjectAttribute { Name = "LicensePlate", Value = "ABC-123" };
        var model = new PersistentObjectAttribute { Name = "Model" };

        var po = manager.NewPersistentObject("ConfirmDelete", plate, model);

        po.Name.Should().Be("ConfirmDelete");
        po.ObjectTypeId.Should().Be(Guid.Empty);
        po.Id.Should().BeNull();
        po.Attributes.Should().HaveCount(2);
        po.Attributes.Should().ContainInOrder(plate, model);
    }

    [Fact]
    public void NewPersistentObject_Synthetic_AttachesParentOnEverySuppliedAttribute()
    {
        var manager = CreateManager();
        var plate = new PersistentObjectAttribute { Name = "LicensePlate" };
        var model = new PersistentObjectAttribute { Name = "Model" };

        var po = manager.NewPersistentObject("ConfirmDelete", plate, model);

        plate.Parent.Should().BeSameAs(po, "AddAttribute sets Parent on each supplied attribute");
        model.Parent.Should().BeSameAs(po);
    }

    [Fact]
    public void NewPersistentObject_Synthetic_WithNoAttributes_ReturnsEmptyPo()
    {
        var manager = CreateManager();

        var po = manager.NewPersistentObject("Ping");

        po.Name.Should().Be("Ping");
        po.ObjectTypeId.Should().Be(Guid.Empty);
        po.Attributes.Should().BeEmpty();
    }
}
