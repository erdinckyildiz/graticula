using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Graticula.Api.ArcGis.Offline;
using Graticula.Geometries;

namespace Graticula.Offline.Sqlite;

/// <summary>
/// The XML definitions of a mobile geodatabase's items — workspace, feature class, table, domain, relationship class,
/// sync replica and sync dataset — ADR-288, research note §3.
/// </summary>
/// <remarks>
/// The element families are the ones Esri publishes for XML workspace documents (<c>DEFeatureClassInfo</c>,
/// <c>GPFieldInfoEx</c>, …); the order and the values are as the server's files hold them.
/// </remarks>
internal static class ItemXml
{
    /// <summary>The namespaces every definition declares.</summary>
    public const string Namespaces =
        "xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xmlns:xs='http://www.w3.org/2001/XMLSchema' xmlns:typens='http://www.esri.com/schemas/ArcGIS/10.8'";

    /// <summary>The class id of a feature class.</summary>
    public const string FeatureClassClsid = "{52353152-891A-11D0-BEC6-00805F7C4268}";

    /// <summary>The class id of a table.</summary>
    public const string TableClsid = "{7A566981-C114-11D2-8A28-006097AFF44E}";

    /// <summary>The WKT of Web Mercator, for a query extent the layers do not describe.</summary>
    public const string WebMercatorWkt =
        "PROJCS[\"WGS_1984_Web_Mercator_Auxiliary_Sphere\",GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\",SPHEROID[\"WGS_1984\",6378137.0,298.257223563]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]],PROJECTION[\"Mercator_Auxiliary_Sphere\"],PARAMETER[\"False_Easting\",0.0],PARAMETER[\"False_Northing\",0.0],PARAMETER[\"Central_Meridian\",0.0],PARAMETER[\"Standard_Parallel_1\",0.0],PARAMETER[\"Auxiliary_Sphere_Type\",0.0],UNIT[\"Meter\",1.0],AUTHORITY[\"EPSG\",3857]]";

    /// <summary>The WKT of WGS 84, for a query extent the layers do not describe.</summary>
    public const string Wgs84Wkt =
        "GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\",SPHEROID[\"WGS_1984\",6378137.0,298.257223563]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433],AUTHORITY[\"EPSG\",4326]]";

    /// <summary>The workspace item's definition.</summary>
    /// <param name="user">The replica's owner, or empty for a snapshot.</param>
    public static string Workspace(string user) =>
        $"<DEWorkspace xsi:type='typens:DEWorkspace' {Namespaces}><CatalogPath>\\</CatalogPath><Name></Name><ChildrenExpanded>false</ChildrenExpanded><WorkspaceType>esriRemoteDatabaseWorkspace</WorkspaceType><WorkspaceFactoryProgID></WorkspaceFactoryProgID><ConnectionString></ConnectionString><ConnectionInfo xsi:nil='true'/><Domains xsi:type='typens:ArrayOfDomain'></Domains><MajorVersion>3</MajorVersion><MinorVersion>0</MinorVersion><BugfixVersion>0</BugfixVersion><Realm></Realm><User>{FileValues.Xml(user)}</User><MaxAttributeRuleID>0</MaxAttributeRuleID></DEWorkspace>";

    /// <summary>The <c>DEFAULT</c> historical marker's definition.</summary>
    public static string HistoricalMarker(DateTimeOffset now) =>
        $"<GPHistoricalMarker xsi:type='typens:GPHistoricalMarker' {Namespaces}><Name>DEFAULT</Name><TimeStamp xsi:type='xs:dateTime'>{Stamp(now)}</TimeStamp></GPHistoricalMarker>";

    /// <summary>A moment as the definitions write one: UTC to the second, no zone.</summary>
    public static string Stamp(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>A spatial reference, with the grid the layer's geometries are stored on.</summary>
    public static string SpatialReference(int wkid, bool geographic, string wkt, CoordinateGrid grid, string tag = "SpatialReference")
    {
        string kind = geographic ? "GeographicCoordinateSystem" : "ProjectedCoordinateSystem";
        int written = wkid is 3857 or 102100 ? 102100 : wkid;
        int latest = wkid is 3857 or 102100 ? 3857 : wkid;
        string tolerance = geographic ? "8.983152841195215e-09" : "0.001";
        return $"<{tag} xsi:type='typens:{kind}'><WKT>{FileValues.Xml(wkt)}</WKT><XOrigin>{FileValues.Number(grid.XOrigin)}</XOrigin><YOrigin>{FileValues.Number(grid.YOrigin)}</YOrigin><XYScale>{FileValues.Number(grid.XYScale)}</XYScale>"
            + $"<ZOrigin>{FileValues.Number(grid.ZOrigin)}</ZOrigin><ZScale>{FileValues.Number(grid.ZScale)}</ZScale><MOrigin>{FileValues.Number(grid.MOrigin)}</MOrigin><MScale>{FileValues.Number(grid.MScale)}</MScale>"
            + $"<XYTolerance>{tolerance}</XYTolerance><ZTolerance>0.001</ZTolerance><MTolerance>0.001</MTolerance><HighPrecision>true</HighPrecision>"
            + $"<WKID>{written}</WKID><LatestWKID>{latest}</LatestWKID></{tag}>";
    }

    /// <summary>One field's definition.</summary>
    /// <param name="field">The field.</param>
    /// <param name="domain">The name of its domain in the file, or null.</param>
    /// <param name="alias">Whether to write its alias.</param>
    public static string Field(OfflineField field, string? domain, bool alias = true)
    {
        var x = new StringBuilder("<GPFieldInfoEx xsi:type='typens:GPFieldInfoEx'>");
        x.Append("<Name>").Append(FileValues.Xml(field.Name)).Append("</Name>");
        if (alias && !string.IsNullOrEmpty(field.Alias) && field.EsriType != "esriFieldTypeGeometry")
        {
            x.Append("<AliasName>").Append(FileValues.Xml(field.Alias)).Append("</AliasName>");
        }

        if (domain is not null)
        {
            x.Append("<DomainName>").Append(FileValues.Xml(domain)).Append("</DomainName>");
        }

        bool key = field.EsriType is "esriFieldTypeOID" or "esriFieldTypeGlobalID";
        x.Append("<FieldType>").Append(field.EsriType).Append("</FieldType>");
        x.Append("<IsNullable>").Append(field.Nullable && !key ? "true" : "false").Append("</IsNullable>");
        if (key)
        {
            x.Append("<Required>true</Required><Editable>false</Editable>");
        }

        if (field.EsriType == "esriFieldTypeGeometry")
        {
            x.Append("<Required>true</Required>");
        }

        return x.Append("</GPFieldInfoEx>").ToString();
    }

    private static string Tail(string clsid, string? globalId) =>
        $"<CLSID>{clsid}</CLSID><EXTCLSID></EXTCLSID><RelationshipClassNames xsi:type='typens:Names'></RelationshipClassNames><AliasName></AliasName><ModelName></ModelName>"
        + $"<HasGlobalID>{(globalId is null ? "false" : "true")}</HasGlobalID><GlobalIDFieldName>{FileValues.Xml(globalId)}</GlobalIDFieldName><RasterFieldName></RasterFieldName>"
        + "<ExtensionProperties xsi:type='typens:PropertySet'><PropertyArray xsi:type='typens:ArrayOfPropertySetProperty'></PropertyArray></ExtensionProperties>"
        + "<ControllerMemberships xsi:type='typens:ArrayOfControllerMembership'></ControllerMemberships><EditorTrackingEnabled>false</EditorTrackingEnabled>"
        + "<CreatorFieldName></CreatorFieldName><CreatedAtFieldName></CreatedAtFieldName><EditorFieldName></EditorFieldName><EditedAtFieldName></EditedAtFieldName><IsTimeInUTC>true</IsTimeInUTC>";

    private static string Head(string kind, string datasetType, string name, int dsid, string clientVersion, bool canVersion, string objectId) =>
        $"<{kind} xsi:type='typens:{kind}' {Namespaces}><CatalogPath>\\{FileValues.Xml(name)}</CatalogPath><Name>{FileValues.Xml(name)}</Name><ChildrenExpanded>false</ChildrenExpanded><DatasetType>{datasetType}</DatasetType><DSID>{dsid}</DSID><Versioned>false</Versioned><CanVersion>{(canVersion ? "true" : "false")}</CanVersion><ConfigurationKeyword></ConfigurationKeyword><RequiredGeodatabaseClientVersion>{clientVersion}</RequiredGeodatabaseClientVersion><HasOID>{(objectId.Length > 0 ? "true" : "false")}</HasOID><OIDFieldName>{FileValues.Xml(objectId)}</OIDFieldName>";

    private static string ChangeTracked(bool tracked) =>
        $"<ChangeTracked>{(tracked ? "true" : "false")}</ChangeTracked><FieldFilteringEnabled>false</FieldFilteringEnabled><FilteredFieldNames xsi:type='typens:Names'></FilteredFieldNames><ReplicaTracked>false</ReplicaTracked>";

    /// <summary>A table's definition.</summary>
    public static string Table(string name, int dsid, string objectId, string fields, string? globalId, bool changeTracked, string clientVersion) =>
        Head("DETableInfo", "esriDTTable", name, dsid, clientVersion, false, objectId)
        + $"<GPFieldInfoExs xsi:type='typens:ArrayOfGPFieldInfoEx'>{fields}</GPFieldInfoExs>"
        + Tail(TableClsid, globalId)
        + ChangeTracked(changeTracked)
        + "</DETableInfo>";

    /// <summary>A feature class's definition.</summary>
    public static string FeatureClass(
        string name, int dsid, string objectId, string fields, string? globalId, bool changeTracked, string clientVersion,
        string geometryType, string shape, bool hasZ, bool hasM, string extent, string spatialReference) =>
        Head("DEFeatureClassInfo", "esriDTFeatureClass", name, dsid, clientVersion, false, objectId)
        + $"<GPFieldInfoExs xsi:type='typens:ArrayOfGPFieldInfoEx'>{fields}</GPFieldInfoExs>"
        + Tail(FeatureClassClsid, globalId)
        + $"<FeatureType>esriFTSimple</FeatureType><ShapeType>{geometryType}</ShapeType><ShapeFieldName>{shape}</ShapeFieldName><HasM>{(hasM ? "true" : "false")}</HasM><HasZ>{(hasZ ? "true" : "false")}</HasZ><HasSpatialIndex>false</HasSpatialIndex><AreaFieldName></AreaFieldName><LengthFieldName></LengthFieldName>"
        + extent + spatialReference
        + ChangeTracked(changeTracked)
        + "</DEFeatureClassInfo>";

    /// <summary>A layer extent, with its Z range when it has one.</summary>
    public static string Extent(double xmin, double ymin, double xmax, double ymax, double? zmin, double? zmax, string spatialReference) =>
        $"<Extent xsi:type='typens:EnvelopeN'><XMin>{FileValues.Number(xmin)}</XMin><YMin>{FileValues.Number(ymin)}</YMin><XMax>{FileValues.Number(xmax)}</XMax><YMax>{FileValues.Number(ymax)}</YMax>"
        + (zmin is double lo && zmax is double hi ? $"<ZMin>{FileValues.Number(lo)}</ZMin><ZMax>{FileValues.Number(hi)}</ZMax>" : string.Empty)
        + spatialReference + "</Extent>";

    /// <summary>A coded value domain's definition.</summary>
    public static string CodedDomain(OfflineCodedDomain domain, string fieldType)
    {
        string xs = fieldType switch
        {
            "esriFieldTypeSmallInteger" => "xs:short",
            "esriFieldTypeInteger" => "xs:int",
            "esriFieldTypeString" => "xs:string",
            "esriFieldTypeDouble" or "esriFieldTypeSingle" => "xs:double",
            _ => throw new NotSupportedException($"A coded value domain on a {fieldType} field cannot be written to an offline copy."),
        };
        string codes = string.Concat(domain.Codes.Select(c =>
            $"<CodedValue xsi:type='typens:CodedValue'><Name>{FileValues.Xml(c.Value)}</Name><Code xsi:type='{xs}'>{FileValues.Xml(Code(c.Key))}</Code></CodedValue>"));
        return $"<GPCodedValueDomain2 xsi:type='typens:GPCodedValueDomain2' {Namespaces}><DomainName>{FileValues.Xml(domain.Name)}</DomainName><FieldType>{fieldType}</FieldType><MergePolicy>esriMPTDefaultValue</MergePolicy><SplitPolicy>esriSPTDefaultValue</SplitPolicy><Description></Description><Owner></Owner><CodedValues xsi:type='typens:ArrayOfCodedValue'>{codes}</CodedValues></GPCodedValueDomain2>";
    }

    private static string Code(object code) => code switch
    {
        double d => FileValues.Number(d),
        float f => FileValues.Number(f),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => code.ToString() ?? string.Empty,
    };

    /// <summary>A range domain's definition.</summary>
    public static string RangeDomain(OfflineRangeDomain domain, string fieldType) =>
        $"<GPRangeDomain2 xsi:type='typens:GPRangeDomain2' {Namespaces}><DomainName>{FileValues.Xml(domain.Name)}</DomainName><FieldType>{fieldType}</FieldType><MergePolicy>esriMPTDefaultValue</MergePolicy><SplitPolicy>esriSPTDefaultValue</SplitPolicy><Description></Description><Owner></Owner><MaxValue xsi:type='xs:double'>{FileValues.Number(domain.Maximum)}</MaxValue><MinValue xsi:type='xs:double'>{FileValues.Number(domain.Minimum)}</MinValue></GPRangeDomain2>";

    /// <summary>The definition of the relationship class that ties a layer to its attachment table.</summary>
    public static string AttachmentRelationship(string name, int dsid, string origin, string destination, string originGlobalId) =>
        Head("DERelationshipClassInfo", "esriDTRelationshipClass", name, dsid, "10.0", true, string.Empty)
        + "<GPFieldInfoExs xsi:type='typens:ArrayOfGPFieldInfoEx'></GPFieldInfoExs>"
        + Tail(string.Empty, null)
        + $"<Cardinality>esriRelCardinalityOneToMany</Cardinality><Notification>esriRelNotificationNone</Notification><IsAttributed>false</IsAttributed><IsComposite>true</IsComposite><OriginClassNames xsi:type='typens:Names'><Name>{FileValues.Xml(origin)}</Name></OriginClassNames><DestinationClassNames xsi:type='typens:Names'><Name>{FileValues.Xml(destination)}</Name></DestinationClassNames><KeyType>esriRelKeyTypeSingle</KeyType><ClassKey>esriRelClassKeyUndefined</ClassKey><ForwardPathLabel>attachment</ForwardPathLabel><BackwardPathLabel>object</BackwardPathLabel><IsReflexive>false</IsReflexive>"
        + $"<OriginClassKeys xsi:type='typens:ArrayOfRelationshipClassKey'><RelationshipClassKey xsi:type='typens:RelationshipClassKey'><ObjectKeyName>{FileValues.Xml(originGlobalId)}</ObjectKeyName><ClassKeyName></ClassKeyName><KeyRole>esriRelKeyRoleOriginPrimary</KeyRole></RelationshipClassKey><RelationshipClassKey xsi:type='typens:RelationshipClassKey'><ObjectKeyName>REL_GLOBALID</ObjectKeyName><ClassKeyName></ClassKeyName><KeyRole>esriRelKeyRoleOriginForeign</KeyRole></RelationshipClassKey></OriginClassKeys><RelationshipRules xsi:type='typens:ArrayOfRelationshipRule'></RelationshipRules><IsAttachmentRelationship>true</IsAttachmentRelationship><ChangeTracked>false</ChangeTracked><ReplicaTracked>false</ReplicaTracked></DERelationshipClassInfo>";

    /// <summary>What a sync dataset says about one table of a replica.</summary>
    public sealed record SyncDataset(int Dsid, string Name, bool FeatureClass, int LayerId, string? LayerName, bool HasAttachments, bool IsAttachment, string Table);

    /// <summary>The body of a sync dataset, shared by the replica's list and the dataset's own item.</summary>
    public static string SyncDatasetBody(SyncDataset d, long serverGeneration)
    {
        ArgumentNullException.ThrowIfNull(d);
        string g = FileValues.Number(serverGeneration);
        string query = d.FeatureClass
            ? "<UseGeometry>true</UseGeometry><IncludeRelated>true</IncludeRelated><QueryOption>esriRowsTypeFilter</QueryOption>"
            : d.IsAttachment
                ? "<UseGeometry>false</UseGeometry><IncludeRelated>false</IncludeRelated><QueryOption>esriRowsTypeNone</QueryOption>"
                : "<UseGeometry>false</UseGeometry><IncludeRelated>true</IncludeRelated><QueryOption>esriRowsTypeNone</QueryOption>";
        return $"<DatasetID>{d.Dsid}</DatasetID><DatasetName>{FileValues.Xml(d.Name)}</DatasetName><DatasetType>{(d.FeatureClass ? "esriDTFeatureClass" : "esriDTTable")}</DatasetType><LayerID>{d.LayerId}</LayerID>"
            + (d.LayerName is null ? string.Empty : $"<LayerName>{FileValues.Xml(d.LayerName)}</LayerName>")
            + $"<Direction>esriSyncDirectionBidirectional</Direction><ReplicaServerGen xsi:type='xs:long'>{g}</ReplicaServerGen><ReplicaClientDownloadGen xsi:type='xs:long'>1000</ReplicaClientDownloadGen><ReplicaClientUploadGen xsi:type='xs:long'>1000</ReplicaClientUploadGen><ReplicaClientAcknowledgeUploadGen xsi:type='xs:long'>1000</ReplicaClientAcknowledgeUploadGen>"
            + query
            + (d.HasAttachments ? "<HasAttachments>true</HasAttachments>" : string.Empty)
            + (d.IsAttachment ? "<IsAttachment>true</IsAttachment>" : string.Empty);
    }

    /// <summary>A sync dataset item's definition.</summary>
    public static string SyncDatasetItem(string body) =>
        $"<GPSyncDataset xsi:type='typens:GPSyncDataset' {Namespaces}>{body}</GPSyncDataset>";

    /// <summary>The sync replica item's definition.</summary>
    public static string SyncReplica(OfflineReplica replica, IEnumerable<string> datasetBodies, string queryReference)
    {
        ArgumentNullException.ThrowIfNull(replica);
        Envelope q = replica.QueryExtent;
        string id = replica.Id.ToString("D").ToUpperInvariant();
        return $"<GPSyncReplica xsi:type='typens:GPSyncReplica' {Namespaces}><ReplicaName>{FileValues.Xml(replica.Name)}</ReplicaName><ID>1</ID><ReplicaID>{id}</ReplicaID><ServiceName>{FileValues.Xml(replica.ServiceUrl)}</ServiceName><Owner>{FileValues.Xml(replica.Owner)}</Owner><Role>esriReplicaRoleChild</Role><SyncModel>esriSyncModelPerLayer</SyncModel><Direction>esriSyncDirectionBidirectional</Direction>"
            + $"<CreationDate>{Stamp(replica.Created)}.000</CreationDate><LastSyncDate>1970-01-01T00:00:01</LastSyncDate><ReturnsAttachments>true</ReturnsAttachments><SpatialRelation>esriSpatialRelIntersects</SpatialRelation>"
            + $"<QueryGeometry xsi:type='typens:EnvelopeN'><XMin>{FileValues.Number(q.MinX)}</XMin><YMin>{FileValues.Number(q.MinY)}</YMin><XMax>{FileValues.Number(q.MaxX)}</XMax><YMax>{FileValues.Number(q.MaxY)}</YMax>{queryReference}</QueryGeometry>"
            + "<GPSyncDatasets xsi:type='typens:ArrayOfGPSyncDataset'>" + string.Concat(datasetBodies.Select(static b => $"<GPSyncDataset xsi:type='typens:GPSyncDataset'>{b}</GPSyncDataset>")) + "</GPSyncDatasets>"
            + queryReference
            + "<AttachmentsSyncDirection>esriAttachmentsSyncDirectionBidirectional</AttachmentsSyncDirection><TargetType>esriReplicaTargetTypeClient</TargetType><SyncDataOptions>0</SyncDataOptions></GPSyncReplica>";
    }
}
