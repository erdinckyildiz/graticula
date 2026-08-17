using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Cartography;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Api.ArcGis;

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

    /// <summary>
    /// The figure to advertise, once a service's own ceiling is taken into account.
    /// </summary>
    /// <remarks>
    /// <b>Reporting the server's figure while enforcing a lower one is a lie a client
    /// acts on.</b> ADR-031 lets a service cap its rows; until 2026-08-17 both
    /// documents here reported the server constant regardless, so a service capped at
    /// 20,000 advertised 50,000 — and a client that trusts <c>maxRecordCount</c> to
    /// size its paging, as the SDK does, would page against a number that does not
    /// exist. The ceiling only narrows, so the smaller of the two is the truth.
    /// </remarks>
    /// <param name="ceiling">The service's ceiling, or null when it has none.</param>
    /// <returns>What the documents should say.</returns>
    public static int AdvertisedMaxRecordCount(int? ceiling) =>
        ceiling is { } c && c < MaxRecordCount ? c : MaxRecordCount;

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

    /// <summary>
    /// The <c>drawingInfo</c> an ArcGIS client draws with, from the generated appearance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The renderer is <c>simple</c>, and the symbol vocabulary is the documented
    /// subset</b> — <c>esriSFS</c>, <c>esriSLS</c>, <c>esriSMS</c>. ADR-033 §5e says we do
    /// not claim CIM, and the honest way to not claim something is to emit only what we
    /// mean: a client asking for a fill gets a fill it understands rather than a symbol
    /// reference it has to resolve.
    /// </para>
    /// <para>
    /// <b>Colours are <c>[r, g, b, a]</c> with alpha in 0–255</b>, which is the shape the
    /// ArcGIS REST specification defines for a symbol colour. Opacity therefore lives in
    /// the alpha channel here and in a separate paint property on the tile face — the same
    /// decision expressed twice because the two formats spell it differently, which is
    /// precisely why one generator decides it and two writers only translate.
    /// </para>
    /// </remarks>
    /// <param name="layerName">The layer, which is what the colour is derived from.</param>
    /// <param name="geometryType">Its geometry, which decides the symbol shape.</param>
    /// <returns>An object ready for JSON serialisation.</returns>
    public static object DrawingInfo(string layerName, GeometryKind geometryType)
    {
        Appearance appearance = GeneratedSymbology.For(layerName, geometryType);

        (byte red, byte green, byte blue) = GeneratedSymbology.Bytes(appearance.Colour);
        int alpha = (int)Math.Round(appearance.Opacity * 255);

        int[] colour = [red, green, blue, alpha];

        object? outline = appearance.Outline is { } line
            ? new
            {
                type = "esriSLS",
                style = "esriSLSSolid",
                color = Outline(line),
                width = appearance.OutlineWidth,
            }
            : null;

        object symbol = appearance.Kind switch
        {
            AppearanceKind.Marker => new
            {
                type = "esriSMS",
                style = "esriSMSCircle",
                color = colour,

                // Points, because that is the unit an ArcGIS symbol size is in — the
                // generated appearance is in pixels, and at 96 DPI the conversion is
                // three quarters. Rounding is deliberate: a fractional point size is
                // rendered inconsistently across clients.
                size = Math.Round(appearance.Size * 2 * 0.75, 1),
                outline,
            },

            AppearanceKind.Line => new
            {
                type = "esriSLS",
                style = "esriSLSSolid",
                color = colour,
                width = Math.Round(appearance.Size * 0.75, 1),
            },

            _ => new
            {
                type = "esriSFS",
                style = "esriSFSSolid",
                color = colour,
                outline,
            },
        };

        return new
        {
            renderer = new
            {
                type = "simple",
                symbol,

                // Named because ArcGIS Pro's table of contents shows this string, and an
                // empty label there reads as a broken layer rather than as a single
                // symbol. The layer's own name is the only honest thing to put in it.
                label = layerName,
                description = string.Empty,
            },

            // <b>Zero, and the opacity is in the symbol's alpha instead.</b> Both are
            // honoured by clients and applying both would multiply them, so a 45% fill
            // would arrive at 20% — the class of fault that looks like a rendering bug.
            transparency = 0,

            // No labels yet. Emitting null rather than omitting it says the server
            // considered labelling and has nothing to say, which is what ADR-033 §5g
            // leaves for later.
            labelingInfo = (object?)null,
        };
    }

    private static int[] Outline(string hex)
    {
        (byte red, byte green, byte blue) = GeneratedSymbology.Bytes(hex);
        return [red, green, blue, 255];
    }

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
    /// <param name="maxRecordCount">
    /// This service's own row ceiling, or null when it has none. The smaller of it
    /// and the server's is advertised, because that is the one a client will meet.
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
        IReadOnlyList<ServiceGroup>? groups = null,
        int? maxRecordCount = null)
    {
        ArgumentNullException.ThrowIfNull(layers);
        // <b>An empty capabilities string is a real state, and refusing it was a 500 on a public
        // document.</b> Found 2026-08-17: `hosted/look_EarlyAlert` carried `capability_ceiling = {}`
        // — an explicit ceiling of *no operations* — so `Restrict` returned nothing, `Join` produced
        // `""`, and this guard threw. The service document answered **500** to an anonymous client,
        // and sixteen conformance tests with it.
        //
        // <b>The guard was asking the wrong question.</b> Null or whitespace is not one condition
        // here: null is a caller that forgot to pass anything, and empty is a service configured to
        // offer nothing. ArcGIS expresses the second as `"capabilities": ""`, which is a document a
        // client can read and act on. So null still throws — that is a programming error — and empty
        // renders.
        //
        // <b>It is this month's recurring class again</b>, from the other end: the write path
        // accepted a state the read path could not represent. D-57 was a setter writing a column
        // nothing read; this is a setter writing a value nothing could render. The lesson each time
        // is that the two paths have to agree about what the set of values *is*.
        ArgumentNullException.ThrowIfNull(capabilities);

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
            maxRecordCount = AdvertisedMaxRecordCount(maxRecordCount),
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
        // Empty is a service that offers nothing — see Service() for why that renders
        // rather than throwing.
        ArgumentNullException.ThrowIfNull(capabilities);

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
    /// <param name="maxRecordCount">
    /// This service's own row ceiling, or null when it has none. The smaller of it
    /// and the server's is advertised, because that is the one a client will meet.
    /// </param>
    public static object Layer(
        LayerDefinition layer,
        GeometryKind geometryType,
        LayerDescription description,
        string capabilities,
        IEnumerable<object>? relationships = null,
        int layerId = 0,
        int? maxRecordCount = null)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(description);
        // Empty is a service that offers nothing — see Service() for why that renders
        // rather than throwing.
        ArgumentNullException.ThrowIfNull(capabilities);

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

            // <b>What this layer looks like, which this document said nothing about until
            // 2026-08-17 (ADR-033).</b> An ArcGIS client with no `drawingInfo` invents a
            // default, so the same layer arrived grey in one client and blue in another
            // and the server had no opinion. It is generated rather than authored until
            // somebody stores a style — `drawingInfoGenerated` says which, because a
            // default presented as a decision is a decision nobody made.
            drawingInfo = DrawingInfo(layer.Name, geometryType),
            drawingInfoGenerated = true,

            maxRecordCount = AdvertisedMaxRecordCount(maxRecordCount),
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
            supportsStatistics = true,
            supportsPagination = true,
            supportsOrderBy = true,
            supportsDistinct = true,
            supportsReturningQueryExtent = true,

            // <b>The nested object is the one clients actually read.</b> The
            // flat flags above are the older shape and are kept because some
            // clients still look at them; advancedQueryCapabilities is where the
            // ArcGIS REST specification puts these, and a client that reads only
            // it would have concluded we support nothing.
            // <b>Audited against the parser on 2026-08-15, not carried
            // forward.</b> Every true below names a parameter
            // FeatureServerQueryParameters honours today, and every false names
            // one it refuses by name with a reason. The list went stale once
            // already — pagination and ordering sat false for weeks while both
            // worked — so the rule now is that changing a flag and changing the
            // parser are the same commit.
            advancedQueryCapabilities = new
            {
                supportsPagination = true,
                supportsOrderBy = true,
                supportsStatistics = true,
                supportsDistinct = true,
                supportsReturningQueryExtent = true,
                supportsQueryWithDistance = true,
                // <b>False since 2026-08-16, and it was true for the wrong
                // reason.</b> The clause was accepted and appended to the SQL
                // statement unparsed, so this flag advertised an injection as a
                // feature. It goes back to true when the clause is parsed the way
                // `where` is — D-41, Q-109 — and not before, because a client that
                // reads this flag and sends a clause deserves an answer rather
                // than a 400.
                supportsHavingClause = false,
                supportsReturningGeometryCentroid = false,

                // No arithmetic or function calls in the where grammar, which is
                // what this flag claims — see WhereClause, where the omission is
                // deliberate rather than pending.
                supportsSqlExpression = false,
                supportsCountDistinct = false,
                supportsQueryWithResultType = false,
                supportsPercentileStatistics = false,
            },

            // Said out loud because a client uses them to decide whether to
            // offer a z/m toggle at all.
            hasZ = false,
            hasM = false,
            supportsCoordinatesQuantization = false,

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
