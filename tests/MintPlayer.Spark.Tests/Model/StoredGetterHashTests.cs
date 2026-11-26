using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// #536 O13: <c>modelHashes.json</c> records each stored getter's formula (the hash <c>StoredGetterGenerator</c> puts in
/// <see cref="SparkStoredGetterAttribute"/>), so a changed formula moves the model hash, the drift names the getter,
/// and synchronize tells the developer that saved rows keep the old value.
/// </summary>
public class StoredGetterHashTests : IDisposable
{
    private static readonly string FormulaA = new('a', 64);
    private static readonly string FormulaB = new('b', 64);

    private readonly string contentRoot = Path.Combine(Path.GetTempPath(), "spark-storedgetters-" + Guid.NewGuid().ToString("N"));
    private readonly IIndexCatalog indexCatalog = Substitute.For<IIndexCatalog>();

    public StoredGetterHashTests() => Directory.CreateDirectory(contentRoot);

    public void Dispose()
    {
        try { Directory.Delete(contentRoot, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private ModelHashFile Hashes<TContext>() => ModelSynchronizer.BuildModelHashes(typeof(TContext), indexCatalog, contentRoot);

    [Fact]
    public void The_hash_file_records_each_stored_getter_with_its_formula_including_a_base_types()
    {
        var hashes = Hashes<FormulaAContext>();

        hashes.StoredGetters.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["StoredProbe.Label"] = FormulaA,
            ["StoredProbe.Inherited"] = FormulaB,
        });
    }

    [Fact]
    public void A_model_without_stored_getters_records_none_and_keeps_its_hash()
    {
        var hashes = Hashes<HashProbeContext>();

        hashes.StoredGetters.Should().BeNull();
        hashes.ModelHash.Should().Be(SparkModelShape.ComputeModelHash(
            hashes.Entities, hashes.ContextRoots, hashes.ModelFiles,
            hashes.ConfigFiles is { } config ? ModelHashFile.CombineFileHashes(config) : null,
            ModelHashFile.CombineLayerHashes(hashes.Layers ?? [])));
    }

    [Fact]
    public void A_changed_formula_moves_the_model_hash_and_the_drift_names_the_getter()
    {
        var before = Hashes<FormulaAContext>();
        var after = Hashes<FormulaBContext>();

        after.ModelHash.Should().NotBe(before.ModelHash);
        ModelHashVerifier.DescribeDrift(before, after).Should()
            .Contain(line => line.Contains("stored getter StoredProbe.Label") && line.Contains("formula changed"));
    }

    [Fact]
    public void Synchronize_names_the_changed_and_the_new_stored_getters()
    {
        var before = Hashes<FormulaAContext>();
        var after = Hashes<FormulaBContext>();
        before.StoredGetters!.Remove("StoredProbe.Inherited");

        var report = ModelSynchronizer.DescribeStoredGetterChanges(before, after);

        report.Should().Contain(line => line.Contains("StoredProbe.Label") && line.Contains("formula changed"));
        report.Should().Contain(line => line.Contains("StoredProbe.Inherited") && line.Contains("new"));
        report.Should().Contain(line => line.Contains("ISparkMigration"), "recomputing saved rows is a data migration");
        ModelSynchronizer.DescribeStoredGetterChanges(after, after).Should().BeEmpty();
        ModelSynchronizer.DescribeStoredGetterChanges(null, after).Should().BeEmpty("a first synchronize has nothing to compare");
    }

    [SparkStoredGetter("Inherited", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public abstract class StoredProbeBase
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Inherited => Name;
    }

    public static class A
    {
        [SparkStoredGetter("Label", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
        public sealed class StoredProbe : StoredProbeBase
        {
            public string? Label => Name;
        }
    }

    public static class B
    {
        [SparkStoredGetter("Label", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
        public sealed class StoredProbe : StoredProbeBase
        {
            public string? Label => Name + "!";
        }
    }

    public sealed class FormulaAContext : SparkContext
    {
        public IQueryable<A.StoredProbe> Probes => Session.Query<A.StoredProbe>();
    }

    public sealed class FormulaBContext : SparkContext
    {
        public IQueryable<B.StoredProbe> Probes => Session.Query<B.StoredProbe>();
    }
}
