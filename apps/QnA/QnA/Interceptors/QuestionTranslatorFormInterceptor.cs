using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Interceptors;
using QnA.Actions;
using QnA.Entities;
using QnA.Services;

namespace QnA.Interceptors;

/// <summary>
/// The form half of <see cref="QuestionActions.GetProtectedAttributesAsync"/>: someone who may translate
/// a question but not manage it sees its own attributes read-only, so the edit form offers only the
/// translations. A UI hint only — the save drops those attributes whatever the form sent.
/// </summary>
public sealed partial class QuestionTranslatorFormInterceptor : IPersistentObjectInterceptor
{
    [Inject] private readonly QnAAccess access;

    public bool AppliesTo(Type entityType) => entityType == typeof(Question);

    public async ValueTask OnAfterLoadAsync(LoadContext context)
    {
        if (context.IsSystemContext || access.UserId is null)
            return;

        var po = context.PersistentObject;
        var authorId = po.Attributes.FirstOrDefault(a => a.Name == nameof(Question.AuthorId))?.Value?.ToString();
        if (await access.CanManageAsync(authorId))
            return;

        foreach (var attribute in po.Attributes)
        {
            if (QuestionActions.AuthorOnlyAttributes.Contains(attribute.Name))
                attribute.IsReadOnly = true;
        }
    }
}
