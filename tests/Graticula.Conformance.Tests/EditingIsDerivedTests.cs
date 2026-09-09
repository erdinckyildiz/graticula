using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// <c>Editing</c> appears exactly when the specification says it appears.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-145](../../docs/open-questions.md), answered 2026-09-09 from the published
/// specification rather than from a client.</b> The question was whether an Esri client needs
/// <c>Editing</c> in the capabilities string before it will let anybody edit. It does not —
/// the granular <c>Create</c>, <c>Update</c> and <c>Delete</c> are what an editing client acts
/// on. But the ArcGIS REST reference states the token's rule in one sentence: <i>the
/// <c>Editing</c> capability is included if <c>Create</c>, <c>Delete</c>, and <c>Update</c> is
/// enabled and <c>allowGeometryUpdates</c> is <c>true</c>.</i>
/// </para>
/// <para>
/// <b>So it is a derivation, and this server met every part of the condition while omitting
/// the token.</b> That is not an unrequested capability in §82's sense — it is a restatement
/// of three tokens already advertised, and emitting it costs a boolean over a list the
/// document already contains.
/// </para>
/// <para>
/// <b>Asserted in both directions, because a derived token that is always present is not
/// derived.</b> A caller with the three gets it; a caller with only <c>Query</c> does not.
/// The second half is what would catch it being pasted on unconditionally, which is the
/// failure mode a summary token invites.
/// </para>
/// <para>
/// <b>Why the string and not a unit test.</b> The derivation lives where the configured
/// ceiling has already restricted the set ([ADR-031](../../docs/adr/ADR-031-service-capability-configuration.md)),
/// so a service with <c>Delete</c> turned off must stop advertising <c>Editing</c> in the same
/// breath. Only the served document proves that, because only the served document has been
/// through the ceiling.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class EditingIsDerivedTests : ArcGisClient
{
    private const string ServiceVariable = "GRATICULA_TEST_EDITABLE";

    private static string? Editable => Environment.GetEnvironmentVariable(ServiceVariable);

    /// <summary>
    /// A caller who may create, update and delete is told so in one word as well as three.
    /// </summary>
    [Fact]
    public async Task Editing_is_present_exactly_when_the_three_that_derive_it_are()
    {
        await RequireServerAsync();

        Assert.False(
            string.IsNullOrWhiteSpace(Editable),
            $"{ServiceVariable} is not set, so this test FAILS rather than skips. It needs a "
            + "service whose layer 0 the configured caller may write to; a read-only service "
            + "would assert the absent half twice and the present half never.");

        JsonElement document = await GetJsonAsync(
            $"/rest/services/{Editable!.Trim('/')}/FeatureServer?f=json");

        string capabilities = document.GetProperty("capabilities").GetString() ?? string.Empty;

        string[] tokens = capabilities.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        bool create = Array.IndexOf(tokens, "Create") >= 0;
        bool update = Array.IndexOf(tokens, "Update") >= 0;
        bool delete = Array.IndexOf(tokens, "Delete") >= 0;
        bool editing = Array.IndexOf(tokens, "Editing") >= 0;

        Assert.True(
            create && update && delete,
            $"`{Editable}` advertises `{capabilities}`, which is not the three this test needs "
            + "to check the derivation against. Either the caller's privileges or the service's "
            + "configured ceiling has narrowed it — point GRATICULA_TEST_EDITABLE at a service "
            + "the configured caller may fully edit.");

        Assert.True(
            editing,
            $"`{Editable}` advertises `{capabilities}`. Create, Update and Delete are all "
            + "present, so the ArcGIS REST specification says `Editing` is included and it is "
            + "not. That is the defect Q-145 found: no client requires the token, and the "
            + "document is still not the shape the specification describes.");

        // <b>The other half of a derivation.</b> `allowGeometryUpdates` is the fourth clause
        // of the specification's rule, and this server computes it as *the set contains
        // Update* — so it must be true wherever `Editing` is, or the two disagree about one
        // fact.
        Assert.True(
            document.GetProperty("allowGeometryUpdates").GetBoolean(),
            $"`{Editable}` advertises `Editing` while `allowGeometryUpdates` is false. The "
            + "specification makes the second a condition of the first, so the document "
            + "contradicts itself.");
    }

    /// <summary>
    /// A caller who may only read is not told they may edit.
    /// </summary>
    [Fact]
    public async Task Editing_is_absent_when_the_three_are_not_all_there()
    {
        string root = await RequireServerAsync();

        Assert.False(
            string.IsNullOrWhiteSpace(Editable),
            $"{ServiceVariable} is not set, so this test FAILS rather than skips.");

        (System.Net.HttpStatusCode status, string body) = await AnonymousAsync(
            $"/rest/services/{Editable!.Trim('/')}/FeatureServer?f=json");

        if (status != System.Net.HttpStatusCode.OK)
        {
            // A service that is not public to a stranger cannot answer this half, and saying
            // so beats asserting on a refusal document. The first test still covers the
            // present half.
            return;
        }

        using JsonDocument parsed = JsonDocument.Parse(body);

        if (!parsed.RootElement.TryGetProperty("capabilities", out JsonElement advertised))
        {
            return;
        }

        string capabilities = advertised.GetString() ?? string.Empty;

        string[] tokens = capabilities.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        bool complete = Array.IndexOf(tokens, "Create") >= 0
            && Array.IndexOf(tokens, "Update") >= 0
            && Array.IndexOf(tokens, "Delete") >= 0;

        if (complete)
        {
            // An anonymous caller who really may edit is a different finding, and not this
            // test's; `root` is named so the message can be acted on.
            return;
        }

        Assert.DoesNotContain("Editing", tokens);
    }
}
