using MintPlayer.Spark.Abstractions.Model;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// <c>serverSideRowLifecycle</c> must stay outside the model hash — and that is only safe for as
/// long as nothing which gates a write reads it.
/// </summary>
/// <remarks>
/// The two halves are one decision, and this suite pins the half a test can actually hold.
/// <para>
/// The flag governs a round trip: whether a detail grid asks the server before adding or removing a
/// row. That is presentation, it changes per deployment, and hashing it would make switching the
/// behaviour on require a rebuild — which is why it is deliberately excluded from
/// <see cref="ModelFileShape"/>'s five entity-level fields.
/// </para>
/// <para>
/// ⚠️ The price of that exclusion is stated in <c>ModelFileShapeWriteGateTests</c>'s own rule:
/// <b>every field that gates a write is part of the hash.</b> So the moment save-time rights
/// enforcement consults this flag, editing one unhashed line in a deployed model file would switch
/// a security check off — and the flag would have to be hashed instead. The save path enforces the
/// row type's New/Edit/Delete rights on every embedded collection unconditionally, precisely so
/// that never becomes true.
/// </para>
/// </remarks>
public class ServerSideRowLifecycleFlagTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "spark-lifecycle-" + Guid.NewGuid().ToString("N"));

    public ServerSideRowLifecycleFlagTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string HashOf(string entityLevelJson)
    {
        var path = Path.Combine(_dir, "Probe.json");
        File.WriteAllText(path,
            $$"""
            {
              "persistentObject": {
                "name": "Probe",
                "clrType": "X.Probe",{{entityLevelJson}}
                "attributes": [
                  { "name": "Description", "dataType": "string", "isVisible": true, "isReadOnly": false }
                ],
                "queries": []
              },
              "queries": []
            }
            """);

        return ModelFileShape.ComputeFileHashes(_dir)["Probe.json"];
    }

    [Fact]
    public void Turning_the_round_trip_on_does_not_move_the_hash()
    {
        var off = HashOf("");
        var on = HashOf("\n    \"serverSideRowLifecycle\": true,");

        on.Should().Be(off,
            "switching a client round trip on is a deployment decision, not a change of shape — "
            + "hashing it would make it require a rebuild");
    }

    [Fact]
    public void Turning_the_round_trip_off_again_does_not_move_the_hash()
    {
        var absent = HashOf("");
        var explicitlyOff = HashOf("\n    \"serverSideRowLifecycle\": false,");

        explicitlyOff.Should().Be(absent);
    }

    [Fact]
    public void A_field_that_gates_a_write_still_does_move_the_hash()
    {
        // The control. Without it, the two assertions above would also pass against a
        // ComputeFileHashes that had stopped reading the file at all.
        var writable = HashOf("");
        var readOnly = HashOfWithReadOnlyAttribute();

        readOnly.Should().NotBe(writable,
            "isReadOnly gates a write, so it must be structural — if this ever passes, the two "
            + "assertions above have stopped proving anything");
    }

    private string HashOfWithReadOnlyAttribute()
    {
        var path = Path.Combine(_dir, "Probe.json");
        File.WriteAllText(path,
            """
            {
              "persistentObject": {
                "name": "Probe",
                "clrType": "X.Probe",
                "attributes": [
                  { "name": "Description", "dataType": "string", "isVisible": true, "isReadOnly": true }
                ],
                "queries": []
              },
              "queries": []
            }
            """);

        return ModelFileShape.ComputeFileHashes(_dir)["Probe.json"];
    }
}
