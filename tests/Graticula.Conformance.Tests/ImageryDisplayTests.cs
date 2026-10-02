using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-123 condition 3: an elevation model is drawn with contrast by default — not the white page the format's range
/// gave it — and its stretch and colours are set by its owner.
/// </summary>
/// <remarks>
/// <b>The model is written here</b>, sixty-four by sixty-four float metres from 800 to 2500 in a plain GeoTIFF, and the
/// answer is read back pixel by pixel from the PNG, so the test measures what a client sees.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ImageryDisplayTests : ArcGisClient
{
    private const int Side = 64;

    /// <summary>A float elevation model as the smallest GeoTIFF this server opens.</summary>
    private static byte[] Elevation() => Elevation(Side);

    private static byte[] Elevation(int side, double west = 30.0, double pixel = 0)
    {
        using MemoryStream file = new();
        using BinaryWriter w = new(file);

        float[] heights = [.. Enumerable.Range(0, side * side).Select(i => 800f + ((i % side) + (i / side)) * 1700f / ((2 * side) - 2))];

        // One hole, as a float model marks one: NaN, which no declared no-data value catches (ADR-128).
        heights[^1] = float.NaN;
        byte[] image = new byte[heights.Length * 4];
        Buffer.BlockCopy(heights, 0, image, 0, image.Length);

        int imageAt = 8, scaleAt = imageAt + image.Length, tieAt = scaleAt + 24, geoAt = tieAt + 48, ifdAt = geoAt + 32;

        w.Write("II"u8.ToArray());
        w.Write((ushort)42);
        w.Write(ifdAt);
        w.Write(image);
        double cell = pixel > 0 ? pixel : 0.64 / side;
        foreach (double d in new[] { cell, cell, 0.0 }) w.Write(d);
        foreach (double d in new[] { 0.0, 0.0, 0.0, west, 41.0, 0.0 }) w.Write(d);
        foreach (ushort k in new ushort[] { 1, 1, 0, 3, 1024, 0, 1, 2, 1025, 0, 1, 1, 2048, 0, 1, 4326 }) w.Write(k);

        (ushort Tag, ushort Type, int Count, int Value)[] tags =
        [
            (256, 3, 1, side), (257, 3, 1, side), (258, 3, 1, 32), (259, 3, 1, 1), (262, 3, 1, 1), (273, 4, 1, imageAt),
            (277, 3, 1, 1), (278, 3, 1, side), (279, 4, 1, image.Length), (284, 3, 1, 1), (339, 3, 1, 3),
            (33550, 12, 3, scaleAt), (33922, 12, 6, tieAt), (34735, 3, 16, geoAt),
        ];

        w.Write((ushort)tags.Length);
        foreach ((ushort tag, ushort type, int count, int value) in tags)
        {
            w.Write(tag);
            w.Write(type);
            w.Write(count);

            if (type == 3 && count == 1)
            {
                w.Write((ushort)value);
                w.Write((ushort)0);
            }
            else
            {
                w.Write(value);
            }
        }

        w.Write(0);
        return file.ToArray();
    }

    /// <summary>The red and green of every opaque pixel of an 8-bit RGBA PNG.</summary>
    private static List<(byte Red, byte Green)> Pixels(byte[] png)
    {
        int at = 8, width = 0, height = 0;
        using MemoryStream data = new();

        while (at < png.Length)
        {
            int length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            string type = Encoding.ASCII.GetString(png, at + 4, 4);

            if (type == "IHDR")
            {
                width = (png[at + 8] << 24) | (png[at + 9] << 16) | (png[at + 10] << 8) | png[at + 11];
                height = (png[at + 12] << 24) | (png[at + 13] << 16) | (png[at + 14] << 8) | png[at + 15];
                Assert.Equal(6, png[at + 17]);
            }
            else if (type == "IDAT")
            {
                data.Write(png, at + 8, length);
            }

            at += length + 12;
        }

        data.Position = 0;
        using ZLibStream inflate = new(data, CompressionMode.Decompress);
        using MemoryStream raw = new();
        inflate.CopyTo(raw);
        byte[] bytes = raw.ToArray();

        int stride = width * 4;
        byte[] previous = new byte[stride];
        List<(byte, byte)> pixels = [];

        for (int y = 0, p = 0; y < height; y++)
        {
            byte filter = bytes[p++];
            byte[] line = bytes[p..(p + stride)];
            p += stride;

            for (int x = 0; x < stride; x++)
            {
                int left = x >= 4 ? line[x - 4] : 0, up = previous[x], corner = x >= 4 ? previous[x - 4] : 0;
                int predicted = filter switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, corner),
                    _ => 0,
                };
                line[x] = (byte)(line[x] + predicted);
            }

            for (int x = 0; x < stride; x += 4)
            {
                if (line[x + 3] > 0) pixels.Add((line[x], line[x + 1]));
            }

            previous = line;
        }

        return pixels;
    }

    /// <summary>The 32-bit float values of a one-band, one-strip, little-endian TIFF.</summary>
    private static float[] Floats(byte[] tiff)
    {
        Assert.Equal((byte)'I', tiff[0]);
        int directory = BitConverter.ToInt32(tiff, 4);
        int entries = BitConverter.ToUInt16(tiff, directory);
        int width = 0, height = 0, bits = 0, format = 0, offset = 0;

        for (int i = 0; i < entries; i++)
        {
            int at = directory + 2 + (i * 12);
            int tag = BitConverter.ToUInt16(tiff, at);
            int type = BitConverter.ToUInt16(tiff, at + 2);
            int value = type == 3 ? BitConverter.ToUInt16(tiff, at + 8) : BitConverter.ToInt32(tiff, at + 8);

            switch (tag)
            {
                case 256: width = value; break;
                case 257: height = value; break;
                case 258: bits = value; break;
                case 273: offset = value; break;
                case 339: format = value; break;
            }
        }

        Assert.True(bits == 32 && format == 3, $"The TIFF holds {bits}-bit samples of format {format}, not the model's float.");
        float[] values = new float[width * height];
        Buffer.BlockCopy(tiff, offset, values, 0, values.Length * 4);
        return values;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private async Task<(HttpStatusCode Status, byte[] Body)> SendAsync(
        string root, string token, HttpMethod method, string path, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task An_image_uploaded_without_overviews_is_given_them_and_its_values_are_its_own()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        // ADR-139: 600 pixels a side and no overviews, so the server halves it twice, to 300 and 150.
        string name = $"zz_pyr_{Guid.NewGuid():N}"[..16];
        string service = $"/rest/services/hosted/{name}/ImageServer";

        try
        {
            using MultipartFormDataContent form = new();
            form.Add(new ByteArrayContent(Elevation(600)), "file", "dem.tif");
            form.Add(new StringContent(name), "name");
            (HttpStatusCode made, byte[] madeBody) = await SendAsync(root, token!, HttpMethod.Post, "/admin/coverages/upload", form);
            Assert.True(made == HttpStatusCode.Created, $"Uploading answered {(int)made}: {Encoding.UTF8.GetString(madeBody)}");
            Assert.Equal(2, JsonDocument.Parse(madeBody).RootElement.GetProperty("overviews").GetInt32());

            // ADR-143: the file uploaded is given back as it was sent — the overviews beside it are this server's.
            (HttpStatusCode fetched, byte[] original) = await SendAsync(root, token!, HttpMethod.Get, $"/admin/coverages/{name}/file?folder=hosted");
            Assert.Equal(HttpStatusCode.OK, fetched);
            Assert.Equal(Elevation(600), original);

            // Zoomed out, it draws from an overview; close up, a value is the file's own, not an average.
            List<(byte Red, byte Green)> whole = Pixels((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/exportImage?bbox=30,40.36,30.64,41&bboxSR=4326&size=100,100&format=png&f=image")).Body);
            Assert.True(whole.Count > 9000 && whole.Max(p => p.Red) - whole.Min(p => p.Red) > 200,
                "The image drawn whole from its overviews is not the model's contrast.");

            // Pixel (10, 20) of a 600-pixel model whose cells are 0.64/600 degrees: its centre, and its height.
            double cell = 0.64 / 600;
            string at = FormattableString.Invariant($"{30 + (10.5 * cell)},{41 - (20.5 * cell)}");
            string value = JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/identify?geometry={at}&geometryType=esriGeometryPoint&renderingRule={Uri.EscapeDataString("{\"rasterFunction\":\"None\"}")}&f=json")).Body)
                .RootElement.GetProperty("value").GetString()!;
            Assert.Equal(800f + (30 * 1700f / 1198), float.Parse(value, System.Globalization.CultureInfo.InvariantCulture), 2);
        }
        finally
        {
            (HttpStatusCode removed, _) = await SendAsync(root, token!, HttpMethod.Delete, $"/admin/coverages/{name}?folder=hosted");
            Assert.Equal(HttpStatusCode.OK, removed);
        }
    }

    [Fact]
    public async Task An_area_is_described_and_values_are_sampled_at_points_along_lines_and_across_areas()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        // ADR-141: the 64-pixel model, 0.01° a cell from 30° E, 41° N, whose height is 800 + (column + row) × 1700 / 126.
        string name = $"zz_area_{Guid.NewGuid():N}"[..16];
        string service = $"/rest/services/hosted/{name}/ImageServer";
        static string Q(string json) => Uri.EscapeDataString(json);
        static double Height(int column, int row) => 800 + ((column + row) * 1700.0 / 126);

        try
        {
            using MultipartFormDataContent form = new();
            form.Add(new ByteArrayContent(Elevation()), "file", "dem.tif");
            form.Add(new StringContent(name), "name");
            (HttpStatusCode made, byte[] madeBody) = await SendAsync(root, token!, HttpMethod.Post, "/admin/coverages/upload", form);
            Assert.True(made == HttpStatusCode.Created, Encoding.UTF8.GetString(madeBody));

            // Columns 10–19 and rows 20–29: a hundred pixels, their mean at the square's middle.
            string square = Q("{\"rings\":[[[30.1,40.8],[30.2,40.8],[30.2,40.7],[30.1,40.7],[30.1,40.8]]],\"spatialReference\":{\"wkid\":4326}}");
            JsonElement stats = JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/computeStatisticsHistograms?geometry={square}&geometryType=esriGeometryPolygon&f=json")).Body).RootElement;
            JsonElement first = stats.GetProperty("statistics")[0];
            Assert.Equal(100, first.GetProperty("count").GetInt32());
            Assert.Equal(Height(10, 20), first.GetProperty("min").GetDouble(), 2);
            Assert.Equal(Height(19, 29), first.GetProperty("max").GetDouble(), 2);
            Assert.Equal((Height(10, 20) + Height(19, 29)) / 2, first.GetProperty("mean").GetDouble(), 2);
            Assert.Equal(100, stats.GetProperty("histograms")[0].GetProperty("counts").EnumerateArray().Sum(c => c.GetInt64()));

            // ADR-142: a float model is drawn bilinear by default and says so; its values are read between cells when the
            // request asks — a TIFF of 4 × 4 over 2 × 2 cells holds values between them — and Majority is refused by name.
            Assert.Equal("Bilinear", JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get, $"{service}?f=json")).Body)
                .RootElement.GetProperty("defaultResamplingMethod").GetString());
            string cells = "bbox=30.1,40.78,30.12,40.8&bboxSR=4326&size=4,4&format=tiff&f=image";
            float[] nearest = Floats((await SendAsync(root, token!, HttpMethod.Get, $"{service}/exportImage?{cells}")).Body);
            float[] smooth = Floats((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/exportImage?{cells}&interpolation=RSP_BilinearInterpolation")).Body);
            Assert.Equal(2, nearest.Distinct().Count(v => v < Height(11, 20) + 0.01 && v > Height(10, 20) - 0.01));
            Assert.Contains(smooth, v => v > Height(10, 20) + 1 && v < Height(11, 20) - 1);
            Assert.Contains("RSP_Majority", Encoding.UTF8.GetString((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/exportImage?{cells}&interpolation=RSP_Majority")).Body), StringComparison.Ordinal);

            // An envelope is an area too; a point is not.
            string box = Q("{\"xmin\":30.1,\"ymin\":40.7,\"xmax\":30.2,\"ymax\":40.8,\"spatialReference\":{\"wkid\":4326}}");
            Assert.Equal(100, JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/computeStatisticsHistograms?geometry={box}&geometryType=esriGeometryEnvelope&f=json")).Body)
                .RootElement.GetProperty("statistics")[0].GetProperty("count").GetInt32());
            Assert.Contains("polygon or an envelope", Encoding.UTF8.GetString((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/computeStatisticsHistograms?geometry=30.15,40.75&f=json")).Body), StringComparison.Ordinal);

            // Samples: a point is its pixel's value; a line is sampled evenly, its ends included.
            JsonElement point = JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/getSamples?geometry={Q("{\"x\":30.105,\"y\":40.795}")}&geometryType=esriGeometryPoint&f=json")).Body)
                .RootElement.GetProperty("samples")[0];
            Assert.Equal(Height(10, 20), double.Parse(point.GetProperty("value").GetString()!, System.Globalization.CultureInfo.InvariantCulture), 2);
            Assert.Equal(0.01, point.GetProperty("resolution").GetDouble(), 9);

            string line = Q("{\"paths\":[[[30.005,40.995],[30.635,40.995]]],\"spatialReference\":{\"wkid\":4326}}");
            JsonElement[] along = [.. JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/getSamples?geometry={line}&geometryType=esriGeometryPolyline&sampleCount=4&f=json")).Body)
                .RootElement.GetProperty("samples").EnumerateArray()];
            Assert.Equal(4, along.Length);
            Assert.Equal(Height(0, 0), double.Parse(along[0].GetProperty("value").GetString()!, System.Globalization.CultureInfo.InvariantCulture), 2);
            Assert.Equal(Height(63, 0), double.Parse(along[3].GetProperty("value").GetString()!, System.Globalization.CultureInfo.InvariantCulture), 2);

            // Across an area, at most the count asked for, every one inside it.
            JsonElement[] across = [.. JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/getSamples?geometry={square}&geometryType=esriGeometryPolygon&sampleCount=25&f=json")).Body)
                .RootElement.GetProperty("samples").EnumerateArray()];
            Assert.InRange(across.Length, 1, 25);
            Assert.All(across, s => Assert.InRange(s.GetProperty("location").GetProperty("x").GetDouble(), 30.1, 30.2));
        }
        finally
        {
            await SendAsync(root, token!, HttpMethod.Delete, $"/admin/coverages/{name}?folder=hosted");
        }
    }

    [Fact]
    public async Task Several_images_uploaded_together_are_one_mosaic_and_ones_that_do_not_fit_are_refused_by_name()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        // ADR-140: two 300-pixel models side by side, 0.64° each, one grid: one image 600 × 300 from 30° to 31.28° east.
        string name = $"zz_mos_{Guid.NewGuid():N}"[..16];
        string service = $"/rest/services/hosted/{name}/ImageServer";

        try
        {
            using MultipartFormDataContent form = new();
            form.Add(new ByteArrayContent(Elevation(300)), "file", "west.tif");
            form.Add(new ByteArrayContent(Elevation(300, west: 30.64)), "file", "east.tif");
            form.Add(new StringContent(name), "name");
            (HttpStatusCode made, byte[] madeBody) = await SendAsync(root, token!, HttpMethod.Post, "/admin/coverages/upload", form);
            Assert.True(made == HttpStatusCode.Created, $"Uploading two images answered {(int)made}: {Encoding.UTF8.GetString(madeBody)}");
            JsonElement said = JsonDocument.Parse(madeBody).RootElement;
            Assert.Equal((2, 600, 300), (said.GetProperty("images").GetInt32(), said.GetProperty("width").GetInt32(), said.GetProperty("height").GetInt32()));

            JsonElement extent = JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get, $"{service}?f=json")).Body)
                .RootElement.GetProperty("extent");
            Assert.Equal(30, extent.GetProperty("xmin").GetDouble(), 6);
            Assert.Equal(31.28, extent.GetProperty("xmax").GetDouble(), 6);

            // A point in each half answers that image's own height: the same pixel of each, so the same value.
            double cell = 0.64 / 300;
            string None = Uri.EscapeDataString("{\"rasterFunction\":\"None\"}");
            string[] values = new string[2];

            foreach ((int i, double west) in new[] { (0, 30.0), (1, 30.64) })
            {
                string at = FormattableString.Invariant($"{west + (10.5 * cell)},{41 - (20.5 * cell)}");
                values[i] = JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get,
                    $"{service}/identify?geometry={at}&geometryType=esriGeometryPoint&renderingRule={None}&f=json")).Body)
                    .RootElement.GetProperty("value").GetString()!;
            }

            Assert.Equal(800f + (30 * 1700f / 598), float.Parse(values[0], System.Globalization.CultureInfo.InvariantCulture), 2);
            Assert.Equal(values[0], values[1]);

            // ADR-143: a mosaic is given back as its tiles and a virtual raster that places them by their new names.
            (HttpStatusCode zipped, byte[] zip) = await SendAsync(root, token!, HttpMethod.Get, $"/admin/coverages/{name}/file?folder=hosted");
            Assert.Equal(HttpStatusCode.OK, zipped);
            using (System.IO.Compression.ZipArchive archive = new(new MemoryStream(zip)))
            {
                Assert.Equal([$"{name}.vrt", $"{name}_001.tif", $"{name}_002.tif"], archive.Entries.Select(e => e.FullName));
                using StreamReader vrt = new(archive.Entries[0].Open());
                string placed = vrt.ReadToEnd();
                Assert.Contains($"{name}_001.tif", placed, StringComparison.Ordinal);
                Assert.Contains($"{name}_002.tif", placed, StringComparison.Ordinal);
            }

            // Drawn whole, both halves are there.
            List<(byte Red, byte Green)> whole = Pixels((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/exportImage?bbox=30,40.36,31.28,41&bboxSR=4326&size=200,100&format=png&f=image")).Body);
            Assert.True(whole.Count > 19000, $"The mosaic drawn whole has {whole.Count} of 20000 pixels.");
        }
        finally
        {
            (HttpStatusCode removed, _) = await SendAsync(root, token!, HttpMethod.Delete, $"/admin/coverages/{name}?folder=hosted");
            Assert.Equal(HttpStatusCode.OK, removed);
        }

        // Images with another pixel size are not one grid: refused, naming the file as it was sent, and nothing published.
        string refusedName = $"zz_mosx_{Guid.NewGuid():N}"[..16];
        using MultipartFormDataContent mixed = new();
        mixed.Add(new ByteArrayContent(Elevation(300)), "file", "fine.tif");
        mixed.Add(new ByteArrayContent(Elevation(300, west: 30.64, pixel: 0.004)), "file", "coarse.tif");
        mixed.Add(new StringContent(refusedName), "name");
        (HttpStatusCode refused, byte[] refusedBody) = await SendAsync(root, token!, HttpMethod.Post, "/admin/coverages/upload", mixed);
        string why = Encoding.UTF8.GetString(refusedBody);
        Assert.True(refused == HttpStatusCode.BadRequest && why.Contains("coarse.tif", StringComparison.Ordinal)
            && why.Contains("pixel size", StringComparison.Ordinal), $"A mosaic of two pixel sizes answered {(int)refused}: {why}");
        Assert.Contains("\"error\"", Encoding.UTF8.GetString((await SendAsync(root, token!, HttpMethod.Get,
            $"/rest/services/hosted/{refusedName}/ImageServer?f=json")).Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_elevation_model_is_drawn_with_contrast_and_its_owner_sets_its_colours()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string name = $"zz_dem_{Guid.NewGuid():N}"[..16];
        string draw = $"/rest/services/hosted/{name}/ImageServer/exportImage?bbox=30,40.36,30.64,41&bboxSR=4326&size=64,64&f=image";

        try
        {
            using MultipartFormDataContent form = new();
            form.Add(new ByteArrayContent(Elevation()), "file", "dem.tif");
            form.Add(new StringContent(name), "name");
            (HttpStatusCode made, byte[] madeBody) = await SendAsync(root, token!, HttpMethod.Post, "/admin/coverages/upload", form);
            Assert.True(made == HttpStatusCode.Created, $"Uploading the model answered {(int)made}: {Encoding.UTF8.GetString(madeBody)}");

            // By default: the whole grey scale, darkest to lightest — not one white.
            List<(byte Red, byte Green)> drawn = Pixels((await SendAsync(root, token!, HttpMethod.Get, draw)).Body);
            Assert.True(drawn.Min(p => p.Red) < 30 && drawn.Max(p => p.Red) > 225,
                $"The default draws {drawn.Min(p => p.Red)}–{drawn.Max(p => p.Red)}, not the model's contrast.");

            // The format's full range is what this used to be, and it is a white page.
            await SendAsync(root, token!, HttpMethod.Put, $"/admin/coverages/{name}/style?folder=hosted",
                new StringContent("{\"stretch\":\"full\"}", Encoding.UTF8, "application/json"));
            Assert.True(Pixels((await SendAsync(root, token!, HttpMethod.Get, draw)).Body).All(p => p.Red > 250),
                "The full range of a float band is not the white page this test exists to keep away from the default.");

            // Its owner gives it a colour ramp, which colours rather than greys.
            (HttpStatusCode styled, byte[] styledBody) = await SendAsync(root, token!, HttpMethod.Put, $"/admin/coverages/{name}/style?folder=hosted",
                new StringContent("{\"stretch\":\"auto\",\"ramp\":\"terrain\"}", Encoding.UTF8, "application/json"));
            Assert.True(styled == HttpStatusCode.OK, $"Setting a ramp answered {(int)styled}: {Encoding.UTF8.GetString(styledBody)}");
            Assert.Contains(Pixels((await SendAsync(root, token!, HttpMethod.Get, draw)).Body), p => Math.Abs(p.Red - p.Green) > 30);

            JsonElement said = JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get, $"/admin/coverages/{name}/style?folder=hosted")).Body).RootElement;
            Assert.Equal("terrain", said.GetProperty("ramp").GetString());
            Assert.Equal("auto", said.GetProperty("stretch").GetString());

            // ADR-125: the legend a client draws says the range and the ramp's ends, and Pro's stretch has its numbers.
            string service = $"/rest/services/hosted/{name}/ImageServer";
            JsonElement layer = JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get, $"{service}/legend?f=json")).Body)
                .RootElement.GetProperty("layers")[0];
            Assert.Equal("Stretched", layer.GetProperty("legendType").GetString());
            string[] labels = [.. layer.GetProperty("legend").EnumerateArray().Select(e => e.GetProperty("label").GetString() ?? "")];
            Assert.True(labels.Length == 2 && labels[0].StartsWith("High : 2", StringComparison.Ordinal)
                && labels[1].StartsWith("Low : 8", StringComparison.Ordinal), $"The legend reads {string.Join(" | ", labels)}.");
            Assert.False(string.IsNullOrEmpty(layer.GetProperty("legend")[0].GetProperty("imageData").GetString()));

            Assert.Equal("{}", Encoding.UTF8.GetString((await SendAsync(root, token!, HttpMethod.Get, $"{service}/keyProperties?f=json")).Body).Trim());

            JsonElement band = JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get, $"{service}/statistics?f=json")).Body)
                .RootElement.GetProperty("statistics")[0];
            Assert.InRange(band.GetProperty("min").GetDouble(), 799, 900);
            Assert.InRange(band.GetProperty("max").GetDouble(), 2400, 2501);

            // ADR-128: the histogram Pro's stretch draws counts every measured pixel and not the hole.
            JsonElement histogram = JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get, $"{service}/histograms?f=json")).Body)
                .RootElement.GetProperty("histograms")[0];
            Assert.Equal(256, histogram.GetProperty("size").GetInt32());
            Assert.Equal((Side * Side) - 1, histogram.GetProperty("counts").EnumerateArray().Sum(c => c.GetInt64()));

            // ADR-123 condition 5: a pixel asked about as an ArcGIS client asks — an Esri point in Web Mercator.
            double lon = 30.005, lat = 40.995;
            double mx = lon * 20037508.342789244 / 180;
            double my = Math.Log(Math.Tan((90 + lat) * Math.PI / 360)) * 20037508.342789244 / Math.PI;
            string point = Uri.EscapeDataString(FormattableString.Invariant(
                $"{{\"x\":{mx},\"y\":{my},\"spatialReference\":{{\"wkid\":102100,\"latestWkid\":3857}}}}"));
            (HttpStatusCode identified, byte[] identifiedBody) = await SendAsync(root, token!, HttpMethod.Get,
                $"/rest/services/hosted/{name}/ImageServer/identify?geometry={point}&geometryType=esriGeometryPoint&f=json");
            string pixel = Encoding.UTF8.GetString(identifiedBody);
            Assert.True(identified == HttpStatusCode.OK && !pixel.Contains("\"error\"", StringComparison.Ordinal),
                $"identify with an Esri point in Web Mercator answered {(int)identified}: {pixel}");
            Assert.Contains("800", pixel, StringComparison.Ordinal);

            // ADR-127: the values themselves, as a float GeoTIFF — in the image's own reference and warped into Web
            // Mercator — whatever the style; every value one the model holds.
            foreach (string box in new[]
            {
                "bbox=30,40.36,30.64,41&bboxSR=4326",
                "bbox=3340000,4920000,3400000,5000000&bboxSR=3857",
            })
            {
                (HttpStatusCode exported, byte[] tiff) = await SendAsync(root, token!, HttpMethod.Get,
                    $"{service}/exportImage?{box}&size=32,32&format=tiff&f=image");
                Assert.Equal(HttpStatusCode.OK, exported);
                float[] values = Floats(tiff);
                Assert.Equal(32 * 32, values.Length);
                float[] measured = [.. values.Where(v => v != 0 && !float.IsNaN(v))];
                Assert.NotEmpty(measured);
                Assert.All(measured, v => Assert.InRange(v, 800f, 2500f));
            }

            // ADR-137: the same values as LERC, which the JS SDK asks for to render on the client. The blob's header says
            // its size, its type and the range of what it holds, so the values are checked without a decoder here.
            static (int Width, int Height, int Type, int Valid, double Low, double High) Lerc(byte[] blob)
            {
                Assert.True(blob.Length > 62 && Encoding.ASCII.GetString(blob, 0, 6) == "Lerc2 ",
                    $"Not a LERC blob: {Encoding.UTF8.GetString(blob, 0, Math.Min(blob.Length, 300))}");
                Assert.Equal(blob.Length, BitConverter.ToInt32(blob, 30));
                return (BitConverter.ToInt32(blob, 18), BitConverter.ToInt32(blob, 14), BitConverter.ToInt32(blob, 34),
                    BitConverter.ToInt32(blob, 22), BitConverter.ToDouble(blob, 46), BitConverter.ToDouble(blob, 54));
            }

            string lercBox = "bbox=30.1,40.5,30.5,40.9&bboxSR=4326&size=32,32";
            var heights = Lerc((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/exportImage?{lercBox}&format=lerc&lercVersion=2&compressionTolerance=0.01&f=image")).Body);
            Assert.Equal((32, 32, 6, 32 * 32), (heights.Width, heights.Height, heights.Type, heights.Valid));
            Assert.InRange(heights.Low, 800, 2500);
            Assert.InRange(heights.High, heights.Low + 1, 2500);
            var lercSlope = Lerc((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/exportImage?{lercBox}&format=lerc&renderingRule={Rule("Slope")}&f=image")).Body);
            Assert.InRange(lercSlope.Low, 0.7, 1.6);
            Assert.InRange(lercSlope.High, 0.7, 1.6);
            Assert.Contains("lercVersion", Encoding.UTF8.GetString((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/exportImage?{lercBox}&format=lerc&lercVersion=1&f=image")).Body), StringComparison.Ordinal);
            Assert.Contains("compressionTolerance", Encoding.UTF8.GetString((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/exportImage?{lercBox}&format=lerc&compressionTolerance=-1&f=image")).Body), StringComparison.Ordinal);

            // ADR-138: a renderer set on the JS SDK's ImageryLayer arrives as a Stretch and Colormap chain, and is drawn —
            // a ramp from blue to red over the stretched heights, and two classes coloured by their ranges.
            string ramp = Uri.EscapeDataString("""{"rasterFunction":"Colormap","rasterFunctionArguments":{"colorRamp":{"type":"algorithmic","algorithm":"esriCIELabAlgorithm","fromColor":[0,0,255,255],"toColor":[255,0,0,255]},"Raster":{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":5,"DRA":false}}}}""");
            List<(byte Red, byte Green)> ramped = Pixels((await SendAsync(root, token!, HttpMethod.Get, $"{draw}&format=png&renderingRule={ramp}")).Body);
            Assert.NotEmpty(ramped);
            Assert.All(ramped, p => Assert.True(p.Green < 60, $"{p.Red},{p.Green} is not between blue and red."));
            Assert.True(ramped.Max(p => p.Red) - ramped.Min(p => p.Red) > 100, "The ramp was not spread over the heights.");

            string classes = Uri.EscapeDataString("""{"rasterFunction":"Colormap","rasterFunctionArguments":{"Colormap":[[0,255,0,0],[1,0,255,0]],"Raster":{"rasterFunction":"Remap","rasterFunctionArguments":{"InputRanges":[0,1500,1500,3000],"OutputValues":[0,1],"NoDataRanges":[]}}}}""");
            List<(byte Red, byte Green)> classed = Pixels((await SendAsync(root, token!, HttpMethod.Get, $"{draw}&format=png&renderingRule={classes}")).Body);
            Assert.All(classed, p => Assert.True(p is (255, 0) or (0, 255), $"{p.Red},{p.Green} is neither class."));
            Assert.Contains((byte)255, classed.Select(p => p.Red));
            Assert.Contains((byte)255, classed.Select(p => p.Green));

            Assert.Contains("png", Encoding.UTF8.GetString((await SendAsync(root, token!, HttpMethod.Get,
                $"{draw}&format=tiff&renderingRule={ramp}")).Body), StringComparison.Ordinal);

            // ADR-136: raster functions. The model rises 1700/126 m a cell east and south over cells of 0.01° at about
            // 40.7° N, so its slope is a little over one degree and it faces north-west.
            JsonElement root2 = JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get, $"{service}?f=json")).Body).RootElement;
            Assert.True(root2.GetProperty("allowRasterFunction").GetBoolean());
            Assert.Contains("Hillshade", root2.GetProperty("rasterFunctionInfos").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
            Assert.Contains("LERC", root2.GetProperty("supportedImageFormatTypes").GetString(), StringComparison.Ordinal);
            // The JS SDK's ImageryLayer reads them from their own operation whenever the service allows them, and did not
            // load at all while it was refused.
            JsonElement functions = JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/rasterFunctionInfos?f=json")).Body).RootElement;
            Assert.Equal(
                root2.GetProperty("rasterFunctionInfos").EnumerateArray().Select(f => f.GetProperty("name").GetString()),
                functions.GetProperty("rasterFunctionInfos").EnumerateArray().Select(f => f.GetProperty("name").GetString()));

            string Rule(string function) => Uri.EscapeDataString($"{{\"rasterFunction\":\"{function}\"}}");
            string inside = "bbox=30.1,40.5,30.5,40.9&bboxSR=4326&size=32,32";

            float[] slope = Floats((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/exportImage?{inside}&format=tiff&renderingRule={Rule("Slope")}&f=image")).Body);
            float[] slopes = [.. slope.Where(v => !float.IsNaN(v))];
            Assert.NotEmpty(slopes);
            Assert.All(slopes, v => Assert.InRange(v, 0.7f, 1.6f));

            float[] aspect = Floats((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/exportImage?{inside}&format=tiff&renderingRule={Rule("Aspect")}&f=image")).Body);
            Assert.All(aspect.Where(v => !float.IsNaN(v)), v => Assert.InRange(v, 285f, 345f));

            // A hillshade is drawn grey; identify through Slope answers the slope there.
            List<(byte Red, byte Green)> shade = Pixels((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/exportImage?{inside}&format=png&renderingRule={Rule("Hillshade")}&f=image")).Body);
            Assert.All(shade, p => Assert.Equal(p.Red, p.Green));
            string slopeHere = Encoding.UTF8.GetString((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/identify?geometry=30.3,40.7&geometryType=esriGeometryPoint&renderingRule={Rule("Slope")}&f=json")).Body);
            double slopeValue = double.Parse(JsonDocument.Parse(slopeHere).RootElement.GetProperty("value").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(slopeValue, 0.7, 1.6);

            // A function this server does not apply is refused by name.
            string ndvi = Encoding.UTF8.GetString((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/exportImage?{inside}&renderingRule={Rule("NDVI")}&f=image")).Body);
            Assert.Contains("NDVI", ndvi, StringComparison.Ordinal);

            // The owner shows the service as its slope by default: a plain draw is then the slope's colours.
            (HttpStatusCode shownAs, byte[] shownBody) = await SendAsync(root, token!, HttpMethod.Put, $"/admin/coverages/{name}/style?folder=hosted",
                new StringContent("{\"function\":\"slope\"}", Encoding.UTF8, "application/json"));
            Assert.True(shownAs == HttpStatusCode.OK, Encoding.UTF8.GetString(shownBody));
            // Barely a degree everywhere, so the slope ramp's flat end: green, not the grey of the values.
            Assert.All(Pixels((await SendAsync(root, token!, HttpMethod.Get, draw)).Body), p => Assert.True(p.Green - p.Red > 60, $"{p.Red},{p.Green}"));
            Assert.Equal("slope", JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get, $"/admin/coverages/{name}/style?folder=hosted")).Body)
                .RootElement.GetProperty("function").GetString());

            // The service says its default, identify answers through it and says which, and None asks for the height.
            Assert.Equal("Slope", JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get, $"{service}?f=json")).Body)
                .RootElement.GetProperty("defaultRasterFunction").GetString());
            JsonElement byDefault = JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/identify?geometry=30.3,40.7&geometryType=esriGeometryPoint&f=json")).Body).RootElement;
            Assert.Equal("Slope", byDefault.GetProperty("rasterFunction").GetString());
            JsonElement asHeight = JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/identify?geometry=30.3,40.7&geometryType=esriGeometryPoint&renderingRule={Rule("None")}&f=json")).Body).RootElement;
            Assert.False(asHeight.TryGetProperty("rasterFunction", out _));
            Assert.InRange(double.Parse(asHeight.GetProperty("value").GetString()!, System.Globalization.CultureInfo.InvariantCulture), 800, 2500);

            // A fixed range that runs backwards, and a ramp that does not exist, are refused.
            (HttpStatusCode backwards, _) = await SendAsync(root, token!, HttpMethod.Put, $"/admin/coverages/{name}/style?folder=hosted",
                new StringContent("{\"stretch\":\"fixed\",\"minimum\":2000,\"maximum\":1000}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, backwards);
            (HttpStatusCode unknown, _) = await SendAsync(root, token!, HttpMethod.Put, $"/admin/coverages/{name}/style?folder=hosted",
                new StringContent("{\"ramp\":\"rainbow\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, unknown);
        }
        finally
        {
            await SendAsync(root, token!, HttpMethod.Delete, $"/admin/coverages/{name}?folder=hosted");
        }
    }
}
