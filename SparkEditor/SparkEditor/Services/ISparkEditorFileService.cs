using SparkEditor.Entities;

namespace SparkEditor.Services;

public interface ISparkEditorFileService
{
    // Target App_Data paths
    IReadOnlyList<string> TargetPaths { get; }

    // Entity Model files
    List<PersistentObjectDefinition> LoadAllPersistentObjects();
    PersistentObjectDefinition? LoadPersistentObject(string id);
    void SavePersistentObject(PersistentObjectDefinition po);
    void DeletePersistentObject(string id);

    List<AttributeDefinition> LoadAllAttributes();
    List<AttributeDefinition> LoadAttributesForPO(string poName);

    List<QueryDefinition> LoadAllQueries();
    List<QueryDefinition> LoadQueriesForPO(string poName);

    // Custom Actions
    List<CustomActionDef> LoadAllCustomActions();
    void SaveCustomAction(string name, CustomActionDef action);
    void DeleteCustomAction(string name);

    // Program Units
    List<ProgramUnitGroupDef> LoadAllProgramUnitGroups();
    List<ProgramUnitDef> LoadAllProgramUnits();

    // Security
    List<SecurityGroupDef> LoadAllSecurityGroups();
    List<SecurityRightDef> LoadAllSecurityRights();

    // Culture
    List<LanguageDef> LoadAllLanguages();

    // Translations
    List<TranslationDef> LoadAllTranslations();
}
