using MintPlayer.Entities;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Actions;

namespace MintPlayer.Actions;

public partial class PersonActions : DefaultPersistentObjectActions<Person>, IBeforeSave<Person>, ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => CatalogRowSecurity.Rationale;

    public ValueTask OnBeforeSaveAsync(Person entity, SaveContext context)
    {
        entity.FirstName = entity.FirstName.Trim();
        entity.LastName = entity.LastName.Trim();
        return ValueTask.CompletedTask;
    }
}
