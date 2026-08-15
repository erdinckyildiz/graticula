using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace GisServer.Conformance.Tests;

/// <summary>
/// The resources a vector tile style needs before it can draw a label.
/// </summary>
/// <remarks>
/// <para>
/// <b>The style had no <c>glyphs</c> key until 2026-08-15, so the tile service
/// could not put a name on a map.</b> A MapLibre or Mapbox GL style with a
/// <c>text-field</c> fetches <c>{fontstack}/{range}.pbf</c> and renders nothing
/// at all without it. That is most of what anybody wants vector tiles for, and
/// the gap was invisible from the server's side: every tile test passed.
/// </para>
/// <para>
/// These tests walk the client's own sequence — read the style, take the URL out
/// of it, substitute a font stack a real style names, fetch the range, and check
/// the bytes are the format the client parses. Anything less proves the routes
/// exist rather than that a label can be drawn.
/// </para>
/// </remarks>
public sealed class GlyphConformanceTests : ArcGisClient
{
    private const string ServiceVariable = "GISSERVER_TEST_TILE_SERVICE";

    private static string? TileService => Environment.GetEnvironmentVariable(ServiceVariable);

    private async Task<string> RequireTileServiceAsync()
    {
        await RequireServerAsync();

        Assert.False(
            string.IsNullOrWhiteSpace(TileService),
            $"{ServiceVariable} is not set, so these tests FAIL rather than skip. Name a service "
            + "that has a VectorTileServer.");

        return TileService!.Trim('/');
    }

    private static HttpClient Client() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    private async Task<string> ResourcesAsync() =>
        $"{await RequireServerAsync()}/rest/services/{await RequireTileServiceAsync()}"
        + "/VectorTileServer/resources";

    // ---------- the style says where the glyphs are ----------

    /// <summary>
    /// The style carries a <c>glyphs</c> URL, with the placeholders a client
    /// substitutes rather than values we resolved.
    /// </summary>
    [Fact]
    public async Task The_style_says_where_the_glyphs_are()
    {
        JsonElement style = await GetJsonAsync(
            $"/rest/services/{await RequireTileServiceAsync()}"
            + "/VectorTileServer/resources/styles");

        Assert.True(
            style.TryGetProperty("glyphs", out JsonElement glyphs),
            "A style with no glyphs key renders no text, whatever the layers say.");

        string url = glyphs.GetString()!;

        Assert.Contains("{fontstack}", url, StringComparison.Ordinal);
        Assert.Contains("{range}", url, StringComparison.Ordinal);

        Assert.True(style.TryGetProperty("sprite", out _),
            "Clients probe the sprite sheet; a 404 there reads as a broken service.");
    }

    // ---------- the range a client actually asks for ----------

    /// <summary>
    /// A range comes back as the protobuf a client parses, and the bytes are
    /// shaped like one.
    /// </summary>
    /// <remarks>
    /// <b>Decoded rather than weighed.</b> A test that only checks the status
    /// and a non-zero length passes against a file of zeroes. This walks the
    /// wire format far enough to find the fontstack name and at least one glyph
    /// with a bitmap, which is what the client needs and nothing less.
    /// </remarks>
    [Fact]
    public async Task A_glyph_range_is_a_glyph_range()
    {
        using HttpClient http = Client();

        byte[] pbf = await http.GetByteArrayAsync(
            new Uri($"{await ResourcesAsync()}/fonts/DejaVu%20Sans%20Regular/0-255.pbf"));

        Assert.True(pbf.Length > 1000, $"a Latin range of {pbf.Length} bytes is not plausible");

        (string name, int glyphs, int withBitmaps) = Describe(pbf);

        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.True(glyphs > 90, $"only {glyphs} glyphs in the Latin range");
        Assert.True(withBitmaps > 80, $"only {withBitmaps} glyphs carry a bitmap");
    }

    /// <summary>
    /// A style naming a font nobody can ship is served, not refused.
    /// </summary>
    /// <remarks>
    /// <b>ArcGIS styles name <c>Arial Unicode MS Regular</c> and Mapbox ones
    /// name <c>Open Sans Regular</c>.</b> We have neither and cannot licence
    /// either. A 404 makes a client drop every label and log a fetch failure,
    /// which reads as a broken server; substituting the font we do have is
    /// visibly a substitution and obviously better.
    /// </remarks>
    [Theory]
    [InlineData("Arial%20Unicode%20MS%20Regular")]
    [InlineData("Open%20Sans%20Regular")]
    [InlineData("Noto%20Sans%20Regular,Arial%20Unicode%20MS%20Regular")]
    public async Task A_font_we_do_not_have_is_substituted(string fontstack)
    {
        using HttpClient http = Client();

        using HttpResponseMessage response = await http.GetAsync(
            new Uri($"{await ResourcesAsync()}/fonts/{fontstack}/0-255.pbf"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(
            response.Headers.TryGetValues("X-Font-Stack", out IEnumerable<string>? served),
            "the response should say which font actually answered");

        Assert.Equal("DejaVu Sans Regular", served!.Single());
    }

    /// <summary>Turkish resolves, because that is the audience.</summary>
    /// <remarks>
    /// Latin Extended-A carries the dotted capital I and the s-cedilla. A font
    /// stack that covers Latin-1 and stops is the classic way a Turkish label
    /// renders as boxes.
    /// </remarks>
    [Fact]
    public async Task The_range_carrying_Turkish_is_served()
    {
        using HttpClient http = Client();

        byte[] pbf = await http.GetByteArrayAsync(
            new Uri($"{await ResourcesAsync()}/fonts/DejaVu%20Sans%20Regular/256-511.pbf"));

        (_, int glyphs, _) = Describe(pbf);

        Assert.True(glyphs > 100, $"Latin Extended-A came back with {glyphs} glyphs");
    }

    // ---------- what is refused ----------

    /// <summary>
    /// Nothing in the URL becomes a path.
    /// </summary>
    /// <remarks>
    /// The font stack and the range are both caller-supplied and both look like
    /// filenames. security.md: filenames are data, never paths.
    /// </remarks>
    [Theory]
    [InlineData("DejaVu%20Sans%20Regular", "100-200")]
    [InlineData("DejaVu%20Sans%20Regular", "0-100")]
    [InlineData("DejaVu%20Sans%20Regular", "..%2F..%2Fappsettings")]
    [InlineData("DejaVu%20Sans%20Regular", "65536-65791")]
    public async Task A_range_that_is_not_one_is_refused(string fontstack, string range)
    {
        using HttpClient http = Client();

        using HttpResponseMessage response = await http.GetAsync(
            new Uri($"{await ResourcesAsync()}/fonts/{fontstack}/{range}.pbf"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- the sprite sheet ----------

    /// <summary>
    /// The sprite sheet answers, and is honestly empty.
    /// </summary>
    [Theory]
    [InlineData("sprite.json")]
    [InlineData("sprite.png")]
    [InlineData("sprite@2x.json")]
    [InlineData("sprite@2x.png")]
    public async Task The_sprite_sheet_answers(string name)
    {
        using HttpClient http = Client();

        using HttpResponseMessage response =
            await http.GetAsync(new Uri($"{await ResourcesAsync()}/sprites/{name}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------- reading just enough of the wire format ----------

    /// <summary>
    /// Walks the glyph protobuf far enough to prove it is one.
    /// </summary>
    /// <remarks>
    /// Written from the format rather than from our encoder, so an encoder that
    /// agrees with itself and with nothing else still fails here.
    /// </remarks>
    private static (string Name, int Glyphs, int WithBitmaps) Describe(byte[] pbf)
    {
        int i = 0;
        ulong Varint(byte[] b, ref int at)
        {
            ulong value = 0;
            int shift = 0;
            while (at < b.Length)
            {
                byte piece = b[at++];
                value |= (ulong)(piece & 0x7F) << shift;
                if ((piece & 0x80) == 0)
                {
                    break;
                }

                shift += 7;
            }

            return value;
        }

        // glyphs { repeated fontstack stacks = 1 }
        ulong key = Varint(pbf, ref i);
        Assert.Equal(1UL, key >> 3);
        Assert.Equal(2UL, key & 7);

        int length = (int)Varint(pbf, ref i);
        int end = i + length;

        string name = string.Empty;
        int glyphs = 0;
        int withBitmaps = 0;

        while (i < end)
        {
            ulong field = Varint(pbf, ref i);
            int size = (int)Varint(pbf, ref i);

            if ((field >> 3) == 1)
            {
                name = System.Text.Encoding.UTF8.GetString(pbf, i, size);
            }
            else if ((field >> 3) == 3)
            {
                glyphs++;

                int at = i;
                int stop = i + size;

                while (at < stop)
                {
                    ulong inner = Varint(pbf, ref at);

                    if ((inner & 7) == 2)
                    {
                        int bitmap = (int)Varint(pbf, ref at);

                        if ((inner >> 3) == 2 && bitmap > 0)
                        {
                            withBitmaps++;
                        }

                        at += bitmap;
                    }
                    else
                    {
                        Varint(pbf, ref at);
                    }
                }
            }

            i += size;
        }

        return (name, glyphs, withBitmaps);
    }
}
