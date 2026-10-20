using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Services;

namespace MintPlayer.Spark.Commerce.Ticketing.Events.Answers;

/// <summary>One answer as the buyer submits it.</summary>
/// <param name="Value">Text, a choice key, an ISO date, <c>true</c>/<c>false</c> for a checkbox; for a multiple choice the keys joined by <c>,</c>.</param>
/// <param name="Consent">For a sensitive question: the buyer ticked the explicit-consent box shown with it.</param>
public sealed record AttendeeAnswerInput(string QuestionKey, string? Value, bool Consent = false);

/// <summary>Why an answer was refused: a translation key under <c>ticketingEvents.answers.</c> and the question.</summary>
public sealed record AttendeeAnswerError(string QuestionKey, string Code);

/// <summary>
/// The attendee-question rules (GB3, PRD §4.11). Pure. Text, choice, date and checkbox questions (no files); required or
/// optional; a sensitive question (health, dietary needs) is answered only with explicit consent to its text
/// (GDPR Art. 9(2)(a) for special categories, Art. 7(1) proof of consent; the consent text is stored as a hash with the
/// answer). Art. 9(2)(a) ("explicit consent … for one or more specified purposes") and Art. 7(1) checked against
/// EUR-Lex 32016R0679 on 2026-10-20; national Art. 9(4) conditions not checked.
/// </summary>
public static class AttendeeQuestionRules
{
    public const string Required = "required";
    public const string UnknownQuestion = "unknownQuestion";
    public const string UnknownChoice = "unknownChoice";
    public const string InvalidDate = "invalidDate";
    public const string TooLong = "tooLong";
    public const string ConsentRequired = "consentRequired";

    /// <summary>The longest text answer.</summary>
    public const int MaxTextLength = 2000;

    /// <summary>What is wrong with a ticket type's questions (English, for the publication message).</summary>
    public static IEnumerable<string> Problems(TicketType type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in type.Questions)
        {
            if (!TicketEventPublisher.IsKey(question.Key))
                yield return $"Ticket type {type.Id}: question key '{question.Key}' is not letters, digits, '.', '_' or '-'.";
            else if (!seen.Add(question.Key))
                yield return $"Ticket type {type.Id}: question key '{question.Key}' is used twice.";
            if (question.Label.IsEmpty)
                yield return $"Ticket type {type.Id}: question '{question.Key}' has no label.";
            if (question.Kind is EAttendeeQuestionKind.Choice or EAttendeeQuestionKind.MultipleChoice)
            {
                if (question.Choices.Count == 0)
                    yield return $"Ticket type {type.Id}: choice question '{question.Key}' has no options.";
                if (question.Choices.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() != question.Choices.Count
                    || question.Choices.Any(c => !TicketEventPublisher.IsKey(c.Key)))
                    yield return $"Ticket type {type.Id}: question '{question.Key}' has empty or repeated option keys.";
            }
            if (question.Sensitive && (question.ConsentText is null || question.ConsentText.IsEmpty))
                yield return $"Ticket type {type.Id}: sensitive question '{question.Key}' needs a consent text (GA9).";
        }
    }

    /// <summary>
    /// Checks the answers of one ticket against its questions. <paramref name="timing"/> limits the required check to
    /// the questions asked at that moment (null: every question). Answers to unknown questions are refused; an empty
    /// value clears an optional answer.
    /// </summary>
    public static IReadOnlyList<AttendeeAnswerError> Validate(IReadOnlyList<AttendeeQuestion> questions, IReadOnlyList<AttendeeAnswerInput> answers, EAttendeeQuestionTiming? timing = null)
    {
        var errors = new List<AttendeeAnswerError>();
        var byKey = questions.ToDictionary(q => q.Key, StringComparer.Ordinal);
        var given = new Dictionary<string, AttendeeAnswerInput>(StringComparer.Ordinal);
        foreach (var answer in answers)
        {
            if (!byKey.TryGetValue(answer.QuestionKey, out var question))
            {
                errors.Add(new(answer.QuestionKey, UnknownQuestion));
                continue;
            }
            given[answer.QuestionKey] = answer;
            if (IsEmpty(question, answer.Value))
                continue;
            if (Check(question, answer.Value!) is { } code)
                errors.Add(new(question.Key, code));
            else if (question.Sensitive && !answer.Consent)
                errors.Add(new(question.Key, ConsentRequired));
        }
        foreach (var question in questions.Where(q => q.Required && (timing is null || q.AskAt == timing)))
        {
            if (!given.TryGetValue(question.Key, out var answer) || IsEmpty(question, answer.Value))
                errors.Add(new(question.Key, Required));
        }
        return errors;
    }

    /// <summary>Whether a value counts as no answer (an unticked checkbox counts as none).</summary>
    public static bool IsEmpty(AttendeeQuestion question, string? value)
        => string.IsNullOrWhiteSpace(value)
           || (question.Kind == EAttendeeQuestionKind.Checkbox && !string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase));

    private static string? Check(AttendeeQuestion question, string value)
    {
        switch (question.Kind)
        {
            case EAttendeeQuestionKind.Text:
            case EAttendeeQuestionKind.MultilineText:
                return value.Length > MaxTextLength ? TooLong : null;
            case EAttendeeQuestionKind.Choice:
                return question.Choices.Any(c => c.Key == value.Trim()) ? null : UnknownChoice;
            case EAttendeeQuestionKind.MultipleChoice:
                return Keys(value).All(k => question.Choices.Any(c => c.Key == k)) ? null : UnknownChoice;
            case EAttendeeQuestionKind.Date:
                return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : InvalidDate;
            default:
                return null;
        }
    }

    /// <summary>The stored form of a valid, non-empty answer.</summary>
    public static string Normalize(AttendeeQuestion question, string value) => question.Kind switch
    {
        EAttendeeQuestionKind.MultipleChoice => string.Join(",", Keys(value).Distinct(StringComparer.Ordinal)),
        EAttendeeQuestionKind.Checkbox => "true",
        EAttendeeQuestionKind.Text or EAttendeeQuestionKind.MultilineText => value,
        _ => value.Trim(),
    };

    private static IEnumerable<string> Keys(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The proof of the consent text shown (GDPR Art. 7(1)): SHA-256 (hex) of the text in the language shown.</summary>
    public static string ConsentHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
