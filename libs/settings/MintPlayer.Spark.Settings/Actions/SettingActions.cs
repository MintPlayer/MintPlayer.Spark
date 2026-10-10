using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Settings.Models;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Settings.Actions;

/// <summary>
/// The settings screen's query (<c>Custom.Settings</c>, alias <c>settings</c>). A library cannot use a
/// <c>Database.X</c> source, which needs a property on the application's context. Who sees and edits the
/// rows is the library's right <c>QueryReadEdit/Setting</c>, granted to the slot the application binds.
/// </summary>
public partial class SettingActions : DefaultPersistentObjectActions<Setting>
{
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>Every setting document.</summary>
    public IRavenQueryable<Setting> Settings() => session.Query<Setting>();
}
