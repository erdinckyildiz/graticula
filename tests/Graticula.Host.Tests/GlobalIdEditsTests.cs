using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// <c>useGlobalIds=true</c> edits are read by GlobalID and rewritten to the object-id edit the writer applies.
/// </summary>
/// <remarks>Written 2026-09-15, when applyEdits first honoured the parameter.</remarks>
public sealed class GlobalIdEditsTests
{
    private static readonly Guid First = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Second = Guid.Parse("66666666-7777-8888-9999-000000000000");

    private const string Updates =
        """[{"attributes":{"GlobalID":"{11111111-2222-3333-4444-555555555555}","objectid":999,"label":"x"}}]""";

    [Fact]
    public void Updates_and_deletes_are_collected_in_either_delete_spelling()
    {
        Assert.True(GlobalIdEdits.TryCollect(Updates, "[\"{66666666-7777-8888-9999-000000000000}\"]", "globalid", out HashSet<Guid> ids, out _));
        Assert.Equal([First, Second], ids);

        Assert.True(GlobalIdEdits.TryCollect(null, "{66666666-7777-8888-9999-000000000000}, 11111111-2222-3333-4444-555555555555", "globalid", out ids, out _));
        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public void The_rewrite_addresses_by_object_id_and_replaces_one_the_client_sent()
    {
        (string? updates, string? deletes) = GlobalIdEdits.Rewrite(
            Updates, "[\"{66666666-7777-8888-9999-000000000000}\"]", "globalid", "objectid",
            new Dictionary<Guid, long> { [First] = 7, [Second] = 9 }, out List<Guid> missing);

        Assert.Empty(missing);
        Assert.Equal("9", deletes);

        JsonElement attributes = JsonDocument.Parse(updates!).RootElement[0].GetProperty("attributes");
        Assert.Equal(7, attributes.GetProperty("objectid").GetInt64());
        Assert.Equal("x", attributes.GetProperty("label").GetString());
    }

    [Fact]
    public void A_GlobalID_no_feature_has_is_reported_by_name()
    {
        GlobalIdEdits.Rewrite(Updates, null, "globalid", "objectid", new Dictionary<Guid, long>(), out List<Guid> missing);

        Assert.Equal([First], missing);
        Assert.Contains("{11111111-2222-3333-4444-555555555555}", GlobalIdEdits.Unknown(missing), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[{"attributes":{"label":"x"}}]""", null, "every update needs")]
    [InlineData(null, "7,8", "not one")]
    [InlineData("not json", null, "must be a JSON array")]
    public void An_edit_that_does_not_name_its_features_by_GlobalID_is_refused(string? updates, string? deletes, string reason)
    {
        Assert.False(GlobalIdEdits.TryCollect(updates, deletes, "globalid", out _, out string? error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }
}
