using System.Net;
using System.Text;
using System.Text.Json;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>An owner's source document: what <see cref="ReplicaCar"/> replicates (collection <c>ReplSourceCars</c>).</summary>
public class ReplSourceCar
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public ReplOwner? Owner { get; set; }
    public int Mileage { get; set; }
}

public class ReplOwner
{
    public string? Name { get; set; }
}

/// <summary>
/// A consumer's replica class with the constants <c>ReplicatedGenerator</c> emits for
/// <c>[Replicated(SourceCollection = "ReplSourceCars", SourceModule = "Fleet", WriteBack = true)]</c> (this test project
/// does not run the library generators, so they are written out; <c>ReplicaCatalog.Problems</c> re-renders them, and
/// <see cref="ReplicaCatalogTests"/> checks they agree). Collection <c>ReplicaCars</c>.
/// </summary>
[Replicated(SourceCollection = "ReplSourceCars", SourceModule = "Fleet", WriteBack = true)]
public class ReplicaCar : IReplicatedEntity
{
    public const string ReplicationSourceCollection = "ReplSourceCars";
    public const string ReplicationTargetCollection = "ReplicaCars";
    public const string? ReplicationSourceModule = "Fleet";
    public const bool ReplicationWriteBack = true;
    public const string ReplicationManifest = "{\"v\":1,\"source\":\"ReplSourceCars\",\"target\":\"ReplicaCars\",\"fields\":[[\"LicensePlate\",\"LicensePlate\"],[\"Model\",\"Model\"],[\"OwnerName\",\"Owner.Name\"]]}";
    public const string ReplicationScript = "loadToReplicaCars({\n    LicensePlate: this.LicensePlate,\n    Model: this.Model,\n    OwnerName: (this.Owner == null ? null : this.Owner.Name),\n    '@metadata': { 'Spark-Replica-Source-Change-Vector': getMetadata(this)['@change-vector'] }\n});";
    public const string ReplicationHash = "sha256:6b17c32cd3b790ce6a74bc31cd6cec86c6e6d6d5053a6165fca67e1c45bcc13d";

    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? OwnerName { get; set; }
}

/// <summary>The same collection in an older, narrower shape (no <c>OwnerName</c>): another hash.</summary>
public static class ReplicaCarNarrow
{
    public const string Manifest = "{\"v\":1,\"source\":\"ReplSourceCars\",\"target\":\"ReplicaCars\",\"fields\":[[\"LicensePlate\",\"LicensePlate\"],[\"Model\",\"Model\"]]}";
    public const string Script = "loadToReplicaCars({\n    LicensePlate: this.LicensePlate,\n    Model: this.Model,\n    '@metadata': { 'Spark-Replica-Source-Change-Vector': getMetadata(this)['@change-vector'] }\n});";
    public const string Hash = "sha256:e951dae94bbd69c845a43fe47703570a50d390a42c3a07586c389829908d71da";
}

internal static class ReplicationFixtures
{
    public static SparkReplicationOptions Options(string module = "HR") => new()
    {
        ModuleName = module,
        ModuleUrl = $"https://{module.ToLowerInvariant()}.test",
    };

    public static ReplicaCatalog Catalog(SparkReplicationOptions? options = null) => new(options ?? Options(), typeof(ReplicaCar));

    public static ReplicaRegistrationItem Item(string manifest = ReplicaCar.ReplicationManifest, string script = ReplicaCar.ReplicationScript, string hash = ReplicaCar.ReplicationHash) => new()
    {
        TargetCollection = ReplicaCar.ReplicationTargetCollection,
        SourceCollection = ReplicaCar.ReplicationSourceCollection,
        Manifest = manifest,
        Script = script,
        Hash = hash,
    };

    /// <summary>An <see cref="IReplicationHttpClientProvider"/> whose clients answer through <paramref name="answer"/>, recording requests.</summary>
    public static IReplicationHttpClientProvider Http(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer, List<HttpRequestMessage>? requests = null)
    {
        var provider = Substitute.For<IReplicationHttpClientProvider>();
        provider.GetClient(Arg.Any<string>()).Returns(_ => new HttpClient(new FakeHandler(answer, requests)));
        return provider;
    }

    public static HttpResponseMessage Json(HttpStatusCode status, object? body = null) => new(status)
    {
        Content = new StringContent(body is null ? "" : JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, "application/json"),
    };

    /// <summary>A registry that knows <paramref name="modules"/> at <c>https://{module}.test</c>.</summary>
    public static IModuleDirectory Directory(params string[] modules)
    {
        var directory = Substitute.For<IModuleDirectory>();
        directory.FindAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var name = call.Arg<string>();
            return Task.FromResult(modules.Contains(name, StringComparer.OrdinalIgnoreCase)
                ? new ModuleInformation { AppName = name, AppUrl = $"https://{name.ToLowerInvariant()}.test", DatabaseName = name + "-db", DatabaseUrls = ["http://localhost:8080"] }
                : null);
        });
        return directory;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer, List<HttpRequestMessage>? requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                await request.Content.LoadIntoBufferAsync(cancellationToken);
            requests?.Add(request);
            return await answer(request);
        }
    }
}
