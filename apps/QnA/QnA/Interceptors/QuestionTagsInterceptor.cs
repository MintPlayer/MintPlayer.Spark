using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using QnA.Entities;

namespace QnA.Interceptors;

/// <summary>
/// Normalises a question's tags on every save (#460 M2): lower-case, trimmed, de-duplicated, at most
/// five, before the entity is checked and written. Its order among the other before-save hooks is
/// irrelevant: none of them writes Tags.
/// </summary>
public sealed class QuestionTagsInterceptor : IBeforeSave
{
    public const int MaxTags = 5;

    public bool AppliesTo(Type entityType) => entityType == typeof(Question);

    public ValueTask OnBeforeSaveAsync(SaveContext context)
    {
        if (context.Entity is not Question question || string.IsNullOrWhiteSpace(question.Tags))
            return ValueTask.CompletedTask;

        var tags = Normalize(question.Tags);
        if (tags.Count > MaxTags)
            throw new SparkValidationException($"A question takes at most {MaxTags} tags.", nameof(Question.Tags));

        question.Tags = tags.Count == 0 ? null : string.Join(", ", tags);
        return ValueTask.CompletedTask;
    }

    public static IReadOnlyList<string> Normalize(string tags)
        => tags.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
