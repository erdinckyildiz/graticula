using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Graticula.Host.Forms;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-287 §5.10 and condition 5: a form is checked against the first version's list; what touches data is supported or
/// refused with its row and column, what only changes the look is drawn the default way with a notice, and nothing is
/// quietly ignored.
/// </summary>
public sealed class AFormIsCheckedAgainstTheFirstVersionsListTests
{
    private static readonly string[] SurveyHeader = ["type", "name", "label", "required", "relevant", "constraint", "calculation", "appearance", "parameters"];

    private static FormModel Form(IEnumerable<string[]> survey, IEnumerable<string[]>? choices = null, string[][]? settings = null,
        string[]? surveyHeader = null) =>
        new([
            new ModelSheet("survey", surveyHeader ?? SurveyHeader, survey.Select(r => (IReadOnlyList<string>)r).ToList()),
            new ModelSheet("choices", ["list_name", "name", "label"], (choices ?? []).Select(r => (IReadOnlyList<string>)r).ToList()),
            .. settings is null ? Array.Empty<ModelSheet>() : [new ModelSheet("settings", settings[0], settings.Skip(1).Select(r => (IReadOnlyList<string>)r).ToList())],
        ]);

    private static FormCheck Checked(params string[][] survey) => FormDefinition.Check(Form(survey, [["renk", "kirmizi", "Kırmızı"], ["renk", "mavi", "Mavi"]]));

    private static void RefusedAt(FormCheck check, int row, string column, string words)
    {
        Assert.False(check.Accepted);
        Assert.Contains(check.Refusals, r => r.Row == row && string.Equals(r.Column, column, StringComparison.OrdinalIgnoreCase)
            && r.Message.Contains(words, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_fixture_is_refused_only_for_a_field_length_that_is_not_a_whole_number()
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Forms", "inspection.xlsx"));
        FormCheck check = FormDefinition.Check(FormModel.ReadWorkbook(file));

        FormIssue only = Assert.Single(check.Refusals);
        Assert.Equal(("survey", 16, "bind::esri:fieldLength"), (only.Sheet, only.Row, only.Column));
        Assert.Contains(check.Notices, n => n.Column == "kendi_sütunumuz" && n.Message.Contains("kept", StringComparison.Ordinal));
        Assert.Contains(check.Notices, n => n.Column == "N" && n.Row == 4);
    }

    [Fact]
    public void A_repeat_inside_a_repeat_is_a_table_related_to_a_table()
    {
        FormCheck check = Checked(
            ["geopoint", "konum", "Konum"],
            ["text", "bina", "Bina"],
            ["begin group", "ek", "Ek bilgiler"],
            ["integer", "yil", "Yapım yılı"],
            ["end group"],
            ["begin repeat", "katlar", "Katlar"],
            ["integer", "kat_no", "Kat"],
            ["begin repeat", "odalar", "Odalar"],
            ["select_one renk", "duvar", "Duvar rengi"],
            ["image", "foto", "Fotoğraf"],
            ["end repeat"],
            ["end repeat"]);

        Assert.True(check.Accepted, string.Join("; ", check.Refusals.Select(r => r.Message)));
        FormTable root = check.Definition!.Root;
        Assert.Equal(["konum", "bina", "yil"], root.Questions.Select(q => q.Name));
        FormTable floors = Assert.Single(root.Repeats);
        Assert.Equal("katlar", floors.Name);
        Assert.Equal(["kat_no"], floors.Questions.Select(q => q.Name));
        FormTable rooms = Assert.Single(floors.Repeats);
        Assert.Equal(["duvar", "foto"], rooms.Questions.Select(q => q.Name));
        Assert.Equal(["", "katlar", "odalar"], root.AllTables.Select(t => t.Name));
        Assert.Equal("renk", rooms.Questions[0].Argument);
    }

    [Theory]
    [InlineData("rank renk", "ordering")]
    [InlineData("barcode", "Safari")]
    [InlineData("audit", "audit")]
    [InlineData("select_one_external renk", "external")]
    [InlineData("osm", "not a question type")]
    [InlineData("select_one_from_file yerler.xml", "CSV")]
    [InlineData("select_one yok_boyle", "not on the choices sheet")]
    public void A_type_off_the_list_is_refused_with_its_reason(string type, string words)
    {
        RefusedAt(Checked([type, "s", "Soru"]), 2, "type", words);
    }

    [Fact]
    public void Each_table_has_at_most_one_location_and_a_repeats_makes_it_a_related_layer()
    {
        RefusedAt(Checked(["geopoint", "a", "A"], ["geoshape", "b", "B"]), 3, "type", "second location question at the top level");
        RefusedAt(Checked(["begin repeat", "r", "R"], ["geopoint", "p", "P"], ["begin group", "g", "G"], ["geotrace", "q", "Q"], ["end group"], ["end repeat"]),
            5, "type", "in the repeat 'r'");

        FormCheck trees = Checked(
            ["geoshape", "parsel", "Parsel"],
            ["begin repeat", "agaclar", "Ağaçlar"],
            ["geopoint", "agac_konum", "Ağacın yeri"],
            ["text", "tur", "Tür"],
            ["end repeat"],
            ["begin repeat", "notlar", "Notlar"],
            ["text", "not", "Not"],
            ["end repeat"]);

        Assert.True(trees.Accepted, string.Join("; ", trees.Refusals.Select(r => r.Message)));
        Assert.Equal("parsel", trees.Definition!.Root.Location!.Name);
        Assert.Equal("agac_konum", trees.Definition.Root.Repeats[0].Location!.Name);
        Assert.Null(trees.Definition.Root.Repeats[1].Location);
    }

    [Fact]
    public void Groups_and_repeats_must_close_and_close_in_order()
    {
        RefusedAt(Checked(["begin group", "g", "G"], ["text", "t", "T"]), 2, "type", "never closed");
        RefusedAt(Checked(["end repeat"]), 2, "type", "closes nothing");
        RefusedAt(Checked(["begin repeat", "r", "R"], ["end group"]), 3, "type", "closes the repeat 'r'");
    }

    [Fact]
    public void Names_are_valid_unique_ignoring_case_and_not_the_layers_own()
    {
        RefusedAt(Checked(["text", "ad", "Ad"], ["text", "AD", "Ad"]), 3, "name", "row 2");
        RefusedAt(Checked(["text", "1ad", "Ad"]), 2, "name", "not a valid name");
        RefusedAt(Checked(["text", "globalid", "G"]), 2, "name", "keeps for itself");
        RefusedAt(Checked(["text", "", "Ad"]), 2, "name", "no name");
        Assert.True(Checked(["text", "şehir_adı", "Şehir"]).Accepted);
    }

    [Fact]
    public void A_question_needs_a_label_and_a_calculate_needs_a_calculation()
    {
        RefusedAt(Checked(["text", "ad", ""]), 2, "label", "no label");
        RefusedAt(Checked(["calculate", "c", ""]), 2, "calculation", "no calculation");
        Assert.True(Checked(["calculate", "c", "", "", "", "", "1 + 1"], ["hidden", "h"], ["start", "s"]).Accepted);
    }

    [Theory]
    [InlineData("pulldata('@layer', 'getFeature', 'x', 'y')", "@layer")]
    [InlineData("pulldata('@json', 'a', 'b')", "@json")]
    [InlineData("pulldata('@javascript', 'f.js', 'run')", "@javascript")]
    [InlineData("pulldata('@exif', ${ad}, 'GPSLatitude')", "@exif")]
    [InlineData("digest(${ad}, 'SHA-256')", "digest")]
    [InlineData("jr:itext('x')", "label::language")]
    [InlineData("frobnicate(${ad})", "not a function")]
    [InlineData("instance('yok')/root/item", "choice list only")]
    [InlineData("lang('tr')", "xml:lang")]
    [InlineData("name(.)", "node names")]
    [InlineData("local-name()", "node names")]
    [InlineData("namespace-uri()", "namespaces")]
    [InlineData("id('x')", "ID attribute")]
    [InlineData("${yok} = 'a'", "names no question")]
    [InlineData("concat(${ad}, 'x'", "never closed")]
    [InlineData("${ad = 'a'", "no closing")]
    public void An_expression_off_the_list_is_refused_with_its_reason(string expression, string words)
    {
        RefusedAt(Checked(["text", "ad", "Ad"], ["text", "b", "B", "", expression]), 3, "relevant", words);
    }

    [Theory]
    [InlineData("selected(${renk}, 'mavi') and count-selected(${renk}) > 0")]
    [InlineData("if(${ad} = '', 'boş', concat('x ', ${ad}))")]
    [InlineData("jr:choice-name(${renk}, '${renk}') != 'if('")]
    [InlineData("pulldata('ilceler', 'ad', 'kod_key', ${ad})")]
    [InlineData("count(instance('renk')/root/item[name = 'mavi']) > 0")]
    [InlineData("format-date(today(), '%Y') >= 2020 and . mod 2 = 0 or not(true())")]
    [InlineData("area(${ad}) div 10000 > 1")]
    // XPath 1.0 core functions the specification's list leaves out: floor, ceiling and last are XPath 1.0 §4.1 and §4.4,
    // substring is XPath 1.0 §4.2 (ODK's substr is its own).
    [InlineData("floor(${ad} div 2) + ceiling(1.5) = 2 and substring(${ad}, 1, 2) != ''")]
    [InlineData("count(${renk}[position() = last()]) = 1")]
    public void An_expression_on_the_list_is_accepted(string expression)
    {
        FormCheck check = Checked(["text", "ad", "Ad"], ["select_multiple renk", "renk", "Renk"], ["text", "b", "B", "", expression]);

        Assert.True(check.Accepted, string.Join("; ", check.Refusals.Select(r => r.Message)));
    }

    [Fact]
    public void Appearances_are_supported_drawn_the_default_way_or_refused()
    {
        RefusedAt(Checked(["text", "t", "T", "", "", "", "", "draw"]), 2, "appearance", "applies to image");
        Assert.True(Checked(["image", "f", "F", "", "", "", "", "draw"]).Accepted);
        RefusedAt(Checked(["text", "t", "T", "", "", "", "", "signature"]), 2, "appearance", "applies to image");
        // search() is accepted since §5.23 (AFormSearchesACsvOrALayerTests); on a question without choices it is still refused.
        RefusedAt(Checked(["text", "t", "T", "", "", "", "", "search('x')"]), 2, "appearance", "search()");
        RefusedAt(Checked(["text", "t", "T", "", "", "", "", "sparkly"]), 2, "appearance", "not an appearance");

        FormCheck drawn = Checked(["select_one renk", "r", "R", "", "", "", "", "minimal horizontal w2"], ["image", "s", "S", "", "", "", "", "signature"]);
        Assert.True(drawn.Accepted);
        Assert.Contains(drawn.Notices, n => n.Message.Contains("'horizontal'", StringComparison.Ordinal));
        Assert.Contains(drawn.Notices, n => n.Message.Contains("'w2'", StringComparison.Ordinal));
        Assert.DoesNotContain(drawn.Notices, n => n.Message.Contains("'minimal'", StringComparison.Ordinal));
    }

    [Fact]
    public void Parameters_are_those_of_range_and_image_only()
    {
        Assert.True(Checked(["range", "r", "R", "", "", "", "", "", "start=0 end=10 step=0.5"]).Accepted);
        RefusedAt(Checked(["range", "r", "R", "", "", "", "", "", "start=0 end=10 step=0"]), 2, "parameters", "greater than zero");
        RefusedAt(Checked(["range", "r", "R", "", "", "", "", "", "start=a"]), 2, "parameters", "needs a number");
        RefusedAt(Checked(["text", "t", "T", "", "", "", "", "", "rows=3"]), 2, "parameters", "takes no parameters");
    }

    [Fact]
    public void Survey_columns_are_supported_drawn_the_default_way_or_refused_and_unknown_ones_are_named()
    {
        string[] header = ["type", "name", "label", "trigger", "body::esri:style", "bind::odk:length", "notlarim"];

        FormCheck check = FormDefinition.Check(Form([["text", "a", "A", "${b}", "", "", ""], ["text", "b", "B", "", "backgroundColor='red'", "5", "kendi notum"]],
            surveyHeader: header));

        RefusedAt(check, 2, "trigger", "triggered");
        RefusedAt(check, 3, "bind::odk:length", "bind::");
        Assert.Contains(check.Notices, n => n.Column == "body::esri:style" && n.Message.Contains("default way", StringComparison.Ordinal));
        Assert.Contains(check.Notices, n => n.Column == "notlarim" && n.Message.Contains("kept", StringComparison.Ordinal));
    }

    [Fact]
    public void Esri_field_types_are_stored_when_this_server_has_them()
    {
        string[] header = ["type", "name", "label", "bind::esri:fieldType", "bind::esri:fieldLength"];
        FormCheck ok = FormDefinition.Check(Form([["date", "d", "D", "esriFieldTypeDateOnly", ""], ["text", "t", "T", "", "40"], ["calculate", "c", "", "null", ""]],
            surveyHeader: ["type", "name", "label", "bind::esri:fieldType", "bind::esri:fieldLength", "calculation"]));
        FormCheck time = FormDefinition.Check(Form([["time", "t", "T", "esriFieldTypeTimeOnly", ""]], surveyHeader: header));
        FormCheck image = FormDefinition.Check(Form([["image", "i", "I", "esriFieldTypeString", ""]], surveyHeader: header));

        Assert.False(ok.Accepted);
        RefusedAt(ok, 4, "calculation", "no calculation");
        RefusedAt(time, 2, "bind::esri:fieldType", "time-only");
        RefusedAt(image, 2, "bind::esri:fieldType", "attachment");
        Assert.Contains(ok.Notices, n => n.Message.Contains("midnight", StringComparison.Ordinal));
    }

    [Fact]
    public void A_duplicate_choice_is_accepted_for_a_select_one_and_refused_for_a_select_multiple_unless_the_settings_allow_it()
    {
        // ADR-287 §5.19 (3): Survey123 accepts duplicates on select_one and not on select_multiple; the settings column
        // of XLSForm's own tools lifts the refusal. The cases in full are in AFormImportsSurvey123SpellingsTests.
        FormModel twice = Form([["select_one renk", "r", "R"]], [["renk", "a", "A"], ["renk", "a", "A2"]]);
        FormModel multiple = Form([["select_multiple renk", "r", "R"]], [["renk", "a", "A"], ["renk", "a", "A2"]]);
        FormModel allowed = Form([["select_multiple renk", "r", "R"]], [["renk", "a", "A"], ["renk", "a", "A2"]],
            [["allow_choice_duplicates"], ["yes"]]);

        Assert.True(FormDefinition.Check(twice).Accepted);
        RefusedAt(FormDefinition.Check(multiple), 3, "name", "select_multiple");
        Assert.True(FormDefinition.Check(allowed).Accepted);
    }

    [Fact]
    public void Settings_are_read_and_checked()
    {
        FormModel form = Form([["text", "ad", "Ad"]], settings: [
            ["form_title", "form_id", "version", "style", "instance_name", "namespaces"],
            ["Denetim", "denetim", "3", "pages theme-grid", "concat('D-', ${ad})", "esri=\"x\""]]);
        FormCheck check = FormDefinition.Check(form);

        Assert.True(check.Accepted, string.Join("; ", check.Refusals.Select(r => r.Message)));
        FormSettings settings = check.Definition!.Settings;
        Assert.Equal(("Denetim", "denetim", "3", true), (settings.Title, settings.FormId, settings.Version, settings.Pages));
        Assert.Contains(check.Notices, n => n.Message.Contains("theme-grid", StringComparison.Ordinal));
        Assert.Contains(check.Notices, n => n.Column == "namespaces");

        RefusedAt(FormDefinition.Check(Form([["text", "ad", "Ad"]], settings: [["public_key"], ["MIIB"]])), 2, "public_key", "encrypted");
        RefusedAt(FormDefinition.Check(Form([["text", "ad", "Ad"]], settings: [["instance_name"], ["${yok}"]])), 2, "instance_name", "names no question");
    }

    [Fact]
    public void Labels_per_language_are_read_and_the_default_language_must_be_one_of_them()
    {
        string[] header = ["type", "name", "label::Türkçe (tr)", "label::English (en)", "hint::Türkçe (tr)"];
        FormModel form = new([
            new ModelSheet("survey", header, [["text", "ad", "Ad", "Name", "Tam adınız"]]),
            new ModelSheet("settings", ["default_language"], [["Deutsch (de)"]]),
        ]);
        FormCheck check = FormDefinition.Check(form);

        RefusedAt(check, 2, "default_language", "not one of the label languages");

        FormCheck good = FormDefinition.Check(new FormModel([new ModelSheet("survey", header, [["text", "ad", "Ad", "Name"]])]));
        Assert.Equal(["Türkçe (tr)", "English (en)"], good.Definition!.Languages);
        Assert.Equal("Name", good.Definition.Root.Questions[0].Labels["English (en)"]);
    }

    [Fact]
    public void A_column_that_cannot_be_translated_is_refused_when_given_a_language()
    {
        FormModel form = new([new ModelSheet("survey", ["type", "name", "label", "relevant::English"], [["text", "a", "A", "true()"]])]);

        RefusedAt(FormDefinition.Check(form), 1, "relevant::English", "per language");
    }

    [Fact]
    public void A_form_without_a_survey_sheet_is_refused()
    {
        FormCheck check = FormDefinition.Check(new FormModel([new ModelSheet("choices", [], [])]));

        Assert.Contains("survey", Assert.Single(check.Refusals).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("select_one  renk", "select_one", "renk")]
    [InlineData("select all that apply renk", "select_multiple", "renk")]
    [InlineData("begin_group", "begin group", null)]
    [InlineData("DateTime", "dateTime", null)]
    [InlineData("select_multiple_from_file ilceler.csv", "select_multiple_from_file", "ilceler.csv")]
    public void Type_spellings_are_read_as_xlsform_tools_read_them(string raw, string type, string? argument)
    {
        (string parsed, string? arg, string? problem) = FormDefinition.ParseType(raw);

        Assert.Null(problem);
        Assert.Equal((type, argument), (parsed, arg));
    }

    [Fact]
    public void A_literal_default_is_text_and_a_formula_default_is_checked_as_one()
    {
        string[] header = ["type", "name", "label", "default"];

        // HTML and a parenthesis are text here: a default is a formula only with a question reference or a call at its start.
        Assert.True(FormDefinition.Check(Form([["text", "a", "A", "<p>123</p> (x)"]], surveyHeader: header)).Accepted);
        RefusedAt(FormDefinition.Check(Form([["text", "a", "A", "${yok}"]], surveyHeader: header)), 2, "default", "names no question");
        RefusedAt(FormDefinition.Check(Form([["text", "a", "A", "concat(${a}, 'x'"]], surveyHeader: header)), 2, "default", "never closed");
    }

    [Fact]
    public void A_label_language_written_with_one_colon_is_read_as_a_label_in_that_language()
    {
        // pyxform's own fixture (tests/fixtures/example_forms/group) writes label:English (en); a corpus form does too.
        FormModel form = new([new ModelSheet("survey", ["type", "name", "label:English (en)"], [["text", "ad", "Name"]])]);
        FormCheck check = FormDefinition.Check(form);

        Assert.True(check.Accepted, string.Join("; ", check.Refusals.Select(r => r.Message)));
        Assert.Equal("Name", check.Definition!.Root.Questions[0].Labels["English (en)"]);
        Assert.Equal(["English (en)"], check.Definition.Languages);
    }

    [Fact]
    public void A_select_multiple_with_an_other_option_is_read_as_or_other_whatever_the_spelling()
    {
        FormCheck check = Checked(["select_multiple renk or other", "r", "R"]);

        Assert.True(check.Accepted, string.Join("; ", check.Refusals.Select(x => x.Message)));
        Assert.Equal(["r", "r_other"], check.Definition!.Root.Questions.Select(q => q.Name));
    }
}
