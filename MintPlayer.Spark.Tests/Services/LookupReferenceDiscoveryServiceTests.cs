using System.Diagnostics.CodeAnalysis;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// LookupReferenceDiscoveryService scans loaded assemblies for
/// <see cref="TransientLookupReference{TKey}"/> and <see cref="DynamicLookupReference{TValue}"/>
/// subclasses on construction. The map it builds drives every lookup-reference dropdown
/// in the UI — a regression silently empties dropdowns at runtime, with no compile-time
/// signal.
/// </summary>
public class LookupReferenceDiscoveryServiceTests
{
    [Fact]
    public void Discovers_TransientLookupReference_subclasses_with_KeyType_set()
    {
        var service = new LookupReferenceDiscoveryService();

        var info = service.GetLookupReference(nameof(LDSTestStatus));

        info.Should().NotBeNull();
        info!.IsTransient.Should().BeTrue();
        info.KeyType.Should().Be<int>();
        info.Type.Should().Be<LDSTestStatus>();
    }

    [Fact]
    public void Discovers_DynamicLookupReference_subclasses_with_ValueType_set()
    {
        var service = new LookupReferenceDiscoveryService();

        var info = service.GetLookupReference(nameof(LDSTestBrand));

        info.Should().NotBeNull();
        info!.IsTransient.Should().BeFalse();
        info.ValueType.Should().Be<EmptyValue>();
    }

    [Fact]
    public void GetLookupReference_is_case_insensitive()
    {
        var service = new LookupReferenceDiscoveryService();

        service.GetLookupReference("ldsteststatus").Should().NotBeNull();
        service.GetLookupReference("LDSTESTSTATUS").Should().NotBeNull();
    }

    [Fact]
    public void GetLookupReference_returns_null_for_unknown_names()
    {
        var service = new LookupReferenceDiscoveryService();

        service.GetLookupReference("__never_registered__").Should().BeNull();
    }

    [Fact]
    public void GetAllLookupReferences_includes_both_transient_and_dynamic_fixtures()
    {
        var service = new LookupReferenceDiscoveryService();

        var all = service.GetAllLookupReferences();

        all.Select(i => i.Name).Should().Contain(nameof(LDSTestStatus))
            .And.Contain(nameof(LDSTestBrand));
    }

    [Fact]
    public void IsTransient_returns_true_only_for_transient_lookups()
    {
        var service = new LookupReferenceDiscoveryService();

        service.IsTransient(nameof(LDSTestStatus)).Should().BeTrue();
        service.IsTransient(nameof(LDSTestBrand)).Should().BeFalse();
        service.IsTransient("nonexistent").Should().BeFalse();
    }

    [Fact]
    public void IsDynamic_returns_true_only_for_dynamic_lookups()
    {
        var service = new LookupReferenceDiscoveryService();

        service.IsDynamic(nameof(LDSTestBrand)).Should().BeTrue();
        service.IsDynamic(nameof(LDSTestStatus)).Should().BeFalse();
        service.IsDynamic("nonexistent").Should().BeFalse();
    }

    [Fact]
    public void DisplayType_for_transient_is_read_from_one_of_the_static_Items()
    {
        var service = new LookupReferenceDiscoveryService();

        service.GetLookupReference(nameof(LDSTestStatus))!.DisplayType
            .Should().Be(ELookupDisplayType.Modal);
    }

    [Fact]
    public void DisplayType_for_dynamic_is_read_from_a_temp_instance()
    {
        var service = new LookupReferenceDiscoveryService();

        service.GetLookupReference(nameof(LDSTestBrand))!.DisplayType
            .Should().Be(ELookupDisplayType.Dropdown);
    }
}

// Top-level fixtures so AppDomain.CurrentDomain.GetAssemblies() picks them up.

public class LDSTestStatus : TransientLookupReference<int>
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Modal;

    public static IReadOnlyList<LDSTestStatus> Items { get; } = new List<LDSTestStatus>
    {
        new() { Key = 1, Values = TranslatedString.Create("Active") },
        new() { Key = 2, Values = TranslatedString.Create("Inactive") },
    };
}

public class LDSTestBrand : DynamicLookupReference
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    [SetsRequiredMembers]
    public LDSTestBrand()
    {
        Name = nameof(LDSTestBrand);
    }
}
