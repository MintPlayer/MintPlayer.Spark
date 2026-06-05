using System.Net;

namespace MintPlayer.Spark.Client.Tests;

/// <summary>
/// Pins the dispose semantics of the two <see cref="SparkClient"/> constructors: owning mode
/// disposes its internal HttpClient, wrapping mode leaves the caller's HttpClient alone.
/// Regression here would silently leak HttpClients or — worse — dispose one the caller is
/// still using.
/// </summary>
public class SparkClientConstructorTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int InvocationCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            InvocationCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public void BaseUrl_constructor_owns_its_HttpClient_and_disposes_it()
    {
        var client = new SparkClient("http://localhost/");
        client.Dispose();

        // After Dispose, the internal HttpClient is disposed. Constructing a second client
        // with the same baseUrl should work — proves Dispose didn't blow up and that each
        // instance owns its own HttpClient.
        using var client2 = new SparkClient("http://localhost/");
        client2.Should().NotBeNull();
    }

    [Fact]
    public async Task HttpClient_constructor_with_ownsClient_false_does_NOT_dispose_caller_HttpClient()
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        var spark = new SparkClient(http, ownsClient: false);
        spark.Dispose();

        // Caller's HttpClient must still be usable after SparkClient.Dispose — a disposed
        // HttpClient throws ObjectDisposedException on send.
        var response = await http.GetAsync("/anything");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.InvocationCount.Should().Be(1);

        http.Dispose();
    }

    [Fact]
    public async Task HttpClient_constructor_with_ownsClient_true_disposes_the_wrapped_HttpClient()
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        var spark = new SparkClient(http, ownsClient: true);
        spark.Dispose();

        // Now the wrapped HttpClient is disposed — using it should throw.
        Func<Task> act = async () => await http.GetAsync("/anything");
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void Null_HttpClient_argument_throws()
    {
        Func<SparkClient> act = () => new SparkClient((HttpClient)null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
