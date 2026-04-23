using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MintPlayer.Spark.Tests.Services;

public class ManagerTests
{
    private readonly IRetryAccessor _retry = Substitute.For<IRetryAccessor>();
    private readonly ITranslationsLoader _translations = Substitute.For<ITranslationsLoader>();
    private readonly IRequestCultureResolver _culture = Substitute.For<IRequestCultureResolver>();
    private readonly IEntityMapper _entityMapper = Substitute.For<IEntityMapper>();

    private Manager CreateManager() => new(_retry, _translations, _culture, _entityMapper);

    [Fact]
    public void NewPersistentObject_ByName_ForwardsToEntityMapper()
    {
        var expected = new PersistentObject { Name = "Car", ObjectTypeId = Guid.NewGuid() };
        _entityMapper.NewPersistentObject("Car").Returns(expected);
        var manager = CreateManager();

        var actual = manager.NewPersistentObject("Car");

        actual.Should().BeSameAs(expected);
        _entityMapper.Received(1).NewPersistentObject("Car");
    }

    [Fact]
    public void NewPersistentObject_ByGuid_ForwardsToEntityMapper()
    {
        var carId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var expected = new PersistentObject { Name = "Car", ObjectTypeId = carId };
        _entityMapper.NewPersistentObject(carId).Returns(expected);
        var manager = CreateManager();

        var actual = manager.NewPersistentObject(carId);

        actual.Should().BeSameAs(expected);
        _entityMapper.Received(1).NewPersistentObject(carId);
    }

    [Fact]
    public void NewPersistentObject_UnknownName_PropagatesEntityMapperException()
    {
        _entityMapper.NewPersistentObject("Unknown")
            .Throws(new KeyNotFoundException("No entity type with Name 'Unknown' is registered."));
        var manager = CreateManager();

        var act = () => manager.NewPersistentObject("Unknown");

        act.Should().Throw<KeyNotFoundException>()
            .WithMessage("*Unknown*");
    }
}
