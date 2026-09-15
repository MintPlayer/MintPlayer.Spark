using System.Net;
using System.Text;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client.Tests._Infrastructure;

namespace MintPlayer.Spark.Client.Tests;

/// <summary>
/// M3 — the client can answer a retry.
/// </summary>
/// <remarks>
/// <para>
/// Scripted rather than driven through a host, because what M3 gets right or wrong is entirely a
/// matter of <b>what goes on the wire</b>: whether the whole body is resent, whether the answers
/// accumulate, and whether <c>step</c> is the server's or the client's. A real host proves the
/// server's half, which <c>RetryFromEveryHookTests</c> already does across all nine endpoints, and it
/// cannot easily be made to ask the same question twice with a skipped step — which is the case that
/// separates a correct client from a plausible one.
/// </para>
/// <para>
/// The envelope shapes below are the ones the S1 spike captured off a real Fleet host, not invented:
/// <c>{ "result": null, "operations": [ { "type": "retry", "step": 0, … } ] }</c>.
/// </para>
/// </remarks>
public class RetryConversationTests
{
    private static (SparkClient client, ScriptedHttpHandler handler) NewClientWithWarmup()
    {
        var handler = new ScriptedHttpHandler()
            .EnqueueWithCookies(
                ".AspNetCore.Antiforgery.abc=val; Path=/",
                "XSRF-TOKEN=token; Path=/");
        return (NewClient(handler), handler);
    }

    /// <summary>
    /// ⚠️ No warmup response queued. <c>po/load</c> and <c>queries/execute</c> carry no antiforgery
    /// metadata, so the client does not prime a token for them and never issues the warmup GET — a
    /// queued warmup response would be eaten by the request under test. That asymmetry is the
    /// endpoints being honest about what they are: a read is not a mutation.
    /// </summary>
    private static SparkClient NewClient(ScriptedHttpHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, ownsClient: true);

    /// <summary>A 449 carrying one retry operation, exactly as the server emits it.</summary>
    private static HttpResponseMessage Prompt(int step, string title, params string[] options)
        => new((HttpStatusCode)449)
        {
            Content = new StringContent(
                $$"""
                {"result":null,"operations":[{"type":"retry","step":{{step}},"title":"{{title}}",
                 "options":[{{string.Join(",", options.Select(o => $"\"{o}\""))}}],
                 "defaultOption":null,"persistentObject":null,"message":"Are you sure?"}]}
                """,
                Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage PromptWithForm(int step, string title, string[] options, PersistentObject form)
        => new((HttpStatusCode)449)
        {
            Content = new StringContent(
                $$"""
                {"result":null,"operations":[{"type":"retry","step":{{step}},"title":"{{title}}",
                 "options":[{{string.Join(",", options.Select(o => $"\"{o}\""))}}],
                 "defaultOption":null,"message":null,
                 "persistentObject":{{JsonSerializer.Serialize(form, new JsonSerializerOptions(JsonSerializerDefaults.Web))}}}]}
                """,
                Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Envelope(string resultJson)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"result":{{resultJson}},"operations":[]}""", Encoding.UTF8, "application/json"),
        };

    private static JsonElement[] RetryResults(JsonElement body)
        => body.TryGetProperty("retryResults", out var rr) && rr.ValueKind == JsonValueKind.Array
            ? [.. rr.EnumerateArray()]
            : [];

    // ------------------------------------------------------------------------------------------
    // FR1 / FR2 — a single prompt, answered
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_action_prompt_is_a_result_not_an_exception()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.Enqueue(Prompt(0, "Report vehicle as stolen", "Confirm"));

        using (client)
        {
            var result = await client.ExecuteActionAsync(Guid.NewGuid(), "MarkStolen");

            result.IsRetry.Should().BeTrue();
            result.StatusCode.Should().Be(449);
            result.Retry!.Step.Should().Be(0);
            result.Retry.Title.Should().Be("Report vehicle as stolen");
            result.Retry.Options.Should().BeEquivalentTo(["Confirm"]);
        }
    }

    [Fact]
    public async Task ContinueAsync_resends_the_whole_body_with_the_answer_appended()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.Enqueue(Prompt(0, "Delete car", "Delete", "Cancel"));
        handler.EnqueueStatus(HttpStatusCode.NoContent);

        var typeId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        using (client)
        {
            var prompt = await client.ExecuteActionAsync(typeId, "DeleteCar", selectedItemIds: ["cars/1"], queryId: "cars");
            var done = await client.ContinueAsync(prompt, "Delete");
            done.IsRetry.Should().BeFalse();
        }

        var first = handler.Body(1);
        var second = handler.LastBody();

        // Everything the first attempt carried, the second carries too. The server replays the hook
        // from the top and needs the original request to replay it against — a body carrying only
        // the answers would have nothing to replay.
        second.GetProperty("objectTypeId").GetString().Should().Be(first.GetProperty("objectTypeId").GetString());
        second.GetProperty("actionName").GetString().Should().Be("DeleteCar");
        second.GetProperty("queryId").GetString().Should().Be("cars");
        second.GetProperty("selectedItemIds").EnumerateArray().Single().GetString().Should().Be("cars/1");

        RetryResults(first).Should().BeEmpty();
        var answers = RetryResults(second);
        answers.Should().HaveCount(1);
        answers[0].GetProperty("step").GetInt32().Should().Be(0);
        answers[0].GetProperty("option").GetString().Should().Be("Delete");
    }

    // ------------------------------------------------------------------------------------------
    // FR3 — a repeated prompt: the second request carries BOTH answers
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Two_prompts_accumulate_both_answers()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.Enqueue(Prompt(0, "Report vehicle as stolen", "Confirm"));
        handler.Enqueue(Prompt(1, "Notify fleet managers", "Yes, notify", "No, skip"));
        handler.EnqueueStatus(HttpStatusCode.OK);

        using (client)
        {
            var first = await client.ExecuteActionAsync(Guid.NewGuid(), "MarkStolen");
            var second = await client.ContinueAsync(first, "Confirm");
            second.IsRetry.Should().BeTrue();
            second.Retry!.Step.Should().Be(1);

            var done = await client.ContinueAsync(second, "Yes, notify");
            done.IsRetry.Should().BeFalse();
        }

        var answers = RetryResults(handler.LastBody());
        answers.Should().HaveCount(2);
        answers[0].GetProperty("option").GetString().Should().Be("Confirm");
        answers[1].GetProperty("option").GetString().Should().Be("Yes, notify");
    }

    /// <summary>
    /// ⚠️ The case that separates a correct client from a plausible one. A hook may skip a step — it
    /// asks the second question only when the first was answered a particular way — so the client
    /// must echo the server's <c>step</c> rather than count its own answers. A locally incremented
    /// counter agrees with the server right up until this happens, and then silently answers a
    /// different question than the one that was asked.
    /// </summary>
    [Fact]
    public async Task The_step_is_the_servers_even_when_it_skips_one()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.Enqueue(Prompt(0, "First", "Yes"));
        handler.Enqueue(Prompt(7, "Seventh", "Yes"));
        handler.EnqueueStatus(HttpStatusCode.OK);

        using (client)
        {
            var first = await client.ExecuteActionAsync(Guid.NewGuid(), "Whatever");
            var second = await client.ContinueAsync(first, "Yes");
            await client.ContinueAsync(second, "Yes");
        }

        var answers = RetryResults(handler.LastBody());
        answers.Select(a => a.GetProperty("step").GetInt32()).Should().Equal(0, 7);
    }

    // ------------------------------------------------------------------------------------------
    // The prompt's own form comes back filled in
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_prompt_that_carries_a_form_gets_it_back_with_the_values()
    {
        var form = new PersistentObject
        {
            Name = "ConfirmDeleteCar",
            ObjectTypeId = Guid.Parse("2e9b7c64-4d35-4f1a-8e8c-7d6a1f3b5c29"),
            Attributes = [new PersistentObjectAttribute { Name = "Confirmation", Value = null }],
        };

        var (client, handler) = NewClientWithWarmup();
        handler.Enqueue(PromptWithForm(0, "Delete car", ["Delete", "Cancel"], form));
        handler.EnqueueStatus(HttpStatusCode.NoContent);

        using (client)
        {
            var prompt = await client.ExecuteActionAsync(Guid.NewGuid(), "DeleteCar");
            prompt.Retry!.PersistentObject.Should().NotBeNull();

            var filled = prompt.Retry.PersistentObject!;
            filled.Attributes.Single(a => a.Name == "Confirmation").Value = "1-ABC-123";

            await client.ContinueAsync(prompt, "Delete", filled);
        }

        var answer = RetryResults(handler.LastBody()).Single();
        answer.GetProperty("persistentObject").GetProperty("attributes")
            .EnumerateArray().Single().GetProperty("value").GetString()
            .Should().Be("1-ABC-123");
    }

    // ------------------------------------------------------------------------------------------
    // FR6 — an option that was not offered is rejected before it reaches the server
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Rejected client-side because the server cannot tell an option that was never offered from one
    /// a hook stopped offering between attempts: both simply fail to match, and the hook then runs
    /// its else-branch as though the caller had chosen something. A typo would assert the wrong path
    /// and pass.
    /// </summary>
    [Fact]
    public async Task An_option_that_was_not_offered_is_refused_without_a_round_trip()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.Enqueue(Prompt(0, "Delete car", "Delete", "Cancel"));

        using (client)
        {
            var prompt = await client.ExecuteActionAsync(Guid.NewGuid(), "DeleteCar");

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => client.ContinueAsync(prompt, "Yes"));
            ex.Message.Should().Contain("Delete / Cancel");
        }

        // Warmup + the one attempt. Nothing was sent for the rejected option.
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Continuing_a_completed_action_is_a_programming_error()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.EnqueueStatus(HttpStatusCode.OK);

        using (client)
        {
            var done = await client.ExecuteActionAsync(Guid.NewGuid(), "Whatever");
            done.IsRetry.Should().BeFalse();

            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ContinueAsync(done, "Yes"));
        }
    }

    // ------------------------------------------------------------------------------------------
    // FR4 — a handler answers the whole conversation inside one call
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_handler_answers_every_prompt_without_the_caller_looping()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.Enqueue(Prompt(0, "Report vehicle as stolen", "Confirm"));
        handler.Enqueue(Prompt(1, "Notify fleet managers", "Yes, notify", "No, skip"));
        handler.EnqueueStatus(HttpStatusCode.OK);

        var seen = new List<string>();
        using (client)
        {
            var result = await client.ExecuteActionAsync(
                Guid.NewGuid(), "MarkStolen",
                onRetry: (prompt, _) =>
                {
                    seen.Add(prompt.Title);
                    return Task.FromResult<RetryAnswer?>(RetryAnswer.Choose(prompt.Options[0]));
                });

            result.IsRetry.Should().BeFalse();
        }

        seen.Should().Equal("Report vehicle as stolen", "Notify fleet managers");
        RetryResults(handler.LastBody()).Should().HaveCount(2);
    }

    [Fact]
    public async Task The_client_level_handler_applies_when_a_call_supplies_none()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.Enqueue(Prompt(0, "Confirm?", "Yes"));
        handler.Enqueue(Envelope("""{"id":"people/1","name":"Alice","objectTypeId":"11111111-2222-3333-4444-555555555555","attributes":[]}"""));

        using (client)
        {
            client.RetryHandler = (_, _) => Task.FromResult<RetryAnswer?>(RetryAnswer.Choose("Yes"));

            var saved = await client.UpdatePersistentObjectAsync(new PersistentObject
            {
                Id = "people/1",
                Name = "Alice",
                ObjectTypeId = Guid.NewGuid(),
                Attributes = [],
            });

            saved.Id.Should().Be("people/1");
        }

        RetryResults(handler.LastBody()).Single().GetProperty("option").GetString().Should().Be("Yes");
    }

    // ------------------------------------------------------------------------------------------
    // A prompt nobody answers — on every endpoint that can raise one
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// ⚠️ Before M3, a 449 from anything but <c>actions/execute</c> surfaced as a bare
    /// <see cref="SparkClientException"/> carrying a status nobody recognises and an envelope nobody
    /// parsed — so a caller could not even <em>read</em> the question raised from a save, a load, or
    /// a query. Nine endpoints can prompt; the client understood one. Measured by the S1 spike.
    /// </summary>
    [Theory]
    [InlineData("load")]
    [InlineData("update")]
    [InlineData("create")]
    [InlineData("delete")]
    [InlineData("query")]
    public async Task An_unanswered_prompt_names_the_question(string endpoint)
    {
        var needsAntiforgery = endpoint is "update" or "create" or "delete";
        var handler = new ScriptedHttpHandler();
        if (needsAntiforgery)
            handler.EnqueueWithCookies(".AspNetCore.Antiforgery.abc=val; Path=/", "XSRF-TOKEN=token; Path=/");
        var client = NewClient(handler);
        handler.Enqueue(Prompt(3, "Are you quite sure?", "Yes", "No"));

        using (client)
        {
            var po = new PersistentObject { Id = "people/1", Name = "Alice", ObjectTypeId = Guid.NewGuid(), Attributes = [] };
            Func<Task> call = endpoint switch
            {
                "load" => () => client.GetPersistentObjectAsync("person", "people/1"),
                "update" => () => client.UpdatePersistentObjectAsync(po),
                "create" => () => client.CreatePersistentObjectAsync(po),
                "delete" => () => client.DeletePersistentObjectAsync(Guid.NewGuid(), "people/1"),
                "query" => () => client.ExecuteQueryAsync("allpeople"),
                _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
            };

            var ex = await Assert.ThrowsAsync<SparkRetryRequiredException>(call);
            ex.Prompt.Step.Should().Be(3);
            ex.Prompt.Title.Should().Be("Are you quite sure?");
            ex.Prompt.Options.Should().BeEquivalentTo(["Yes", "No"]);
            ex.AnsweredSoFar.Should().Be(0);

            // Still a SparkClientException, so a caller that catches the general shape is not
            // suddenly missing the failure.
            ex.Should().BeAssignableTo<SparkClientException>();
        }
    }

    [Fact]
    public async Task A_handler_that_declines_reports_how_far_it_got()
    {
        var handler = new ScriptedHttpHandler();
        var client = NewClient(handler);
        handler.Enqueue(Prompt(0, "First", "Yes"));
        handler.Enqueue(Prompt(1, "Second", "Yes"));

        using (client)
        {
            var ex = await Assert.ThrowsAsync<SparkRetryRequiredException>(() =>
                client.ExecuteQueryAsync("allpeople", onRetry: (prompt, _) =>
                    Task.FromResult(prompt.Step == 0 ? RetryAnswer.Choose("Yes") : null)));

            ex.AnsweredSoFar.Should().Be(1);
            ex.Prompt.Title.Should().Be("Second");
        }
    }

    // ------------------------------------------------------------------------------------------
    // FR5 — a hook that never stops asking
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// A hook that re-raises regardless of the answer is the ordinary shape of a bug in a hook, and
    /// every round trip in it looks individually reasonable. Without a bound the client spins until
    /// the process dies.
    /// </summary>
    [Fact]
    public async Task A_hook_that_never_stops_asking_is_given_up_on()
    {
        var (client, handler) = NewClientWithWarmup();
        for (var i = 0; i < 10; i++)
            handler.Enqueue(Prompt(0, "Again?", "Yes"));

        using (client)
        {
            client.MaxRetryDepth = 3;

            var ex = await Assert.ThrowsAsync<SparkClientException>(() =>
                client.ExecuteActionAsync(Guid.NewGuid(), "Loop",
                    onRetry: (_, _) => Task.FromResult<RetryAnswer?>(RetryAnswer.Choose("Yes"))));

            ex.Message.Should().Contain("Gave up after answering 3");
        }

        // Warmup + MaxRetryDepth attempts + the one that tripped the bound.
        handler.Requests.Should().HaveCount(1 + 3 + 1);
    }

    [Fact]
    public async Task The_explicit_loop_is_bounded_too()
    {
        var (client, handler) = NewClientWithWarmup();
        for (var i = 0; i < 10; i++)
            handler.Enqueue(Prompt(0, "Again?", "Yes"));

        using (client)
        {
            client.MaxRetryDepth = 2;

            var result = await client.ExecuteActionAsync(Guid.NewGuid(), "Loop");
            result = await client.ContinueAsync(result, "Yes");
            result = await client.ContinueAsync(result, "Yes");

            var ex = await Assert.ThrowsAsync<SparkClientException>(() => client.ContinueAsync(result, "Yes"));
            ex.Message.Should().Contain("Gave up after answering 2");
        }
    }

    // ------------------------------------------------------------------------------------------
    // Two conversations at once
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// ⚠️ The reason the conversation lives on the result and not on the client. Were "the current
    /// retry" client state, these two would answer into each other — and the failure would be a
    /// wrong answer silently accepted, not an exception.
    /// </summary>
    [Fact]
    public async Task Two_conversations_through_one_client_do_not_interleave()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.Enqueue(Prompt(0, "First conversation", "A"));
        handler.Enqueue(Prompt(0, "Second conversation", "B"));
        handler.EnqueueStatus(HttpStatusCode.OK);
        handler.EnqueueStatus(HttpStatusCode.OK);

        using (client)
        {
            var one = await client.ExecuteActionAsync(Guid.NewGuid(), "One");
            var two = await client.ExecuteActionAsync(Guid.NewGuid(), "Two");

            one.Retry!.Title.Should().Be("First conversation");
            two.Retry!.Title.Should().Be("Second conversation");

            await client.ContinueAsync(one, "A");
            await client.ContinueAsync(two, "B");
        }

        RetryResults(handler.Body(3)).Single().GetProperty("option").GetString().Should().Be("A");
        RetryResults(handler.Body(4)).Single().GetProperty("option").GetString().Should().Be("B");
        handler.Body(3).GetProperty("actionName").GetString().Should().Be("One");
        handler.Body(4).GetProperty("actionName").GetString().Should().Be("Two");
    }
}
