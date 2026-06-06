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

    [Fact]
    public void GetPersistentObject_Generic_ForwardsToEntityMapper()
    {
        var expected = new PersistentObject { Name = "Person", ObjectTypeId = Guid.NewGuid() };
        _entityMapper.GetPersistentObject<Person>().Returns(expected);
        var manager = CreateManager();

        var actual = manager.GetPersistentObject<Person>();

        actual.Should().BeSameAs(expected);
    }

    [Fact]
    public void Retry_property_returns_the_injected_accessor()
        => CreateManager().Retry.Should().BeSameAs(_retry);

    [Fact]
    public void Client_property_returns_the_injected_accessor()
        => CreateManager().Client.Should().BeSameAs(_client);

    [Fact]
    public void GetMessage_returns_template_for_requested_language_with_format_arguments_applied()
    {
        _translations.Resolve("validation.required").Returns(
            new TranslatedString { Translations = { ["en"] = "{0} is required", ["nl"] = "{0} is verplicht" } });

        CreateManager().GetMessage("validation.required", "nl", "Email")
            .Should().Be("Email is verplicht");
    }

    [Fact]
    public void GetMessage_with_no_format_arguments_returns_the_raw_template()
    {
        _translations.Resolve("greeting").Returns(
            new TranslatedString { Translations = { ["en"] = "Hello, world" } });

        CreateManager().GetMessage("greeting", "en").Should().Be("Hello, world");
    }

    [Fact]
    public void GetMessage_returns_the_key_when_translations_loader_has_no_entry()
    {
        _translations.Resolve("missing.key").Returns((TranslatedString?)null);

        CreateManager().GetMessage("missing.key", "en").Should().Be("missing.key");
    }

    [Fact]
    public void GetTranslatedMessage_uses_the_culture_resolver_to_pick_the_language()
    {
        _culture.GetCurrentCulture().Returns("nl");
        _translations.Resolve("validation.required").Returns(
            new TranslatedString { Translations = { ["en"] = "{0} is required", ["nl"] = "{0} is verplicht" } });

        CreateManager().GetTranslatedMessage("validation.required", "Email")
            .Should().Be("Email is verplicht");
    }

    private sealed class Person { }
}
