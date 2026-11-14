using CodeCoverage.Entities;

namespace CodeCoverage.Commands;

/// <summary>
/// What a command handler answers, and how the command ended (#660 M4). A bare string converts to a refusal, the common
/// case, so a handler's many "Nothing was …, because …" returns stay as they are and only the other outcomes are spelled out.
/// </summary>
public sealed record CommandAnswer(string Text, ECommandOutcome Outcome)
{
    public static implicit operator CommandAnswer(string text) => new(text, ECommandOutcome.Refused);
}
