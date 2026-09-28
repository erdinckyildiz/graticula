using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A sprite sheet that lacks an icon the stored style names by literal is refused — ADR-092.
/// </summary>
/// <remarks>
/// <b>The other direction of the style's icon check.</b> A style naming an icon the sheet lacks is refused when the
/// style is written; this is what stops the sheet being replaced under it afterwards. Expressions are skipped, because
/// which names they produce depends on the features.
/// </remarks>
public sealed class ASpriteSheetMayNotTakeAwayAStylesIconTests
{
    private const string Style = """
        {
          "version": 8,
          "layers": [
            { "id": "a", "type": "symbol", "source-layer": "p", "layout": { "icon-image": "marker" } },
            { "id": "b", "type": "symbol", "source-layer": "p", "layout": { "icon-image": ["literal", "school"] } },
            { "id": "c", "type": "symbol", "source-layer": "p", "layout": { "icon-image": ["get", "kind"] } }
          ]
        }
        """;

    private static readonly string[] Both = ["marker", "school", "extra"];

    private static readonly string[] MarkerOnly = ["marker"];

    private static readonly string[] School = ["school"];

    [Fact]
    public void A_sheet_holding_every_literal_icon_takes_nothing_away()
    {
        Assert.Empty(AdminEndpoints.MissingFromSheet(Style, Both));
    }

    [Fact]
    public void A_sheet_missing_a_literal_icon_names_it()
    {
        Assert.Equal(School, AdminEndpoints.MissingFromSheet(Style, MarkerOnly));
    }

    [Fact]
    public void No_stored_style_holds_nothing()
    {
        Assert.Empty(AdminEndpoints.MissingFromSheet(null, []));
    }
}
