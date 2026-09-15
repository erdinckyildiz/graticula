using System;
using System.Linq;
using System.Text.Json;
using Graticula.Features;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// What an <c>applyEdits</c> response says about each feature when the batch was rolled back.
/// </summary>
/// <remarks>
/// <b>Written 2026-09-15, against a defect found on the showcase.</b> One add that was fine and one
/// outside its domain, sent with <c>rollbackOnFailure=true</c>, answered
/// <c>{"objectId":4,"success":true}</c> for the first — and there was no feature 4, because the batch
/// had been rolled back. The only sign was a <c>rolledBack</c> flag no ArcGIS client reads.
/// </remarks>
public sealed class ApplyEditsResponseTests
{
    private static ApplyEditsRequest.Parsed NothingRejected() =>
        new(new EditBatch([], [], []), [], [], []);

    private static JsonElement Json(object response) =>
        JsonDocument.Parse(JsonSerializer.Serialize(response)).RootElement;

    [Fact]
    public void An_add_that_ran_in_a_rolled_back_batch_is_not_reported_as_a_success()
    {
        EditOutcome outcome = new(
            [EditResult.Ok(4), EditResult.Failed(-1, "'pressure' is 80, outside the domain.")],
            [],
            [],
            RolledBack: true);

        JsonElement[] adds = [.. Json(ApplyEditsResponse.Build(outcome, NothingRejected()))
            .GetProperty("addResults").EnumerateArray()];

        Assert.Equal(2, adds.Length);
        Assert.All(adds, add => Assert.False(add.GetProperty("success").GetBoolean()));

        // The add was given id 4 and the id was rolled back with it; naming it would
        // send the client looking for a feature that is not there.
        Assert.Equal(-1, adds[0].GetProperty("objectId").GetInt64());
        Assert.Contains(
            "rollbackOnFailure",
            adds[0].GetProperty("error").GetProperty("description").GetString(),
            StringComparison.Ordinal);

        // The feature that caused it keeps its own reason.
        Assert.Contains(
            "outside the domain",
            adds[1].GetProperty("error").GetProperty("description").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_update_or_delete_in_a_rolled_back_batch_keeps_the_id_it_named()
    {
        EditOutcome outcome = new(
            [EditResult.Failed(-1, "bad")],
            [EditResult.Ok(7)],
            [EditResult.Ok(9)],
            RolledBack: true);

        JsonElement response = Json(ApplyEditsResponse.Build(outcome, NothingRejected()));

        JsonElement update = response.GetProperty("updateResults").EnumerateArray().Single();
        JsonElement delete = response.GetProperty("deleteResults").EnumerateArray().Single();

        Assert.False(update.GetProperty("success").GetBoolean());
        Assert.Equal(7, update.GetProperty("objectId").GetInt64());
        Assert.False(delete.GetProperty("success").GetBoolean());
        Assert.Equal(9, delete.GetProperty("objectId").GetInt64());
    }

    [Fact]
    public void The_single_operation_endpoints_say_the_same()
    {
        EditOutcome outcome = new(
            [EditResult.Ok(4), EditResult.Failed(-1, "bad")], [], [], RolledBack: true);

        JsonElement adds = Json(ApplyEditsResponse.One(outcome, NothingRejected(), ApplyEditsResponse.EditKind.Add))
            .GetProperty("addResults");

        Assert.All(adds.EnumerateArray(), add => Assert.False(add.GetProperty("success").GetBoolean()));
    }

    [Fact]
    public void An_edit_moment_asked_for_is_returned_in_epoch_milliseconds()
    {
        DateTimeOffset moment = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        EditOutcome outcome = new([EditResult.Ok(4)], [], [], RolledBack: false);

        JsonElement full = Json(ApplyEditsResponse.Build(outcome, NothingRejected(), moment));
        JsonElement one = Json(ApplyEditsResponse.One(outcome, NothingRejected(), ApplyEditsResponse.EditKind.Add, moment));

        Assert.Equal(moment.ToUnixTimeMilliseconds(), full.GetProperty("editMoment").GetInt64());
        Assert.Equal(moment.ToUnixTimeMilliseconds(), one.GetProperty("editMoment").GetInt64());
    }

    [Fact]
    public void A_rolled_back_batch_has_no_edit_moment_and_one_not_asked_for_has_none_either()
    {
        DateTimeOffset moment = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

        JsonElement rolledBack = Json(ApplyEditsResponse.Build(
            new EditOutcome([EditResult.Failed(-1, "bad")], [], [], RolledBack: true), NothingRejected(), moment));
        JsonElement notAsked = Json(ApplyEditsResponse.Build(
            new EditOutcome([EditResult.Ok(4)], [], [], RolledBack: false), NothingRejected()));

        Assert.False(rolledBack.TryGetProperty("editMoment", out _));
        Assert.False(notAsked.TryGetProperty("editMoment", out _));
    }

    [Fact]
    public void Without_a_rollback_a_success_is_a_success()
    {
        EditOutcome outcome = new(
            [EditResult.Ok(4), EditResult.Failed(-1, "bad")], [], [], RolledBack: false);

        JsonElement[] adds = [.. Json(ApplyEditsResponse.Build(outcome, NothingRejected()))
            .GetProperty("addResults").EnumerateArray()];

        Assert.True(adds[0].GetProperty("success").GetBoolean());
        Assert.Equal(4, adds[0].GetProperty("objectId").GetInt64());
        Assert.False(adds[1].GetProperty("success").GetBoolean());
    }
}
