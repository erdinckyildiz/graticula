using System;
using System.IO;
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
/// ADR-154: a classified image's classes, named by its owner, are ArcGIS's raster attribute table; ADR-155: an image
/// service measures on the ground and moves between its columns and rows and the map. On images written here: a
/// land cover of 1 in the west and 2 in the east, and a surface 250 high, both 8 × 8 cells of 0.01° from 30° E, 41° N.
/// </summary>
[Collection("catalogue walk")]
public sealed class ClassesAndMensurationConformanceTests : ArcGisClient
{
    private const int Side = 8;

    private static byte[] Image(int bits, int format, Func<int, int, double> value)
    {
        using MemoryStream file = new();
        using BinaryWriter w = new(file);
        using MemoryStream pixels = new();
        using BinaryWriter p = new(pixels);

        for (int row = 0; row < Side; row++)
        {
            for (int column = 0; column < Side; column++)
            {
                if (bits == 8) p.Write((byte)value(column, row)); else p.Write((float)value(column, row));
            }
        }

        byte[] image = pixels.ToArray();
        int imageAt = 8, scaleAt = imageAt + image.Length, tieAt = scaleAt + 24, geoAt = tieAt + 48, ifdAt = geoAt + 32;

        w.Write("II"u8.ToArray());
        w.Write((ushort)42);
        w.Write(ifdAt);
        w.Write(image);
        foreach (double d in new[] { 0.01, 0.01, 0.0 }) w.Write(d);
        foreach (double d in new[] { 0.0, 0.0, 0.0, 30.0, 41.0, 0.0 }) w.Write(d);
        foreach (ushort k in new ushort[] { 1, 1, 0, 3, 1024, 0, 1, 2, 1025, 0, 1, 1, 2048, 0, 1, 4326 }) w.Write(k);

        (ushort Tag, ushort Type, int Count, int Value)[] tags =
        [
            (256, 3, 1, Side), (257, 3, 1, Side), (258, 3, 1, bits), (259, 3, 1, 1), (262, 3, 1, 1), (273, 4, 1, imageAt),
            (277, 3, 1, 1), (278, 3, 1, Side), (279, 4, 1, image.Length), (284, 3, 1, 1), (339, 3, 1, format),
            (33550, 12, 3, scaleAt), (33922, 12, 6, tieAt), (34735, 3, 16, geoAt),
        ];

        w.Write((ushort)tags.Length);
        foreach ((ushort tag, ushort type, int count, int v) in tags)
        {
            w.Write(tag);
            w.Write(type);
            w.Write(count);

            if (type == 3 && count == 1)
            {
                w.Write((ushort)v);
                w.Write((ushort)0);
            }
            else
            {
                w.Write(v);
            }
        }

        w.Write(0);
        return file.ToArray();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        string root, string token, HttpMethod method, string path, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement.Clone());
    }

    private async Task<string> UploadAsync(string root, string token, string name, byte[] tiff)
    {
        using MultipartFormDataContent form = new();
        form.Add(new ByteArrayContent(tiff), "file", name + ".tif");
        form.Add(new StringContent(name), "name");
        (HttpStatusCode made, JsonElement said) = await SendAsync(root, token, HttpMethod.Post, "/admin/coverages/upload", form);
        Assert.True(made == HttpStatusCode.Created, said.ToString());
        return $"/rest/services/hosted/{name}/ImageServer";
    }

    [Fact]
    public async Task A_classified_image_s_classes_are_its_attribute_table_named_by_identify_and_listed_by_the_legend()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential; set the suite's user and password.");
        string name = $"zz_rat_{Guid.NewGuid():N}"[..16];
        string service = await UploadAsync(root, token!, name, Image(8, 1, (column, _) => column < Side / 2 ? 1 : 2));

        try
        {
            (HttpStatusCode none, JsonElement noneBody) = await SendAsync(root, token!, HttpMethod.Get, $"{service}/rasterAttributeTable?f=json");
            Assert.True(noneBody.TryGetProperty("error", out _), $"{(int)none}: {noneBody}");

            // Studio offers the values the image holds.
            (_, JsonElement offered) = await SendAsync(root, token!, HttpMethod.Get, $"/admin/coverages/{name}/classes?folder=hosted");
            Assert.True(offered.GetProperty("classifiable").GetBoolean());
            Assert.Equal([1d, 2d], offered.GetProperty("values").EnumerateArray().Select(v => v.GetDouble()));

            (HttpStatusCode saved, JsonElement savedBody) = await SendAsync(root, token!, HttpMethod.Put, $"/admin/coverages/{name}/classes?folder=hosted",
                new StringContent("""{"classes":[{"value":1,"name":"Water","colour":"#0000ff"},{"value":2,"name":"Forest","colour":"#008000"}]}""",
                    Encoding.UTF8, "application/json"));
            Assert.True(saved == HttpStatusCode.OK, savedBody.ToString());

            JsonElement info = (await SendAsync(root, token!, HttpMethod.Get, $"{service}?f=json")).Body;
            Assert.True(info.GetProperty("hasRasterAttributeTable").GetBoolean());

            (_, JsonElement table) = await SendAsync(root, token!, HttpMethod.Get, $"{service}/rasterAttributeTable?f=json");
            JsonElement[] rows = [.. table.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("attributes"))];
            Assert.Equal(["Water", "Forest"], rows.Select(r => r.GetProperty("ClassName").GetString()));
            Assert.Equal(255, rows[0].GetProperty("Blue").GetInt32());

            (_, JsonElement east) = await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/identify?geometry=30.065,40.975&geometryType=esriGeometryPoint&f=json");
            Assert.Equal("Forest", east.GetProperty("attributes").GetProperty("ClassName").GetString());

            (_, JsonElement legend) = await SendAsync(root, token!, HttpMethod.Get, $"{service}/legend?f=json");
            Assert.Contains("Water", legend.ToString(), StringComparison.Ordinal);

            // A colour that is not one is refused saying the form.
            (HttpStatusCode refused, _) = await SendAsync(root, token!, HttpMethod.Put, $"/admin/coverages/{name}/classes?folder=hosted",
                new StringContent("""{"classes":[{"value":1,"name":"Water","colour":"blue"}]}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, refused);
        }
        finally
        {
            await SendAsync(root, token!, HttpMethod.Delete, $"/admin/coverages/{name}?folder=hosted");
        }
    }

    [Fact]
    public async Task An_image_service_measures_on_the_ground_and_moves_between_its_cells_and_the_map()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential; set the suite's user and password.");
        string name = $"zz_msr_{Guid.NewGuid():N}"[..16];
        string service = await UploadAsync(root, token!, name, Image(32, 3, (_, _) => 250));

        static string Point(double x, double y) =>
            Uri.EscapeDataString(FormattableString.Invariant($$$"""{"x":{{{x}}},"y":{{{y}}},"spatialReference":{"wkid":4326}}"""));

        try
        {
            JsonElement info = (await SendAsync(root, token!, HttpMethod.Get, $"{service}?f=json")).Body;
            Assert.Contains("Mensuration", info.GetProperty("capabilities").GetString(), StringComparison.Ordinal);

            // One degree east at 41° N is about 84.13 km, heading a little north of east.
            (_, JsonElement distance) = await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/measure?fromGeometry={Point(30, 41)}&toGeometry={Point(31, 41)}&geometryType=esriGeometryPoint"
                + "&measureOperation=esriMensurationDistanceAndAngle&linearUnit=esriKilometers&f=json");
            Assert.InRange(distance.GetProperty("distance").GetProperty("value").GetDouble(), 84.0, 84.2);
            Assert.InRange(distance.GetProperty("azimuthAngle").GetProperty("value").GetDouble(), 89.0, 90.0);

            (_, JsonElement height) = await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/measure?fromGeometry={Point(30.025, 40.975)}&geometryType=esriGeometryPoint&measureOperation=esriMensurationPoint3D&f=json");
            Assert.Equal(250, height.GetProperty("point").GetProperty("z").GetDouble());

            // A cell of 0.01° by 0.01° at 41° N: about 841 m by 1111 m.
            string box = Uri.EscapeDataString("""{"rings":[[[30,40.99],[30,41],[30.01,41],[30.01,40.99],[30,40.99]]],"spatialReference":{"wkid":4326}}""");
            (_, JsonElement area) = await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/measure?fromGeometry={box}&geometryType=esriGeometryPolygon&measureOperation=esriMensurationAreaAndPerimeter&f=json");
            Assert.InRange(area.GetProperty("area").GetProperty("value").GetDouble(), 925_000, 945_000);
            Assert.InRange(area.GetProperty("perimeter").GetProperty("value").GetDouble(), 3880, 3925);

            (_, JsonElement shadow) = await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/measure?fromGeometry={Point(30.02, 40.98)}&toGeometry={Point(30.03, 40.98)}&geometryType=esriGeometryPoint"
                + "&measureOperation=esriMensurationHeightFromBaseAndTop&f=json");
            Assert.Contains("sensor model", shadow.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

            // Cell 2.5, 2.5 from the top-left is 30.025° E, 40.975° N — and back.
            string points = Uri.EscapeDataString("""[{"x":30.025,"y":40.975,"spatialReference":{"wkid":4326}}]""");
            (_, JsonElement located) = await SendAsync(root, token!, HttpMethod.Get, $"{service}/computePixelLocation?geometries={points}&f=json");
            Assert.Equal(2.5, located.GetProperty("geometries")[0].GetProperty("x").GetDouble(), 6);
            Assert.Equal(2.5, located.GetProperty("geometries")[0].GetProperty("y").GetDouble(), 6);

            (_, JsonElement mapped) = await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/imageToMap?geometry={Uri.EscapeDataString("""{"x":2.5,"y":2.5}""")}&f=json");
            Assert.Equal(30.025, mapped.GetProperty("geometry").GetProperty("x").GetDouble(), 6);
            Assert.Equal(40.975, mapped.GetProperty("geometry").GetProperty("y").GetDouble(), 6);
        }
        finally
        {
            await SendAsync(root, token!, HttpMethod.Delete, $"/admin/coverages/{name}?folder=hosted");
        }
    }

    /// <summary>ADR-156: Clip, Remap, Mask, Statistics and Arithmetic over HTTP, on a surface rising 10 a column eastward.</summary>
    [Fact]
    public async Task The_value_functions_answer_as_ArcGIS_clients_ask()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential; set the suite's user and password.");
        string name = $"zz_vfn_{Guid.NewGuid():N}"[..16];
        string service = await UploadAsync(root, token!, name, Image(32, 3, (column, _) => column * 10));

        async Task<string> IdentifyAsync(string rule, string at = "30.035,40.965")
        {
            (_, JsonElement said) = await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/identify?geometry={at}&geometryType=esriGeometryPoint&renderingRule={Uri.EscapeDataString(rule)}&f=json");
            Assert.False(said.TryGetProperty("error", out _), said.ToString());
            return said.GetProperty("value").GetString()!;
        }

        try
        {
            // Column 3 holds 30.
            Assert.Equal("2", await IdentifyAsync("""{"rasterFunction":"Remap","rasterFunctionArguments":{"InputRanges":[0,25,25,80],"OutputValues":[1,2]}}"""));
            Assert.Equal("30", await IdentifyAsync("""{"rasterFunction":"Statistics","rasterFunctionArguments":{"Type":3,"KernelColumns":3,"KernelRows":3}}"""));
            Assert.Equal("60", await IdentifyAsync("""{"rasterFunction":"Arithmetic","rasterFunctionArguments":{"Raster2":2,"Operation":3}}"""));
            Assert.Equal("NoData", await IdentifyAsync("""{"rasterFunction":"Mask","rasterFunctionArguments":{"IncludedRanges":[40,70]}}"""));
            Assert.Equal("NoData", await IdentifyAsync(
                """{"rasterFunction":"Clip","rasterFunctionArguments":{"ClippingGeometry":{"xmin":30.04,"ymin":40.92,"xmax":30.08,"ymax":41},"ClipType":1}}"""));
            Assert.Equal("30", await IdentifyAsync(
                """{"rasterFunction":"Clip","rasterFunctionArguments":{"ClippingGeometry":{"xmin":30.04,"ymin":40.92,"xmax":30.08,"ymax":41},"ClipType":2}}"""));

            using HttpRequestMessage picture = new(HttpMethod.Get,
                $"{root}{service}/exportImage?bbox=30,40.92,30.08,41&bboxSR=4326&size=16,16&format=png&f=image&renderingRule="
                + Uri.EscapeDataString("""{"rasterFunction":"Clip","rasterFunctionArguments":{"ClippingGeometry":{"xmin":30,"ymin":40.92,"xmax":30.04,"ymax":41}}}"""));
            picture.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage drawn = await Http.SendAsync(picture);
            Assert.Equal("image/png", drawn.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            await SendAsync(root, token!, HttpMethod.Delete, $"/admin/coverages/{name}?folder=hosted");
        }
    }
}
