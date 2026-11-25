using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MintPlayer.Spark.Relational.Model;

namespace MintPlayer.Spark.Relational.Migrations.Framework;

/// <summary>
/// The change feed's framework schema (#536 M4e, D6): the <c>Tombstones</c> table, and what the dialect's stamp needs
/// besides (PostgreSQL's trigger function, which every table's stamp trigger calls). Hand-written, as 0001.
/// </summary>
[DbContext(typeof(SparkFrameworkDbContext))]
[Migration("20261125000002_SparkFramework0002")]
public sealed class SparkFramework0002 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var dialect = SparkRelationalDialect.ForProvider(migrationBuilder.ActiveProvider);
        const int key = SparkFrameworkSchema.KeyLength;
        const string schema = SparkFrameworkSchema.Schema;

        if (dialect.StampFrameworkSql is { } sql)
            migrationBuilder.Sql(sql);

        migrationBuilder.CreateTable(
            name: SparkFrameworkSchema.Tombstones,
            schema: schema,
            columns: t => new
            {
                Id = t.Column<Guid>(),
                Collection = t.Column<string>(maxLength: key, collation: dialect.KeyCollation),
                DocumentId = t.Column<string>(maxLength: key, collation: dialect.KeyCollation),
                Stamp = t.Column<ulong>(type: dialect.StampColumnType, defaultValueSql: dialect.StampDefaultSql),
                DeletedAt = t.Column<DateTimeOffset>(),
            },
            constraints: t => t.PrimaryKey("PK_Tombstones", x => x.Id));

        migrationBuilder.CreateIndex(name: "IX_Tombstones_Stamp", schema: schema, table: SparkFrameworkSchema.Tombstones, column: "Stamp");
        migrationBuilder.CreateIndex(name: "IX_Tombstones_DeletedAt", schema: schema, table: SparkFrameworkSchema.Tombstones, column: "DeletedAt");
    }
}
