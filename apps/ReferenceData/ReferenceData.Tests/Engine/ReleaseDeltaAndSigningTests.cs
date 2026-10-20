using System.Text;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Import;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Seed;
using static ReferenceData.Tests.Engine.RefFixtures;

namespace ReferenceData.Tests.Engine;

/// <summary>
/// Releases (PRD §4.6, §4.7; RS7, RS17): deltas reproduce the target release's digests, Ed25519 signatures round-trip
/// and refuse a tampered manifest or an unsigned release, and the import checkpoint survives a restart.
/// </summary>
public class ReleaseDeltaAndSigningTests
{
    /// <summary>Release A (the fixtures' build) and release B (one place renamed, one place added), with a delta A→B.</summary>
    private static async Task<(MemoryReleaseFileStore A, MemoryReleaseFileStore B, ReleaseContent ContentA)> TwoReleasesAsync(Func<byte[], byte[]>? sign = null)
    {
        var build = await BuildAsync();
        var a = build.Documents().Select(Clone).ToList();
        foreach (var document in a) Stamp(document, "A", "A");

        var b = a.Select(Clone).ToList();
        var renamed = b.OfType<RefPlace>().First(p => p.Id == "places/be/nis/45068");
        renamed.Population = 999_999;
        renamed.Hash = ReleaseCanonical.Hash(renamed);
        Stamp(renamed, "A", "B");
        var added = (RefPlace)Clone(renamed);
        added.Id = "places/be/nis/99999";
        added.Hash = ReleaseCanonical.Hash(added);
        Stamp(added, "B", "B");
        b.Add(added);

        var storeA = new MemoryReleaseFileStore();
        await ReleaseWriter.WriteAsync(storeA, new ReleaseManifest { Release = "A" }, a, CancellationToken.None);
        var storeB = new MemoryReleaseFileStore();
        await ReleaseWriter.WriteAsync(storeB, new ReleaseManifest { Release = "B", Previous = "A" }, b, CancellationToken.None,
            [("A", b.Where(d => (d.GetType().GetProperty("Release")!.GetValue(d) as ReleaseStamp)!.Changed == "B").ToList())], sign);
        return (storeA, storeB, await ReleaseReader.ReadAsync(storeA, CancellationToken.None));
    }

    [Fact]
    public async Task Applying_the_delta_to_release_A_gives_release_B_digests()
    {
        var (_, storeB, contentA) = await TwoReleasesAsync();
        var delta = await ReleaseReader.ReadAsync(storeB, CancellationToken.None, deltaFrom: "A");
        delta.DeltaFrom.Should().Be("A");
        delta.Documents.Count.Should().Be(2);

        var held = new Dictionary<string, object>(contentA.Documents, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, document) in delta.Documents) held[id] = document;

        foreach (var file in delta.Manifest.Files)
        {
            var local = ReleaseCanonical.Digest(held.Values
                .Where(d => ReleaseFormat.CollectionOf(d.GetType()).Name == file.Collection && ReleaseWriter.CountryOfDocument(d) == file.Country)
                .Select(d => (Id: (string)d.GetType().GetProperty("Id")!.GetValue(d)!, Hash: (string)d.GetType().GetProperty("Hash")!.GetValue(d)!)));
            local.Should().Be(file.Digest);
        }
    }

    [Fact]
    public async Task A_reader_without_a_matching_delta_reads_the_full_files()
    {
        var (_, storeB, _) = await TwoReleasesAsync();
        var full = await ReleaseReader.ReadAsync(storeB, CancellationToken.None, deltaFrom: "Z");
        full.DeltaFrom.Should().BeNull();
        full.Documents.Should().ContainKey("places/be/nis/99999");
    }

    [Fact]
    public async Task A_release_that_drops_an_id_is_refused()
    {
        var (_, storeB, contentA) = await TwoReleasesAsync();
        var b = await ReleaseReader.ReadAsync(storeB, CancellationToken.None);
        ReleaseReader.RefuseDroppedIds(contentA.Documents.Keys, b);
        new Action(() => ReleaseReader.RefuseDroppedIds([.. contentA.Documents.Keys, "places/be/nis/00001"], b)).Should().Throw<ReleaseRefusedException>();
    }

    [Fact]
    public async Task A_signed_release_verifies_and_a_tampered_manifest_is_refused()
    {
        var key = ReleaseSignatures.NewPrivateKey();
        var publicKey = ReleaseSignatures.PublicKeyOf(key);
        var (_, storeB, _) = await TwoReleasesAsync(manifest => ReleaseSignatures.Sign(key, manifest));

        (await ReleaseReader.ReadAsync(storeB, CancellationToken.None, ReleaseSignatures.Verifier(publicKey))).Manifest.Release.Should().Be("B");

        storeB.Files[ReleaseFormat.ManifestFile] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(storeB.Files[ReleaseFormat.ManifestFile]).Replace("\"B\"", "\"C\""));
        await new Func<Task>(() => ReleaseReader.ReadAsync(storeB, CancellationToken.None, ReleaseSignatures.Verifier(publicKey))).Should().ThrowAsync<ReleaseRefusedException>();
    }

    [Fact]
    public async Task Another_key_or_an_unsigned_release_is_refused_when_a_public_key_is_configured()
    {
        var key = ReleaseSignatures.NewPrivateKey();
        var (storeA, storeB, _) = await TwoReleasesAsync(manifest => ReleaseSignatures.Sign(key, manifest));
        var other = ReleaseSignatures.PublicKeyOf(ReleaseSignatures.NewPrivateKey());

        await new Func<Task>(() => ReleaseReader.ReadAsync(storeB, CancellationToken.None, ReleaseSignatures.Verifier(other))).Should().ThrowAsync<ReleaseRefusedException>();
        await new Func<Task>(() => ReleaseReader.ReadAsync(storeA, CancellationToken.None, ReleaseSignatures.Verifier(ReleaseSignatures.PublicKeyOf(key)))).Should().ThrowAsync<ReleaseRefusedException>();
        // Without a configured key, signatures are not checked.
        (await ReleaseReader.ReadAsync(storeA, CancellationToken.None, ReleaseSignatures.Verifier(null))).Manifest.Release.Should().Be("A");
    }

    [Fact]
    public void Ed25519_matches_the_RFC_8032_test_vector()
    {
        // RFC 8032 §7.1, TEST 1: the empty message.
        var secret = Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        Convert.ToHexStringLower(ReleaseSignatures.PublicKeyOf(secret)).Should().Be("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        Convert.ToHexStringLower(ReleaseSignatures.Sign(secret, [])).Should().Be(
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
    }

    [Fact]
    public void A_malformed_key_is_reported_without_its_value()
    {
        var ex = new Action(() => ReleaseSignatures.DecodeKey("c2VjcmV0", "signing key")).Should().Throw<FormatException>().Which;
        ex.Message.Should().NotContain("c2VjcmV0");
    }

    [Fact]
    public void The_import_checkpoint_round_trips_and_ignores_garbage()
    {
        var checkpoint = new RefImportCheckpoint("refdata/importruns/x", "work", "2026.10.0", 7);
        RefImportCheckpoint.Parse(checkpoint.Serialize()).Should().Be(checkpoint);
        RefImportCheckpoint.Parse("not json").Should().BeNull();
        RefImportCheckpoint.Parse(null).Should().BeNull();
    }

    private static object Clone(object document)
        => ReleaseCanonical.Deserialize(ReleaseCanonical.Serialize(document), ReleaseFormat.CollectionOf(document.GetType()));

    private static void Stamp(object document, string introduced, string changed)
        => document.GetType().GetProperty("Release")!.SetValue(document, new ReleaseStamp { Introduced = introduced, Changed = changed });
}
