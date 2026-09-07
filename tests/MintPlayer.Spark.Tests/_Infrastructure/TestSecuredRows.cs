using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// Fabricates the gate's token for tests whose subject is <em>not</em> row security.
/// </summary>
/// <remarks>
/// <para>
/// <c>QueryResultProjector.ToItems</c> takes a <c>SecuredRows</c> so that no framework path can put
/// rows on the wire without going through the gate. A test of the projector itself — id uniqueness,
/// AsDetail shaping, column projection — has no rows to secure and nothing to secure them with, so
/// it needs a way past that parameter.
/// </para>
/// <para>
/// <b>Only for tests about the projector.</b> A test about whether some path enforces row security
/// must go through the real gate, or it is back to the problem the permissive double created:
/// unable to tell "enforced" from "never asked". This helper lives in the test assembly precisely so
/// it can never be reached from framework code.
/// </para>
/// </remarks>
internal static class TestSecuredRows
{
    public static RowSecurityGate.SecuredRows Of(params PersistentObject[] rows)
        => RowSecurityGate.SecuredRows.FromRowGatedLoad(rows);

    public static RowSecurityGate.SecuredRows Of(IEnumerable<PersistentObject> rows)
        => RowSecurityGate.SecuredRows.FromRowGatedLoad([.. rows]);
}
