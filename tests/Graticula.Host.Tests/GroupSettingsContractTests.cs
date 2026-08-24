using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A settings write has to carry the whole set, and the type is what says so.
/// </summary>
/// <remarks>
/// <para>
/// <b><see href="../../docs/architecture-debt.md">D-79</see>: a write that replaces every field
/// was guarded by one console helper and nothing else.</b>
/// <c>PUT /admin/groups/{name}/settings</c> replaces title, summary, description and four
/// policies together, so a caller that assembles the body from the controls in front of it
/// erases whatever is not in front of it. Measured in
/// <see href="../../docs/adr/ADR-036-groups.md">ADR-036</see> §4h against a running server:
/// whole object overlaid keeps the text, policies-only erases it. The console has always sent
/// all seven, which is why nothing had gone wrong — and is exactly the kind of *nothing has
/// gone wrong yet* the register exists for.
/// </para>
/// <para>
/// <b>Absence is the error, not emptiness.</b> Clearing a summary is a legitimate act, so an
/// empty string is a value and only a *missing* member is wrong. A check on the deserialised
/// object could not tell the two apart at all; `required` can, because it is enforced before
/// the object exists.
/// </para>
/// </remarks>
public sealed class GroupSettingsContractTests
{
    private const string Whole = """
        {
          "title": "Planning",
          "summary": "",
          "description": null,
          "visibility": "members",
          "joinPolicy": "invitation",
          "contribute": "managers",
          "deleteLocked": false
        }
        """;

    private static readonly JsonSerializerOptions Wire =
        new(JsonSerializerDefaults.Web);

    /// <summary>The whole set binds, including empty and null values.</summary>
    /// <remarks>
    /// <b>Empty and null are values and must stay so.</b> An operator clearing a summary sends
    /// one; a check that refused them would make the field write-once, which is a different
    /// defect and a worse one.
    /// </remarks>
    [Fact]
    public void The_whole_set_binds_and_empty_is_a_value()
    {
        GroupSettingsRequest request =
            JsonSerializer.Deserialize<GroupSettingsRequest>(Whole, Wire)!;

        Assert.Equal("Planning", request.Title);
        Assert.Equal(string.Empty, request.Summary);
        Assert.Null(request.Description);
        Assert.False(request.DeleteLocked);
    }

    /// <summary>A body that leaves a member out is refused before the object exists.</summary>
    /// <remarks>
    /// <b>Every member, one at a time.</b> A test that removed only <c>title</c> would pass
    /// while six others were optional, and the defect D-79 records is precisely that a caller
    /// sends *the controls in front of it* — which is a different subset each time.
    /// </remarks>
    [Theory]
    [InlineData("title")]
    [InlineData("summary")]
    [InlineData("description")]
    [InlineData("visibility")]
    [InlineData("joinPolicy")]
    [InlineData("contribute")]
    [InlineData("deleteLocked")]
    public void A_body_missing_any_member_is_refused(string member)
    {
        using JsonDocument whole = JsonDocument.Parse(Whole);

        Dictionary<string, JsonElement> partial = [];

        foreach (JsonProperty property in whole.RootElement.EnumerateObject())
        {
            if (!string.Equals(property.Name, member, StringComparison.Ordinal))
            {
                partial[property.Name] = property.Value.Clone();
            }
        }

        Assert.Equal(6, partial.Count);

        JsonException refused = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<GroupSettingsRequest>(
                JsonSerializer.Serialize(partial, Wire), Wire));

        Assert.Contains(member, refused.Message, StringComparison.OrdinalIgnoreCase);
    }
}
