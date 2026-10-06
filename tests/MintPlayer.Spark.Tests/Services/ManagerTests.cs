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
    public async Task GetPersistentObject_ByName_ForwardsToEntityMapper()
    {
        var expected = new PersistentObject { Name = "Car", ObjectTypeId = Guid.NewGuid() };
        _entityMapper.GetPersistentObjectAsync("Car", "Read", Arg.Any<CancellationToken>()).Returns(expected);
        var manager = CreateManager();

        var actual = await manager.GetPersistentObjectAsync("Car");

        actual.Should().BeSameAs(expected);
        await _entityMapper.Received(1).GetPersistentObjectAsync("Car", "Read", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPersistentObject_ByGuid_ForwardsToEntityMapper_with_the_verb()
    {
        var carId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var expected = new PersistentObject { Name = "Car", ObjectTypeId = carId };
        _entityMapper.GetPersistentObjectAsync(carId, "New", Arg.Any<CancellationToken>()).Returns(expected);
        var manager = CreateManager();

        var actual = await manager.GetPersistentObjectAsync(carId, "New");

        actual.Should().BeSameAs(expected);
        await _entityMapper.Received(1).GetPersistentObjectAsync(carId, "New", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPersistentObject_UnknownName_PropagatesEntityMapperException()
    {
        _entityMapper.GetPersistentObjectAsync("Unknown", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new KeyNotFoundException("No entity type with Name 'Unknown' is registered."));
        var manager = CreateManager();

        var act = () => manager.GetPersistentObjectAsync("Unknown");

        (await act.Should().ThrowAsync<KeyNotFoundException>())
            .WithMessage("*Unknown*");
    }

    [Fact]
    public async Task GetPersistentObject_Generic_ForwardsToEntityMapper()
    {
        var expected = new PersistentObject { Name = "Person", ObjectTypeId = Guid.NewGuid() };
        _entityMapper.GetPersistentObjectAsync<Person>(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(expected);
        var manager = CreateManager();

        var actual = await manager.GetPersistentObjectAsync<Person>();

        actual.Should().BeSameAs(expected);
    }

    /// <summary>The elevated construction is the mapper's own, by name, so it can be reviewed (D13a).</summary>
    [Fact]
    public void AsSystem_is_the_entity_mappers_system_construction()
    {
        var system = Substitute.For<ISystemEntityMapper>();
        _entityMapper.AsSystem().Returns(system);

        CreateManager().AsSystem().Should().BeSameAs(system);
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
