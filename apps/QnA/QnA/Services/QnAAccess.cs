using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Moderation;

namespace QnA.Services;

/// <summary>
/// Who the caller is, for QnA's own rules: the user id (never a name, #460 D8) and whether the caller
/// moderates. A moderator is whoever holds <c>Lock/Question</c> in <c>security.json</c> — the one right
/// the Moderators group holds that no earned privilege can (D12), so it cannot be farmed.
/// </summary>
public sealed partial class QnAAccess
{
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IPermissionService permissions;

    private bool? isModerator;

    /// <summary>The signed-in user's id, or null for an anonymous caller.</summary>
    public string? UserId => currentUser.IsAuthenticated ? currentUser.Id : null;

    /// <summary>Whether the caller moderates (holds <c>Lock/Question</c>). Answered once per request.</summary>
    public async Task<bool> IsModeratorAsync()
        => isModerator ??= UserId is not null && await permissions.IsAllowedAsync(ModerationRights.Lock, "Question");

    /// <summary>Whether the caller may manage a post: its author, or a moderator.</summary>
    public async Task<bool> CanManageAsync(string? authorId)
        => (UserId is { } id && id == authorId) || await IsModeratorAsync();
}
