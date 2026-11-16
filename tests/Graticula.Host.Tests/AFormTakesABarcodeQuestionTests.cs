using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Host.Forms;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-287 §5.22, 2026-11-16: the <c>barcode</c> question, refused until today because Safari has no barcode reader, is
/// accepted. It is a text field that can always be typed into; the scanner is the client's, where the browser has one.
/// What Survey123 documents for it — the <c>minimal</c> appearance, a field length, <c>barcodeType</c> in
/// <c>body::esri:style</c> — is read, and what would change a scanned code or capture nothing is refused in words.
/// </summary>
public sealed class AFormTakesABarcodeQuestionTests
{
    private static readonly string[] Header =
        ["type", "name", "label", "appearance", "parameters", "bind::esri:fieldType", "bind::esri:fieldLength", "body::esri:style", "body::esri:inputMask", "bind::esri:parameters", "calculation"];

    private static FormCheck Checked(params string[][] survey) =>
        FormDefinition.Check(new FormModel([
            new ModelSheet("survey", Header, survey.Select(r => (IReadOnlyList<string>)r).ToList()),
            new ModelSheet("choices", ["list_name", "name", "label"], []),
        ]));

    private static string[] Row(string type = "barcode", string appearance = "", string parameters = "", string fieldType = "",
        string fieldLength = "", string style = "", string mask = "", string esriParameters = "", string calculation = "") =>
        [type, "kod", "Kod", appearance, parameters, fieldType, fieldLength, style, mask, esriParameters, calculation];

    private static FormDefinition Accepted(FormCheck check)
    {
        Assert.True(check.Accepted, string.Join("; ", check.Refusals.Select(r => $"{r.Row} {r.Column}: {r.Message}")));
        return check.Definition!;
    }

    private static void RefusedAt(FormCheck check, int row, string column, string words)
    {
        Assert.False(check.Accepted);
        Assert.Contains(check.Refusals, r => r.Row == row && string.Equals(r.Column, column, StringComparison.OrdinalIgnoreCase)
            && r.Message.Contains(words, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_barcode_is_accepted_and_is_no_longer_on_the_refused_list()
    {
        FormDefinition form = Accepted(Checked(Row()));

        FormQuestion q = Assert.Single(form.Root.Questions);
        Assert.Equal("barcode", q.Type);
        Assert.Contains("barcode", (IReadOnlySet<string>)FormCatalog.InputTypes);
        Assert.DoesNotContain("barcode", FormCatalog.RefusedTypes.Keys);
    }

    [Fact]
    public void A_barcode_makes_a_text_field_of_the_default_length_or_of_the_length_the_form_gives()
    {
        PlannedField plain = Assert.Single(Assert.Single(FormLayerPlan.For(Accepted(Checked(Row())), "Kodlar").Layers).Fields);
        Assert.Equal(Graticula.Features.FieldType.Text, plain.Type);
        Assert.Equal(FormLayerPlan.DefaultTextLength, plain.Length);

        PlannedField sized = Assert.Single(Assert.Single(FormLayerPlan.For(Accepted(Checked(Row(fieldLength: "48"))), "Kodlar").Layers).Fields);
        Assert.Equal(Graticula.Features.FieldType.Text, sized.Type);
        Assert.Equal(48, sized.Length);

        RefusedAt(Checked(Row(fieldLength: "0")), 2, "bind::esri:fieldLength", "whole number from 1 to 10000");
    }

    [Fact]
    public void A_barcode_is_stored_as_text_or_not_at_all_and_never_as_a_number()
    {
        Accepted(Checked(Row(fieldType: "esriFieldTypeString")));

        FormLayerPlan unstored = FormLayerPlan.For(Accepted(Checked(Row(fieldType: "null"), ["text", "ad", "Ad", "", "", "", "", "", "", "", ""])), "Kodlar");
        Assert.Equal(["ad"], Assert.Single(unstored.Layers).Fields.Select(f => f.Column));

        RefusedAt(Checked(Row(fieldType: "esriFieldTypeInteger")), 2, "bind::esri:fieldType", "leading zeros");
        RefusedAt(Checked(Row(fieldType: "esriFieldTypeDouble")), 2, "bind::esri:fieldType", "stored as text");
    }

    [Theory]
    [InlineData("minimal")]
    [InlineData("hidden-answer")]
    public void Survey123s_minimal_and_odks_hidden_answer_are_accepted_and_sent_to_the_client(string appearance)
    {
        FormCheck check = Checked(Row(appearance: appearance));
        FormDefinition form = Accepted(check);
        Assert.DoesNotContain(check.Notices, n => n.Column == "appearance");

        JsonObject client = FormCompiled.Json(form);
        JsonNode question = client["root"]!["body"]!.AsArray()[0]!;
        Assert.Equal("barcode", (string?)question["type"]);
        Assert.Equal([appearance], question["appearance"]!.AsArray().Select(a => (string?)a));
    }

    [Fact]
    public void Hidden_answer_belongs_on_a_barcode_only()
    {
        RefusedAt(Checked(Row(type: "text", appearance: "hidden-answer")), 2, "appearance", "applies to barcode");
    }

    [Fact]
    public void Barcode_type_in_the_style_column_is_accepted_and_the_upload_says_every_format_the_browser_reads_is_read()
    {
        FormCheck check = Checked(Row(style: "barcodeType=qrcode,ean13"));
        Accepted(check);
        Assert.Contains(check.Notices, n => n.Column == "body::esri:style");
    }

    [Fact]
    public void What_a_barcode_does_not_take_is_refused_in_words()
    {
        // A camera choice: ODK's front, and Survey123's new-front, are not built.
        RefusedAt(Checked(Row(appearance: "front")), 2, "appearance", "not an appearance");
        RefusedAt(Checked(Row(appearance: "new-front")), 2, "appearance", "choosing the camera");
        // Survey123's barcode takes no parameters column; an input mask and a calculation mode are text's, not a barcode's.
        RefusedAt(Checked(Row(parameters: "barcodeType=qrcode")), 2, "parameters", "'barcode' takes no parameters");
        RefusedAt(Checked(Row(mask: "999")), 2, "body::esri:inputMask", "belongs on a text question");
        RefusedAt(Checked(Row(esriParameters: "calculationMode=manual", calculation: "'x'")), 2, "bind::esri:parameters", "calculationMode");
        // A multiline box has no meaning for a code.
        RefusedAt(Checked(Row(appearance: "multiline")), 2, "appearance", "applies to");
    }

    [Fact]
    public void A_barcode_can_be_required_have_a_default_and_be_read_by_another_questions_expression()
    {
        FormDefinition form = Accepted(Checked(
            Row(),
            ["calculate", "uzunluk", "", "", "", "", "", "", "", "", "string-length(${kod})"]));
        Assert.Equal(["kod", "uzunluk"], form.Root.Questions.Select(q => q.Name));
    }
}
