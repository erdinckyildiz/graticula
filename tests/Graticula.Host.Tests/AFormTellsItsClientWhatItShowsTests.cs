using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Host.Forms;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-287 §5.10, 2026-11-15: what the form client is sent — guidance hints, pictures, the pages it draws, the ground
/// under its map, the thank-you screen — and what is refused at upload so that nothing is accepted and ignored.
/// </summary>
public sealed class AFormTellsItsClientWhatItShowsTests
{
    private static FormModel Form(string[] header, IEnumerable<string[]> survey, IEnumerable<string[]>? choices = null, string[]? choiceHeader = null,
        string[][]? settings = null) =>
        new([
            new ModelSheet("survey", header, survey.Select(r => (IReadOnlyList<string>)r).ToList()),
            new ModelSheet("choices", choiceHeader ?? ["list_name", "name", "label"], (choices ?? []).Select(r => (IReadOnlyList<string>)r).ToList()),
            .. settings is null ? Array.Empty<ModelSheet>() : [new ModelSheet("settings", settings[0], settings.Skip(1).Select(r => (IReadOnlyList<string>)r).ToList())],
        ]);

    private static JsonObject Client(FormModel model)
    {
        FormCheck check = FormDefinition.Check(model);
        Assert.True(check.Accepted, string.Join("; ", check.Refusals.Select(r => $"{r.Sheet} {r.Row} {r.Column}: {r.Message}")));
        return FormCompiled.Json(check.Definition!);
    }

    private static JsonObject FirstQuestion(JsonObject client) => (JsonObject)client["root"]!["body"]!.AsArray()[0]!;

    private static void RefusedAt(FormCheck check, string sheet, int row, string column, string words)
    {
        Assert.False(check.Accepted);
        Assert.Contains(check.Refusals, r => r.Sheet == sheet && r.Row == row && string.Equals(r.Column, column, StringComparison.OrdinalIgnoreCase)
            && r.Message.Contains(words, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_guidance_hint_is_sent_per_language_beside_the_hint()
    {
        JsonObject client = Client(Form(
            ["type", "name", "label::Türkçe", "label::English", "hint::Türkçe", "guidance_hint::Türkçe", "guidance_hint::English"],
            [["text", "ad", "Ad", "Name", "Kısa ipucu", "Uzun yardım", "Longer help"]]));

        JsonObject q = FirstQuestion(client);
        Assert.Equal("Uzun yardım", (string?)q["guidanceHints"]!["Türkçe"]);
        Assert.Equal("Longer help", (string?)q["guidanceHints"]!["English"]);
        Assert.Equal("Kısa ipucu", (string?)q["hints"]!["Türkçe"]);
    }

    [Fact]
    public void A_picture_on_a_question_and_on_its_choices_is_sent_by_file_name()
    {
        JsonObject client = Client(Form(
            ["type", "name", "label", "media::image"],
            [["select_one renk", "renk", "Renk", "palet.png"]],
            [["renk", "kirmizi", "Kırmızı", "kirmizi.jpg"], ["renk", "mavi", "Mavi", ""]],
            ["list_name", "name", "label", "media::image"]));

        Assert.Equal("palet.png", (string?)FirstQuestion(client)["images"]![""]);
        JsonArray list = client["choices"]!["renk"]!.AsArray();
        Assert.Equal("kirmizi.jpg", (string?)list[0]!["images"]![""]);
        Assert.Empty(list[1]!["images"]!.AsObject());
    }

    [Fact]
    public void The_older_spelling_of_the_picture_column_is_read_and_the_newer_one_wins()
    {
        JsonObject client = Client(Form(
            ["type", "name", "label", "image", "media::image"],
            [["note", "a", "Bir", "eski.png", "yeni.png"], ["note", "b", "İki", "yalniz.png", ""]]));

        JsonArray body = client["root"]!["body"]!.AsArray();
        Assert.Equal("yeni.png", (string?)body[0]!["images"]![""]);
        Assert.Equal("yalniz.png", (string?)body[1]!["images"]![""]);
    }

    [Theory]
    [InlineData("klasor/foto.png", "no folder")]
    [InlineData("..\\foto.png", "no folder")]
    [InlineData("https://example.org/foto.png", "no folder")]
    [InlineData("foto.exe", "none of them")]
    [InlineData("foto", "none of them")]
    public void A_picture_that_is_not_a_plain_image_file_is_refused_at_its_row_and_column(string name, string words)
    {
        FormCheck check = FormDefinition.Check(Form(["type", "name", "label", "media::image"], [["note", "a", "Bir", name]]));
        RefusedAt(check, "survey", 2, "media::image", words);
    }

    [Fact]
    public void A_drop_down_over_choices_with_pictures_is_refused_because_it_could_not_show_them()
    {
        FormCheck check = FormDefinition.Check(Form(["type", "name", "label", "appearance"], [["select_one renk", "renk", "Renk", "minimal"]],
            [["renk", "kirmizi", "Kırmızı", "kirmizi.jpg"]], ["list_name", "name", "label", "media::image"]));
        RefusedAt(check, "survey", 2, "appearance", "drop-down");
    }

    [Fact]
    public void A_choices_picture_is_refused_on_the_choices_sheet()
    {
        FormCheck check = FormDefinition.Check(Form(["type", "name", "label"], [["select_one renk", "renk", "Renk"]],
            [["renk", "kirmizi", "Kırmızı", "x/y.png"]], ["list_name", "name", "label", "media::image"]));
        RefusedAt(check, "choices", 2, "media::image", "no folder");
    }

    [Fact]
    public void A_picture_or_guidance_hint_on_a_group_or_repeat_is_refused_because_a_heading_cannot_carry_it()
    {
        FormCheck check = FormDefinition.Check(Form(["type", "name", "label", "media::image", "guidance_hint"], [
            ["begin group", "g", "Grup", "g.png", ""],
            ["text", "a", "A", "", ""],
            ["end group", "", "", "", ""],
            ["begin repeat", "r", "Tekrar", "", "uzun"],
            ["text", "b", "B", "", ""],
            ["end repeat", "", "", "", ""]]));

        RefusedAt(check, "survey", 2, "media::image", "heading");
        RefusedAt(check, "survey", 5, "guidance_hint", "no hint");
    }

    [Fact]
    public void Every_picture_the_form_names_is_listed_with_its_sheet_row_and_column()
    {
        FormCheck check = FormDefinition.Check(Form(
            ["type", "name", "label", "media::image::Türkçe", "label::Türkçe"],
            [["select_one renk", "renk", "Renk", "palet.png", "Renk"]],
            [["renk", "kirmizi", "Kırmızı", "kirmizi.jpg"]],
            ["list_name", "name", "label", "media::image"]));

        Assert.True(check.Accepted, string.Join("; ", check.Refusals.Select(r => r.Message)));
        Assert.Equal(
            [("survey", 2, "media::image::Türkçe", "palet.png"), ("choices", 2, "media::image", "kirmizi.jpg")],
            check.Definition!.MediaReferences().ToArray());
    }

    [Fact]
    public void A_table_list_group_of_selects_over_one_list_is_accepted_and_reaches_the_client_as_a_group_with_its_appearance()
    {
        JsonObject client = Client(Form(["type", "name", "label", "appearance"], [
            ["begin group", "matris", "Memnuniyet", "table-list"],
            ["select_one olcek", "hiz", "Hız", ""],
            ["select_one olcek", "temizlik", "Temizlik", ""],
            ["end group", "", "", ""]],
            [["olcek", "iyi", "İyi"], ["olcek", "kotu", "Kötü"]]));

        JsonObject group = (JsonObject)client["root"]!["body"]!.AsArray()[0]!;
        Assert.Equal("group", (string?)group["kind"]);
        Assert.Equal("table-list", (string?)group["appearance"]![0]);
        Assert.Equal(2, group["body"]!.AsArray().Count);
    }

    [Fact]
    public void A_table_list_with_anything_but_selects_over_one_list_is_refused()
    {
        string[] header = ["type", "name", "label", "appearance", "choice_filter"];
        string[][] lists = [["olcek", "iyi", "İyi"], ["olcek", "kotu", "Kötü"], ["baska", "x", "X"]];

        RefusedAt(FormDefinition.Check(Form(header, [
            ["begin group", "m", "M", "table-list", ""], ["select_one olcek", "a", "A", "", ""], ["text", "b", "B", "", ""], ["end group", "", "", "", ""]], lists)),
            "survey", 2, "appearance", "only select_one and select_multiple");
        RefusedAt(FormDefinition.Check(Form(header, [
            ["begin group", "m", "M", "table-list", ""], ["select_one olcek", "a", "A", "", ""], ["select_one baska", "b", "B", "", ""], ["end group", "", "", "", ""]], lists)),
            "survey", 4, "type", "shares one choice list");
        RefusedAt(FormDefinition.Check(Form(header, [
            ["begin group", "m", "M", "table-list", ""], ["select_one olcek", "a", "A", "", "true()"], ["end group", "", "", "", ""]], lists)),
            "survey", 3, "choice_filter", "same for every row");
        RefusedAt(FormDefinition.Check(Form(header, [
            ["begin group", "m", "M", "table-list", ""], ["end group", "", "", "", ""]], lists)),
            "survey", 2, "appearance", "no questions");
    }

    [Fact]
    public void A_table_list_is_refused_on_a_repeat_by_the_appearances_own_rule()
    {
        FormCheck check = FormDefinition.Check(Form(["type", "name", "label", "appearance"], [
            ["begin repeat", "r", "R", "table-list"], ["text", "a", "A", ""], ["end repeat", "", "", ""]]));
        RefusedAt(check, "survey", 2, "appearance", "applies to begin group");
    }

    [Fact]
    public void A_field_list_says_when_it_changes_nothing_and_is_quiet_under_the_pages_style()
    {
        string[] header = ["type", "name", "label", "appearance"];
        string[][] survey = [["begin group", "g", "G", "field-list"], ["text", "a", "A", ""], ["end group", "", "", ""]];

        FormCheck plain = FormDefinition.Check(Form(header, survey));
        Assert.True(plain.Accepted);
        Assert.Contains(plain.Notices, n => n.Row == 2 && n.Column == "appearance" && n.Message.Contains("one page", StringComparison.Ordinal));

        FormCheck paged = FormDefinition.Check(Form(header, survey, settings: [["style"], ["pages"]]));
        Assert.True(paged.Accepted);
        Assert.DoesNotContain(paged.Notices, n => n.Message.Contains("field-list", StringComparison.Ordinal));
    }

    [Fact]
    public void The_ground_the_thank_you_and_the_redirect_are_settings_that_reach_the_client()
    {
        JsonObject client = Client(Form(["type", "name", "label"], [["text", "a", "A"]], settings: [
            ["form_title", "graticula:basemap", "graticula:thank_you", "graticula:redirect"],
            ["Deneme", "Topo", "Teşekkürler!", "https://example.org/tesekkur"]]));

        Assert.Equal("topo", (string?)client["basemap"]);
        Assert.Equal("Teşekkürler!", (string?)client["thankYou"]);
        Assert.Equal("https://example.org/tesekkur", (string?)client["redirect"]);

        JsonObject plain = Client(Form(["type", "name", "label"], [["text", "a", "A"]]));
        Assert.Null(plain["basemap"]);
        Assert.Null(plain["redirect"]);
    }

    [Theory]
    [InlineData("graticula:basemap", "satellite", "not a basemap")]
    [InlineData("graticula:redirect", "javascript:alert(1)", "http or https")]
    [InlineData("graticula:redirect", "ftp://example.org/x", "http or https")]
    [InlineData("graticula:redirect", "/relative/path", "http or https")]
    [InlineData("graticula:redirect", "https://user:pass@example.org/", "http or https")]
    public void A_setting_the_client_could_not_honour_is_refused_at_its_column(string column, string value, string words)
    {
        FormCheck check = FormDefinition.Check(Form(["type", "name", "label"], [["text", "a", "A"]], settings: [[column], [value]]));
        RefusedAt(check, "settings", 2, column, words);
    }
}
