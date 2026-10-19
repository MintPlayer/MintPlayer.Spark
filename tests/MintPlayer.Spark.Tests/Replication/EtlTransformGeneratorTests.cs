using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Services;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// X20 item 7 (warehouse Q-W17): the owner turns a contract's C# projection into its ETL transform.
/// Field selections and renames translate; anything JavaScript would not compute identically is
/// refused at startup, naming the contract and the member.
/// </summary>
public class EtlTransformGeneratorTests
{
    public class Address
    {
        public string Street { get; set; } = "";
        public Geo? Geo { get; set; }
    }

    public class Geo
    {
        public double Lat { get; set; }
    }

    public class Vehicle
    {
        public string? Id { get; set; }
        public string LicensePlate { get; set; } = "";
        public string Model { get; set; } = "";
        public int Year { get; set; }
        public int? Mileage { get; set; }
        public Address? Address { get; set; }
    }

    public class CarV1
    {
        public string? Id { get; set; }
        public string Plate { get; set; } = "";
        public string Model { get; set; } = "";
        public int? Year { get; set; }
        public string? Street { get; set; }
        public double? Lat { get; set; }
        public int? Mileage { get; set; }
    }

    public record CarRecord(string Plate, string Model);

    private static ReplicationContract Contract<TContract>(Expression<Func<Vehicle, TContract>> projection)
        => new SparkReplicationOptions { ModuleName = "Fleet", ModuleUrl = "https://fleet.test" }
            .Publish("cars", 1, projection)
            .Contracts.Single();

    [Fact]
    public void A_field_selection_with_renames_becomes_a_loadTo_call()
    {
        var fields = EtlTransformGenerator.Translate(Contract(v => new CarV1 { Plate = v.LicensePlate, Model = v.Model }));

        EtlTransformGenerator.Script(fields, "Cars").ReplaceLineEndings("\n").Should().Be("""
            loadToCars({
                Plate: this.LicensePlate,
                Model: this.Model
            });

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_nested_path_is_read_null_safely()
    {
        var fields = EtlTransformGenerator.Translate(Contract(v => new CarV1 { Street = v.Address!.Street, Lat = v.Address!.Geo!.Lat }));

        var script = EtlTransformGenerator.Script(fields, "Cars");

        script.Should().Contain("Street: (this.Address == null ? null : this.Address.Street)");
        script.Should().Contain("Lat: (this.Address == null ? null : (this.Address.Geo == null ? null : this.Address.Geo.Lat))");
    }

    [Fact]
    public void A_nullable_lift_is_a_plain_selection()
    {
        var fields = EtlTransformGenerator.Translate(Contract(v => new CarV1 { Year = v.Year, Mileage = v.Mileage }));

        fields.Select(f => (f.Target, string.Join(".", f.Path))).Should().Equal(("Year", "Year"), ("Mileage", "Mileage"));
    }

    [Fact]
    public void A_positional_record_constructor_maps_its_arguments_to_the_properties_they_initialize()
    {
        var fields = EtlTransformGenerator.Translate(Contract(v => new CarRecord(v.LicensePlate, v.Model)));

        fields.Select(f => (f.Target, string.Join(".", f.Path))).Should().Equal(("Plate", "LicensePlate"), ("Model", "Model"));
    }

    [Fact]
    public void The_id_is_not_a_body_field()
    {
        var fields = EtlTransformGenerator.Translate(Contract(v => new CarV1 { Id = v.Id, Plate = v.LicensePlate }));

        fields.Select(f => f.Target).Should().Equal("Plate");
    }

    public static TheoryData<string, Expression<Func<Vehicle, CarV1>>> Complex => new()
    {
        { "Plate", v => new CarV1 { Plate = v.LicensePlate.ToUpperInvariant() } },
        { "Model", v => new CarV1 { Model = v.Model + " (car)" } },
        { "Year", v => new CarV1 { Year = v.Year > 2000 ? (int?)v.Year : null } },
        { "Model", v => new CarV1 { Model = "constant" } },
        { "Mileage", v => new CarV1 { Mileage = v.Mileage!.Value } },
    };

    [Theory]
    [MemberData(nameof(Complex))]
    public void A_projection_that_needs_code_is_refused_naming_the_contract_and_the_member(string member, Expression<Func<Vehicle, CarV1>> projection)
    {
        var contract = Contract(projection);

        var act = () => EtlTransformGenerator.Translate(contract);

        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains("'cars' v1") && e.Message.Contains($"member '{member}'"));
    }

    /// <summary>
    /// The refusal happens at startup: building the owner's catalog (which <c>UseSparkReplication</c>
    /// resolves before anything else) translates every published projection.
    /// </summary>
    [Fact]
    public void Building_the_catalog_refuses_a_complex_projection()
    {
        var options = new SparkReplicationOptions { ModuleName = "Fleet", ModuleUrl = "https://fleet.test" };
        options.Publish<Vehicle, CarV1>("cars", 1, v => new CarV1 { Plate = v.LicensePlate.Trim() });
        var store = Substitute.For<IDocumentStore>();
        store.Conventions.Returns(new DocumentConventions());

        var act = () => new ReplicationContractCatalog(Options.Create(options), store);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'cars' v1*member 'Plate'*calls Trim()*");
    }

    [Fact]
    public void A_captured_variable_is_refused()
    {
        var plate = "AB-123";
        var contract = Contract(v => new CarV1 { Plate = plate });

        var act = () => EtlTransformGenerator.Translate(contract);

        act.Should().Throw<InvalidOperationException>().WithMessage("*member 'Plate'*captured variable*");
    }

    [Fact]
    public void A_projection_that_constructs_nothing_is_refused()
    {
        var contract = Contract(v => v.Address!);

        var act = () => EtlTransformGenerator.Translate(contract);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'cars' v1*does not construct the contract record*");
    }

    /// <summary>
    /// The consumer names the target collection, and it becomes part of code that runs on the owner's
    /// database. Only a plain identifier may get there.
    /// </summary>
    [Theory]
    [InlineData("Cars({}); load('SparkUsers/1'); loadToCars")]
    [InlineData("Cars-v2")]
    [InlineData("")]
    [InlineData("Cars\n")]
    public void A_target_collection_that_is_not_a_plain_identifier_is_refused(string targetCollection)
    {
        var fields = EtlTransformGenerator.Translate(Contract(v => new CarV1 { Plate = v.LicensePlate }));

        var act = () => EtlTransformGenerator.Script(fields, targetCollection);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("Cars")]
    [InlineData("cars")]
    [InlineData("fulfilment-orders-")]
    [InlineData("1cars")]
    public void Publish_refuses_a_name_that_is_not_lower_kebab(string name)
    {
        var options = new SparkReplicationOptions { ModuleName = "Fleet", ModuleUrl = "https://fleet.test" };

        var act = () => options.Publish<Vehicle, CarV1>(name, 1, v => new CarV1 { Plate = v.LicensePlate });

        if (name == "cars")
            act.Should().NotThrow();
        else
            act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Publish_refuses_the_same_contract_version_twice()
    {
        var options = new SparkReplicationOptions { ModuleName = "Fleet", ModuleUrl = "https://fleet.test" };
        options.Publish<Vehicle, CarV1>("cars", 1, v => new CarV1 { Plate = v.LicensePlate });

        var act = () => options.Publish<Vehicle, CarV1>("cars", 1, v => new CarV1 { Model = v.Model });

        act.Should().Throw<ArgumentException>().WithMessage("*'cars' v1*twice*");
        options.Invoking(o => o.Publish<Vehicle, CarV1>("cars", 2, v => new CarV1 { Model = v.Model }))
            .Should().NotThrow("versions run side by side");
    }
}
