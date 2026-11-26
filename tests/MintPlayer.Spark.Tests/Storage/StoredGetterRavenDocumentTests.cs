using Newtonsoft.Json.Serialization;
using Raven.Client.Documents;
using Raven.Client.Json.Serialization.NewtonsoftJson;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>
/// #536 O13: the generated <c>__stored_</c> backing field is for the SQL providers only. A RavenDB document keeps the
/// getter, as it always did, and never the private field, so the documents in production stay as they are.
/// </summary>
public class StoredGetterRavenDocumentTests
{
    [Fact]
    public void A_document_holds_the_getter_and_not_its_backing_field()
    {
        using var store = new DocumentStore().ApplySparkConventions();
        var serialization = (NewtonsoftJsonSerializationConventions)store.Conventions.Serialization;

        var contract = (JsonObjectContract)serialization.JsonContractResolver.ResolveContract(typeof(SparkStoreConformanceTests.ConfDoc));
        var written = contract.Properties.Where(p => !p.Ignored && p.Readable).Select(p => p.PropertyName).ToList();

        written.Should().Contain(nameof(SparkStoreConformanceTests.ConfDoc.Label));
        written.Should().NotContain(name => name!.StartsWith("__stored_"));
    }
}
