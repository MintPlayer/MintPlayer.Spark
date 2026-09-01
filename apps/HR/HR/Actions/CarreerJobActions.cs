using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents.Session;

namespace HR.Actions;

/// <summary>
/// The AsDetail counterpart to Fleet's sample: the trigger lives on a column <em>inside</em> the
/// inline Jobs grid rather than on a top-level attribute.
///
/// <para>
/// A refresh from inside a detail grid runs against the <b>row's</b> type, so a change to
/// <c>CarreerJob.ProfessionId</c> arrives here rather than in <c>PersonActions</c> — the hook that
/// owns a type's shape is that type's own. The row is handed its owner as
/// <c>args.PersistentObject.Parent</c> for the context it cannot have alone.
/// </para>
///
/// <para>
/// A freelance engagement has no end date to speak of and needs none, so picking a freelance
/// profession clears <c>ContractEnd</c> and locks it. Picking anything else releases it — the half
/// a hook that only ever adds gets wrong, and the reason every branch here sets both sides.
/// </para>
/// </summary>
public partial class CarreerJobActions : DefaultPersistentObjectActions<Entities.CarreerJob>
{
    [Inject] private readonly IAsyncDocumentSession session;

    private const string FreelanceRegime = "Freelance";

    public override async Task OnRefreshAsync(SparkRefreshArgs<Entities.CarreerJob> args)
    {
        var obj = args.PersistentObject;
        var professionId = obj[nameof(Entities.CarreerJob.ProfessionId)].Value?.ToString();

        // Loading inside a refresh is a real cost — this runs on every pick, far more often than a
        // save — but the regime lives on the Profession and nothing else on the row carries it.
        var profession = string.IsNullOrWhiteSpace(professionId)
            ? null
            : await session.LoadAsync<Entities.Profession>(professionId, args.CancellationToken);

        var isFreelance = string.Equals(profession?.Regime, FreelanceRegime, StringComparison.OrdinalIgnoreCase);

        obj[nameof(Entities.CarreerJob.ContractEnd)].IsReadOnly = isFreelance;
        obj[nameof(Entities.CarreerJob.ContractEnd)].IsRequired = !isFreelance;

        if (isFreelance)
            obj[nameof(Entities.CarreerJob.ContractEnd)].SetValue<object?>(null);
    }
}
