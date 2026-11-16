using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Host.Forms;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-287 §5.17: a form whose settings sheet has <c>submission_url</c> is matched to the layer it names, and what would
/// fail the first submission is refused at publishing, with the row and the column — before anything is written.
/// </summary>
public sealed class AFormThatNamesALayerIsMatchedToItTests
{
    // ------------------------------------------------------------------------------------------------------ the address

    [Theory]
    [InlineData("https://gis.example.com/rest/services/hosted/inspections/FeatureServer/0", null, "gis.example.com", "hosted", "inspections", 0)]
    [InlineData("https://gis.example.com:8443/rest/services/hosted/inspections/FeatureServer/2/", null, "gis.example.com:8443", "hosted", "inspections", 2)]
    [InlineData("/rest/services/hosted/inspections/FeatureServer/1", null, null, "hosted", "inspections", 1)]
    [InlineData("/rest/services/hosted/inspections/FeatureServer", null, null, "hosted", "inspections", null)]
    [InlineData("/arcgis/rest/services/hosted/inspections/FeatureServer/0", null, null, "hosted", "inspections", 0)]
    [InlineData("https://h/server/rest/services/hosted/inspections/FeatureServer/0?f=json", null, "h", "hosted", "inspections", 0)]
    [InlineData("/rest/services/inspections/FeatureServer/0", null, null, null, "inspections", 0)]
    [InlineData("/gis/rest/services/hosted/inspections/FeatureServer/0", "/gis", null, "hosted", "inspections", 0)]
    [InlineData("/rest/services/hosted/Bina%20Denetimi/FeatureServer/0", null, null, "hosted", "Bina Denetimi", 0)]
    public void Every_form_an_ArcGIS_address_takes_is_read(string url, string? pathBase, string? authority, string? folder, string service, int? layer)
    {
        Assert.True(SubmissionTarget.TryParse(url, out SubmissionAddress? address, out string? error, pathBase), error);
        Assert.Equal((authority, folder, service, layer), (address!.Authority, address.Folder, address.Service, address.Layer));
    }

    [Theory]
    [InlineData("https://gis.example.com/rest/services/hosted/inspections/MapServer/0")]
    [InlineData("https://gis.example.com/rest/services/hosted/inspections/FeatureServer/zero")]
    [InlineData("https://gis.example.com/somewhere/else")]
    [InlineData("/rest/services/FeatureServer/0")]
    [InlineData("/rest/services/a/b/c/FeatureServer/0")]
    [InlineData("//gis.example.com/rest/services/hosted/inspections/FeatureServer/0")]
    [InlineData("ftp://gis.example.com/rest/services/hosted/inspections/FeatureServer/0")]
    [InlineData("inspections")]
    [InlineData("https://user:secret@gis.example.com/rest/services/hosted/inspections/FeatureServer/0")]
    public void Anything_else_is_refused_with_a_sentence_about_what_an_address_looks_like(string url)
    {
        Assert.False(SubmissionTarget.TryParse(url, out SubmissionAddress? address, out string? error, string.Empty));
        Assert.Null(address);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.DoesNotContain("secret", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_base_the_server_does_not_run_under_is_not_stripped_when_publishing_knows_the_server_has_none()
    {
        Assert.False(SubmissionTarget.TryParse("/gis/rest/services/hosted/x/FeatureServer/0", out _, out _, string.Empty));
        Assert.True(SubmissionTarget.TryParse("/gis/rest/services/hosted/x/FeatureServer/0", out _, out _, pathBase: null));
    }

    [Theory]
    [InlineData("https://gis.example.com/rest/services/hosted/x/FeatureServer/0", "gis.example.com", "https", true)]
    [InlineData("https://GIS.example.com:443/rest/services/hosted/x/FeatureServer/0", "gis.example.com", "https", true)]
    [InlineData("https://gis.example.com:8443/rest/services/hosted/x/FeatureServer/0", "gis.example.com:8443", "https", true)]
    [InlineData("/rest/services/hosted/x/FeatureServer/0", "anything.example.com", "https", true)]
    [InlineData("https://other.example.com/rest/services/hosted/x/FeatureServer/0", "gis.example.com", "https", false)]
    [InlineData("https://gis.example.com:9000/rest/services/hosted/x/FeatureServer/0", "gis.example.com", "https", false)]
    [InlineData("http://gis.example.com/rest/services/hosted/x/FeatureServer/0", "gis.example.com", "https", false)]
    [InlineData("https://gis.example.com/rest/services/hosted/x/FeatureServer/0", "", "https", false)]
    public void An_address_is_this_servers_when_its_host_and_port_are_the_ones_that_answered(string url, string host, string scheme, bool own)
    {
        Assert.True(SubmissionTarget.TryParse(url, out SubmissionAddress? address, out string? error), error);
        Assert.Equal(own, SubmissionTarget.IsOwn(address!, host, scheme));
    }

    [Fact]
    public void A_form_naming_something_that_is_not_a_layer_is_refused_wherever_it_is_checked()
    {
        FormCheck bad = FormDefinition.Check(Model("https://x.example.com/not/a/layer"));
        FormIssue refusal = Assert.Single(bad.Refusals);
        Assert.Equal(("settings", 2, "submission_url"), (refusal.Sheet, refusal.Row, refusal.Column));

        Assert.True(FormDefinition.Check(Model("/rest/services/hosted/x/FeatureServer/0")).Accepted);
        Assert.True(FormDefinition.Check(Model(null)).Accepted);
    }

    // ------------------------------------------------------------------------------------------------------- the match

    private static FormModel Model(string? url, string[][]? survey = null)
    {
        string[] header = ["type", "name", "label", "bind::esri:fieldLength", "bind::esri:fieldType"];
        string[][] rows = survey ??
        [
            ["geopoint", "yer", "Place"],
            ["text", "ad", "Name"],
            ["integer", "kat", "Floor"],
            ["select_one hasar", "hasar", "Damage"],
            ["image", "foto", "Photo"],
            ["begin repeat", "odalar", "Rooms"],
            ["text", "oda", "Room"],
            ["end repeat"],
        ];

        List<ModelSheet> sheets =
        [
            new("survey", header, rows.Select(r => (IReadOnlyList<string>)r).ToList()),
            new("choices", ["list_name", "name", "label"], [["hasar", "yok", "None"], ["hasar", "agir", "Heavy"]]),
        ];

        sheets.Add(url is null
            ? new ModelSheet("settings", ["form_id"], [["x"]])
            : new ModelSheet("settings", ["form_id", "submission_url"], [["x", url]]));
        return new FormModel(sheets);
    }

    private static (FormDefinition Definition, FormLayerPlan Plan) Plan(string[][]? survey = null)
    {
        FormCheck check = FormDefinition.Check(Model("/rest/services/hosted/inspections/FeatureServer/0", survey));
        Assert.True(check.Accepted, string.Join("; ", check.Refusals.Select(r => $"{r.Row} {r.Column}: {r.Message}")));
        FormLayerPlan plan = FormLayerPlan.For(check.Definition!, "unused");
        Assert.True(plan.Accepted);
        return (check.Definition!, plan);
    }

    private static readonly Guid TopId = Guid.NewGuid();

    private static readonly Guid RoomsId = Guid.NewGuid();

    private static FieldDescription Field(string name, FieldType type, int? length = null, FieldDomain? domain = null) =>
        new FieldDescription(name, type, true, length) { Domain = domain };

    private static FieldDescription[] Standard => [Field("objectid", FieldType.Integer), Field("globalid", FieldType.Guid)];

    private static ExistingLayer Top(
        IEnumerable<FieldDescription>? fields = null, GeometryKind? geometry = GeometryKind.Point, bool hosted = true, bool attachments = true,
        string related = "odalar", string originKey = "globalid", string relatedKey = "parentglobalid") =>
        new(0, TopId, "inspections", geometry, hosted, attachments,
            [.. Standard, Field("ad", FieldType.Text, 255), Field("kat", FieldType.Integer), Field("hasar", FieldType.Text, 255), .. fields ?? []],
            related.Length == 0 ? [] : [new ExistingRelationship(originKey, RoomsId, relatedKey, 7)]);

    private static ExistingLayer Rooms(string name = "odalar", bool withParent = true) =>
        new(1, RoomsId, name, null, true, true,
            [.. Standard, .. withParent ? new[] { Field("parentglobalid", FieldType.Guid) } : Array.Empty<FieldDescription>(), Field("oda", FieldType.Text, 255)], []);

    private static SubmissionMatch Match(ExistingLayer top, ExistingLayer? rooms = null, int? layer = 0, string[][]? survey = null)
    {
        (FormDefinition definition, FormLayerPlan plan) = Plan(survey);
        SubmissionAddress address = new(null, null, "hosted", "inspections", layer);
        return SubmissionMatcher.Match(definition, plan, address, new ExistingService("hosted", "inspections", [top, .. rooms is null ? Array.Empty<ExistingLayer>() : new[] { rooms }]));
    }

    private static string Said(SubmissionMatch match) =>
        string.Join("\n", match.Refusals.Select(r => $"{r.Sheet} row {r.Row} {r.Column}: {r.Message}"));

    [Fact]
    public void A_layer_with_the_form_s_fields_a_related_table_and_attachments_is_matched()
    {
        SubmissionMatch match = Match(Top(), Rooms());

        Assert.True(match.Accepted, Said(match));
        Assert.Equal(["inspections", "odalar"], match.Matched.Select(m => m.Existing.Name));
        Assert.Equal(7, match.Matched[1].RelationshipNumber);
    }

    [Fact]
    public void A_field_the_layer_lacks_is_refused_naming_the_question_s_row_and_nothing_is_added()
    {
        SubmissionMatch match = Match(Top(fields: null).WithFields(Standard.Concat([Field("ad", FieldType.Text, 255)]).ToArray()), Rooms());

        Assert.False(match.Accepted);
        FormIssue kat = match.Refusals.First(r => r.Message.Contains("'kat'", StringComparison.Ordinal));
        Assert.Equal(("survey", 4, "name"), (kat.Sheet, kat.Row, kat.Column));
        Assert.Contains("Nothing is added to an existing layer", kat.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_that_does_not_fit_is_refused_at_the_row_and_the_column_that_decided_it()
    {
        ExistingLayer top = Top().WithFields([.. Standard, Field("ad", FieldType.Integer), Field("kat", FieldType.Text, 20), Field("hasar", FieldType.Text, 255)]);
        SubmissionMatch match = Match(top, Rooms());

        Assert.Equal(2, match.Refusals.Count);
        Assert.Contains(match.Refusals, r => r is { Row: 3, Column: "type" } && r.Message.Contains("'ad'", StringComparison.Ordinal));
        Assert.Contains(match.Refusals, r => r is { Row: 4, Column: "type" } && r.Message.Contains("'kat'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_number_goes_into_a_wider_field_and_nothing_else_changes_type()
    {
        Assert.True(SubmissionMatcher.Compatible(FieldType.Integer, FieldType.Double));
        Assert.True(SubmissionMatcher.Compatible(FieldType.Integer, FieldType.BigInteger));
        Assert.True(SubmissionMatcher.Compatible(FieldType.SmallInteger, FieldType.Integer));
        Assert.False(SubmissionMatcher.Compatible(FieldType.Double, FieldType.Integer));
        Assert.False(SubmissionMatcher.Compatible(FieldType.Text, FieldType.Integer));
        Assert.False(SubmissionMatcher.Compatible(FieldType.Date, FieldType.Text));
        Assert.True(SubmissionMatcher.Compatible(FieldType.Guid, FieldType.Guid));
    }

    [Fact]
    public void A_text_length_the_form_states_is_checked_and_the_default_is_not()
    {
        string[][] survey =
        [
            ["geopoint", "yer", "Place"], ["text", "ad", "Name", "100"], ["integer", "kat", "Floor"], ["select_one hasar", "hasar", "Damage"], ["image", "foto", "Photo"],
        ];
        ExistingLayer narrow = Top().WithFields([.. Standard, Field("ad", FieldType.Text, 50), Field("kat", FieldType.Integer), Field("hasar", FieldType.Text, 20)]);
        SubmissionMatch match = Match(narrow, survey: survey);

        FormIssue only = Assert.Single(match.Refusals);
        Assert.Equal((3, "bind::esri:fieldLength"), (only.Row, only.Column));
        Assert.Contains("holds 50", only.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_choice_the_fields_own_domain_would_refuse_is_found_before_somebody_picks_it()
    {
        FieldDomain domain = FieldDomain.Coded("damage", [new CodedValue(DomainValue.Of("yok"), "None")]);
        ExistingLayer top = Top().WithFields([.. Standard, Field("ad", FieldType.Text, 255), Field("kat", FieldType.Integer), Field("hasar", FieldType.Text, 255, domain)]);
        SubmissionMatch match = Match(top, Rooms());

        FormIssue refusal = Assert.Single(match.Refusals);
        Assert.Contains("'agir'", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(5, refusal.Row);
    }

    [Fact]
    public void Attachment_questions_need_a_layer_that_keeps_attachments()
    {
        SubmissionMatch match = Match(Top(attachments: false), Rooms());

        FormIssue refusal = Assert.Single(match.Refusals);
        Assert.Equal((6, "type"), (refusal.Row, refusal.Column));
        Assert.Contains("'foto'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_registered_layer_is_refused_because_the_answers_view_is_made_over_hosted_rows()
    {
        SubmissionMatch match = Match(Top(hosted: false), Rooms());

        FormIssue refusal = Assert.Single(match.Refusals);
        Assert.Equal(("settings", 2, "submission_url"), (refusal.Sheet, refusal.Row, refusal.Column));
        Assert.Contains("registered", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repeat_needs_a_related_table_that_already_exists()
    {
        SubmissionMatch none = Match(Top(related: string.Empty), Rooms());
        FormIssue missing = Assert.Single(none.Refusals);
        Assert.Equal(("survey", 7), (missing.Sheet, missing.Row));
        Assert.Contains("Nothing is added", missing.Message, StringComparison.Ordinal);

        SubmissionMatch wrongName = Match(Top(), Rooms(name: "bedrooms"));
        Assert.Single(wrongName.Refusals);

        SubmissionMatch wrongKey = Match(Top(relatedKey: "inspection_id"), Rooms());
        Assert.Contains("parentglobalid", Assert.Single(wrongKey.Refusals).Message, StringComparison.Ordinal);

        SubmissionMatch noParent = Match(Top(), Rooms(withParent: false));
        Assert.Contains("no parentglobalid field", Assert.Single(noParent.Refusals).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_service_address_with_no_layer_means_the_first_layer_of_the_kind_the_form_writes()
    {
        SubmissionMatch match = Match(Top(), Rooms(), layer: null);
        Assert.True(match.Accepted, Said(match));
        Assert.Equal(0, match.Matched[0].Existing.Index);

        SubmissionMatch missing = Match(Top(), Rooms(), layer: 5);
        Assert.Contains("no layer 5", Assert.Single(missing.Refusals).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_form_s_location_and_the_layer_s_geometry_have_to_agree()
    {
        SubmissionMatch line = Match(Top(geometry: GeometryKind.LineString), Rooms());
        Assert.Contains("Point", Assert.Single(line.Refusals).Message, StringComparison.Ordinal);

        SubmissionMatch table = Match(Top(geometry: null), Rooms());
        Assert.Contains("nowhere to go", table.Refusals[0].Message, StringComparison.Ordinal);

        string[][] noLocation = [["text", "ad", "Name"], ["integer", "kat", "Floor"], ["select_one hasar", "hasar", "Damage"]];
        SubmissionMatch geometry = Match(Top(), layer: 0, survey: noLocation);
        Assert.Contains("no location question", Assert.Single(geometry.Refusals).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_layer_without_a_GlobalID_is_refused_and_one_is_not_added()
    {
        ExistingLayer top = Top().WithFields([Field("objectid", FieldType.Integer), Field("ad", FieldType.Text, 255), Field("kat", FieldType.Integer), Field("hasar", FieldType.Text, 255)]);
        SubmissionMatch match = Match(top, Rooms());

        FormIssue refusal = Assert.Single(match.Refusals);
        Assert.Contains("no GlobalID field", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("does not add one", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_mismatch_is_listed_not_the_first()
    {
        ExistingLayer top = Top(attachments: false).WithFields([Field("globalid", FieldType.Guid), Field("ad", FieldType.Integer)]);
        SubmissionMatch match = Match(top, Rooms(withParent: false));

        Assert.True(match.Refusals.Count >= 5, Said(match));
    }
}

internal static class ExistingLayerExtensions
{
    public static ExistingLayer WithFields(this ExistingLayer layer, FieldDescription[] fields) => layer with { Fields = fields };
}
