using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Models;
using MintPlayer.Spark.Models;

namespace SparkEditor;

public class SparkEditorContext : SparkContext
{
    public IQueryable<EntityTypeDefinition> PersistentObjects => Session.Query<EntityTypeDefinition>();
    public IQueryable<EntityAttributeDefinition> Attributes => Session.Query<EntityAttributeDefinition>();
    public IQueryable<SparkQuery> Queries => Session.Query<SparkQuery>();
    public IQueryable<CustomActionDefinition> CustomActions => Session.Query<CustomActionDefinition>();
    public IQueryable<ProgramUnitGroup> ProgramUnitGroups => Session.Query<ProgramUnitGroup>();
    public IQueryable<ProgramUnit> ProgramUnits => Session.Query<ProgramUnit>();
    public IQueryable<SecurityGroupDefinition> SecurityGroups => Session.Query<SecurityGroupDefinition>();
    public IQueryable<Right> SecurityRights => Session.Query<Right>();
    public IQueryable<LanguageDefinition> Languages => Session.Query<LanguageDefinition>();
    public IQueryable<TranslationEntry> Translations => Session.Query<TranslationEntry>();
    public IQueryable<LookupReferenceDef> LookupReferenceDefs => Session.Query<LookupReferenceDef>();
}
