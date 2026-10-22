using MintPlayer.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Session;

namespace MintPlayer.Actions;

public partial class TagActions : DefaultPersistentObjectActions<Tag>, ISparkOwnsRowSecurity
{
    [Inject] private readonly IAsyncDocumentSession session;

    public string RowSecurityRationale => CatalogRowSecurity.Rationale;

    /// <summary>The tags of a category. Source: "Custom.Category_Tags".</summary>
    public Task<IQueryable<Tag>> Category_Tags(CustomQueryArgs args)
    {
        args.EnsureParent("TagCategory");
        var categoryId = args.Parent!.Id;
        return Task.FromResult<IQueryable<Tag>>(session.Query<Tag>().Where(t => t.CategoryId == categoryId));
    }

    /// <summary>The tags nested under a tag. Source: "Custom.Tag_Children".</summary>
    public Task<IQueryable<Tag>> Tag_Children(CustomQueryArgs args)
    {
        args.EnsureParent("Tag");
        var parentId = args.Parent!.Id;
        return Task.FromResult<IQueryable<Tag>>(session.Query<Tag>().Where(t => t.ParentId == parentId));
    }
}
