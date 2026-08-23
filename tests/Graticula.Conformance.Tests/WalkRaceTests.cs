using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The walks tell a service that was deleted from a service that is broken.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-89](../../docs/architecture-debt.md) is about a false failure, and the danger of
/// repairing one is a true failure that stops being reported.</b> Six classes here enumerate
/// every service the server has and then ask each one about itself, while three others publish
/// and delete fixtures beside them. A service deleted between the listing and the question
/// answers 404, and the walk blamed the server. The repair is that a 404 is a question rather
/// than a verdict: the catalogue is read again, and only a service it still lists makes the 404
/// a defect.
/// </para>
/// <para>
/// <b>These two tests are the repair's own check, and they exist because the failure mode is
/// silence.</b> A `catch` that swallowed every 404 would make the suite green and useless, and
/// nothing in a green run would say so. So one test proves a real 404 still fails, and the other
/// proves a vanished service still passes.
/// </para>
/// </remarks>
public sealed class WalkRaceTests : ArcGisClient
{
    /// <summary>A 404 about a service the catalogue still lists fails the walk.</summary>
    /// <remarks>
    /// <b>Layer 999 of a real service is the honest way to stage this.</b> The service exists,
    /// the catalogue lists it, and the document is genuinely not there — which is exactly the
    /// shape of the defect a conformance walk exists to find, and exactly the shape of the
    /// fixture race it must not be confused with. The difference is the second read, and this
    /// asserts the second read reaches the right conclusion.
    /// </remarks>
    [Fact]
    public async Task A_document_that_is_absent_from_a_service_that_exists_fails_the_walk()
    {
        await RequireServerAsync();

        string? service = await AnyServiceNameAsync();

        Assert.True(
            service is not null,
            "this server publishes no feature service, so there is nothing to ask about");

        XunitException failure = await Assert.ThrowsAnyAsync<XunitException>(
            () => AboutServiceAsync(service, $"/rest/services/{service}/FeatureServer/999"));

        Assert.Contains(service, failure.Message, StringComparison.Ordinal);
        Assert.Contains("still in the services directory", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A 404 about a service nothing lists is skipped without failing.</summary>
    /// <remarks>
    /// <b>The name is one no deployment and no fixture will have.</b> A service that is not in
    /// the directory and answers 404 is the state a deleted fixture leaves behind, and the walk's
    /// only correct response is to move on.
    /// </remarks>
    [Fact]
    public async Task A_document_for_a_service_nobody_lists_is_skipped()
    {
        await RequireServerAsync();

        const string Gone = "zz_walk_race_no_such_service";

        JsonElement? answer = await AboutServiceAsync(
            Gone, $"/rest/services/{Gone}/FeatureServer/0");

        Assert.Null(answer);
    }

    /// <summary>The fixture rule matches what the fixtures are actually called.</summary>
    /// <remarks>
    /// <b>A prefix list is a fact about other files, and it rots when they are renamed.</b> If
    /// this stops matching, the walks start racing again and the symptom is a failure that moves
    /// between runs — which is the lesson D-89 says a suite must never teach.
    /// </remarks>
    [Theory]
    [InlineData("zz_scope_org", true)]
    [InlineData("hosted/zz_delete_me", true)]
    [InlineData("hosted/corpus_holed_1234", true)]
    [InlineData("hosted/look_buildings", false)]
    [InlineData("turkiye/tr_ref", false)]
    public void The_fixture_rule_names_fixtures_and_nothing_else(string name, bool fixture) =>
        Assert.Equal(fixture, Fixture(name));
}
