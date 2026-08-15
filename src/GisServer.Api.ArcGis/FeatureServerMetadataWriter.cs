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

    /// <summary>The service catalogue, at the root or inside a folder.</summary>
    /// <param name="serviceNames">The layers this caller may see, unqualified.</param>
    /// <param name="folders">Folder names to advertise. Empty inside a folder.</param>
    /// <param name="folder">
    /// Which folder is being listed, or null for the root. Service names are
    /// reported prefixed with it, because that is what a client builds its URL
    /// from.
    /// </param>
    /// <param name="tileServices">
    /// Layers that also have a VectorTileServer. ArcGIS lists a layer with two
    /// services as two entries of the same name and different types; omitting
    /// the second means a client browsing the catalogue never finds it.
    /// </param>
    /// <param name="systemServices">
    /// Services with no layer behind them — the geometry service — already
    /// carrying their folder in their name. Owner correction 2026-08-15:
    /// "geometry server is also a service", so it belongs in the directory
    /// beside the layers.
    /// </param>
    /// <returns>The catalogue document.</returns>
    public static object Catalogue(
        IEnumerable<string> serviceNames,
        IEnumerable<string> folders,
        string? folder = null,
        IEnumerable<string>? tileServices = null,
        IEnumerable<(string Name, string Type)>? systemServices = null)
    {
        ArgumentNullException.ThrowIfNull(serviceNames);
        ArgumentNullException.ThrowIfNull(folders);

        // <b>A service inside a folder reports its name with the folder on the
        // front.</b> That is what ArcGIS does, and a client builds its request
        // URL from this string — so returning the bare name here produces a
        // catalogue whose every entry 404s.
        string Qualify(string name) => folder is null ? name : $"{folder}/{name}";

        List<object> services =
            [.. serviceNames.Select(name => new { name = Qualify(name), type = "FeatureServer" })];

        // A layer can have two services and ArcGIS lists them as two entries
        // with the same name and different types. Omitting the tile entry means
        // a client browsing the catalogue never finds the tile service at all.
        if (tileServices is not null)
        {
            services.AddRange(
                tileServices.Select(name => new { name = Qualify(name), type = "VectorTileServer" }));
        }

        // A service that is not a layer — the geometry service — carries its
        // folder in its own name already, so it is added verbatim.
        if (systemServices is not null)
        {
            services.AddRange(systemServices.Select(s => new { name = s.Name, type = s.Type }));
        }

        return new
        {
            currentVersion = CurrentVersion,
            folders = folders.ToArray(),
            services,
        };
    }

    /// <summary>
    /// The folder hosted services live in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Hosted and registered services are separated by URL, not only by a
    /// flag.</b> This is the ArcGIS Enterprise shape — hosted feature services
    /// live under a <c>Hosted</c> folder and referenced ones do not — and it
    /// matters beyond convention: the two have different lifecycles. A hosted
    /// service owns its table and unpublishing may drop it; a registered service
    /// points at somebody else's table and must never. Keeping them in one
    /// namespace means every operation has to re-derive which kind it is holding.
    /// </para>
    /// <para>
    /// Lower case, and routing is case-insensitive, so a client sending
    /// <c>Hosted</c> reaches the same place.
    /// </para>
    /// </remarks>
    public const string HostedFolder = "hosted";

    /// <summary>One layer's entry in a service document.</summary>
    /// <param name="Id">Its number within the service, which is the URL segment.</param>
    /// <param name="Name">Its name.</param>
    /// <param name="GeometryType">Its declared geometry type.</param>
    /// <param name="Srid">Its spatial reference.</param>
    /// <param name="Extent">Where its features are, or null if unknown.</param>
    public readonly record struct ServiceLayer(
        int Id, string Name, GeometryKind GeometryType, int Srid, Envelope? Extent)
    {
        /// <summary>The group above it, or null at the top level.</summary>
        public int? ParentId { get; init; }
    }

    /// <summary>One group layer's entry in a service document.</summary>
    /// <param name="Id">Its number within the service.</param>
    /// <param name="Name">Its name.</param>
    /// <param name="ParentId">The group above it, or null at the top level.</param>
    /// <param name="ChildIds">The indices directly beneath it.</param>
    public readonly record struct ServiceGroup(
        int Id, string Name, int? ParentId, IReadOnlyList<int> ChildIds);

    /// <summary>
    /// The service document at <c>/rest/services/{name}/FeatureServer</c>.
    /// </summary>
    /// <param name="layers">Its layers, in index order.</param>
    /// <param name="capabilities">What the caller may do — see the remarks on the type.</param>
    /// <param name="description">What the service is for, or null.</param>
    /// <param name="groups">
    /// Its group layers. They share one numbering with the feature layers and
    /// appear in the same <c>layers</c> array, because that is ArcGIS's shape:
    /// the tree is carried by <c>parentLayerId</c> and <c>subLayerIds</c> rather
    /// than by nesting the JSON.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>A service is a container of layers</b> — owner correction 2026-08-15.
    /// It used to take one layer and hard-code <c>id = 0</c>, which is why every
    /// route in this server ended in <c>/0</c>.
    /// </para>
    /// <para>
    /// <b>The extent is the union of its layers', and the spatial reference is
    /// the first layer's.</b> A service whose layers disagree about their
    /// reference cannot state one, and every client reads a single
    /// <c>spatialReference</c> here — so mixing them is refused at publication
    /// rather than papered over at read time.
    /// </para>
    /// </remarks>
    public static object Service(
        IReadOnlyList<ServiceLayer> layers,
        string capabilities,
        string? description = null,
        IReadOnlyList<ServiceGroup>? groups = null)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilities);

        // A service with no layers is a real state — it has been created and not
        // filled yet — and it still has to answer with a valid document.
        int srid = layers.Count > 0 ? layers[0].Srid : 4326;
        Envelope? extent = Union(layers);

        return new
        {
            currentVersion = CurrentVersion,
            serviceDescription = string.Empty,
            hasVersionedData = false,
            supportsDisconnectedEditing = false,
            hasStaticData = true,
            maxRecordCount = MaxRecordCount,
            supportedQueryFormats = "JSON",

            // <b>Computed per caller, and saying so accurately is the point.</b>
            // ADR-008 §2's never-degrade-silently applies here before anywhere
            // else: a client reads this string to decide whether to offer an
            // edit button, so over-claiming puts that button in front of
            // somebody who will be refused when they press it.
            capabilities,
            description = description ?? string.Empty,
            copyrightText = string.Empty,
            spatialReference = SpatialReference(srid),
            initialExtent = ExtentOrNull(extent, srid),
            fullExtent = ExtentOrNull(extent, srid),
            allowGeometryUpdates = capabilities.Contains("Update", StringComparison.Ordinal),
            units = UnitsOf(srid),

            // <b>One flat array holding a tree, which is ArcGIS's shape and not
            // an approximation of it.</b> Groups and feature layers share one
            // numbering and one list; the structure is carried by
            // <c>parentLayerId</c> and <c>subLayerIds</c> rather than by
            // nesting. A client reads the list once and rebuilds the tree.
            //
            // <b>-1 means no parent; null would mean unknown.</b> The two are
            // different answers and clients treat them differently, so the
            // field is always written.
            layers = Tree(layers, groups ?? []),
            tables = Array.Empty<object>(),
        };
    }

    /// <summary>
    /// Groups and feature layers as one list, ordered by index.
    /// </summary>
    /// <remarks>
    /// <b>Ordered by id, because the ids are the order.</b> A client that draws
    /// the list in the order given, with a group's children indented beneath it,
    /// gets the publisher's arrangement — and indices are allocated in
    /// publication order, so sorting by id is sorting by when things were added.
    /// </remarks>
    private static object[] Tree(
        IReadOnlyList<ServiceLayer> layers, IReadOnlyList<ServiceGroup> groups)
    {
        List<(int Id, object Entry)> entries = [];

        foreach (ServiceGroup group in groups)
        {
            entries.Add((group.Id, new
            {
                id = group.Id,
                name = group.Name,
                parentLayerId = group.ParentId ?? -1,
                defaultVisibility = true,

                // <b>Empty stays an empty array, never null.</b> A group with no
                // children is a real state — it was just created — and a client
                // reading null there may treat it as a leaf and draw the group
                // as though it were a layer with no geometry.
                subLayerIds = group.ChildIds.ToArray(),
                minScale = 0,
                maxScale = 0,
                type = "Group Layer",

                // A group has no geometry, and saying so as null rather than
                // omitting the field keeps every entry the same shape.
                geometryType = (string?)null,
            }));
        }

        foreach (ServiceLayer layer in layers)
        {
            entries.Add((layer.Id, new
            {
                id = layer.Id,
                name = layer.Name,
                parentLayerId = layer.ParentId ?? -1,
                defaultVisibility = true,
                subLayerIds = (int[]?)null,
                minScale = 0,
                maxScale = 0,
                type = "Feature Layer",
                geometryType = (string?)ArcGisGeometryWriter.TypeName(layer.GeometryType),
            }));
        }

        entries.Sort((a, b) => a.Id.CompareTo(b.Id));

        return [.. entries.Select(e => e.Entry)];
    }

    /// <summary>
    /// The document for a group layer at <c>/FeatureServer/{id}</c>.
    /// </summary>
    /// <param name="group">The group.</param>
    /// <param name="capabilities">What the caller may do in this service.</param>
    /// <returns>The document.</returns>
    /// <remarks>
    /// <b>A group answers at its own index, and answers as a group.</b> A client
    /// that follows a <c>subLayerIds</c> entry, or a person who clicks it in the
    /// directory, arrives here — and getting a 404 for an id the service itself
    /// advertised is the kind of inconsistency that makes a client give up on
    /// the whole service. It has no fields and no extent because it has no data;
    /// those are absent rather than empty, which is the honest difference
    /// between <em>none</em> and <em>not applicable</em>.
    /// </remarks>
    public static object GroupLayerDocument(ServiceGroup group, string capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilities);

        return new
        {
            currentVersion = CurrentVersion,
            id = group.Id,
            name = group.Name,
            type = "Group Layer",
            description = string.Empty,
            parentLayerId = group.ParentId ?? -1,
            subLayerIds = group.ChildIds.ToArray(),
            defaultVisibility = true,
            minScale = 0,
            maxScale = 0,
            capabilities,

            // Said out loud rather than left to inference. A client that tries
            // to query a group gets a refusal from the query endpoint; this is
            // where it can find out first.
            note = "A group layer organises other layers and holds no data of its own. It has no "
                 + "fields and cannot be queried; query the layers listed in subLayerIds.",
        };
    }

    /// <summary>The smallest box containing every layer's, or null.</summary>
    private static Envelope? Union(IReadOnlyList<ServiceLayer> layers)
    {
        Envelope? union = null;

        foreach (ServiceLayer layer in layers)
        {
            if (layer.Extent is not { } extent)
            {
                continue;
            }

            union = union is not { } sofar
                ? extent
                : new Envelope(
                    Math.Min(sofar.MinX, extent.MinX),
                    Math.Min(sofar.MinY, extent.MinY),
                    Math.Max(sofar.MaxX, extent.MaxX),
                    Math.Max(sofar.MaxY, extent.MaxY));
        }

        return union;
    }

    /// <summary>The layer document at <c>/rest/services/{name}/FeatureServer/0</c>.</summary>
    /// <param name="layer">The layer definition.</param>
    /// <param name="geometryType">Its declared geometry type.</param>
    /// <param name="description">Its fields and extent.</param>
    /// <param name="capabilities">What the caller may do.</param>
    /// <param name="relationships">
    /// Declared relationships this layer takes part in, from either side, in the
    /// shape an ArcGIS client reads. A relationship a client cannot discover is
    /// one nobody follows.
    /// </param>
    /// <param name="layerId">
    /// Its number within the service, which is the segment the client used to
    /// reach it. Zero for a single-layer service, which was every service until
    /// 2026-08-15.
    /// </param>
    public static object Layer(
        LayerDefinition layer,
        GeometryKind geometryType,
        LayerDescription description,
        string capabilities,
        IEnumerable<object>? relationships = null,
        int layerId = 0)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilities);

        return new
        {
            currentVersion = CurrentVersion,
            id = layerId,
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

            fields = Fields(layer, description, capabilities),
            extent = ExtentOrNull(description.Extent, layer.Srid),

            capabilities,
            maxRecordCount = MaxRecordCount,
            supportedQueryFormats = "JSON",
            // Hosted layers only: ADR-013 §4c's registered cases are designed
            // and not built, and declaring a capability on a layer that refuses
            // it is worse than not declaring it.
            hasAttachments = layer.IsHosted,

            // <b>Declared relationships, so a client can find them.</b> A
            // relationship a client cannot discover is one nobody follows —
            // queryRelatedRecords needs an id, and this document is the only
            // place an ArcGIS client looks for it.
            relationships = relationships ?? Array.Empty<object>(),
            hasStaticData = true,
            isDataVersioned = false,

            // <b>Two of these were false while the query endpoint honoured
            // them, and that is the never-degrade-silently rule broken in the
            // direction nobody checks.</b> ADR-008 §2 is usually read as "do not
            // over-claim", because over-claiming puts a button in front of
            // somebody that returns an error. Under-claiming is quieter and not
            // harmless: a client reading supportsPagination=false does not page,
            // so it asks for the whole layer in one request or gives up on the
            // large ones. resultOffset and resultRecordCount have worked since
            // the query endpoint was written, and orderByFields with them.
            //
            // <b>Pagination is honest here because the order is deterministic.</b>
            // Esri's documentation is explicit that a paginated query with a
            // constant where clause must return a consistent sort order across
            // pages, and PostgreSQL's LIMIT/OFFSET without an ORDER BY does not
            // — page two can repeat rows from page one. The provider therefore
            // orders by the identity column whenever an offset is given
            // (PostGisFeatureSource), which is what makes the claim true rather
            // than merely convenient.
            supportsAdvancedQueries = true,
            supportsStatistics = false,
            supportsPagination = true,
            supportsOrderBy = true,
            supportsDistinct = false,
            supportsReturningQueryExtent = false,

            // <b>The nested object is the one clients actually read.</b> The
            // flat flags above are the older shape and are kept because some
            // clients still look at them; advancedQueryCapabilities is where the
            // ArcGIS REST specification puts these, and a client that reads only
            // it would have concluded we support nothing.
            advancedQueryCapabilities = new
            {
                supportsPagination = true,
                supportsOrderBy = true,
                supportsStatistics = false,
                supportsDistinct = false,
                supportsReturningQueryExtent = false,
                supportsQueryWithDistance = false,
                supportsSqlExpression = false,
                supportsHavingClause = false,
                supportsCountDistinct = false,
                supportsQueryWithResultType = false,
            },

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

    private static object[] Fields(
        LayerDefinition layer, LayerDescription description, string capabilities) =>
        [.. description.Fields.Select(field => new
        {
            name = field.Name,
            type = string.Equals(field.Name, layer.ObjectIdColumn, StringComparison.Ordinal)
                ? "esriFieldTypeOID"
                : TypeName(field.Type),
            alias = field.Name,
            length = field.MaxLength,
            nullable = field.Nullable,

            // The object id is never editable even when the layer is: it is
            // assigned by the database, and a client offering to change it would
            // be offering to break every reference to the feature.
            editable = capabilities.Contains("Update", StringComparison.Ordinal)
                && !string.Equals(field.Name, layer.ObjectIdColumn, StringComparison.Ordinal),
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
