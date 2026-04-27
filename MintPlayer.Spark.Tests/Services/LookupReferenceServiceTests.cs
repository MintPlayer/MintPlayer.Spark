using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// LookupReferenceService is the read/write API behind every lookup-reference dropdown.
/// Two modes: transient lookups (compile-time enum-like, served from a static Items property —
/// read-only) and dynamic lookups (CRUD-able, persisted as RavenDB documents). Pins the
/// transient/dynamic dispatch, the duplicate-key guard, the key-rename collision check, and
/// the explicit "cannot mutate transient" guard rails.
/// </summary>
public class LookupReferenceServiceTests : SparkTestDriver
{
    private readonly ILookupReferenceDiscoveryService _discovery = Substitute.For<ILookupReferenceDiscoveryService>();

    private LookupReferenceService CreateService() => new(_discovery, Store);

    private static LookupReferenceInfo TransientInfo() => new()
    {
        Name = nameof(LDSTestStatus),
        Type = typeof(LDSTestStatus),
        IsTransient = true,
        KeyType = typeof(int),
        DisplayType = ELookupDisplayType.Modal,
    };

    private static LookupReferenceInfo DynamicInfo(string name = "CarBrand") => new()
    {
        Name = name,
        Type = typeof(LDSTestBrand),
        IsTransient = false,
        ValueType = typeof(EmptyValue),
        DisplayType = ELookupDisplayType.Dropdown,
    };

    private static LookupReferenceValueDto NewValue(string key, string en) => new()
    {
        Key = key,
        Values = TranslatedString.Create(en),
        IsActive = true,
    };

    private async Task SeedDynamicDocAsync(string name, params (string Key, string En)[] values)
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new
        {
            Id = $"LookupReferences/{name}",
            Name = name,
            Values = values.Select(v => new
            {
                v.Key,
                Translations = new Dictionary<string, string> { ["en"] = v.En },
                IsActive = true,
                Extra = (Dictionary<string, object>?)null,
            }).ToList(),
        });
        await session.SaveChangesAsync();
    }

    #region GetAllAsync

    [Fact]
    public async Task GetAllAsync_returns_one_item_per_discovered_lookup()
    {
        _discovery.GetAllLookupReferences().Returns(new[] { TransientInfo(), DynamicInfo() });

        var service = CreateService();
        var items = (await service.GetAllAsync()).ToList();

        items.Should().HaveCount(2);
        items.Select(i => i.Name).Should().BeEquivalentTo([nameof(LDSTestStatus), "CarBrand"]);
    }

    [Fact]
    public async Task GetAllAsync_transient_value_count_comes_from_static_Items_property()
    {
        // LDSTestStatus.Items has 2 entries (defined in LookupReferenceDiscoveryServiceTests.cs).
        _discovery.GetAllLookupReferences().Returns(new[] { TransientInfo() });

        var service = CreateService();
        var items = (await service.GetAllAsync()).ToList();

        items.Should().ContainSingle().Which.ValueCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAllAsync_dynamic_value_count_comes_from_persisted_document()
    {
        await SeedDynamicDocAsync("CarBrand", ("BMW", "BMW"), ("Audi", "Audi"), ("Tesla", "Tesla"));
        _discovery.GetAllLookupReferences().Returns(new[] { DynamicInfo("CarBrand") });

        var service = CreateService();
        var items = (await service.GetAllAsync()).ToList();

        items.Should().ContainSingle().Which.ValueCount.Should().Be(3);
    }

    [Fact]
    public async Task GetAllAsync_dynamic_value_count_is_zero_when_no_document_exists()
    {
        _discovery.GetAllLookupReferences().Returns(new[] { DynamicInfo("Empty") });

        var service = CreateService();
        var items = (await service.GetAllAsync()).ToList();

        items.Should().ContainSingle().Which.ValueCount.Should().Be(0);
    }

    #endregion

    #region GetAsync

    [Fact]
    public async Task GetAsync_returns_null_for_unknown_lookup()
    {
        _discovery.GetLookupReference("NotReal").Returns((LookupReferenceInfo?)null);

        var service = CreateService();

        (await service.GetAsync("NotReal")).Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_returns_transient_values_from_static_Items()
    {
        _discovery.GetLookupReference(nameof(LDSTestStatus)).Returns(TransientInfo());

        var service = CreateService();
        var dto = await service.GetAsync(nameof(LDSTestStatus));

        dto.Should().NotBeNull();
        dto!.IsTransient.Should().BeTrue();
        dto.Values.Should().HaveCount(2);
        dto.Values.Select(v => v.Key).Should().BeEquivalentTo(["1", "2"]);
    }

    [Fact]
    public async Task GetAsync_returns_dynamic_values_from_persisted_document()
    {
        await SeedDynamicDocAsync("CarBrand", ("BMW", "BMW"), ("Audi", "Audi"));
        _discovery.GetLookupReference("CarBrand").Returns(DynamicInfo("CarBrand"));

        var service = CreateService();
        var dto = await service.GetAsync("CarBrand");

        dto!.IsTransient.Should().BeFalse();
        dto.Values.Select(v => v.Key).Should().BeEquivalentTo(["BMW", "Audi"]);
    }

    [Fact]
    public async Task GetAsync_returns_empty_values_for_dynamic_lookup_with_no_document()
    {
        _discovery.GetLookupReference("Empty").Returns(DynamicInfo("Empty"));

        var service = CreateService();
        var dto = await service.GetAsync("Empty");

        dto.Should().NotBeNull();
        dto!.Values.Should().BeEmpty();
    }

    #endregion

    #region AddValueAsync

    [Fact]
    public async Task AddValueAsync_throws_when_lookup_is_unknown()
    {
        _discovery.GetLookupReference("Nope").Returns((LookupReferenceInfo?)null);

        var service = CreateService();

        var act = async () => await service.AddValueAsync("Nope", NewValue("k", "v"));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }

    [Fact]
    public async Task AddValueAsync_throws_when_lookup_is_transient()
    {
        _discovery.GetLookupReference(nameof(LDSTestStatus)).Returns(TransientInfo());

        var service = CreateService();

        var act = async () => await service.AddValueAsync(nameof(LDSTestStatus), NewValue("3", "Pending"));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*transient*");
    }

    [Fact]
    public async Task AddValueAsync_creates_document_when_missing_and_persists_value()
    {
        _discovery.GetLookupReference("CarBrand").Returns(DynamicInfo("CarBrand"));

        var service = CreateService();
        await service.AddValueAsync("CarBrand", NewValue("BMW", "BMW"));

        // Verify by re-loading via the service.
        var dto = await service.GetAsync("CarBrand");
        dto!.Values.Should().ContainSingle().Which.Key.Should().Be("BMW");
    }

    [Fact]
    public async Task AddValueAsync_throws_on_duplicate_key()
    {
        _discovery.GetLookupReference("CarBrand").Returns(DynamicInfo("CarBrand"));

        var service = CreateService();
        await service.AddValueAsync("CarBrand", NewValue("BMW", "BMW"));

        var act = async () => await service.AddValueAsync("CarBrand", NewValue("BMW", "BMW Again"));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*BMW*already*");
    }

    #endregion

    #region UpdateValueAsync

    [Fact]
    public async Task UpdateValueAsync_throws_when_lookup_unknown()
    {
        _discovery.GetLookupReference("Nope").Returns((LookupReferenceInfo?)null);

        var service = CreateService();
        var act = async () => await service.UpdateValueAsync("Nope", "x", NewValue("x", "v"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }

    [Fact]
    public async Task UpdateValueAsync_throws_when_lookup_is_transient()
    {
        _discovery.GetLookupReference(nameof(LDSTestStatus)).Returns(TransientInfo());

        var service = CreateService();
        var act = async () => await service.UpdateValueAsync(nameof(LDSTestStatus), "1", NewValue("1", "X"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*transient*");
    }

    [Fact]
    public async Task UpdateValueAsync_throws_when_document_missing()
    {
        _discovery.GetLookupReference("Empty").Returns(DynamicInfo("Empty"));

        var service = CreateService();
        var act = async () => await service.UpdateValueAsync("Empty", "x", NewValue("x", "v"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no values*");
    }

    [Fact]
    public async Task UpdateValueAsync_throws_when_key_missing()
    {
        await SeedDynamicDocAsync("CarBrand", ("BMW", "BMW"));
        _discovery.GetLookupReference("CarBrand").Returns(DynamicInfo("CarBrand"));

        var service = CreateService();
        var act = async () => await service.UpdateValueAsync("CarBrand", "Audi", NewValue("Audi", "Audi"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Audi*not found*");
    }

    [Fact]
    public async Task UpdateValueAsync_persists_translations_and_active_flag_changes()
    {
        await SeedDynamicDocAsync("CarBrand", ("BMW", "BMW"));
        _discovery.GetLookupReference("CarBrand").Returns(DynamicInfo("CarBrand"));

        var service = CreateService();
        var update = new LookupReferenceValueDto
        {
            Key = "BMW",
            Values = TranslatedString.Create("Bayerische Motoren Werke"),
            IsActive = false,
        };

        await service.UpdateValueAsync("CarBrand", "BMW", update);

        var dto = await service.GetAsync("CarBrand");
        var bmw = dto!.Values.Single();
        bmw.Values.GetDefaultValue().Should().Be("Bayerische Motoren Werke");
        bmw.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateValueAsync_supports_renaming_key_when_new_key_is_unique()
    {
        await SeedDynamicDocAsync("CarBrand", ("BMW", "BMW"), ("Audi", "Audi"));
        _discovery.GetLookupReference("CarBrand").Returns(DynamicInfo("CarBrand"));

        var service = CreateService();
        await service.UpdateValueAsync("CarBrand", "BMW", new LookupReferenceValueDto
        {
            Key = "BMW-AG",
            Values = TranslatedString.Create("BMW AG"),
        });

        var dto = await service.GetAsync("CarBrand");
        dto!.Values.Select(v => v.Key).Should().BeEquivalentTo(["BMW-AG", "Audi"]);
    }

    [Fact]
    public async Task UpdateValueAsync_throws_when_renaming_to_an_existing_key()
    {
        await SeedDynamicDocAsync("CarBrand", ("BMW", "BMW"), ("Audi", "Audi"));
        _discovery.GetLookupReference("CarBrand").Returns(DynamicInfo("CarBrand"));

        var service = CreateService();
        var act = async () => await service.UpdateValueAsync("CarBrand", "BMW",
            new LookupReferenceValueDto { Key = "Audi", Values = TranslatedString.Create("BMW") });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Audi*already*");
    }

    #endregion

    #region DeleteValueAsync

    [Fact]
    public async Task DeleteValueAsync_throws_when_lookup_unknown()
    {
        _discovery.GetLookupReference("Nope").Returns((LookupReferenceInfo?)null);

        var service = CreateService();
        var act = async () => await service.DeleteValueAsync("Nope", "x");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }

    [Fact]
    public async Task DeleteValueAsync_throws_when_lookup_is_transient()
    {
        _discovery.GetLookupReference(nameof(LDSTestStatus)).Returns(TransientInfo());

        var service = CreateService();
        var act = async () => await service.DeleteValueAsync(nameof(LDSTestStatus), "1");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*transient*");
    }

    [Fact]
    public async Task DeleteValueAsync_throws_when_document_missing()
    {
        _discovery.GetLookupReference("Empty").Returns(DynamicInfo("Empty"));

        var service = CreateService();
        var act = async () => await service.DeleteValueAsync("Empty", "x");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no values*");
    }

    [Fact]
    public async Task DeleteValueAsync_throws_when_key_missing()
    {
        await SeedDynamicDocAsync("CarBrand", ("BMW", "BMW"));
        _discovery.GetLookupReference("CarBrand").Returns(DynamicInfo("CarBrand"));

        var service = CreateService();
        var act = async () => await service.DeleteValueAsync("CarBrand", "Tesla");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Tesla*not found*");
    }

    [Fact]
    public async Task DeleteValueAsync_removes_value_from_document()
    {
        await SeedDynamicDocAsync("CarBrand", ("BMW", "BMW"), ("Audi", "Audi"));
        _discovery.GetLookupReference("CarBrand").Returns(DynamicInfo("CarBrand"));

        var service = CreateService();
        await service.DeleteValueAsync("CarBrand", "BMW");

        var dto = await service.GetAsync("CarBrand");
        dto!.Values.Select(v => v.Key).Should().BeEquivalentTo(["Audi"]);
    }

    #endregion
}
