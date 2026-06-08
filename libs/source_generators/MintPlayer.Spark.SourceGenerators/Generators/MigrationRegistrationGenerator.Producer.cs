using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;

namespace MintPlayer.Spark.SourceGenerators.Generators;

public class MigrationRegistrationProducer : Producer
{
    private readonly IEnumerable<MigrationClassInfo> migrationClasses;
    private readonly bool knowsMigrations;

    public MigrationRegistrationProducer(
        IEnumerable<MigrationClassInfo> migrationClasses,
        bool knowsMigrations,
        string rootNamespace)
        : base(rootNamespace, "SparkMigrationRegistrations.g.cs")
    {
        this.migrationClasses = migrationClasses;
        this.knowsMigrations = knowsMigrations;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        var migrationList = migrationClasses.ToList();

        // Don't generate if no migrations found or the project doesn't reference MintPlayer.Spark.Migrations
        if (!knowsMigrations || migrationList.Count == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();

        using (writer.OpenBlock($"namespace {RootNamespace}"))
        {
            using (writer.OpenBlock("internal static class SparkMigrationsBuilderExtensions"))
            {
                using (writer.OpenBlock("internal static global::MintPlayer.Spark.Abstractions.Builder.ISparkBuilder AddMigrations(this global::MintPlayer.Spark.Abstractions.Builder.ISparkBuilder builder)"))
                {
                    using (writer.OpenBlock("global::MintPlayer.Spark.Migrations.SparkMigrationsExtensions.AddMigrations(builder, migrations =>"))
                    {
                        foreach (var migrationClass in migrationList)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            writer.WriteLine($"migrations.AddMigration<{migrationClass.MigrationTypeName}>();");
                        }
                    }
                    writer.WriteLine(");");
                    writer.WriteLine("return builder;");
                }
            }
        }
    }
}
