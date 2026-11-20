using Iconic.Entities;
using MintPlayer.Spark;

namespace Iconic;

/// <summary>The users' projects. The scenes (<see cref="ProjectScene"/>) stay outside the model: only the editor's API touches them.</summary>
public partial class IconicContext : SparkContext
{
    public IQueryable<Project> Projects => Session.Query<Project>();
}
