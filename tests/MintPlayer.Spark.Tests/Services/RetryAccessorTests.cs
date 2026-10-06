using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;
using System.Text.Json;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Pins the retry-action step pump: <see cref="RetryAccessor.Action"/> either throws (to
/// unwind the action and ask the user a question) or sets <see cref="RetryAccessor.Result"/>
/// from a previously-answered step and returns. The step counter must increment on every
/// call so re-runs (after the user answered step 0) advance to step 1.
/// </summary>
public class RetryAccessorTests
{
    [Fact]
    public void Action_pushes_RetryOperation_and_throws_when_no_answer_for_current_step()
    {
        var clientAccessor = new ClientAccessor();
        var retry = new RetryAccessor(clientAccessor);

        var act = () => retry.Action(
            title: "Pick one", options: ["a", "b"], defaultOption: "a",
            persistentObject: null, message: null);

        var ex = act.Should().Throw<SparkRetryActionException>().Which;
        ex.Step.Should().Be(0);
        ex.Title.Should().Be("Pick one");

        // PushRetry was called before the throw → the operation is in the envelope.
        var op = clientAccessor.Operations.OfType<RetryOperation>().Should().ContainSingle().Which;
        op.Step.Should().Be(0);
        op.Title.Should().Be("Pick one");
        op.Options.Should().Equal("a", "b");
        op.DefaultOption.Should().Be("a");
    }

    [Fact]
    public void Action_returns_without_throwing_when_step_is_already_answered()
    {
        var clientAccessor = new ClientAccessor();
        var answered = new RetryResult { Option = "yes" };

        var retry = new RetryAccessor(clientAccessor)
        {
            AnsweredResults = new Dictionary<int, RetryResult> { [0] = answered }
        };

        retry.Action("Confirm?", ["yes", "no"]);

        retry.Result.Should().BeSameAs(answered);
        // No retry operation pushed when the step is satisfied.
        clientAccessor.Operations.OfType<RetryOperation>().Should().BeEmpty();
    }

    [Fact]
    public void Action_increments_currentStep_on_each_call_so_subsequent_runs_advance()
    {
        var clientAccessor = new ClientAccessor();
        var step0Answer = new RetryResult { Option = "ok" };

        var retry = new RetryAccessor(clientAccessor)
        {
            AnsweredResults = new Dictionary<int, RetryResult> { [0] = step0Answer }
        };

        // Step 0 is answered → returns silently.
        retry.Action("Step 0", ["ok"]);
        retry.Result.Should().BeSameAs(step0Answer);

        // Step 1 has no answer → throws SparkRetryActionException with step=1 (NOT 0).
        var act = () => retry.Action("Step 1", ["a", "b"]);

        var ex = act.Should().Throw<SparkRetryActionException>().Which;
        ex.Step.Should().Be(1);
    }

    [Fact]
    public void Action_with_no_AnsweredResults_dictionary_throws_for_every_step()
    {
        var clientAccessor = new ClientAccessor();
        var retry = new RetryAccessor(clientAccessor);

        var act = () => retry.Action("Pick", ["a"]);

        act.Should().Throw<SparkRetryActionException>().Which.Step.Should().Be(0);
    }

    [Fact]
    public void Action_with_AnsweredResults_missing_current_step_throws()
    {
        var clientAccessor = new ClientAccessor();
        var retry = new RetryAccessor(clientAccessor)
        {
            // Step 5 is answered, but we're on step 0 — should still throw.
            AnsweredResults = new Dictionary<int, RetryResult> { [5] = new() { Option = "x" } }
        };

        var act = () => retry.Action("Pick", ["a"]);

        act.Should().Throw<SparkRetryActionException>().Which.Step.Should().Be(0);
    }

    // --- Invoke: client-method steps (generic passkeys page, D7) -----------

    [Fact]
    public async Task Invoke_pushes_a_RetryOperation_carrying_the_method_and_its_arguments_and_throws()
    {
        var clientAccessor = new ClientAccessor();
        var retry = new RetryAccessor(clientAccessor);

        var act = () => retry.Invoke("webauthn.create", () => Task.FromResult<object?>(new { challenge = "abc", timeout = 60000 }));

        var ex = (await act.Should().ThrowAsync<SparkRetryActionException>()).Which;
        ex.Step.Should().Be(0);
        ex.ClientMethod.Should().Be("webauthn.create");

        var op = clientAccessor.Operations.OfType<RetryOperation>().Should().ContainSingle().Which;
        op.Step.Should().Be(0);
        op.ClientMethod.Should().Be("webauthn.create");
        op.Options.Should().BeEmpty();
        op.Arguments.Should().NotBeNull();
        op.Arguments!.Value.GetProperty("challenge").GetString().Should().Be("abc");
        op.Arguments!.Value.GetProperty("timeout").GetInt32().Should().Be(60000);
    }

    [Fact]
    public async Task Invoke_wire_shape_names_the_method_and_an_answer_carries_its_value_back()
    {
        var clientAccessor = new ClientAccessor();
        var retry = new RetryAccessor(clientAccessor);
        var act = () => retry.Invoke("clipboard.read", () => Task.FromResult<object?>(new { format = "text" }));
        await act.Should().ThrowAsync<SparkRetryActionException>();

        // The 449's operation, as the envelope serializes it (polymorphic, discriminated by "type").
        var json = JsonSerializer.Serialize<ClientOperation>(clientAccessor.Operations.OfType<RetryOperation>().Single(), JsonSerializerOptions.Web);
        using (var wire = JsonDocument.Parse(json))
        {
            wire.RootElement.GetProperty("type").GetString().Should().Be("retry");
            wire.RootElement.GetProperty("clientMethod").GetString().Should().Be("clipboard.read");
            wire.RootElement.GetProperty("arguments").GetProperty("format").GetString().Should().Be("text");
        }

        // The browser's answer, as the resubmitted request carries it.
        var answer = JsonSerializer.Deserialize<RetryResult>("""{ "step": 0, "option": "OK", "value": { "text": "hello" } }""", JsonSerializerOptions.Web)!;
        answer.Value!.Value.GetProperty("text").GetString().Should().Be("hello");
    }

    [Fact]
    public async Task Invoke_on_an_answered_step_exposes_the_value_and_does_not_throw()
    {
        var clientAccessor = new ClientAccessor();
        using var value = JsonDocument.Parse("""{ "id": "cred-1" }""");
        var answered = new RetryResult { Option = "OK", Value = value.RootElement.Clone() };
        var retry = new RetryAccessor(clientAccessor)
        {
            AnsweredResults = new Dictionary<int, RetryResult> { [0] = answered }
        };

        await retry.Invoke("webauthn.create", () => Task.FromResult<object?>(null));

        retry.Result.Should().BeSameAs(answered);
        retry.Result!.Value!.Value.GetProperty("id").GetString().Should().Be("cred-1");
        clientAccessor.Operations.OfType<RetryOperation>().Should().BeEmpty();
    }

    [Fact]
    public async Task Invoke_reports_a_failed_client_method_as_Cancel()
    {
        var retry = new RetryAccessor(new ClientAccessor())
        {
            AnsweredResults = new Dictionary<int, RetryResult> { [0] = new() { Option = "Cancel" } }
        };

        await retry.Invoke("webauthn.create", () => Task.FromResult<object?>(null));

        retry.Result!.Option.Should().Be("Cancel");
        retry.Result.Value.Should().BeNull();
    }

    // ⚠️ The trap of D7: the action re-runs from the top on every pass, so building the arguments
    // again on the answering pass would, for WebAuthn, issue a fresh challenge and overwrite the
    // state the answer is checked against.
    [Fact]
    public async Task Invoke_does_not_run_the_arguments_factory_for_an_answered_step()
    {
        var calls = 0;
        var retry = new RetryAccessor(new ClientAccessor())
        {
            AnsweredResults = new Dictionary<int, RetryResult> { [0] = new() { Option = "OK" } }
        };

        await retry.Invoke("webauthn.create", () => { calls++; return Task.FromResult<object?>(new { }); });

        calls.Should().Be(0);
    }

    [Fact]
    public async Task Invoke_runs_the_arguments_factory_exactly_once_for_an_unanswered_step()
    {
        var calls = 0;
        var retry = new RetryAccessor(new ClientAccessor());

        var act = () => retry.Invoke("webauthn.create", () => { calls++; return Task.FromResult<object?>(new { }); });

        await act.Should().ThrowAsync<SparkRetryActionException>();
        calls.Should().Be(1);
    }

    /// <summary>
    /// One action asking the browser, then the user, then the browser again: three passes, each one
    /// answering one more step, all through one step counter.
    /// </summary>
    [Fact]
    public async Task Invoke_and_Action_share_one_step_counter_so_mixed_steps_round_trip_in_order()
    {
        var answers = new Dictionary<int, RetryResult>();
        var log = new List<string>();

        async Task RunAction(RetryAccessor retry)
        {
            await retry.Invoke("first", () => { log.Add("args first"); return Task.FromResult<object?>(1); });
            log.Add($"first -> {retry.Result!.Value!.Value.GetInt32()}");
            retry.Action("Continue?", ["Yes", "No"]);
            log.Add($"prompt -> {retry.Result!.Option}");
            await retry.Invoke("second", () => { log.Add("args second"); return Task.FromResult<object?>(2); });
            log.Add($"second -> {retry.Result!.Value!.Value.GetInt32()}");
        }

        async Task<SparkRetryActionException?> Pass()
        {
            var retry = new RetryAccessor(new ClientAccessor()) { AnsweredResults = new(answers) };
            try { await RunAction(retry); return null; }
            catch (SparkRetryActionException ex) { return ex; }
        }

        var pass1 = await Pass();
        pass1!.Step.Should().Be(0);
        pass1.ClientMethod.Should().Be("first");
        answers[0] = new() { Step = 0, Option = "OK", Value = JsonSerializer.SerializeToElement(10) };

        var pass2 = await Pass();
        pass2!.Step.Should().Be(1);
        pass2.ClientMethod.Should().BeNull();
        pass2.Title.Should().Be("Continue?");
        answers[1] = new() { Step = 1, Option = "Yes" };

        var pass3 = await Pass();
        pass3!.Step.Should().Be(2);
        pass3.ClientMethod.Should().Be("second");
        answers[2] = new() { Step = 2, Option = "OK", Value = JsonSerializer.SerializeToElement(20) };

        log.Clear();
        (await Pass()).Should().BeNull();
        log.Should().Equal("first -> 10", "prompt -> Yes", "second -> 20");
    }
}
