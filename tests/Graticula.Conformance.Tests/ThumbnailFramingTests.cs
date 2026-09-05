using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A layer's picture is framed on the layer's features, not on a box it used to fill.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-199](../../docs/architecture-debt.md), and the second time this property has been
/// wrong.</b> [D-58](../../docs/architecture-debt.md) replaced a sampled canvas because a
/// picture of 800 features out of 46,041 read as *this layer is nearly empty*. The replacement
/// drew every feature and then framed them on `ST_EstimatedExtent`, which reads the GiST index:
/// it grows with every insert and shrinks only under `VACUUM` or `REINDEX`, so it is an upper
/// bound over everything the layer has *ever* held. Measured on `ci_editable` — three features
/// left after a conformance suite — the declared box was 4,611 × 6,042 units and the data
/// occupied 600 × 0. The picture was three dots in a corner: the same false reading, reached by
/// a different route.
/// </para>
/// <para>
/// <b>So the assertion is about the picture rather than about the query behind it.</b> Anything
/// checked further back — the extent the endpoint asked for, the features it read — can be
/// right while the image is wrong. What a reader sees is where the ink is, so that is what this
/// measures: decode the PNG, find the pixels that are not transparent, and require their
/// bounding box to fill most of the frame.
/// </para>
/// <para>
/// <b>Measured before and after, on the same layer.</b> Framed on the declared extent the ink
/// covered **15% of the width and 4% of the height**; framed on the features it covers
/// **96% and 97%**. The floor here is 50%, which is far below the repair and far above the
/// defect — a threshold that would fail on an ordinary layer is a threshold somebody turns off.
/// </para>
/// <para>
/// <b>The decoder is forty lines and it is here rather than in a library.</b> This suite talks
/// HTTP and nothing else on purpose; taking an image library for one assertion would give it a
/// dependency on the thing it is testing.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class ThumbnailFramingTests : ArcGisClient
{
    [Fact]
    public async Task A_layers_picture_is_framed_on_the_features_it_draws()
    {
        string? qualified = Environment.GetEnvironmentVariable(
            OgcWriteConformanceTests.LayerVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(qualified),
            $"{OgcWriteConformanceTests.LayerVariable} is not set, so this test FAILS rather "
            + "than skips. It wants the layer the write suites edit, because a layer that has "
            + "lost features is exactly where a stale frame shows.");

        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri($"{root}/admin/thumbnail?service={Uri.EscapeDataString(qualified!.Trim('/'))}"
                + "&layer=0"));

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"The thumbnail answered {(int)response.StatusCode} for {qualified}.");

        byte[] png = await response.Content.ReadAsByteArrayAsync();

        (int width, int height, int minX, int minY, int maxX, int maxY, int painted) =
            PngInk.Ink(png);

        Assert.True(
            painted > 0,
            $"The {width}×{height} picture has no ink in it at all, so there is nothing to say "
            + "about where it sits.");

        double across = (maxX - minX + 1) / (double)width;
        double down = (maxY - minY + 1) / (double)height;

        Assert.True(
            across >= 0.5 && down >= 0.5,
            $"The features occupy {across:P0} of the width and {down:P0} of the height of their "
            + $"own picture ({painted} pixels of ink in {width}×{height}). A thumbnail framed on "
            + "a box the layer no longer fills reads as an empty layer, which is D-199 — and "
            + "before that repair this same layer measured 15% and 4%.");
    }

}
