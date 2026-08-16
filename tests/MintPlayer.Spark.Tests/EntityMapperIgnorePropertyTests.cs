using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;
using System.Text.Json;

namespace MintPlayer.Spark.Tests;

/// <summary>
/// #254 M6 — the raw-JSON write path. When a client posts a JSON object for a complex property,
/// <c>SetPropertyValue</c> hands the payload straight to System.Text.Json: the model describes the
/// attribute being written, but nothing gates the members of the type behind it. Without the
/// contract-level exclusion, <c>[IgnoreProperty]</c> on an embedded type would be advisory on the
/// read path and simply not enforced on the write path.
/// </summary>
public class EntityMapperIgnorePropertyTests
{
    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();
    private readonly EntityMapper _mapper;

    public EntityMapperIgnorePropertyTests()
    {
        var guard = Substitute.For<ICollectionGuard>();
        guard.BelongsToAuthorizedCollection(default!, default!, default!).ReturnsForAnyArgs(true);
        _mapper = new EntityMapper(_modelLoader, collectionGuard: guard);
    }

    [Fact]
    public void Raw_json_object_cannot_write_an_ignored_member_of_an_embedded_type()
    {
        var holder = new IP_Holder();
        var po = PoWith(("Address", Json("""{"Street":"Main 1","AuditNote":"injected"}"""), "string"));

        _mapper.PopulateObjectValues(po, holder);

        holder.Address.Should().NotBeNull();
        holder.Address!.Street.Should().Be("Main 1", "unignored members still round-trip");
        holder.Address.AuditNote.Should().BeNull("[IgnoreProperty] is dropped from the JSON contract");
    }

    [Fact]
    public void Raw_json_array_cannot_write_an_ignored_member_of_an_embedded_type()
    {
        var holder = new IP_Holder();
        var po = PoWith(("Addresses", Json("""[{"Street":"Main 1","AuditNote":"injected"}]"""), "string"));

        _mapper.PopulateObjectValues(po, holder);

        holder.Addresses.Should().ContainSingle();
        holder.Addresses![0].Street.Should().Be("Main 1");
        holder.Addresses[0].AuditNote.Should().BeNull();
    }

    private static JsonElement Json(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private static PersistentObject PoWith(params (string Name, object? Value, string DataType)[] attrs)
        => new()
        {
            Name = "TestPO",
            ObjectTypeId = Guid.Parse("cccccccc-3333-3333-3333-333333333333"),
            Attributes = attrs.Select(a => new PersistentObjectAttribute
            {
                Name = a.Name,
                Value = a.Value,
                DataType = a.DataType,
            }).ToArray(),
        };

    private sealed class IP_Holder
    {
        public string? Id { get; set; }
        public IP_Address? Address { get; set; }
        public List<IP_Address>? Addresses { get; set; }
    }

    private sealed class IP_Address
    {
        public string? Street { get; set; }

        [IgnoreProperty]
        public string? AuditNote { get; set; }
    }
}
