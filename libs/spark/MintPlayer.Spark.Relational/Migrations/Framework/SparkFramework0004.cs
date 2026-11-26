using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MintPlayer.Spark.Relational.Model;

namespace MintPlayer.Spark.Relational.Migrations.Framework;

/// <summary>
/// The collation of ordinary text columns (#536 O20), where the dialect needs one: PostgreSQL's case-insensitive ICU
/// collation. The framework's migrations run before the app's, so the app's columns can name it. Hand-written, as 0001.
/// </summary>
[DbContext(typeof(SparkFrameworkDbContext))]
[Migration("20261126000004_SparkFramework0004")]
public sealed class SparkFramework0004 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (SparkRelationalDialect.ForProvider(migrationBuilder.ActiveProvider).IgnoreCaseFrameworkSql is { } sql)
            migrationBuilder.Sql(sql);
    }
}
