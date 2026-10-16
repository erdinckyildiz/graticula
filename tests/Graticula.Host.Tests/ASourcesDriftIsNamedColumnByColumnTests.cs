using Graticula.Features;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// <c>GET /admin/datasources/{id}/drift</c> names each column a table gained, lost or changed — ADR-017 §3.1 step 3.
/// </summary>
/// <remarks>
/// Measured end to end on 2026-10-16 against the local fixture: a published table read once, then given a column,
/// relieved of one and narrowed in a third, answered <c>changed</c> with exactly those three names.
/// </remarks>
public sealed class ASourcesDriftIsNamedColumnByColumnTests
{
    private static readonly FieldDescription[] Was =
    [
        new("objectid", FieldType.Integer, false, null),
        new("a", FieldType.Text, true, null),
        new("b", FieldType.Integer, true, null),
        new("d", FieldType.Double, true, null),
    ];

    [Fact]
    public void Added_removed_and_retyped_are_each_named()
    {
        FieldDescription[] now =
        [
            new("objectid", FieldType.Integer, false, null),
            new("a", FieldType.Text, true, 10),
            new("c", FieldType.Date, true, null),
            new("d", FieldType.Double, false, null),
        ];

        (string[] added, string[] removed, string[] retyped) = AdminEndpoints.Drift(Was, now);

        Assert.Equal(["c"], added);
        Assert.Equal(["b"], removed);
        Assert.Equal(["a", "d"], retyped);
    }

    [Fact]
    public void The_same_table_has_not_drifted()
    {
        (string[] added, string[] removed, string[] retyped) = AdminEndpoints.Drift(Was, [.. Was]);

        Assert.Empty(added);
        Assert.Empty(removed);
        Assert.Empty(retyped);
    }
}
