using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Commerce.Checkout;

namespace MintPlayer.Spark.Commerce.Ticketing.Events.Answers;

/// <summary>
/// GB3 at "Pay": Checkout's pre-payment check for required attendee questions (<see cref="AttendeeAnswers.CheckAsync"/>).
/// Resolves the answers service when called, not when built: it depends on <see cref="ISparkCheckout"/>, which depends on
/// the pre-payment checks.
/// </summary>
internal sealed class AttendeeAnswersPrePaymentCheck(IServiceProvider services) : ICheckoutPrePaymentCheck
{
    public Task CheckAsync(CheckoutPrePaymentContext context, CancellationToken cancellationToken)
        => services.GetRequiredService<AttendeeAnswers>().CheckAsync(context, cancellationToken);
}

/// <summary>
/// The attendee answers sent with "Pay" (<c>SparkPlaceOrderRequest.Data["attendeeAnswers"]</c>):
/// <c>[{ "lineId": "…", "position": 1, "answers": [{ "questionKey": "…", "value": "…", "consent": true }] }]</c>.
/// </summary>
internal static class AttendeeCheckoutData
{
    public const string Key = "attendeeAnswers";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Position(string LineId, int Position, List<Answer>? Answers);

    private sealed record Answer(string QuestionKey, string? Value, bool Consent);

    /// <summary>The answers per (line id, position); empty when none were sent. Malformed data counts as none sent.</summary>
    public static IReadOnlyDictionary<(string LineId, int Position), IReadOnlyList<AttendeeAnswerInput>> Parse(IReadOnlyDictionary<string, string> data)
    {
        var result = new Dictionary<(string, int), IReadOnlyList<AttendeeAnswerInput>>();
        if (!data.TryGetValue(Key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return result;
        List<Position>? positions;
        try { positions = JsonSerializer.Deserialize<List<Position>>(raw, Json); }
        catch (JsonException) { return result; }
        foreach (var p in positions ?? [])
        {
            if (string.IsNullOrEmpty(p.LineId) || p.Position < 1)
                continue;
            result[(p.LineId, p.Position)] = (p.Answers ?? [])
                .Where(a => !string.IsNullOrEmpty(a.QuestionKey))
                .Select(a => new AttendeeAnswerInput(a.QuestionKey, a.Value, a.Consent)).ToList();
        }
        return result;
    }
}
