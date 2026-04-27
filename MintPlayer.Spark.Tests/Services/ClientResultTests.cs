using Microsoft.AspNetCore.Http.HttpResults;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Pins the wire shape of action-endpoint responses. Every action endpoint flows through
/// <see cref="ClientResult.Envelope"/> or <see cref="ClientResult.Retry"/>, so a regression
/// here changes the JSON shape on every action call and breaks the SDK envelope reader.
/// </summary>
public class ClientResultTests
{
    [Fact]
    public void Envelope_returns_a_JsonHttpResult_with_status_and_envelope_value()
    {
        var accessor = new ClientAccessor();
        accessor.Notify("hello");

        var result = ClientResult.Envelope(accessor, new { foo = 1 }, statusCode: 201);

        var json = result.Should().BeOfType<JsonHttpResult<ClientOperationEnvelope>>().Which;
        json.StatusCode.Should().Be(201);
        json.Value.Should().NotBeNull();
        json.Value!.Operations.Should().ContainSingle().Which.Should().BeOfType<NotifyOperation>();
    }

    [Fact]
    public void Envelope_passes_the_result_object_through_unchanged()
    {
        var accessor = new ClientAccessor();
        var payload = new { id = "cars/1", color = "red" };

        var result = ClientResult.Envelope(accessor, payload, statusCode: 200);

        var json = (JsonHttpResult<ClientOperationEnvelope>)result;
        json.Value!.Result.Should().BeSameAs(payload);
    }

    [Fact]
    public void Envelope_supports_a_null_result()
    {
        var accessor = new ClientAccessor();

        var result = ClientResult.Envelope(accessor, result: null, statusCode: 200);

        var json = (JsonHttpResult<ClientOperationEnvelope>)result;
        json.Value!.Result.Should().BeNull();
    }

    [Fact]
    public void Retry_returns_a_449_envelope()
    {
        var accessor = new ClientAccessor();
        var ex = new SparkRetryActionException(
            step: 0, title: "Pick", options: ["a", "b"], defaultOption: null,
            persistentObject: null, message: null);

        var result = ClientResult.Retry(accessor, ex);

        var json = result.Should().BeOfType<JsonHttpResult<ClientOperationEnvelope>>().Which;
        json.StatusCode.Should().Be(449);
        json.Value!.Result.Should().BeNull();
    }

    [Fact]
    public void Retry_pushes_a_RetryOperation_when_accessor_has_none_yet()
    {
        // Direct-throw flow: user code threw SparkRetryActionException without going through
        // RetryAccessor, so no RetryOperation is in the accumulator yet. ClientResult.Retry
        // must rebuild it from the exception so the client still sees the retry payload.
        var accessor = new ClientAccessor();
        var ex = new SparkRetryActionException(
            step: 1, title: "Confirm", options: ["yes", "no"], defaultOption: "no",
            persistentObject: null, message: "Are you sure?");

        ClientResult.Retry(accessor, ex);

        var op = accessor.Operations.OfType<RetryOperation>().Should().ContainSingle().Which;
        op.Step.Should().Be(1);
        op.Title.Should().Be("Confirm");
        op.Options.Should().Equal("yes", "no");
        op.DefaultOption.Should().Be("no");
        op.Message.Should().Be("Are you sure?");
    }

    [Fact]
    public void Retry_does_not_duplicate_an_existing_RetryOperation()
    {
        // RetryAccessor flow: the accessor already pushed the retry before throwing.
        // ClientResult.Retry must not push a second one.
        var accessor = new ClientAccessor();
        accessor.PushRetry(0, "Pick", ["a", "b"], null, null, null);
        var ex = new SparkRetryActionException(
            step: 99, title: "Other", options: ["x"], defaultOption: null,
            persistentObject: null, message: null);

        ClientResult.Retry(accessor, ex);

        accessor.Operations.OfType<RetryOperation>().Should().ContainSingle();
        // The pre-existing payload survives — the exception is NOT used to re-push.
        var existing = accessor.Operations.OfType<RetryOperation>().Single();
        existing.Title.Should().Be("Pick");
    }
}
