using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Models;
using MintPlayer.Spark.Models;

namespace SparkEditor.Services;

public interface ISparkEditorFileService
{
    // Target App_Data paths
    IReadOnlyList<string> TargetPaths { get; }

    // === Persistent Objects (Model/{Name}.json files) ===
    List<EntityTypeDefinition> LoadAllPersistentObjects();
    EntityTypeDefinition? LoadPersistentObject(string id);
    void SavePersistentObject(EntityTypeDefinition po);
    void DeletePersistentObject(string id);

    // === Attributes (embedded in Model/{POName}.json) ===
    List<EntityAttributeDefinition> LoadAllAttributes();
    List<EntityAttributeDefinition> LoadAttributesForPO(string poName);
    void SaveAttribute(string poName, EntityAttributeDefinition attr);
    void DeleteAttribute(string poName, string attributeId);

    // === Queries (embedded in Model/{POName}.json) ===
    List<SparkQuery> LoadAllQueries();
    List<SparkQuery> LoadQueriesForPO(string poName);
    void SaveQuery(string poName, SparkQuery query);
    void DeleteQuery(string poName, string queryId);

    // === Custom Actions (customActions.json) ===
    List<CustomActionDefinition> LoadAllCustomActions();
    void SaveCustomAction(string name, CustomActionDefinition action);
    void DeleteCustomAction(string name);

    // === Program Unit Groups (programUnits.json) ===
    List<ProgramUnitGroup> LoadAllProgramUnitGroups();
    void SaveProgramUnitGroup(ProgramUnitGroup group);
    void DeleteProgramUnitGroup(string id);

    // === Program Units (nested in programUnits.json) ===
    List<ProgramUnit> LoadAllProgramUnits();
    void SaveProgramUnit(ProgramUnit unit);
    void DeleteProgramUnit(string id);

    // === Security Groups (security.json) ===
    List<SecurityGroupDefinition> LoadAllSecurityGroups();
    void SaveSecurityGroup(SecurityGroupDefinition group);
    void DeleteSecurityGroup(string id);

    // === Security Rights (security.json) ===
    List<Right> LoadAllSecurityRights();
    void SaveSecurityRight(Right right);
    void DeleteSecurityRight(string id);

    // === Languages (culture.json) ===
    List<LanguageDefinition> LoadAllLanguages();
    void SaveLanguage(LanguageDefinition language);
    void DeleteLanguage(string id);

    // === Translations (translations.json) ===
    List<TranslationEntry> LoadAllTranslations();
    void SaveTranslation(TranslationEntry translation);
    void DeleteTranslation(string id);

    // === File Watching ===
    event EventHandler<FileChangedEventArgs>? FileChanged;
    void StartWatching();
    void StopWatching();
}
