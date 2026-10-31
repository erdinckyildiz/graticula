using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The type that carries "already escaped" — ADR-023 condition 3.
/// </summary>
/// <remarks>
/// <b>Every hole that is not markup is escaped, and markup is not escaped twice.</b> The first half is the security
/// and the second is what makes the type usable: a page is built from pieces that are themselves pages, and a type
/// that escaped those again would be worked around within a day.
/// </remarks>
public sealed class HtmlTests
{
    [Fact]
    public void A_string_hole_is_escaped_and_the_literal_around_it_is_not()
    {
        string name = "<script>alert('x')</script> & \"q\"";

        Html row = Html.Of($"<td class=\"id\">{name}</td>");

        Assert.Equal(
            "<td class=\"id\">&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt; &amp; &quot;q&quot;</td>",
            row.ToString());
    }

    [Fact]
    public void A_markup_hole_is_kept_as_it_is()
    {
        Html cell = Html.Of($"<b>{"a<b"}</b>");

        Html row = Html.Of($"<td>{cell}</td>");

        Assert.Equal("<td><b>a&lt;b</b></td>", row.ToString());
    }

    /// <remarks>
    /// The host runs in globalization-invariant mode, so no other culture can be set to prove the point; what is
    /// asserted is that a format string reaches the value, as the pages' <c>{x:0.######}</c> holes need.
    /// </remarks>
    [Fact]
    public void A_number_takes_its_format_and_the_invariant_culture()
    {
        Html value = Html.Of($"{1234567.891:N2} {0.5} {32.123456789:0.######}");

        Assert.Equal("1,234,567.89 0.5 32.123457", value.ToString());
    }

    [Fact]
    public void Text_escapes_and_markup_does_not_and_null_is_empty()
    {
        Assert.Equal("a&lt;b", Html.Text("a<b").ToString());
        Assert.Equal("<i>none</i>", Html.Markup("<i>none</i>").ToString());
        Assert.Equal(string.Empty, Html.Text(null).ToString());
        Assert.True(Html.Empty.IsEmpty);
    }

    [Fact]
    public void Join_and_concatenation_keep_markup_and_escape_nothing_again()
    {
        Html joined = Html.Join(Html.Markup(" &middot; "), [Html.Text("a&b"), Html.Markup("<i>c</i>")]);

        Assert.Equal("a&amp;b &middot; <i>c</i>", joined.ToString());
        Assert.Equal("<p>x</p><p>y</p>", (Html.Markup("<p>x</p>") + Html.Of($"<p>{"y"}</p>")).ToString());
    }

    [Fact]
    public void A_builder_takes_markup_and_escapes_the_holes_of_what_it_is_given()
    {
        HtmlBuilder body = new();

        body.Append(Html.Markup("<ul>"));
        body.Append($"<li>{"<b>"}</li>");
        body.Append(Html.Markup("</ul>"));

        Assert.Equal("<ul><li>&lt;b&gt;</li></ul>", body.ToHtml().ToString());

        // A builder in a hole is its markup, not escaped again.
        Assert.Equal("<div><ul><li>&lt;b&gt;</li></ul></div>", Html.Of($"<div>{body}</div>").ToString());
    }

    [Fact]
    public void A_layer_named_like_a_script_cannot_reach_a_directory_page_raw()
    {
        string page = RestDirectory.Folder(
            "/rest/services/hosted",
            "<img src=x onerror=alert(1)>",
            10.81,
            ["<script>x</script>"],
            [("hosted/<svg onload=alert(1)>", "FeatureServer")]);

        Assert.DoesNotContain("<script>x", page, System.StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x", page, System.StringComparison.Ordinal);
        Assert.DoesNotContain("<svg onload", page, System.StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;x&lt;/script&gt;", page, System.StringComparison.Ordinal);
    }
    /// <summary>
    /// The OAuth pages' door escapes as <c>System.Text.Encodings.Web</c> does — non-ASCII as numeric references — and
    /// keeps markup holes, as the other door does (ADR-023 condition 3, 2026-10-31).
    /// </summary>
    [Fact]
    public void The_referenced_door_writes_what_the_sign_in_pages_encoder_wrote_and_keeps_markup()
    {
        string title = "Şehir & <Harita> \"İğne\" – ğüşıöç";

        Assert.Equal(System.Text.Encodings.Web.HtmlEncoder.Default.Encode(title), Html.Referenced(title).ToString());
        Assert.Equal(
            "Sign in to &#x15E;ehir &amp; &lt;Harita&gt; &quot;&#x130;&#x11F;ne&quot; &#x2013; &#x11F;&#xFC;&#x15F;&#x131;&#xF6;&#xE7;",
            Html.Referenced("Sign in to " + title).ToString());

        Html kept = Html.Markup("<b>x</b>");
        Assert.Equal(
            "<p>&#x15F; &lt;i&gt; <b>x</b> 1.5</p>",
            Html.OfReferenced($"<p>{"ş <i>"} {kept} {1.5}</p>").ToString());

        // The two doors differ only outside Basic Latin; that difference is why the sign-in pages have their own.
        Assert.Equal("ş &amp;", Html.Text("ş &").ToString());
    }
}
