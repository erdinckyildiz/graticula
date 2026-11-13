using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-299 conditions 1 and 2: what a reference service definition is read as, which queries are served as their table
/// and which are refused, the SOAP job envelope Pro sends, and the portal Pro is told owns the server.
/// </summary>
public sealed class ReferenceServiceDefinitionTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "ServiceDefinitions", name);

    [Fact]
    public async Task The_map_is_read_from_its_CIM_with_groups_flattened_and_tables_after_layers()
    {
        ReferenceDefinition definition = await ReferenceServiceDefinition.ReadAsync(Fixture("reference.sd"), CancellationToken.None);

        Assert.Equal("Reference_Fixture", definition.ServiceName);
        Assert.Null(definition.FolderName);
        Assert.Equal("Referenced", definition.Item.Snippet);
        Assert.Equal(["Roads", "Parcels", "Local", "Owners"], definition.Layers.Select(l => l.Title).ToArray());

        ReferenceLayer roads = definition.Layers[0];
        Assert.Null(roads.Refusal);
        Assert.Equal(("transport", "roads"), (roads.Schema, roads.Table));
        Assert.Equal(["gid", "name", "geom"], roads.Columns);
        Assert.Equal("gid", roads.ObjectIdField);
        Assert.Equal(new ArcGisConnection("postgresql", "db.example", 5433, "gis", "pub"), roads.Connection);

        ReferenceLayer parcels = definition.Layers[1];
        Assert.Null(parcels.Refusal);
        Assert.Equal(("cadastre", "parcels"), (parcels.Schema, parcels.Table));
        Assert.Null(parcels.Columns);

        Assert.Contains("not in a PostgreSQL database", definition.Layers[2].Refusal, StringComparison.Ordinal);

        ReferenceLayer owners = definition.Layers[3];
        Assert.True(owners.IsTable);
        Assert.Null(owners.Columns);
        Assert.Equal(("cadastre", "owners"), (owners.Schema, owners.Table));
    }

    [Theory]
    [InlineData("select objectid,geom from hosted.wms_time_demo", "hosted", "wms_time_demo", "objectid,geom")]
    [InlineData("SELECT * FROM gis_dev.public.cities", "public", "cities", null)]
    [InlineData("select * from cities;", "public", "cities", null)]
    [InlineData("select \"Name\", geom from \"My Schema\".\"Roads\"", "My Schema", "Roads", "Name,geom")]
    [InlineData("select\n  id,\n  geom\nfrom\n  s.t", "s", "t", "id,geom")]
    public void A_plain_select_is_its_table(string sql, string schema, string table, string? columns)
    {
        Assert.True(ReferenceServiceDefinition.TryTable(sql, "gis_dev", out string? s, out string? t, out List<string>? c, out string? why), why);
        Assert.Equal((schema, table), (s, t));
        Assert.Equal(columns, c is null ? null : string.Join(',', c));
    }

    [Theory]
    [InlineData("select * from roads where kind = 'a'")]
    [InlineData("select a.id, b.geom from a join b on a.id = b.id")]
    [InlineData("select id, st_buffer(geom, 1) as geom from roads")]
    [InlineData("select id as gid, geom from roads")]
    [InlineData("select * from (select * from roads) x")]
    [InlineData("select * from other_db.public.roads")]
    [InlineData("select * from a.b.c.d")]
    [InlineData("")]
    public void Anything_else_is_refused_with_a_reason(string sql)
    {
        Assert.False(ReferenceServiceDefinition.TryTable(sql, "gis_dev", out _, out _, out _, out string? why));
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    [Fact]
    public void A_query_layers_own_dataset_name_is_not_a_table()
    {
        Assert.False(ReferenceServiceDefinition.TryName("gis_dev.gis.%cities", "gis_dev", out _, out _, out string? why));
        Assert.Contains("query layer", why, StringComparison.Ordinal);
    }

    [Fact]
    public void The_measured_SubmitJob_envelope_gives_the_item_id_first()
    {
        // Recorded on dev 2026-11-12 (ADR-299 §4), the item id replaced.
        const string envelope =
            "<?xml version=\"1.0\" encoding=\"utf-8\" ?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" "
            + "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:tns=\"http://www.esri.com/schemas/ArcGIS/3.7.0\"><soap:Body>"
            + "<tns:SubmitJob><ToolName>PublishServiceDefinition</ToolName><Values xsi:type=\"tns:GPValues\">"
            + "<GPValue xsi:type=\"tns:GPString\"><Value>i0123</Value></GPValue><GPValue xsi:type=\"tns:GPString\"></GPValue>"
            + "<GPValue xsi:type=\"tns:GPString\"><Value>{&quot;buildInitialCache&quot;:false}</Value></GPValue>"
            + "</Values></tns:SubmitJob></soap:Body></soap:Envelope>";

        Assert.Equal("SubmitJob", PortalEndpoints.SoapOperation(envelope));
        Assert.Equal("PublishServiceDefinition", PortalEndpoints.SoapText(envelope, "ToolName"));
        Assert.Equal(["i0123", null, "{\"buildInitialCache\":false}"], PortalEndpoints.GpValues(envelope));
    }

    [Theory]
    [InlineData("ArcGIS Pro 3.7.0 (00000000000) - ArcGISPro", true)]
    [InlineData("ArcGIS Pro 3.5.2 (12345) - arcpy", true)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0) Chrome/130", false)]
    [InlineData("ArcGIS Field Maps/24.4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Pro_is_known_by_the_name_it_gives(string? agent, bool pro) =>
        Assert.Equal(pro, PortalEndpoints.IsArcGisPro(agent));

    [Theory]
    [InlineData("ArcGIS Pro 3.7.0 (00000000000) - ArcGISPro", true, "https://h.example/studio", "https://h.example/studio/sharing/rest/generateToken")]
    [InlineData("ArcGIS Pro 3.7.0 (00000000000) - ArcGISPro", false, "https://h.example", "https://h.example/rest/generateToken")]
    [InlineData("ArcGIS Field Maps/24.4", true, "https://h.example", "https://h.example/rest/generateToken")]
    public void Only_a_publishing_Pro_is_told_the_studio_portal(string agent, bool publishing, string owning, string token)
    {
        DefaultHttpContext context = new()
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["Graticula:PublishFromPro"] = publishing ? "true" : "false" })
                    .Build())
                .BuildServiceProvider(),
        };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("h.example");
        context.Request.Headers.UserAgent = agent;

        Assert.Equal(owning, PortalEndpoints.OwningSystemUrl(context));
        Assert.Equal(token, PortalEndpoints.TokenServicesUrl(context));
    }
}
