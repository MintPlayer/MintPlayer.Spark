using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace MintPlayer.Spark.Tests.Files.Stores;

/// <summary>
/// Names the environment variables a store's conformance class needs; without them its tests are reported skipped
/// (the <c>SPARK_OPENSEARCH_URI</c> pattern, per class: the tests are inherited, so the gate sits on the derived class).
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class StoreGateAttribute(params string[] variables) : Attribute
{
    public string[] Variables { get; } = variables;

    public string? MissingReason()
    {
        var missing = Variables.Where(v => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v))).ToArray();
        return missing.Length == 0 ? null : $"{string.Join(", ", missing)} not set: no server to test this store against.";
    }
}

/// <summary>A conformance test: skipped when its class's <see cref="StoreGateAttribute"/> is not satisfied.</summary>
[XunitTestCaseDiscoverer("MintPlayer.Spark.Tests.Files.Stores.StoreFactDiscoverer", "MintPlayer.Spark.Tests")]
[AttributeUsage(AttributeTargets.Method)]
public sealed class StoreFactAttribute : FactAttribute;

public sealed class StoreFactDiscoverer(IMessageSink diagnosticMessageSink) : IXunitTestCaseDiscoverer
{
    public IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute)
    {
        var display = discoveryOptions.MethodDisplayOrDefault();
        var displayOptions = discoveryOptions.MethodDisplayOptionsOrDefault();
        var reason = testMethod.TestClass.Class.ToRuntimeType()?.GetCustomAttribute<StoreGateAttribute>()?.MissingReason();
        yield return reason is null
            ? new XunitTestCase(diagnosticMessageSink, display, displayOptions, testMethod)
            : new XunitSkippedDataRowTestCase(diagnosticMessageSink, display, displayOptions, testMethod, reason);
    }
}
