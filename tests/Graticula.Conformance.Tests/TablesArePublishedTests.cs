using System;
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
/// A CSV or an Excel workbook with coordinates published as a hosted layer, and added to one — ADR-112.
/// </summary>
/// <remarks>
/// <b>The files are written here</b>: the CSV as text, the workbook as the five parts of the smallest XLSX, so the test
/// needs no spreadsheet program and no library. <b>What it creates it deletes</b>, under a <c>zz_</c> name.
/// </remarks>
[Collection("catalogue walk")]
public sealed class TablesArePublishedTests : ArcGisClient
{
    private const string Places = "ad,Boylam,Enlem,nufus\nAnkara,32.85,39.93,5700000\nİzmir,27.14,38.42,4400000\n";

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(
        string root, string? token, string path, byte[] file, string fileName, string? name = null)
    {
        using MultipartFormDataContent form = new();
        using ByteArrayContent bytes = new(file);

        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(bytes, "file", fileName);

        if (name is not null) form.Add(new StringContent(name), "name");

        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}{path}") { Content = form };

        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonElement> QueryAsync(string root, string token, string name)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            $"{root}/rest/services/hosted/{name}/FeatureServer/0/query?where=1%3D1&outFields=*&outSR=4326&orderByFields=objectid&f=json");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"Querying '{name}' failed with {(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task DeleteAsync(string root, string token, string name)
    {
        using HttpRequestMessage request = new(HttpMethod.Delete, $"{root}/admin/featureservices/{name}?folder=hosted&drop=true");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);
    }

    /// <summary>The smallest workbook: one sheet, its cells written inline.</summary>
    private static byte[] Workbook(params string[][] rows)
    {
        StringBuilder sheet = new("""<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");

        for (int r = 0; r < rows.Length; r++)
        {
            sheet.Append($"<row r=\"{r + 1}\">");

            for (int c = 0; c < rows[r].Length; c++)
            {
                string cell = $"{(char)('A' + c)}{r + 1}";
                string value = rows[r][c];

                sheet.Append(double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
                    ? $"<c r=\"{cell}\"><v>{value}</v></c>"
                    : $"<c r=\"{cell}\" t=\"inlineStr\"><is><t>{value}</t></is></c>");
            }

            sheet.Append("</row>");
        }

        sheet.Append("</sheetData></worksheet>");

        using MemoryStream bytes = new();

        using (ZipArchive zip = new(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Part(string path, string text)
            {
                using StreamWriter writer = new(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }

            Part("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""");
            Part("_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Part("xl/workbook.xml", """<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Part("xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
            Part("xl/worksheets/sheet1.xml", sheet.ToString());
        }

        return bytes.ToArray();
    }

    [Fact]
    public async Task A_csv_with_longitude_and_latitude_is_published_and_a_workbook_is_added_to_it()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string name = $"zz_table_{Guid.NewGuid():N}"[..20];

        (HttpStatusCode made, string madeBody) =
            await PostAsync(root, token, "/admin/hosted/import", Encoding.UTF8.GetBytes(Places), "iller.csv", name);

        Assert.True(made is HttpStatusCode.Created or HttpStatusCode.OK, $"The CSV import failed: {(int)made} {madeBody}");

        try
        {
            // Points from the Turkish column names, numbers kept as numbers, and the coordinate columns not kept twice.
            JsonElement first = await QueryAsync(root, token!, name);
            JsonElement[] features = [.. first.GetProperty("features").EnumerateArray()];

            Assert.Equal(2, features.Length);
            Assert.Equal("esriGeometryPoint", first.GetProperty("geometryType").GetString());
            Assert.Equal("İzmir", features[1].GetProperty("attributes").GetProperty("ad").GetString());
            Assert.Equal(27.14, features[1].GetProperty("geometry").GetProperty("x").GetDouble(), 6);
            Assert.Equal(5700000, features[0].GetProperty("attributes").GetProperty("nufus").GetInt64());
            Assert.False(features[0].GetProperty("attributes").TryGetProperty("Boylam", out _),
                "The longitude column was kept as an attribute as well as the geometry.");

            // A workbook added to it, its columns named the other way.
            byte[] book = Workbook(["ad", "lon", "lat", "nufus"], ["Bursa", "29.06", "40.18", "3100000"]);

            (HttpStatusCode appended, string appendBody) =
                await PostAsync(root, token, $"/admin/hosted/{name}/append", book, "yeni.xlsx");

            Assert.True(appended == HttpStatusCode.OK, $"Appending the workbook failed: {(int)appended} {appendBody}");

            JsonElement[] after = [.. (await QueryAsync(root, token!, name)).GetProperty("features").EnumerateArray()];

            Assert.Equal(3, after.Length);
            Assert.Equal("Bursa", after[2].GetProperty("attributes").GetProperty("ad").GetString());
            Assert.Equal(40.18, after[2].GetProperty("geometry").GetProperty("y").GetDouble(), 6);

            // A table without coordinates says what it needed, and changes nothing.
            (HttpStatusCode bare, string bareBody) = await PostAsync(
                root, token, $"/admin/hosted/{name}/append", Encoding.UTF8.GetBytes("ad,val\nx,1\n"), "bare.csv");

            Assert.True(bare == HttpStatusCode.BadRequest, $"A table without coordinates answered {(int)bare}: {bareBody}");
            Assert.Contains("coordinates", bareBody, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(3, (await QueryAsync(root, token!, name)).GetProperty("features").GetArrayLength());

            // Metres are not read as degrees: an easting without a coordinate system is refused.
            (HttpStatusCode metres, string metresBody) = await PostAsync(
                root, token, $"/admin/hosted/{name}/append",
                Encoding.UTF8.GetBytes("ad,easting,northing\nx,500000,4400000\n"), "utm.csv");

            Assert.True(metres == HttpStatusCode.BadRequest, $"An easting without an EPSG code answered {(int)metres}: {metresBody}");
            Assert.Contains("EPSG", metresBody, StringComparison.Ordinal);

            // A ZIP named .csv is not believed.
            (HttpStatusCode renamed, _) = await PostAsync(root, token, $"/admin/hosted/{name}/append", book, "book.csv");

            Assert.True(renamed == HttpStatusCode.BadRequest, $"A workbook named .csv answered {(int)renamed}.");
        }
        finally
        {
            await DeleteAsync(root, token!, name);
        }
    }
}
