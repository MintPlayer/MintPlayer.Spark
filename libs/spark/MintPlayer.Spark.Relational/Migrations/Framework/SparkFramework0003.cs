using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MintPlayer.Spark.Relational.Model;

namespace MintPlayer.Spark.Relational.Migrations.Framework;

/// <summary>
/// Revisions (#536 S8, PRD O12): the <c>Revisions</c> table, always present and empty until a collection keeps them.
/// Hand-written, as 0001.
/// </summary>
[DbContext(typeof(SparkFrameworkDbContext))]
[Migration("20261126000003_SparkFramework0003")]
public sealed class SparkFramework0003 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var dialect = SparkRelationalDialect.ForProvider(migrationBuilder.ActiveProvider);
        const int key = SparkFrameworkSchema.KeyLength;
        const string schema = SparkFrameworkSchema.Schema;

        migrationBuilder.CreateTable(
            name: SparkFrameworkSchema.Revisions,
            schema: schema,
            columns: t => new
            {
                Id = t.Column<Guid>(),
                Collection = t.Column<string>(maxLength: key, collation: dialect.KeyCollation),
                DocumentId = t.Column<string>(maxLength: key, collation: dialect.KeyCollation),
                Version = t.Column<long>(),
                At = t.Column<DateTimeOffset>(),
                ClrType = t.Column<string>(maxLength: 1024, nullable: true),
                Json = t.Column<string>(nullable: true),
            },
            constraints: t => t.PrimaryKey("PK_Revisions", x => x.Id));

        migrationBuilder.CreateIndex(name: "IX_Revisions_Collection_DocumentId_At", schema: schema, table: SparkFrameworkSchema.Revisions,
            columns: ["Collection", "DocumentId", "At"]);
    }
}
