using MintPlayer.Spark;

namespace Festival;

/// <summary>
/// The festival demo has no entities of its own: events, venues, tickets, products, orders, invoices and the rest come
/// from the commerce libraries' layers, with their own queries (PRD §4.1).
/// </summary>
public partial class FestivalContext : SparkContext
{
}
