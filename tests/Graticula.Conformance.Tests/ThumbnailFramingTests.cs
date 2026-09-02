using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
            Ink(png);

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

    /// <summary>Where the non-transparent pixels of an RGBA PNG are.</summary>
    /// <remarks>
    /// <b>Only what this needs.</b> Eight-bit RGBA, no interlacing, no palette — which is what
    /// the thumbnail endpoint writes. Anything else throws rather than guessing, because a
    /// decoder that quietly mis-reads a format is worse than one that refuses it.
    /// </remarks>
    /// <param name="png">The image.</param>
    /// <returns>Its size, the ink's bounding box, and how many pixels carry ink.</returns>
    private static (int Width, int Height, int MinX, int MinY, int MaxX, int MaxY, int Painted)
        Ink(byte[] png)
    {
        int width = 0;
        int height = 0;
        using MemoryStream data = new();

        int at = 8;

        while (at + 8 <= png.Length)
        {
            int length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            string kind = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            int body = at + 8;

            if (kind == "IHDR")
            {
                width = (png[body] << 24) | (png[body + 1] << 16)
                    | (png[body + 2] << 8) | png[body + 3];
                height = (png[body + 4] << 24) | (png[body + 5] << 16)
                    | (png[body + 6] << 8) | png[body + 7];

                Assert.True(png[body + 8] == 8, "This decoder reads 8 bits per channel.");
                Assert.True(png[body + 9] == 6, "This decoder reads RGBA.");
                Assert.True(png[body + 12] == 0, "This decoder does not read interlaced images.");
            }
            else if (kind == "IDAT")
            {
                data.Write(png, body, length);
            }

            at = body + length + 4;
        }

        data.Position = 0;

        using ZLibStream inflate = new(data, CompressionMode.Decompress);
        using MemoryStream raw = new();
        inflate.CopyTo(raw);

        byte[] bytes = raw.ToArray();

        const int Bpp = 4;
        int stride = width * Bpp;

        byte[] previous = new byte[stride];
        byte[] line = new byte[stride];

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, painted = 0;
        int read = 0;

        for (int y = 0; y < height; y++)
        {
            byte filter = bytes[read++];

            Array.Copy(bytes, read, line, 0, stride);
            read += stride;

            for (int x = 0; x < stride; x++)
            {
                int a = x >= Bpp ? line[x - Bpp] : 0;
                int b = previous[x];
                int c = x >= Bpp ? previous[x - Bpp] : 0;

                line[x] = filter switch
                {
                    1 => (byte)(line[x] + a),
                    2 => (byte)(line[x] + b),
                    3 => (byte)(line[x] + ((a + b) / 2)),
                    4 => (byte)(line[x] + Paeth(a, b, c)),
                    _ => line[x],
                };
            }

            for (int x = 0; x < width; x++)
            {
                if (line[(x * Bpp) + 3] == 0)
                {
                    continue;
                }

                painted++;
                if (x < minX) { minX = x; }
                if (x > maxX) { maxX = x; }
                if (y < minY) { minY = y; }
                if (y > maxY) { maxY = y; }
            }

            (previous, line) = (line, previous);
        }

        return (width, height, minX, minY, maxX, maxY, painted);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);

        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
