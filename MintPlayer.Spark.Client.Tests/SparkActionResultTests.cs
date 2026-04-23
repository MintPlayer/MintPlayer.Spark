using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client.Tests._Infrastructure;

namespace MintPlayer.Spark.Client.Tests;

/// <summary>
/// Pins how <see cref="SparkClient.ExecuteActionAsync"/> maps the three in-protocol server
/// response shapes into <see cref="SparkActionResult"/> — a naive implementation would either
/// collapse the retry case or throw on 449 (which the server uses for "Retry With", not "error").
/// </summary>
public class SparkActionResultTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static (SparkClient client, ScriptedHttpHandler handler) NewClientWithWarmup()
    {
        var handler = new ScriptedHttpHandler()
            .EnqueueWithCookies(
                ".AspNetCore.Antiforgery.abc=val; Path=/",
                "XSRF-TOKEN=token; Path=/");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new SparkClient(http, ownsClient: true);
        return (client, handler);
    }

    [Fact]
    public async Task Empty_200_response_is_decoded_as_non_retry_success()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.EnqueueStatus(HttpStatusCode.OK);
        using (client)
        {
            var result = await client.ExecuteActionAsync(Guid.NewGuid(), "Archive");
            result.IsRetry.Should().BeFalse();
            result.StatusCode.Should().Be((int)HttpStatusCode.OK);
            result.Retry.Should().BeNull();
        }
    }

    [Fact]
    public async Task Status_449_response_populates_SparkActionResult_Retry_with_all_payload_fields()
    {
        var (client, handler) = NewClientWithWarmup();

        // Mirror the exact JSON the server emits from SparkRetryActionException — see
        // MintPlayer.Spark/Endpoints/Actions/ExecuteCustomAction.cs for the canonical shape.
        var payload = new
        {
            type = "retry-action",
            step = 3,
            title = "Proceed?",
            message = "This operation can't be undone.",
            options = new[] { "Yes", "No" },
            defaultOption = "No",
            persistentObject = (PersistentObject?)null,
        };
        var response = new HttpResponseMessage((HttpStatusCode)449)
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };
        handler.Enqueue(response);

        using (client)
        {
            var result = await client.ExecuteActionAsync(Guid.NewGuid(), "DeleteAll");

            result.IsRetry.Should().BeTrue();
            result.StatusCode.Should().Be(449);
            result.Retry.Should().NotBeNull();
            result.Retry!.Step.Should().Be(3);
            result.Retry.Title.Should().Be("Proceed?");
            result.Retry.Message.Should().Be("This operation can't be undone.");
            result.Retry.Options.Should().Equal("Yes", "No");
            result.Retry.DefaultOption.Should().Be("No");
        }
    }

    [Fact]
    public async Task Non_success_non_449_status_throws_SparkClientException_with_status_preserved()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.EnqueueStatus(HttpStatusCode.InternalServerError);
        using (client)
        {
            var ex = await Assert.ThrowsAsync<SparkClientException>(
                () => client.ExecuteActionAsync(Guid.NewGuid(), "Explode"));
            ex.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
    }

    [Fact]
    public async Task ExecuteAction_sends_POST_with_antiforgery_header_and_json_body()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.EnqueueStatus(HttpStatusCode.OK);
        using (client)
        {
            var typeId = Guid.Parse("99999999-0000-0000-0000-000000000000");
            await client.ExecuteActionAsync(typeId, "Archive");
        }

        // Skip index 0 (warmup GET); the action is the second request.
        var post = handler.Requests[^1];
        post.Method.Should().Be(HttpMethod.Post);
        post.RequestUri!.AbsolutePath.Should().Be("/spark/actions/99999999-0000-0000-0000-000000000000/Archive");
        post.Headers.Contains("X-XSRF-TOKEN").Should().BeTrue();
    }
}
