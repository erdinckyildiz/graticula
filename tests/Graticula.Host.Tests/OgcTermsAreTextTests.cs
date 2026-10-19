using Xunit;

namespace Graticula.Host.Tests;

/// <summary>An item's terms of use reach an OGC document as the text a reader sees — owner decision 2026-10-19.</summary>
public sealed class OgcTermsAreTextTests
{
    [Theory]
    [InlineData("<p>Internal use <b>only</b></p>", "Internal use only")]
    [InlineData("CC BY 4.0", "CC BY 4.0")]
    [InlineData("Fish &amp; Chips<br/>\n  only", "Fish & Chips only")]
    public void Html_becomes_the_text_it_shows(string html, string text) => Assert.Equal(text, OgcTerms.AsText(html));

    [Theory]
    [InlineData("<p></p>")]
    [InlineData("  <br>  ")]
    public void Markup_with_no_words_says_nothing(string html) => Assert.Null(OgcTerms.AsText(html));
}
