using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MintPlayer.Spark.Tests.Services;

public class ManagerTests
{
    private readonly IRetryAccessor _retry = Substitute.For<IRetryAccessor>();
    private readonly IClientAccessor _client = Substitute.For<IClientAccessor>();
    private readonly ITranslationsLoader _translations = Substitute.For<ITranslationsLoader>();
    private readonly IRequestCultureResolver _culture = Substitute.For<IRequestCultureResolver>();
    private readonly IEntityMapper _entityMapper = Substitute.For<IEntityMapper>();

    private Manager CreateManager() => new(_retry, _client, _translations, _culture, _entityMapper);

    [Fact]
    public void GetPersistentObject_ByName_ForwardsToEntityMapper()
    {
        var expected = new PersistentObject { Name = "Car", ObjectTypeId = Guid.NewGuid() };
        _entityMapper.GetPersistentObject("Car").Returns(expected);
        var manager = CreateManager();

        var actual = manager.GetPersistentObject("Car");

        actual.Should().BeSameAs(expected);
        _entityMapper.Received(1).GetPersistentObject("Car");
    }

    [Fact]
    public void GetPersistentObject_ByGuid_ForwardsToEntityMapper()
    {
        var carId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var expected = new PersistentObject { Name = "Car", ObjectTypeId = carId };
        _entityMapper.GetPersistentObject(carId).Returns(expected);
        var manager = CreateManager();

        var actual = manager.GetPersistentObject(carId);

        actual.Should().BeSameAs(expected);
        _entityMapper.Received(1).GetPersistentObject(carId);
    }

    [Fact]
    public void GetPersistentObject_UnknownName_PropagatesEntityMapperException()
    {
        _entityMapper.GetPersistentObject("Unknown")
            .Throws(new KeyNotFoundException("No entity type with Name 'Unknown' is registered."));
        var manager = CreateManager();

        var act = () => manager.GetPersistentObject("Unknown");

        act.Should().Throw<KeyNotFoundException>()
            .WithMessage("*Unknown*");
    }
}
