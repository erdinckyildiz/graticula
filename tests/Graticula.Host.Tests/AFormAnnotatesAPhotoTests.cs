using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Host.Forms;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-287 §5.24, 2026-11-16: the <c>annotate</c> appearance — drawing over a photo — is accepted on an <c>image</c>
/// question and nowhere else, its <c>max-pixels</c> reaches the client, and the combinations this client does not build
/// are refused in words. The client's half is <c>FormFillAnnotateTests</c> in the Console tests.
/// </summary>
public sealed class AFormAnnotatesAPhotoTests
{
    private static readonly string[] Header = ["type", "name", "label", "appearance", "parameters"];

    private static FormCheck Checked(params string[][] survey) =>
        FormDefinition.Check(new FormModel([
            new ModelSheet("survey", Header, survey.Select(r => (IReadOnlyList<string>)r).ToList()),
            new ModelSheet("choices", ["list_name", "name", "label"], [["l", "a", "A"]]),
        ]));

    private static void RefusedAt(FormCheck check, int row, string column, string words)
    {
        Assert.False(check.Accepted);
        Assert.Contains(check.Refusals, r => r.Row == row && string.Equals(r.Column, column, StringComparison.OrdinalIgnoreCase)
            && r.Message.Contains(words, StringComparison.OrdinalIgnoreCase));
    }

    private static void Accepted(FormCheck check) => Assert.True(check.Accepted, string.Join("; ", check.Refusals.Select(r => r.Message)));

    [Fact]
    public void Annotate_is_accepted_on_an_image_and_reaches_the_client_with_its_max_pixels()
    {
        FormCheck check = Checked(["image", "foto", "Foto", "annotate", "max-pixels=800"]);

        Accepted(check);
        Assert.DoesNotContain(check.Notices, n => n.Message.Contains("annotate", StringComparison.Ordinal));
        JsonObject question = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(FormCompiled.Json(check.Definition!)["root"]!["body"])[0]);
        Assert.Equal("annotate", (string?)Assert.IsType<JsonArray>(question["appearance"])[0]);
        Assert.Equal("800", (string?)question["parameters"]!["max-pixels"]);
        Assert.DoesNotContain("annotate", FormCatalog.RefusedAppearances.Keys);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("audio")]
    [InlineData("file")]
    [InlineData("select_one l")]
    public void Annotate_on_any_other_type_is_refused_in_words(string type)
    {
        RefusedAt(Checked([type, "q", "Q", "annotate"]), 2, "appearance", "The appearance 'annotate' applies to image, not to");
    }

    [Fact]
    public void Several_annotated_photos_in_one_question_and_annotate_with_draw_or_signature_are_refused_in_words()
    {
        RefusedAt(Checked(["image", "foto", "Foto", "multiline annotate"]), 2, "appearance", "annotates one photo per question");
        RefusedAt(Checked(["image", "foto", "Foto", "annotate draw"]), 2, "appearance", "drawing on a blank canvas, not both");
        RefusedAt(Checked(["image", "foto", "Foto", "signature annotate"]), 2, "appearance", "or a signature, not both");
    }

    [Fact]
    public void A_default_picture_to_draw_over_is_refused_in_words_not_accepted_as_a_name_with_no_file()
    {
        FormCheck check = FormDefinition.Check(new FormModel([
            new ModelSheet("survey", ["type", "name", "label", "appearance", "default"], [["image", "foto", "Foto", "annotate", "plan.png"]]),
            new ModelSheet("choices", ["list_name", "name", "label"], []),
        ]));

        RefusedAt(check, 2, "default", "default picture on an image question is refused");
    }

    [Fact]
    public void Annotate_beside_a_presentation_appearance_is_still_accepted()
    {
        FormCheck check = Checked(["image", "foto", "Foto", "annotate w2"]);

        Accepted(check);
        Assert.Contains(check.Notices, n => n.Message.Contains("'w2'", StringComparison.Ordinal));
    }
}
