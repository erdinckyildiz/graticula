using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Features;
using Graticula.Host.Forms;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-287 §5.2 and §5.10's last table: a form's layers, fields, choice lists and relationships are planned from its
/// definition, and every refusal is found before anything is made.
/// </summary>
public sealed class AFormsLayersArePlannedBeforeTheyAreMadeTests
{
    private static FormLayerPlan Plan(string[] header, string[][] survey, string[][]? choices = null, string[][]? settings = null)
    {
        List<ModelSheet> sheets =
        [
            new("survey", header, survey.Select(r => (IReadOnlyList<string>)r).ToList()),
            new("choices", ["list_name", "name", "label::Türkçe (tr)", "label::English (en)"], (choices ?? []).Select(r => (IReadOnlyList<string>)r).ToList()),
        ];

        if (settings is not null)
        {
            sheets.Add(new ModelSheet("settings", settings[0], settings.Skip(1).Select(r => (IReadOnlyList<string>)r).ToList()));
        }

        FormCheck check = FormDefinition.Check(new FormModel(sheets));
        Assert.True(check.Accepted, string.Join("; ", check.Refusals.Select(r => $"{r.Row} {r.Column}: {r.Message}")));
        return FormLayerPlan.For(check.Definition!, "Yedek Başlık");
    }

    private static readonly string[] Header = ["type", "name", "label::Türkçe (tr)", "label::English (en)", "parameters", "bind::esri:fieldType", "bind::esri:fieldLength", "calculation"];

    [Fact]
    public void Each_question_becomes_the_field_the_list_says()
    {
        FormLayerPlan plan = Plan(Header, [
            ["integer", "kat", "Kat", "Floor"],
            ["decimal", "oran", "Oran", "Ratio"],
            ["range", "puan", "Puan", "Score", "start=1 end=5 step=1"],
            ["range", "olcek", "Ölçek", "Scale", "start=0 end=1 step=0.1"],
            ["text", "ad", "Ad", "Name", "", "", "40"],
            ["select_one hasar", "hasar", "Hasar", "Damage"],
            ["select_multiple hasar", "hasarlar", "Hasarlar", "Damages"],
            ["date", "tarih", "Tarih", "Date"],
            ["dateTime", "an", "An", "Moment"],
            ["time", "saat", "Saat", "Time"],
            ["note", "bilgi", "Bilgi", "Info"],
            ["note", "ozet", "Özet", "Summary", "", "", "", "concat('x', ${ad})"],
            ["calculate", "gizli", "", "", "", "null", "", "1"],
            ["start", "basla"],
            ["geopoint", "yer", "Yer", "Place"],
            ["image", "foto", "Foto", "Photo"],
            ["integer", "kod", "Kod", "Code", "", "esriFieldTypeSmallInteger"],
        ], [["hasar", "yok", "Yok", "None"], ["hasar", "agir", "Ağır", "Heavy"]], [["form_id", "default_language"], ["Bina Denetimi 2026", "English (en)"]]);

        Assert.True(plan.Accepted);
        PlannedLayer layer = Assert.Single(plan.Layers);
        Assert.Equal("bina_denetimi_2026", plan.ServiceName);
        Assert.Equal("bina_denetimi_2026", layer.Name);
        Assert.Equal("Point", layer.GeometryType);
        Assert.Equal("yer", layer.LocationQuestion);
        Assert.Equal(["foto"], layer.AttachmentQuestions);
        Assert.Null(layer.ParentRepeat);

        Dictionary<string, PlannedField> fields = layer.Fields.ToDictionary(f => f.Question);
        Assert.Equal(["kat", "oran", "puan", "olcek", "ad", "hasar", "hasarlar", "tarih", "an", "saat", "ozet", "basla", "kod"], layer.Fields.Select(f => f.Question));
        Assert.Equal(FieldType.Integer, fields["kat"].Type);
        Assert.Equal(FieldType.Double, fields["oran"].Type);
        Assert.Equal(FieldType.Integer, fields["puan"].Type);
        Assert.Equal(FieldType.Double, fields["olcek"].Type);
        Assert.Equal((FieldType.Text, 40), (fields["ad"].Type, fields["ad"].Length!.Value));
        Assert.Equal((FieldType.Text, 255), (fields["hasar"].Type, fields["hasar"].Length!.Value));
        Assert.NotNull(fields["hasar"].Domain);
        Assert.Null(fields["hasarlar"].Domain);
        Assert.Equal(FieldType.Date, fields["tarih"].Type);
        Assert.Equal(FieldType.Date, fields["basla"].Type);
        Assert.Equal(FieldType.Text, fields["saat"].Type);
        Assert.Equal(FieldType.SmallInteger, fields["kod"].Type);
        Assert.Null(fields["kat"].Length);
        Assert.Equal("Floor", fields["kat"].Alias);
        Assert.Equal("basla", fields["basla"].Alias);

        PlannedDomain domain = Assert.Single(plan.Domains);
        Assert.Equal("bina_denetimi_2026_hasar", domain.Name);
        Assert.Equal(["None", "Heavy"], domain.Codes.Select(c => c.Name));
        Assert.Equal(["yok", "agir"], domain.Codes.Select(c => c.Code.Text));
    }

    [Fact]
    public void Turkish_names_become_latin_columns()
    {
        Assert.Equal("sehir_adi", FormLayerPlan.Column("şehir_adı"));
        Assert.Equal("ilce_ozeti", FormLayerPlan.Column("İlçe_Özeti"));
        Assert.Equal("guzergah", FormLayerPlan.Column("güzergâh"));
        Assert.Equal("strasse", FormLayerPlan.Column("straße"));
        Assert.Equal("yangin_denetimi", FormLayerPlan.Identifier("  Yangın   Denetimi! "));
        Assert.Equal("f_2026_sayim", FormLayerPlan.Identifier("2026 sayım"));
    }

    [Fact]
    public void Repeats_are_related_layers_or_tables_each_naming_its_parent()
    {
        FormLayerPlan plan = Plan(Header, [
            ["geoshape", "parsel", "Parsel", "Parcel"],
            ["begin repeat", "agaclar", "Ağaçlar", "Trees"],
            ["geopoint", "konum", "Konum", "Location"],
            ["begin repeat", "olcumler", "Ölçümler", "Measurements"],
            ["decimal", "cap", "Çap", "Diameter"],
            ["image", "kabuk", "Kabuk", "Bark"],
            ["end repeat"],
            ["end repeat"],
        ], settings: [["form_id"], ["envanter"]]);

        Assert.True(plan.Accepted);
        Assert.Equal(["envanter", "envanter_agaclar", "envanter_olcumler"], plan.Layers.Select(l => l.Name));
        Assert.Equal(["Polygon", "Point", null], plan.Layers.Select(l => l.GeometryType));
        Assert.Equal([null, "", "agaclar"], plan.Layers.Select(l => l.ParentRepeat));
        Assert.Equal(["kabuk"], plan.Layers[2].AttachmentQuestions);
        Assert.Empty(plan.Layers[1].Fields);
    }

    [Fact]
    public void Two_questions_that_become_one_column_are_refused_with_both_rows()
    {
        FormLayerPlan plan = Plan(Header, [["text", "sehir", "Şehir", "City"], ["text", "şehir", "Şehir 2", "City 2"]]);

        FormIssue refused = Assert.Single(plan.Refusals);
        Assert.Equal((3, "name"), (refused.Row, refused.Column));
        Assert.Contains("row 2", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_numeric_field_with_choices_that_are_not_numbers_is_refused()
    {
        FormLayerPlan words = Plan(Header, [["select_one renk", "renk", "Renk", "Colour", "", "esriFieldTypeInteger"]],
            [["renk", "1", "Bir", "One"], ["renk", "mavi", "Mavi", "Blue"]]);
        FormLayerPlan numbers = Plan(Header, [["select_one renk", "renk", "Renk", "Colour", "", "esriFieldTypeInteger"]],
            [["renk", "1", "Bir", "One"], ["renk", "2", "İki", "Two"]]);

        Assert.Contains("'mavi'", Assert.Single(words.Refusals).Message, StringComparison.Ordinal);
        Assert.True(numbers.Accepted);
    }

    [Fact]
    public void Without_a_form_id_the_title_names_the_layer()
    {
        FormLayerPlan plan = Plan(Header, [["text", "a", "A", "A"]]);

        Assert.Equal("yedek_baslik", plan.ServiceName);
    }

    [Fact]
    public void A_table_without_a_location_has_no_geometry()
    {
        FormLayerPlan plan = Plan(Header, [["text", "a", "A", "A"]]);

        Assert.Null(plan.Layers[0].GeometryType);
    }
}
