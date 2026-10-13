using Fleet.Entities;
using Fleet.Replicated;
using MintPlayer.Spark;
using MintPlayer.Spark.MailManager.Deliveries;
using Raven.Client.Documents.Linq;

namespace Fleet;

public partial class FleetContext : SparkContext
{
    public IRavenQueryable<Car> Cars => Session.Query<Car>();
    public IRavenQueryable<Person> People => Session.Query<Person>();
    public IRavenQueryable<Company> Companies => Session.Query<Company>();

    /// <summary>
    /// A second, filtered root of <see cref="Person"/>: the people a reminder mail can reach. It mints its own query,
    /// <c>GetPeopleWithEmail</c>, but it is not the plain <c>Session.Query&lt;Person&gt;()</c>, so the query-less
    /// <c>[Reference(typeof(Person))]</c> on <c>Car.Manager</c> still resolves to <c>GetPeople</c>
    /// (<c>docs/default_index_PRD.md</c>, D6). The generator marks only <see cref="People"/> with <c>[SparkPlainRoot]</c>;
    /// before that the synchronizer took the last root of the type, which would have been this one.
    /// </summary>
    public IRavenQueryable<Person> PeopleWithEmail => Session.Query<Person>().Where(p => p.Email != null);

    /// <summary>
    /// A root over a type the mail package owns: what became of each mail Fleet sent (sent, bounced, suppressed), for
    /// administrators (security.json grants QueryRead to Administrators only). Its attributes are derived from the CLR
    /// type (D9) through the <c>spark.ModelAttributes</c> hook in Program.cs, which creates the audit-like
    /// <c>UpdatedAtUtc</c> hidden. The package expires these documents itself (<c>DeliveryRetentionDays</c>).
    /// </summary>
    public IRavenQueryable<SparkMailDelivery> MailDeliveries => Session.Query<SparkMailDelivery>();
}
