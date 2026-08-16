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

    [Fact]
    public void An_attribute_absent_from_the_model_is_refused_even_if_the_CLR_property_exists()
    {
        // The second line of defence behind model synchronization, and what protects the inbound
        // replication path: once [IgnoreProperty] drops the attribute from the model, a posted
        // value for it is refused because the schema does not declare it — regardless of the
        // property still being perfectly writable on the CLR type.
        var def = new EntityTypeDefinition
        {
            Id = Guid.Parse("dddddddd-4444-4444-4444-444444444444"),
            Name = "Holder",
            ClrType = typeof(IP_Holder).FullName!,
            Attributes = [new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string" }],
        };
        _modelLoader.GetEntityTypeByClrType(typeof(IP_Holder).FullName!).Returns(def);

        var holder = new IP_Holder();
        var po = PoWith(("Name", "visible", "string"), ("Secret", "injected", "string"));

        _mapper.PopulateObjectValues(po, holder);

        holder.Name.Should().Be("visible");
        holder.Secret.Should().BeNull("the model does not declare it, so the schema gate refuses the write");
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
        public string? Name { get; set; }
        public IP_Address? Address { get; set; }
        public List<IP_Address>? Addresses { get; set; }

        [IgnoreProperty]
        public string? Secret { get; set; }
    }

    private sealed class IP_Address
    {
        public string? Street { get; set; }

        [IgnoreProperty]
        public string? AuditNote { get; set; }
    }
}
