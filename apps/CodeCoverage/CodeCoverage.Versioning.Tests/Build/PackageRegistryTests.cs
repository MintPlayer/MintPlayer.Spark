using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using CodeCoverage.Versioning.Api;
using CodeCoverage.Versioning.Build;
using CodeCoverage.Versioning.Graph;

namespace CodeCoverage.Versioning.Tests.Build;

/// <summary>
/// #153 S7: the API hash of a version that is only on the registry. Archives are built in memory and served by a stub
/// handler, so the hash is compared with what the build would compute for the same assembly or declarations.
/// </summary>
public class PackageRegistryTests
{
    private static readonly string AssemblyPath = typeof(PackageRegistryTests).Assembly.Location;
    private static readonly string AssemblyName = Path.GetFileNameWithoutExtension(AssemblyPath);

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(answer(request));
        }
    }

    private static ProjectNode NuGet(string id = "Acme.Core", string? assembly = null, params string[] tfms) => new()
    {
        Path = "src/Core/Core.csproj", Kind = PackageKind.NuGet, Id = id, Publishable = true, AssemblyName = assembly ?? AssemblyName, TargetFrameworks = tfms,
    };

    private static ProjectNode Npm(string id) => new() { Path = "libs/util/package.json", Kind = PackageKind.Npm, Id = id, Publishable = true };

    private static byte[] Nupkg(params (string Path, byte[] Bytes)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, bytes) in entries)
            {
                using var stream = zip.CreateEntry(path).Open();
                stream.Write(bytes);
            }
        return buffer.ToArray();
    }

    private static byte[] Tgz(params (string Name, string Text)[] files)
    {
        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
            foreach (var (name, text) in files)
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
        using var gz = new MemoryStream();
        using (var gzip = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(tar.ToArray());
        return gz.ToArray();
    }

    private static string BuildHash(string tfm)
    {
        using var stream = File.OpenRead(AssemblyPath);
        return DotnetApiListing.Hash(DotnetApiListing.List(stream).Select(l => $"{tfm} {l}"));
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    [Fact]
    public async Task A_nupkg_hashes_its_lib_assembly_as_the_build_does_from_the_lowercased_flat_container_url()
    {
        var nupkg = Nupkg(($"lib/net10.0/{AssemblyName}.dll", File.ReadAllBytes(AssemblyPath)), ("lib/net10.0/Other.dll", [1, 2, 3]), ("README.md", [65]));
        var stub = new Stub(_ => Ok(nupkg));

        var hash = await new PublicPackageRegistry(new HttpClient(stub)).ApiHashAsync(NuGet(), "1.2.0-Preview.1");

        hash.Should().Be(BuildHash("net10.0"));
        stub.Asked.Should().Equal("https://api.nuget.org/v3-flatcontainer/acme.core/1.2.0-preview.1/acme.core.1.2.0-preview.1.nupkg");
    }

    [Fact]
    public void An_analyzer_package_is_hashed_from_its_analyzers_folder_with_the_project_framework()
    {
        var nupkg = Nupkg(($"analyzers/dotnet/cs/{AssemblyName}.dll", File.ReadAllBytes(AssemblyPath)));

        PublicPackageRegistry.NuGetHash(new MemoryStream(nupkg), NuGet(tfms: "netstandard2.0")).Should().Be(BuildHash("netstandard2.0"));
        PublicPackageRegistry.NuGetHash(new MemoryStream(nupkg), NuGet(tfms: ["netstandard2.0", "net10.0"])).Should().BeNull("with two frameworks the analyzer's own is unknown");
    }

    [Fact]
    public async Task An_npm_tarball_hashes_its_declarations_and_skips_everything_else()
    {
        const string declarations = "export declare function greet(name: string): string;\n";
        var tgz = Tgz(
            ("package/index.d.ts", declarations),
            ("package/index.js", "export function greet(n) { return n; }"),
            ("package/node_modules/dep/index.d.ts", "export declare const leaked: number;"));
        var stub = new Stub(_ => Ok(tgz));

        var hash = await new PublicPackageRegistry(new HttpClient(stub)).ApiHashAsync(Npm("@acme/util"), "1.0.0");

        hash.Should().Be(TypeScriptApiListing.Hash(TypeScriptApiListing.List([("index.d.ts", declarations)])));
        stub.Asked.Should().Equal("https://registry.npmjs.org/@acme/util/-/util-1.0.0.tgz");
    }

    [Fact]
    public async Task A_package_without_an_api_has_no_hash()
    {
        var registry = new PublicPackageRegistry(new HttpClient(new Stub(r => Ok(r.RequestUri!.Host == "registry.npmjs.org"
            ? Tgz(("package/README.md", "no types"))
            : Nupkg(("lib/net10.0/Unrelated.dll", [1]))))));

        (await registry.ApiHashAsync(Npm("plain"), "1.0.0")).Should().BeNull();
        (await registry.ApiHashAsync(NuGet(), "1.0.0")).Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_version_the_registry_does_not_have_has_no_hash(HttpStatusCode status)
        => (await new PublicPackageRegistry(new HttpClient(new Stub(_ => new HttpResponseMessage(status)))).ApiHashAsync(NuGet(), "1.0.0")).Should().BeNull();

    [Fact]
    public async Task A_registry_error_is_not_mistaken_for_a_missing_version()
    {
        var read = () => new PublicPackageRegistry(new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)))).ApiHashAsync(NuGet(), "1.0.0");

        await read.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task A_package_announced_larger_than_the_cap_is_refused_before_it_is_read()
    {
        var stub = new Stub(_ =>
        {
            var content = new ByteArrayContent([0]);
            content.Headers.ContentLength = PublicPackageRegistry.MaxPackageBytes + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var read = () => new PublicPackageRegistry(new HttpClient(stub)).ApiHashAsync(NuGet(), "1.0.0");

        await read.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("1.0.0/../../evil")]
    [InlineData("1.0.0?x=1")]
    public async Task A_version_that_is_not_a_plain_version_is_never_requested(string version)
    {
        var stub = new Stub(_ => throw new InvalidOperationException("requested"));

        (await new PublicPackageRegistry(new HttpClient(stub)).ApiHashAsync(NuGet(), version)).Should().BeNull();
        stub.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task A_package_without_a_valid_id_is_never_requested()
    {
        var stub = new Stub(_ => throw new InvalidOperationException("requested"));

        (await new PublicPackageRegistry(new HttpClient(stub)).ApiHashAsync(NuGet(id: "not a package id"), "1.0.0")).Should().BeNull();
        stub.Asked.Should().BeEmpty();
    }
}
