using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Models;
using MintPlayer.Spark.Models;

namespace SparkEditor.Services;

public interface ISparkEditorFileService
{
    // Target App_Data paths
    IReadOnlyList<string> TargetPaths { get; }

    // Entity Model files
    List<EntityTypeDefinition> LoadAllPersistentObjects();
    EntityTypeDefinition? LoadPersistentObject(string id);
    void SavePersistentObject(EntityTypeDefinition po);
    void DeletePersistentObject(string id);

    List<EntityAttributeDefinition> LoadAllAttributes();
    List<EntityAttributeDefinition> LoadAttributesForPO(string poName);

    List<SparkQuery> LoadAllQueries();
    List<SparkQuery> LoadQueriesForPO(string poName);

    // Custom Actions
    List<CustomActionDefinition> LoadAllCustomActions();
    void SaveCustomAction(string name, CustomActionDefinition action);
    void DeleteCustomAction(string name);

    // Program Units
    List<ProgramUnitGroup> LoadAllProgramUnitGroups();
    List<ProgramUnit> LoadAllProgramUnits();

    // Security
    List<SecurityGroupDefinition> LoadAllSecurityGroups();
    List<Right> LoadAllSecurityRights();

    // Culture
    List<LanguageDefinition> LoadAllLanguages();

    // Translations
    List<TranslationEntry> LoadAllTranslations();
}
