using System.Collections.Generic;
using Graticula.Api.Wms;
using Graticula.Features;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// WMS GetFeatureInfo's <c>text/html</c> answer is written on the <see cref="Html"/> type and escapes every value.
/// </summary>
/// <remarks>
/// <b>ADR-023 condition 3, its last renderer outside the type but one.</b> <c>FeatureInfoWriter</c> escaped by hand
/// with its own helper until 2026-10-14, because the type lived in the host and the WMS project could not see it.
/// Nothing tested this answer at all, so the move is pinned here by the exact bytes the hand-written version wrote:
/// a value carrying markup, a Latin-1 letter (which the encoder writes as a numeric reference) and a Turkish one
/// (which it does not), and a layer name with markup in it.
/// </remarks>
public sealed class FeatureInfoHtmlTests
{
    [Fact]
    public void Every_layer_field_and_value_is_escaped_and_the_bytes_are_the_old_ones()
    {
        FeatureSchema schema = new(["name", "note"]);
        Feature feature = new("1", null, schema, ["<script>alert(1)</script>", "Çağ & İz"]);

        string html = FeatureInfoWriter.Write(
            "text/html", [new FeatureInfoWriter.Hits("roads <b>", [feature])], (0, 0), 4326);

        Assert.Equal(
            "<h4>roads &lt;b&gt;</h4><table class=\"featureInfo\">"
            + "<tr><th>name</th><td>&lt;script&gt;alert(1)&lt;/script&gt;</td></tr>"
            + "<tr><th>note</th><td>&#199;ağ &amp; İz</td></tr></table>",
            html);
    }

    [Fact]
    public void Nothing_found_is_a_sentence()
    {
        string html = FeatureInfoWriter.Write(
            "text/html", [new FeatureInfoWriter.Hits("roads", new List<Feature>())], (0, 0), 4326);

        Assert.Equal("<p>Nothing at that point.</p>", html);
    }
}
