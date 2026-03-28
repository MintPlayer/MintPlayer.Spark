using MintPlayer.Spark;
using SparkEditor.Entities;

namespace SparkEditor;

public class SparkEditorContext : SparkContext
{
    public IQueryable<PersistentObjectDefinition> PersistentObjects => Session.Query<PersistentObjectDefinition>();
    public IQueryable<AttributeDefinition> Attributes => Session.Query<AttributeDefinition>();
    public IQueryable<QueryDefinition> Queries => Session.Query<QueryDefinition>();
    public IQueryable<CustomActionDef> CustomActions => Session.Query<CustomActionDef>();
    public IQueryable<ProgramUnitGroupDef> ProgramUnitGroups => Session.Query<ProgramUnitGroupDef>();
    public IQueryable<ProgramUnitDef> ProgramUnits => Session.Query<ProgramUnitDef>();
    public IQueryable<SecurityGroupDef> SecurityGroups => Session.Query<SecurityGroupDef>();
    public IQueryable<SecurityRightDef> SecurityRights => Session.Query<SecurityRightDef>();
    public IQueryable<LanguageDef> Languages => Session.Query<LanguageDef>();
    public IQueryable<TranslationDef> Translations => Session.Query<TranslationDef>();
}
