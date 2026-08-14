using System;
using System.Collections.Generic;
using System.Linq;
using GisServer.Catalog;
using GisServer.Features;
using GisServer.Geometries;

namespace GisServer.Api.ArcGis;

/// <summary>
/// The metadata documents an ArcGIS client reads before it asks anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>Without these, a server that answers <c>query</c> perfectly is one no
/// client can reach.</b> Every ArcGIS client — Pro, the JavaScript SDK,
/// esri-leaflet, QGIS's ArcGIS REST connection — begins at <c>/rest/info</c>,
/// walks the catalogue, reads the service document, then reads the layer
/// document to learn the fields, the geometry type, the object-id field and the
/// extent. Only then does it issue a query.
/// </para>
/// <para>
/// This was missing entirely until 2026-08-14, which made the v1 headline claim
/// — ArcGIS FeatureServer compatibility — true for one operation nothing could
/// find. Found by asking what a client actually requests, rather than by testing
/// the operation we had already built.
/// </para>
/// <para>
/// <b>Shapes follow Esri's published REST specification</b>, which CLAUDE.md §5
/// permits as documented behaviour. Values are ours; nothing here is copied.
/// </para>
/// </remarks>
public static class FeatureServerMetadataWriter
{
    /// <summary>
    /// The REST version we report.
    /// </summary>
    /// <remarks>
    /// <b>A number clients compare against, so it is a claim rather than a
    /// label.</b> Reporting a high version invites a client to use operations we
    /// do not implement; reporting a very low one makes modern clients refuse to
    /// connect. 10.81 is chosen as a version whose feature set we can largely
    /// honour for query, and it is a stated approximation rather than a
    /// measurement — nothing has been conformance-tested against any release.
    /// </remarks>
    public const double CurrentVersion = 10.81;

    /// <summary>The maximum features one query will return.</summary>
    /// <remarks>Mirrors <see cref="FeatureQuery.MaximumLimit"/>, so a client that
    /// respects this never triggers our own clamp.</remarks>
    public const int MaxRecordCount = FeatureQuery.MaximumLimit;

    /// <summary>The <c>/rest/info</c> document.</summary>
    /// <param name="tokenServicesUrl">Where a client obtains a token.</param>
    /// <returns>An object ready for JSON serialisation.</returns>
    public static object ServerInfo(string tokenServicesUrl) => new
    {
        currentVersion = CurrentVersion,
        fullVersion = $"{CurrentVersion}.0",

        // Declared so a client knows to authenticate rather than to guess from a
        // 401. isTokenBasedSecurity is what makes Pro prompt for credentials.
        authInfo = new
        {
            isTokenBasedSecurity = true,
            tokenServicesUrl,

            // Minutes. ADR-015 §4's compatibility tokens are not implemented, so
            // this describes the session lifetime a client will actually get.
            shortLivedTokenValidity = 720,
        },
    };

    /// <summary>The service catalogue at <c>/rest/services</c>.</summary>
    /// <param name="serviceNames">The layers this caller may see.</param>
    public static object Catalogue(IEnumerable<string> serviceNames)
    {
        ArgumentNullException.ThrowIfNull(serviceNames);

        return new
        {
            currentVersion = CurrentVersion,

            // Flat. ArcGIS supports folders and we have none: a layer name is
            // unique across the server, which is the model ADR-013 chose.
            folders = Array.Empty<string>(),
            services = serviceNames.Select(name => new { name, type = "FeatureServer" }),
        };
    }

    /// <summary>The service document at <c>/rest/services/{name}/FeatureServer</c>.</summary>
    /// <param name="layer">The layer definition.</param>
    /// <param name="geometryType">Its declared geometry type.</param>
    /// <param name="extent">Where its features are, or null if unknown.</param>
    public static object Service(LayerDefinition layer, GeometryKind geometryType, Envelope? extent)
    {
        ArgumentNullException.ThrowIfNull(layer);

        return new
        {
            currentVersion = CurrentVersion,
            serviceDescription = string.Empty,
            hasVersionedData = false,
            supportsDisconnectedEditing = false,
            hasStaticData = true,
            maxRecordCount = MaxRecordCount,
            supportedQueryFormats = "JSON",

            // <b>Query only, and saying so is the point.</b> ADR-008 §2's
            // never-degrade-silently applies here before it applies anywhere
            // else: a client reads this string to decide whether to offer an
            // edit button. Claiming "Create,Update,Delete" because applyEdits is
            // planned would put that button in front of a user today.
            capabilities = "Query",
            description = string.Empty,
            copyrightText = string.Empty,
            spatialReference = SpatialReference(layer.Srid),
            initialExtent = ExtentOrNull(extent, layer.Srid),
            fullExtent = ExtentOrNull(extent, layer.Srid),
            allowGeometryUpdates = false,
            units = UnitsOf(layer.Srid),

            // One layer per service, always. ADR-013's model is a service per
            // published layer, so the id is always 0 — which is why every query
            // route in this server ends in /0.
            layers = new[]
            {
                new
                {
                    id = 0,
                    name = layer.Name,
                    parentLayerId = -1,
                    defaultVisibility = true,
                    subLayerIds = (int[]?)null,
                    minScale = 0,
                    maxScale = 0,
                    type = "Feature Layer",
                    geometryType = ArcGisGeometryWriter.TypeName(geometryType),
                },
            },
            tables = Array.Empty<object>(),
        };
    }

    /// <summary>The layer document at <c>/rest/services/{name}/FeatureServer/0</c>.</summary>
    /// <param name="layer">The layer definition.</param>
    /// <param name="geometryType">Its declared geometry type.</param>
    /// <param name="description">Its fields and extent.</param>
    public static object Layer(
        LayerDefinition layer, GeometryKind geometryType, LayerDescription description)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(description);

        return new
        {
            currentVersion = CurrentVersion,
            id = 0,
            name = layer.Name,
            type = "Feature Layer",
            description = string.Empty,
            geometryType = ArcGisGeometryWriter.TypeName(geometryType),
            copyrightText = string.Empty,

            // Null when the layer has no integer identity. ADR-013 §2a: such a
            // layer is refused by the query endpoint, and a client reading this
            // document learns why before it tries.
            objectIdField = layer.ObjectIdColumn,
            displayField = DisplayField(layer, description),
            globalIdField = string.Empty,

            fields = Fields(layer, description),
            extent = ExtentOrNull(description.Extent, layer.Srid),

            capabilities = "Query",
            maxRecordCount = MaxRecordCount,
            supportedQueryFormats = "JSON",
            hasAttachments = false,
            hasStaticData = true,
            isDataVersioned = false,

            // <b>Every one of these is false because it is false.</b> A client
            // reads them to decide which UI to offer, and an optimistic answer
            // here is the never-degrade-silently failure at its most direct:
            // the user gets a statistics panel that returns an error.
            supportsAdvancedQueries = false,
            supportsStatistics = false,
            supportsPagination = false,
            supportsOrderBy = false,
            supportsDistinct = false,
            supportsReturningQueryExtent = false,

            // The one thing we do support beyond a plain read, and the only
            // spatial relationship the query endpoint implements.
            supportedSpatialRelationships = new[] { "esriSpatialRelIntersects" },
        };
    }

    /// <summary>Maps our field types onto ArcGIS's.</summary>
    /// <remarks>
    /// <b><see cref="FieldType.BigInteger"/> becomes a string, deliberately.</b>
    /// ArcGIS has no 64-bit integer field type at this version and JavaScript
    /// loses integer precision above 2^53 — an OSM identifier silently rounded
    /// is a bug nobody finds. The same choice is made when writing the value, so
    /// the declared type and the emitted value agree.
    /// </remarks>
    public static string TypeName(FieldType type) => type switch
    {
        FieldType.SmallInteger or FieldType.Boolean => "esriFieldTypeSmallInteger",
        FieldType.Integer => "esriFieldTypeInteger",
        FieldType.Single => "esriFieldTypeSingle",
        FieldType.Double => "esriFieldTypeDouble",
        FieldType.Date => "esriFieldTypeDate",
        FieldType.Guid => "esriFieldTypeGUID",
        FieldType.Binary => "esriFieldTypeBlob",
        _ => "esriFieldTypeString",
    };

    private static object[] Fields(LayerDefinition layer, LayerDescription description) =>
        [.. description.Fields.Select(field => new
        {
            name = field.Name,
            type = string.Equals(field.Name, layer.ObjectIdColumn, StringComparison.Ordinal)
                ? "esriFieldTypeOID"
                : TypeName(field.Type),
            alias = field.Name,
            length = field.MaxLength,
            nullable = field.Nullable,

            // The object id is never editable; nothing is, while capabilities
            // says Query.
            editable = false,
            domain = (object?)null,
        })];

    /// <summary>
    /// The field a client labels features with by default.
    /// </summary>
    /// <remarks>
    /// The first text field that is not the object id, because that is almost
    /// always the name of the thing. Falling back to the object id gives a map
    /// labelled with row numbers, which is unhelpful but honest — and better
    /// than an empty string, which some clients render as a blank callout.
    /// </remarks>
    private static string DisplayField(LayerDefinition layer, LayerDescription description)
    {
        foreach (FieldDescription field in description.Fields)
        {
            if (field.Type == FieldType.Text
                && !string.Equals(field.Name, layer.ObjectIdColumn, StringComparison.Ordinal))
            {
                return field.Name;
            }
        }

        return layer.ObjectIdColumn ?? layer.IdentityColumn;
    }

    private static object SpatialReference(int srid) => new { wkid = srid, latestWkid = srid };

    private static object? ExtentOrNull(Envelope? extent, int srid) => extent is { } box
        ? new
        {
            xmin = box.MinX,
            ymin = box.MinY,
            xmax = box.MaxX,
            ymax = box.MaxY,
            spatialReference = SpatialReference(srid),
        }
        : null;

    /// <summary>
    /// The linear unit of a spatial reference.
    /// </summary>
    /// <remarks>
    /// <b>A two-case guess, and marked as one.</b> 4326 and the other geographic
    /// systems are degrees; everything else is assumed metres, which is wrong
    /// for the US State Plane systems in feet. Getting this right needs the PROJ
    /// lookup that ADR-003 puts behind a port we have not built, so it is
    /// narrow, visible, and cheap to fix when that arrives.
    /// </remarks>
    private static string UnitsOf(int srid) =>
        srid is 4326 or 4269 or 4267 ? "esriDecimalDegrees" : "esriMeters";
}
