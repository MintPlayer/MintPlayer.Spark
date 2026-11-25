using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MintPlayer.Spark.Relational.Model;

namespace MintPlayer.Spark.Relational.Migrations.Framework;

/// <summary>
/// The framework schema's first version (#536 M4b/M4c): reservations, atomic values and the Data Protection key ring.
/// Hand-written: a framework migration is never scaffolded, and must stay valid inside the PostgreSQL idempotent
/// script's <c>DO</c> blocks (S19).
/// </summary>
[DbContext(typeof(SparkFrameworkDbContext))]
[Migration("20261125000001_SparkFramework0001")]
public sealed class SparkFramework0001 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var collation = SparkRelationalDialect.ForProvider(migrationBuilder.ActiveProvider).KeyCollation;
        const int key = SparkFrameworkSchema.KeyLength;
        const string schema = SparkFrameworkSchema.Schema;

        migrationBuilder.EnsureSchema(schema);

        migrationBuilder.CreateTable(
            name: SparkFrameworkSchema.Reservations,
            schema: schema,
            columns: t => new
            {
                Key = t.Column<string>(maxLength: key, collation: collation),
                OwnerId = t.Column<string>(maxLength: key, collation: collation),
                Version = t.Column<long>(),
            },
            constraints: t => t.PrimaryKey("PK_Reservations", x => x.Key));

        migrationBuilder.CreateTable(
            name: SparkFrameworkSchema.AtomicValues,
            schema: schema,
            columns: t => new
            {
                Key = t.Column<string>(maxLength: key, collation: collation),
                Value = t.Column<string>(),
                Version = t.Column<long>(),
            },
            constraints: t => t.PrimaryKey("PK_AtomicValues", x => x.Key));

        migrationBuilder.CreateTable(
            name: SparkFrameworkSchema.DataProtectionKeys,
            schema: schema,
            columns: t => new
            {
                Name = t.Column<string>(maxLength: key, collation: collation),
                Xml = t.Column<string>(),
            },
            constraints: t => t.PrimaryKey("PK_DataProtectionKeys", x => x.Name));
    }
}
