// Written by experiments/mobile-geodatabase/gen_schema_constants.py. Regenerate rather than edit.
//
// FORMAT CONSTANTS, OBSERVED — ADR-288 and docs/research/mobile-geodatabase-format.md §0.
// Esri does not publish the mobile geodatabase. These statements and rows are the format's fixed part as read
// from a registered replica and a download delta ArcGIS Server 12.1 wrote for a service this project created and
// filled (owner's permission, 2026-11-10). They are schema and type catalogue, not data: no row of any service is
// here, and the captured files stay outside the repository.

namespace Graticula.Offline.Sqlite;

/// <summary>The fixed part of a mobile geodatabase: system tables, item types, system column registry.</summary>
internal static class MobileGeodatabaseSchema
{
    /// <summary>The system tables, in the order a file creates them.</summary>
    public static readonly (string Name, string Sql)[] SystemTables =
    [
        ("GDB_Version", @"CREATE TABLE GDB_Version (major           int32 primary key not null,minor           int32 not null,bugfix          int32 not null,description     text(96) not null,release         int32 not null,sdesvr_rel_low  int32 not null)"),
        ("st_aux_spatial_reference_systems", @"CREATE TABLE st_aux_spatial_reference_systems (srid           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, auth_name      text(256), auth_srid      int32, srtext         text(4096), falsex         float64 NOT NULL, falsey         float64 NOT NULL, xyunits        float64 NOT NULL, falsez         float64 DEFAULT 0.0, zunits         float64 DEFAULT 1.0, falsem         float64 DEFAULT 0.0, munits         float64 DEFAULT 1.0, xycluster_tol  float64, zcluster_tol   float64, mcluster_tol   float64, object_flags   int32 DEFAULT 1)"),
        ("st_geometry_columns", @"CREATE TABLE st_geometry_columns (gcid               INTEGER PRIMARY KEY AUTOINCREMENT, f_table_name       clob NOT NULL, f_geometry_column  clob NOT NULL, geometry_type      int32, coord_dimension    int32, srid               int32 NOT NULL, spatial_index_type clob,CONSTRAINT gc_uk UNIQUE (f_table_name,f_geometry_column))"),
        ("GDB_Layers", @"CREATE TABLE GDB_Layers (layer_id        INTEGER PRIMARY KEY AUTOINCREMENT,description     text(65),table_name      text(128) NOT NULL,spatial_column  text(128) NOT NULL,eflags          int32 NOT NULL,layer_mask      int32 NOT NULL,minx            float64 NOT NULL,miny            float64 NOT NULL,maxx            float64 NOT NULL,maxy            float64 NOT NULL,minz            float64,minm            float64,maxz            float64,maxm            float64,cdate           int64 NOT NULL,minimum_id      int64,srid            int32 NOT NULL,base_layer_id   int32 NOT NULL,CONSTRAINT layer_uk UNIQUE (table_name,spatial_column))"),
        ("GDB_Metadata", @"CREATE TABLE GDB_Metadata (record_id       INTEGER PRIMARY KEY AUTOINCREMENT,object_name     text(160) NOT NULL,object_type     int32 NOT NULL,class_name      text(32),property        text(32),prop_value      text(255),description     text(65),creation_date   realdate)"),
        ("GDB_TableRegistry", @"CREATE TABLE GDB_TableRegistry (registration_id    INTEGER PRIMARY KEY AUTOINCREMENT,table_name         text(128) NOT NULL UNIQUE,rowid_column       text(128),description        text(65),object_flags       int32 NOT NULL,object_flags2      int32,registration_date  int64 NOT NULL,minimum_id         int64,table_comment      clob)"),
        ("GDB_Locators", @"CREATE TABLE GDB_Locators (locator_id    INTEGER PRIMARY KEY AUTOINCREMENT,name          text(32) NOT NULL UNIQUE,category      text(128) NOT NULL,type          int32 NOT NULL,description   text(65))"),
        ("GDB_ColumnRegistry", @"CREATE TABLE GDB_ColumnRegistry (table_name         text(128) NOT NULL,column_name        text(128) NOT NULL,sde_type           int32 NOT NULL,column_size        int32,decimal_digits     int32,description        text(65),object_flags       int32 NOT NULL,object_flags2      int32,object_id          int32,column_comment     clob,CONSTRAINT colregistry_pk PRIMARY KEY (table_name,column_name))"),
        ("GDB_TablesModified", @"CREATE TABLE GDB_TablesModified (table_name          text(32) not null,time_last_modified  int64)"),
        ("GDB_RowidGenerators", @"CREATE TABLE GDB_RowidGenerators (registration_id   int32 not null,base_id           int64 not null,num_ids           int64 not null,last_id           int64,CONSTRAINT GDB_RowidGenerators_pk PRIMARY KEY (registration_id,base_id))"),
        ("GDB_Tables_Last_Modified", @"CREATE TABLE [GDB_Tables_Last_Modified] (table_name text(160) check(typeof(table_name) = 'text' and not length(table_name) > 160) not null unique, last_modified_count int32 check(typeof(last_modified_count) = 'integer' and last_modified_count >= -2147483648 and last_modified_count <= 2147483647) not null)"),
        ("GDB_Items", @"CREATE TABLE [GDB_Items] (ObjectID integer primary key not null, UUID uuidtext collate nocase check(typeof(UUID) = 'text' and length(UUID) = 38) not null unique, Type uuidtext collate nocase check(typeof(Type) = 'text' and length(Type) = 38) not null, Name text(257) check((typeof(Name) = 'text' or typeof(Name) = 'null') and not length(Name) > 257), PhysicalName text(257) check((typeof(PhysicalName) = 'text' or typeof(PhysicalName) = 'null') and not length(PhysicalName) > 257), Path text(770) check((typeof(Path) = 'text' or typeof(Path) = 'null') and not length(Path) > 770), Url text(255) check((typeof(Url) = 'text' or typeof(Url) = 'null') and not length(Url) > 255), Properties int32 check((typeof(Properties) = 'integer' or typeof(Properties) = 'null') and Properties >= -2147483648 and Properties <= 2147483647), Defaults blob(52428800) check((typeof(Defaults) = 'blob' or typeof(Defaults) = 'null') and not length(Defaults) > 52428800), DatasetSubtype1 int32 check((typeof(DatasetSubtype1) = 'integer' or typeof(DatasetSubtype1) = 'null') and DatasetSubtype1 >= -2147483648 and DatasetSubtype1 <= 2147483647), DatasetSubtype2 int32 check((typeof(DatasetSubtype2) = 'integer' or typeof(DatasetSubtype2) = 'null') and DatasetSubtype2 >= -2147483648 and DatasetSubtype2 <= 2147483647), DatasetInfo1 text(255) check((typeof(DatasetInfo1) = 'text' or typeof(DatasetInfo1) = 'null') and not length(DatasetInfo1) > 255), DatasetInfo2 text(255) check((typeof(DatasetInfo2) = 'text' or typeof(DatasetInfo2) = 'null') and not length(DatasetInfo2) > 255), Definition xmltext check(typeof(Definition) = 'text' or typeof(Definition) = 'null'), Documentation xmltext check(typeof(Documentation) = 'text' or typeof(Documentation) = 'null'), ItemInfo xmltext check(typeof(ItemInfo) = 'text' or typeof(ItemInfo) = 'null'), ContingentValues blob check(typeof(ContingentValues) = 'blob' or typeof(ContingentValues) = 'null'), Shape geometryblob check((typeof(Shape) = 'blob' and length(Shape) >= 18 and cast(hex(substr(Shape,1,1)) as integer) = 64) or typeof(Shape) = 'null'))"),
        ("GDB_ItemRelationships", @"CREATE TABLE [GDB_ItemRelationships] (ObjectID integer primary key not null, UUID uuidtext collate nocase check(typeof(UUID) = 'text' and length(UUID) = 38) not null unique, Type uuidtext collate nocase check(typeof(Type) = 'text' and length(Type) = 38) not null, OriginID uuidtext collate nocase check(typeof(OriginID) = 'text' and length(OriginID) = 38) not null, DestID uuidtext collate nocase check(typeof(DestID) = 'text' and length(DestID) = 38) not null, Properties int32 check((typeof(Properties) = 'integer' or typeof(Properties) = 'null') and Properties >= -2147483648 and Properties <= 2147483647), Attributes xmltext check(typeof(Attributes) = 'text' or typeof(Attributes) = 'null'))"),
        ("GDB_ItemTypes", @"CREATE TABLE [GDB_ItemTypes] (ObjectID integer primary key not null, UUID uuidtext collate nocase check(typeof(UUID) = 'text' and length(UUID) = 38) not null unique, Name text(257) check(typeof(Name) = 'text' and not length(Name) > 257) not null, ParentTypeID uuidtext collate nocase check(typeof(ParentTypeID) = 'text' and length(ParentTypeID) = 38) not null)"),
        ("GDB_ItemRelationshipTypes", @"CREATE TABLE [GDB_ItemRelationshipTypes] (ObjectID integer primary key not null, UUID uuidtext collate nocase check(typeof(UUID) = 'text' and length(UUID) = 38) not null unique, Name text(257) check(typeof(Name) = 'text' and not length(Name) > 257) not null, ForwardLabel text(257) check((typeof(ForwardLabel) = 'text' or typeof(ForwardLabel) = 'null') and not length(ForwardLabel) > 257), BackwardLabel text(257) check((typeof(BackwardLabel) = 'text' or typeof(BackwardLabel) = 'null') and not length(BackwardLabel) > 257), OrigItemTypeID uuidtext collate nocase check(typeof(OrigItemTypeID) = 'text' and length(OrigItemTypeID) = 38) not null, DestItemTypeID uuidtext collate nocase check(typeof(DestItemTypeID) = 'text' and length(DestItemTypeID) = 38) not null, IsContainment int16 check((typeof(IsContainment) = 'integer' or typeof(IsContainment) = 'null') and IsContainment >= -32768 and IsContainment <= 32767))"),
        ("GDB_ReplicaLog", @"CREATE TABLE [GDB_ReplicaLog] (ID integer primary key not null, ReplicaID int32 check(typeof(ReplicaID) = 'integer' and ReplicaID >= -2147483648 and ReplicaID <= 2147483647) not null, Event int32 check(typeof(Event) = 'integer' and Event >= -2147483648 and Event <= 2147483647) not null, ErrorCode int32 check(typeof(ErrorCode) = 'integer' and ErrorCode >= -2147483648 and ErrorCode <= 2147483647) not null, LogDate realdate check(typeof(LogDate) = 'real' and LogDate >= 0.0) not null, SourceBeginGen int32 check(typeof(SourceBeginGen) = 'integer' and SourceBeginGen >= -2147483648 and SourceBeginGen <= 2147483647) not null, SourceEndGen int32 check(typeof(SourceEndGen) = 'integer' and SourceEndGen >= -2147483648 and SourceEndGen <= 2147483647) not null, TargetGen int32 check(typeof(TargetGen) = 'integer' and TargetGen >= -2147483648 and TargetGen <= 2147483647) not null)"),
        ("GDB_EditingTemplates", @"CREATE TABLE [GDB_EditingTemplates] (ObjectID integer primary key not null, GlobalID uuidtext collate nocase check(typeof(GlobalID) = 'text' and length(GlobalID) = 38) not null unique, Type int16 check(typeof(Type) = 'integer' and Type >= -32768 and Type <= 32767) not null, Name text(255) check(typeof(Name) = 'text' and not length(Name) > 255) not null, DefaultTool uuidtext collate nocase check((typeof(DefaultTool) = 'text' or typeof(DefaultTool) = 'null') and length(DefaultTool) = 38), DatasetGUID uuidtext collate nocase check(typeof(DatasetGUID) = 'text' and length(DatasetGUID) = 38) not null, Subtype int32 check((typeof(Subtype) = 'integer' or typeof(Subtype) = 'null') and Subtype >= -2147483648 and Subtype <= 2147483647), Definition blob check(typeof(Definition) = 'blob') not null, Description text(1024) check((typeof(Description) = 'text' or typeof(Description) = 'null') and not length(Description) > 1024), Thumbnail blob check(typeof(Thumbnail) = 'blob' or typeof(Thumbnail) = 'null'), Tags text(2000) check((typeof(Tags) = 'text' or typeof(Tags) = 'null') and not length(Tags) > 2000), Visible int16 check(typeof(Visible) = 'integer' and Visible >= -32768 and Visible <= 32767) not null, Hash text(64) check(typeof(Hash) = 'text' and not length(Hash) > 64) not null, UserIdentity text(255) check((typeof(UserIdentity) = 'text' or typeof(UserIdentity) = 'null') and not length(UserIdentity) > 255), Status int32 check((typeof(Status) = 'integer' or typeof(Status) = 'null') and Status >= -2147483648 and Status <= 2147483647))"),
        ("GDB_EditingTemplateRelationships", @"CREATE TABLE [GDB_EditingTemplateRelationships] (ObjectID integer primary key not null, Type int16 check(typeof(Type) = 'integer' and Type >= -32768 and Type <= 32767) not null, OriginID uuidtext collate nocase check(typeof(OriginID) = 'text' and length(OriginID) = 38) not null, DestID uuidtext collate nocase check(typeof(DestID) = 'text' and length(DestID) = 38) not null)"),
        ("GDB_TriggerWork", @"CREATE TABLE GDB_TriggerWork (gdb_to_date realdate)"),
        ("GDB_ServiceItems", @"CREATE TABLE [GDB_ServiceItems] (ObjectID integer primary key not null, ItemType int32 check(typeof(ItemType) = 'integer' and ItemType >= -2147483648 and ItemType <= 2147483647) not null, ItemInfo text(100000000) check(typeof(ItemInfo) = 'text' and not length(ItemInfo) > 100000000) not null, ItemId int32 check(typeof(ItemId) = 'integer' and ItemId >= -2147483648 and ItemId <= 2147483647) not null, DatasetName text(1024) check((typeof(DatasetName) = 'text' or typeof(DatasetName) = 'null') and not length(DatasetName) > 1024), AdvancedDrawingInfo text(100000000) check((typeof(AdvancedDrawingInfo) = 'text' or typeof(AdvancedDrawingInfo) = 'null') and not length(AdvancedDrawingInfo) > 100000000))"),
    ];

    /// <summary>The indexes on system tables, each with the table it is on.</summary>
    public static readonly (string Table, string Sql)[] SystemIndexes =
    [
        ("GDB_Items", @"CREATE INDEX [Items_Type_idx] ON [GDB_Items] (Type ASC)"),
        ("GDB_Items", @"CREATE INDEX [Items_Name_idx] ON [GDB_Items] (Name ASC)"),
        ("GDB_Items", @"CREATE INDEX [Items_PhysicalName_idx] ON [GDB_Items] (PhysicalName ASC)"),
        ("GDB_Items", @"CREATE INDEX Items_Name_FC_idx ON GDB_Items (Name COLLATE icufoldcase)"),
        ("GDB_ItemRelationships", @"CREATE INDEX [GDB_ItemRel_Type_idx] ON [GDB_ItemRelationships] (Type ASC)"),
        ("GDB_ItemRelationships", @"CREATE INDEX [GDB_ItemRel_OriginID_idx] ON [GDB_ItemRelationships] (OriginID ASC)"),
        ("GDB_ItemRelationships", @"CREATE INDEX [GDB_ItemRel_DestID_idx] ON [GDB_ItemRelationships] (DestID ASC)"),
        ("GDB_ItemTypes", @"CREATE INDEX [ItemTypes_Name_idx] ON [GDB_ItemTypes] (Name ASC)"),
        ("GDB_ItemTypes", @"CREATE INDEX [ItemTypes_ParentTypeID_idx] ON [GDB_ItemTypes] (ParentTypeID ASC)"),
        ("GDB_ItemRelationshipTypes", @"CREATE INDEX [ItmRTypes_Name_idx] ON [GDB_ItemRelationshipTypes] (Name ASC)"),
        ("GDB_ItemRelationshipTypes", @"CREATE INDEX [ItmRTypes_ForwardLabel_idx] ON [GDB_ItemRelationshipTypes] (ForwardLabel ASC)"),
        ("GDB_ItemRelationshipTypes", @"CREATE INDEX [ItmRTypes_BackwardLabel_idx] ON [GDB_ItemRelationshipTypes] (BackwardLabel ASC)"),
        ("GDB_ItemRelationshipTypes", @"CREATE INDEX [ItmRTypes_OrigItemTypeID_idx] ON [GDB_ItemRelationshipTypes] (OrigItemTypeID ASC)"),
        ("GDB_ItemRelationshipTypes", @"CREATE INDEX [ItmRTypes_DestItemTypeID_idx] ON [GDB_ItemRelationshipTypes] (DestItemTypeID ASC)"),
        ("GDB_EditingTemplates", @"CREATE INDEX [EdTemplates_Type_idx] ON [GDB_EditingTemplates] (Type ASC)"),
        ("GDB_EditingTemplates", @"CREATE INDEX [EdTemplates_Name_idx] ON [GDB_EditingTemplates] (Name ASC)"),
        ("GDB_EditingTemplates", @"CREATE INDEX [EdTemplates_DTGuid_idx] ON [GDB_EditingTemplates] (DatasetGUID ASC)"),
        ("GDB_EditingTemplates", @"CREATE UNIQUE INDEX EdTemplates_Name_DTGuid_uk ON GDB_EditingTemplates (Name COLLATE icufoldcase,DatasetGUID)"),
        ("GDB_EditingTemplateRelationships", @"CREATE INDEX [EdTemplateRlships_Type_idx] ON [GDB_EditingTemplateRelationships] (Type ASC)"),
        ("GDB_EditingTemplateRelationships", @"CREATE INDEX [EdTemplateRlships_OriginID_idx] ON [GDB_EditingTemplateRelationships] (OriginID ASC)"),
        ("GDB_EditingTemplateRelationships", @"CREATE INDEX [EdTemplateRlships_DestID_idx] ON [GDB_EditingTemplateRelationships] (DestID ASC)"),
        ("GDB_Items", @"CREATE INDEX Items_Name_icufoldcase ON GDB_Items (Name COLLATE ICUFOLDCASE ASC)"),
    ];

    /// <summary>The R*Tree index of <c>GDB_Items.Shape</c>.</summary>
    public const string ItemsSpatialIndex = @"CREATE VIRTUAL TABLE [st_spindex__GDB_Items_Shape] USING RTREE (pkid,minx,maxx,miny,maxy)";

    /// <summary>The triggers that keep <c>GDB_Items</c>' spatial index; created last, as they call Esri functions.</summary>
    public static readonly string[] ItemsSpatialIndexTriggers =
    [
        @"CREATE TRIGGER [st_insert_trigger_GDB_Items_Shape] AFTER INSERT ON [GDB_Items] FOR EACH ROW BEGIN SELECT InsertIndexEntry ('st_spindex__GDB_Items_Shape',NEW.Shape,NEW._ROWID_,2); END",
        @"CREATE TRIGGER [st_delete_trigger_GDB_Items_Shape] AFTER DELETE ON [GDB_Items] FOR EACH ROW BEGIN DELETE FROM st_spindex__GDB_Items_Shape WHERE pkid = OLD._ROWID_; END",
        @"CREATE TRIGGER [st_update_trigger_GDB_Items_Shape] AFTER UPDATE OF Shape ON [GDB_Items] WHEN OLD._ROWID_ = NEW._ROWID_ BEGIN SELECT UpdateIndexEntry ('st_spindex__GDB_Items_Shape',NEW.Shape,NEW._ROWID_,2); END",
        @"CREATE TRIGGER [st_update1_trigger_GDB_Items_Shape] AFTER UPDATE ON [GDB_Items] WHEN OLD._ROWID_ != NEW._ROWID_ BEGIN DELETE FROM [st_spindex__GDB_Items_Shape] WHERE pkid = OLD._ROWID_; SELECT UpdateIndexEntry ('st_spindex__GDB_Items_Shape',NEW.Shape,NEW._ROWID_,2); END",
    ];

    /// <summary>
    /// The two virtual tables whose modules only Esri's library has. Plain SQLite cannot execute these; the writer
    /// inserts them as rows of <c>sqlite_master</c>, as research note §1 describes.
    /// </summary>
    public static readonly (string Name, string Sql)[] EsriModuleTables =
    [
        ("st_spatial_reference_systems", @"CREATE VIRTUAL TABLE st_spatial_reference_systems USING VSRS()"),
        ("st_vtspindex_interface", @"CREATE VIRTUAL TABLE st_vtspindex_interface USING VTSpindex()"),
    ];

    /// <summary>The tables a delta file adds to the system set (research note §5).</summary>
    public static readonly (string Name, string Sql)[] DeltaTables =
    [
        ("GDB_DataChanges", @"CREATE TABLE [GDB_DataChanges] (ID integer primary key not null, ParentConnectionInfo text(255) check(typeof(ParentConnectionInfo) = 'text' and not length(ParentConnectionInfo) > 255) not null, ParentID int32 check(typeof(ParentID) = 'integer' and ParentID >= -2147483648 and ParentID <= 2147483647) not null, ReplicaGUID text(38) check(typeof(ReplicaGUID) = 'text' and not length(ReplicaGUID) > 38) not null, ReplicaModelType int16 check(typeof(ReplicaModelType) = 'integer' and ReplicaModelType >= -32768 and ReplicaModelType <= 32767) not null, Major int32 check(typeof(Major) = 'integer' and Major >= -2147483648 and Major <= 2147483647) not null, Minor int32 check(typeof(Minor) = 'integer' and Minor >= -2147483648 and Minor <= 2147483647) not null, Bugfix int32 check(typeof(Bugfix) = 'integer' and Bugfix >= -2147483648 and Bugfix <= 2147483647) not null, SourceGenStart int32 check((typeof(SourceGenStart) = 'integer' or typeof(SourceGenStart) = 'null') and SourceGenStart >= -2147483648 and SourceGenStart <= 2147483647), SourceGenEnd int32 check((typeof(SourceGenEnd) = 'integer' or typeof(SourceGenEnd) = 'null') and SourceGenEnd >= -2147483648 and SourceGenEnd <= 2147483647), TargetGen int32 check((typeof(TargetGen) = 'integer' or typeof(TargetGen) = 'null') and TargetGen >= -2147483648 and TargetGen <= 2147483647), ReplicaState int16 check((typeof(ReplicaState) = 'integer' or typeof(ReplicaState) = 'null') and ReplicaState >= -32768 and ReplicaState <= 32767), MessageType int16 check((typeof(MessageType) = 'integer' or typeof(MessageType) = 'null') and MessageType >= -32768 and MessageType <= 32767), SourceMyTime realdate check((typeof(SourceMyTime) = 'real' or typeof(SourceMyTime) = 'null') and SourceMyTime >= 0.0), ServerGen text(38) check((typeof(ServerGen) = 'text' or typeof(ServerGen) = 'null') and not length(ServerGen) > 38), ServerSibGen text(38) check((typeof(ServerSibGen) = 'text' or typeof(ServerSibGen) = 'null') and not length(ServerSibGen) > 38))"),
        ("GDB_DataChangesDatasets", @"CREATE TABLE [GDB_DataChangesDatasets] (ID integer primary key not null, ParentDB text(255) check((typeof(ParentDB) = 'text' or typeof(ParentDB) = 'null') and not length(ParentDB) > 255), ParentOwner text(255) check((typeof(ParentOwner) = 'text' or typeof(ParentOwner) = 'null') and not length(ParentOwner) > 255), Name text(255) check(typeof(Name) = 'text' and not length(Name) > 255) not null, Type int16 check(typeof(Type) = 'integer' and Type >= -32768 and Type <= 32767) not null, TopoID int32 check((typeof(TopoID) = 'integer' or typeof(TopoID) = 'null') and TopoID >= -2147483648 and TopoID <= 2147483647), LayerId int32 check((typeof(LayerId) = 'integer' or typeof(LayerId) = 'null') and LayerId >= -2147483648 and LayerId <= 2147483647), ServerGen text(38) check((typeof(ServerGen) = 'text' or typeof(ServerGen) = 'null') and not length(ServerGen) > 38), ServerSibGen text(38) check((typeof(ServerSibGen) = 'text' or typeof(ServerSibGen) = 'null') and not length(ServerSibGen) > 38))"),
        ("GDB_DataChangesDeltas", @"CREATE TABLE [GDB_DataChangesDeltas] (ID integer primary key not null, RecordsetClassID int32 check(typeof(RecordsetClassID) = 'integer' and RecordsetClassID >= -2147483648 and RecordsetClassID <= 2147483647) not null, ChangeType int16 check(typeof(ChangeType) = 'integer' and ChangeType >= -32768 and ChangeType <= 32767) not null, ChangedDatasetID int32 check(typeof(ChangedDatasetID) = 'integer' and ChangedDatasetID >= -2147483648 and ChangedDatasetID <= 2147483647) not null)"),
        ("GDB_DataChangesTopologies", @"CREATE TABLE [GDB_DataChangesTopologies] (TopoID int32 check(typeof(TopoID) = 'integer' and TopoID >= -2147483648 and TopoID <= 2147483647) not null, DatabaseName text(255) check((typeof(DatabaseName) = 'text' or typeof(DatabaseName) = 'null') and not length(DatabaseName) > 255), Owner text(255) check((typeof(Owner) = 'text' or typeof(Owner) = 'null') and not length(Owner) > 255), Name text(255) check(typeof(Name) = 'text' and not length(Name) > 255) not null)"),
        ("GDB_RelatedDatasets", @"CREATE TABLE [GDB_RelatedDatasets] (ID integer primary key not null, ParentDB text(255) check((typeof(ParentDB) = 'text' or typeof(ParentDB) = 'null') and not length(ParentDB) > 255), ParentOwner text(255) check((typeof(ParentOwner) = 'text' or typeof(ParentOwner) = 'null') and not length(ParentOwner) > 255), Name text(255) check(typeof(Name) = 'text' and not length(Name) > 255) not null, Type int16 check(typeof(Type) = 'integer' and Type >= -32768 and Type <= 32767) not null)"),
    ];

    /// <summary>The rows of <c>GDB_ItemTypes</c>: object id, UUID, name, parent type UUID.</summary>
    public static readonly (int ObjectId, string Uuid, string Name, string ParentTypeId)[] ItemTypes =
    [
        (1, "{8405ADD5-8DF8-4227-8FAC-3FCADE073386}", "Item", "{00000000-0000-0000-0000-000000000000}"),
        (2, "{F3783E6F-65CA-4514-8315-CE3985DAD3B1}", "Folder", "{8405ADD5-8DF8-4227-8FAC-3FCADE073386}"),
        (3, "{FFD09C28-FE70-4E25-907C-AF8E8A5EC5F3}", "Resource", "{8405ADD5-8DF8-4227-8FAC-3FCADE073386}"),
        (4, "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}", "Dataset", "{FFD09C28-FE70-4E25-907C-AF8E8A5EC5F3}"),
        (5, "{FBDD7DD6-4A25-40B7-9A1A-ECC3D1172447}", "Tin", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}"),
        (6, "{D4912162-3413-476E-9DA4-2AEFBBC16939}", "AbstractTable", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}"),
        (7, "{B606A7E1-FA5B-439C-849C-6E9C2481537B}", "Relationship Class", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}"),
        (8, "{74737149-DCB5-4257-8904-B9724E32A530}", "Feature Dataset", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}"),
        (9, "{73718A66-AFB9-4B88-A551-CFFA0AE12620}", "Geometric Network", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}"),
        (10, "{767152D3-ED66-4325-8774-420D46674E07}", "Topology", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}"),
        (11, "{E6302665-416B-44FA-BE33-4E15916BA101}", "Survey Dataset", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}"),
        (12, "{D5A40288-029E-4766-8C81-DE3F61129371}", "Schematic Dataset", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}"),
        (13, "{DB1B697A-3BB6-426A-98A2-6EE7A4C6AED3}", "Toolbox", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}"),
        (14, "{C673FE0F-7280-404F-8532-20755DD8FC06}", "Workspace", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}"),
        (15, "{DC9EF677-1AA3-45A7-8ACD-303A5202D0DC}", "Workspace Extension", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}"),
        (16, "{77292603-930F-475D-AE4F-B8970F42F394}", "Extension Dataset", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}"),
        (17, "{8637F1ED-8C04-4866-A44A-1CB8288B3C63}", "Domain", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}"),
        (18, "{4ED4A58E-621F-4043-95ED-850FBA45FCBC}", "Replica", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}"),
        (19, "{D98421EB-D582-4713-9484-43304D0810F6}", "Replica Dataset", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}"),
        (20, "{DC64B6E4-DC0F-43BD-B4F5-F22385DCF055}", "Historical Marker", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}"),
        (21, "{CD06BC3B-789D-4C51-AAFA-A467912B8965}", "Table", "{D4912162-3413-476E-9DA4-2AEFBBC16939}"),
        (22, "{70737809-852C-4A03-9E22-2CECEA5B9BFA}", "Feature Class", "{D4912162-3413-476E-9DA4-2AEFBBC16939}"),
        (23, "{5ED667A3-9CA9-44A2-8029-D95BF23704B9}", "Raster Dataset", "{D4912162-3413-476E-9DA4-2AEFBBC16939}"),
        (24, "{35B601F7-45CE-4AFF-ADB7-7702D3839B12}", "Raster Catalog", "{D4912162-3413-476E-9DA4-2AEFBBC16939}"),
        (25, "{7771FC7D-A38B-4FD3-8225-639D17E9A131}", "Network Dataset", "{77292603-930F-475D-AE4F-B8970F42F394}"),
        (26, "{76357537-3364-48AF-A4BE-783C7C28B5CB}", "Terrain", "{77292603-930F-475D-AE4F-B8970F42F394}"),
        (27, "{A3803369-5FC2-4963-BAE0-13EFFC09DD73}", "Parcel Fabric", "{77292603-930F-475D-AE4F-B8970F42F394}"),
        (28, "{A300008D-0CEA-4F6A-9DFA-46AF829A3DF2}", "Representation Class", "{77292603-930F-475D-AE4F-B8970F42F394}"),
        (29, "{787BEA35-4A86-494F-BB48-500B96145B58}", "Catalog Dataset", "{77292603-930F-475D-AE4F-B8970F42F394}"),
        (30, "{F8413DCB-2248-4935-BFE9-315F397E5110}", "Mosaic Dataset", "{77292603-930F-475D-AE4F-B8970F42F394}"),
        (31, "{8C368B12-A12E-4C7E-9638-C9C64E69E98F}", "Coded Value Domain", "{8637F1ED-8C04-4866-A44A-1CB8288B3C63}"),
        (32, "{C29DA988-8C3E-45F7-8B5C-18E51EE7BEB4}", "Range Domain", "{8637F1ED-8C04-4866-A44A-1CB8288B3C63}"),
        (33, "{5B966567-FB87-4DDE-938B-B4B37423539D}", "Sync Replica", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}"),
        (34, "{D86502F9-9758-45C6-9D23-6DD1A0107B47}", "Sync Dataset", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}"),
        (35, "{37672BD2-B9F3-48C1-89B5-8C43BBBB6D57}", "Utility Network", "{77292603-930F-475D-AE4F-B8970F42F394}"),
        (36, "{60EA40CF-2667-45E2-BDFF-7F6892538FE8}", "Trace Network", "{77292603-930F-475D-AE4F-B8970F42F394}"),
        (37, "{EBEEE2C9-FA73-4BED-AC7D-AEE7D68AFC80}", "Parcel Dataset", "{77292603-930F-475D-AE4F-B8970F42F394}"),
        (38, "{CC53CC54-4CCA-43B7-9A9B-64DC59C999BD}", "Location Referencing Dataset", "{77292603-930F-475D-AE4F-B8970F42F394}"),
    ];

    /// <summary>The rows of <c>GDB_ItemRelationshipTypes</c>, every column in table order.</summary>
    public static readonly (int ObjectId, string Uuid, string Name, string? ForwardLabel, string? BackwardLabel, string OrigItemTypeId, string DestItemTypeId, int? IsContainment)[] ItemRelationshipTypes =
    [
        (1, "{0D10B3A7-2F64-45E6-B7AC-2FC27BF2133C}", "FolderInFolder", "Parent Folder Of", "Child Folder Of", "{F3783E6F-65CA-4514-8315-CE3985DAD3B1}", "{F3783E6F-65CA-4514-8315-CE3985DAD3B1}", 1),
        (2, "{5DD0C1AF-CB3D-4FEA-8C51-CB3BA8D77CDB}", "ItemInFolder", "Contains Item", "Contained In Folder", "{F3783E6F-65CA-4514-8315-CE3985DAD3B1}", "{8405ADD5-8DF8-4227-8FAC-3FCADE073386}", 1),
        (3, "{A1633A59-46BA-4448-8706-D8ABE2B2B02E}", "DatasetInFeatureDataset", "Contains Dataset", "Contained In FeatureDataset", "{74737149-DCB5-4257-8904-B9724E32A530}", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}", 1),
        (4, "{DC78F1AB-34E4-43AC-BA47-1C4EABD0E7C7}", "DatasetInFolder", "Contains Dataset", "Contained in Dataset", "{F3783E6F-65CA-4514-8315-CE3985DAD3B1}", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}", 1),
        (5, "{17E08ADB-2B31-4DCD-8FDD-DF529E88F843}", "DomainInDataset", "Contains Domain", "Contained in Dataset", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}", "{8637F1ED-8C04-4866-A44A-1CB8288B3C63}", 0),
        (6, "{725BADAB-3452-491B-A795-55F32D67229C}", "DatasetsRelatedThrough", "Origin Of", "Destination Of", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}", 0),
        (7, "{D088B110-190B-4229-BDF7-89FDDD14D1EA}", "FeatureClassInTopology", "Spatially Manages Feature Class", "Participates In Topology", "{767152D3-ED66-4325-8774-420D46674E07}", "{70737809-852C-4A03-9E22-2CECEA5B9BFA}", 0),
        (8, "{DC739A70-9B71-41E8-868C-008CF46F16D7}", "FeatureClassInGeometricNetwork", "Spatially Manages Feature Class", "Participates In Geometric Network", "{73718A66-AFB9-4B88-A551-CFFA0AE12620}", "{70737809-852C-4A03-9E22-2CECEA5B9BFA}", 0),
        (9, "{B32B8563-0B96-4D32-92C4-086423AE9962}", "FeatureClassInNetworkDataset", "Spatially Manages Feature Class", "Participates In Network Dataset", "{7771FC7D-A38B-4FD3-8225-639D17E9A131}", "{70737809-852C-4A03-9E22-2CECEA5B9BFA}", 0),
        (10, "{55D2F4DC-CB17-4E32-A8C7-47591E8C71DE}", "FeatureClassInTerrain", "Spatially Manages Feature Class", "Participates In Terrain", "{76357537-3364-48AF-A4BE-783C7C28B5CB}", "{70737809-852C-4A03-9E22-2CECEA5B9BFA}", 0),
        (11, "{583A5BAA-3551-41AE-8AA8-1185719F3889}", "FeatureClassInParcelFabric", "Spatially Manages Feature Class", "Participates In Parcel Fabric", "{A3803369-5FC2-4963-BAE0-13EFFC09DD73}", "{70737809-852C-4A03-9E22-2CECEA5B9BFA}", 0),
        (12, "{5F9085E0-788F-4354-AE3C-34C83A7EA784}", "TableInParcelFabric", "Manages Table", "Participates In Parcel Fabric", "{A3803369-5FC2-4963-BAE0-13EFFC09DD73}", "{CD06BC3B-789D-4C51-AAFA-A467912B8965}", 0),
        (13, "{E79B44E3-F833-4B12-90A1-364EC4DDC43E}", "RepresentationOfFeatureClass", "Feature Class Representation", "Represented Feature Class", "{70737809-852C-4A03-9E22-2CECEA5B9BFA}", "{A300008D-0CEA-4F6A-9DFA-46AF829A3DF2}", 0),
        (14, "{8DB31AF1-DF7C-4632-AA10-3CC44B0C6914}", "ReplicaDatasetInReplica", "Replicated Dataset", "Participates In Replica", "{4ED4A58E-621F-4043-95ED-850FBA45FCBC}", "{D98421EB-D582-4713-9484-43304D0810F6}", 1),
        (15, "{D022DE33-45BD-424C-88BF-5B1B6B957BD3}", "DatasetOfReplicaDataset", "Replicated Dataset", "Dataset of Replicated Dataset", "{D98421EB-D582-4713-9484-43304D0810F6}", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}", 0),
        (16, "{CC28387C-441F-4D7C-A802-41A160317FE0}", "SyncDatasetInSyncReplica", "Sync Dataset", "Participates In Sync Replica", "{5B966567-FB87-4DDE-938B-B4B37423539D}", "{D86502F9-9758-45C6-9D23-6DD1A0107B47}", 1),
        (17, "{79CC71C8-B7D9-4141-9014-B6373E236ABB}", "DatasetOfSyncDataset", "Replicated Dataset", "Dataset Of Sync Dataset", "{D86502F9-9758-45C6-9D23-6DD1A0107B47}", "{28DA9E89-FF80-4D6D-8926-4EE2B161677D}", 0),
        (18, "{95C22AFD-AA36-431D-B195-1779A256C6B2}", "FeatureClassInUtilityNetwork", "Spatially Manages Feature Class", "Participates In Utility Network", "{37672BD2-B9F3-48C1-89B5-8C43BBBB6D57}", "{70737809-852C-4A03-9E22-2CECEA5B9BFA}", 0),
        (19, "{CCD6E1C9-9238-40D4-843F-C5DAFD3D2BDE}", "TableInUtilityNetwork", "Manages Table", "Participates In Utility Network", "{37672BD2-B9F3-48C1-89B5-8C43BBBB6D57}", "{CD06BC3B-789D-4C51-AAFA-A467912B8965}", 0),
        (20, "{94CD4FC6-307E-430B-A883-5FE68CF19AE8}", "FeatureClassInTraceNetwork", "Spatially Manages Feature Class", "Participates In Trace Network", "{60EA40CF-2667-45E2-BDFF-7F6892538FE8}", "{70737809-852C-4A03-9E22-2CECEA5B9BFA}", 0),
        (21, "{FB166A8C-C09C-4121-9FA7-CB73406E2944}", "TableInTraceNetwork", "Manages Table", "Participates In Trace Network", "{60EA40CF-2667-45E2-BDFF-7F6892538FE8}", "{CD06BC3B-789D-4C51-AAFA-A467912B8965}", 0),
        (22, "{74DAC6C1-A6F2-4603-88A8-D09BCCFDCF21}", "FeatureClassInParcelDataset", "Spatially Manages Feature Class", "Participates In Parcel Dataset", "{EBEEE2C9-FA73-4BED-AC7D-AEE7D68AFC80}", "{70737809-852C-4A03-9E22-2CECEA5B9BFA}", 0),
        (23, "{7D2A7A69-ABD8-4AA7-AC08-9E4DDB86289E}", "TableInParcelDataset", "Manages Table", "Participates In Parcel Dataset", "{EBEEE2C9-FA73-4BED-AC7D-AEE7D68AFC80}", "{CD06BC3B-789D-4C51-AAFA-A467912B8965}", 0),
        (24, "{20D1F656-14BA-44CB-9A60-58D2D44F7A83}", "FeatureClassInLocationReferencingDataset", "Spatially Manages Feature Class", "Participates In Location Referencing Dataset", "{CC53CC54-4CCA-43B7-9A9B-64DC59C999BD}", "{70737809-852C-4A03-9E22-2CECEA5B9BFA}", 0),
        (25, "{6EEDDDCE-64F9-4549-BAAD-F05A36C205ED}", "TableInLocationReferencingDataset", "Manages Table", "Participates In Location Referencing Dataset", "{CC53CC54-4CCA-43B7-9A9B-64DC59C999BD}", "{CD06BC3B-789D-4C51-AAFA-A467912B8965}", 0),
    ];

    /// <summary>The rows of <c>GDB_ColumnRegistry</c> for the system tables, every column in table order.</summary>
    public static readonly (string Table, string Column, int SdeType, int? Size, int? DecimalDigits, string? Description, int Flags, int? Flags2, int? ObjectId, string? Comment)[] SystemColumnRegistry =
    [
        ("GDB_Tables_Last_Modified", "table_name", 5, 160, null, null, 0, 0, null, null),
        ("GDB_Tables_Last_Modified", "last_modified_count", 2, 0, null, null, 0, 0, null, null),
        ("GDB_Items", "ObjectID", 2, 0, null, null, 3, 0, null, null),
        ("GDB_Items", "UUID", 12, 38, null, null, 256, 0, null, null),
        ("GDB_Items", "Type", 12, 38, null, null, 0, 0, null, null),
        ("GDB_Items", "Name", 5, 257, null, null, 4, 0, null, null),
        ("GDB_Items", "PhysicalName", 5, 257, null, null, 4, 0, null, null),
        ("GDB_Items", "Path", 5, 770, null, null, 4, 0, null, null),
        ("GDB_Items", "Url", 5, 255, null, null, 4, 0, null, null),
        ("GDB_Items", "Properties", 2, 0, null, null, 4, 0, null, null),
        ("GDB_Items", "Defaults", 6, null, null, null, 4, 0, null, null),
        ("GDB_Items", "DatasetSubtype1", 2, 0, null, null, 4, 0, null, null),
        ("GDB_Items", "DatasetSubtype2", 2, 0, null, null, 4, 0, null, null),
        ("GDB_Items", "DatasetInfo1", 5, 255, null, null, 4, 0, null, null),
        ("GDB_Items", "DatasetInfo2", 5, 255, null, null, 4, 0, null, null),
        ("GDB_Items", "Definition", 10, null, null, null, 132, 0, null, null),
        ("GDB_Items", "Documentation", 10, null, null, null, 132, 0, null, null),
        ("GDB_Items", "ItemInfo", 10, null, null, null, 132, 0, null, null),
        ("GDB_Items", "ContingentValues", 6, null, null, null, 4, 0, null, null),
        ("GDB_Items", "Shape", 8, null, null, null, 32772, 0, 1, null),
        ("GDB_ItemRelationships", "ObjectID", 2, 0, null, null, 3, 0, null, null),
        ("GDB_ItemRelationships", "UUID", 12, 38, null, null, 256, 0, null, null),
        ("GDB_ItemRelationships", "Type", 12, 38, null, null, 0, 0, null, null),
        ("GDB_ItemRelationships", "OriginID", 12, 38, null, null, 0, 0, null, null),
        ("GDB_ItemRelationships", "DestID", 12, 38, null, null, 0, 0, null, null),
        ("GDB_ItemRelationships", "Properties", 2, 0, null, null, 4, 0, null, null),
        ("GDB_ItemRelationships", "Attributes", 10, null, null, null, 132, 0, null, null),
        ("GDB_ItemTypes", "ObjectID", 2, 0, null, null, 3, 0, null, null),
        ("GDB_ItemTypes", "UUID", 12, 38, null, null, 0, 0, null, null),
        ("GDB_ItemTypes", "Name", 5, 257, null, null, 0, 0, null, null),
        ("GDB_ItemTypes", "ParentTypeID", 12, 38, null, null, 0, 0, null, null),
        ("GDB_ItemRelationshipTypes", "ObjectID", 2, 0, null, null, 3, 0, null, null),
        ("GDB_ItemRelationshipTypes", "UUID", 12, 38, null, null, 0, 0, null, null),
        ("GDB_ItemRelationshipTypes", "Name", 5, 257, null, null, 0, 0, null, null),
        ("GDB_ItemRelationshipTypes", "ForwardLabel", 5, 257, null, null, 4, 0, null, null),
        ("GDB_ItemRelationshipTypes", "BackwardLabel", 5, 257, null, null, 4, 0, null, null),
        ("GDB_ItemRelationshipTypes", "OrigItemTypeID", 12, 38, null, null, 0, 0, null, null),
        ("GDB_ItemRelationshipTypes", "DestItemTypeID", 12, 38, null, null, 0, 0, null, null),
        ("GDB_ItemRelationshipTypes", "IsContainment", 1, 0, null, null, 4, 0, null, null),
        ("GDB_ReplicaLog", "ID", 2, 0, null, null, 3, 0, null, null),
        ("GDB_ReplicaLog", "ReplicaID", 2, 0, null, null, 0, 0, null, null),
        ("GDB_ReplicaLog", "Event", 2, 0, null, null, 0, 0, null, null),
        ("GDB_ReplicaLog", "ErrorCode", 2, 0, null, null, 0, 0, null, null),
        ("GDB_ReplicaLog", "LogDate", 7, 0, null, null, 0, 0, null, null),
        ("GDB_ReplicaLog", "SourceBeginGen", 2, 0, null, null, 0, 0, null, null),
        ("GDB_ReplicaLog", "SourceEndGen", 2, 0, null, null, 0, 0, null, null),
        ("GDB_ReplicaLog", "TargetGen", 2, 0, null, null, 0, 0, null, null),
        ("GDB_EditingTemplates", "ObjectID", 2, 0, null, null, 3, 0, null, null),
        ("GDB_EditingTemplates", "GlobalID", 12, 38, null, null, 256, 0, null, null),
        ("GDB_EditingTemplates", "Type", 1, 0, null, null, 0, 0, null, null),
        ("GDB_EditingTemplates", "Name", 5, 255, null, null, 0, 0, null, null),
        ("GDB_EditingTemplates", "DefaultTool", 12, 38, null, null, 4, 0, null, null),
        ("GDB_EditingTemplates", "DatasetGUID", 12, 38, null, null, 0, 0, null, null),
        ("GDB_EditingTemplates", "Subtype", 2, 0, null, null, 4, 0, null, null),
        ("GDB_EditingTemplates", "Definition", 6, null, null, null, 0, 0, null, null),
        ("GDB_EditingTemplates", "Description", 5, 1024, null, null, 4, 0, null, null),
        ("GDB_EditingTemplates", "Thumbnail", 6, null, null, null, 4, 0, null, null),
        ("GDB_EditingTemplates", "Tags", 5, 2000, null, null, 4, 0, null, null),
        ("GDB_EditingTemplates", "Visible", 1, 0, null, null, 0, 0, null, null),
        ("GDB_EditingTemplates", "Hash", 5, 64, null, null, 0, 0, null, null),
        ("GDB_EditingTemplateRelationships", "ObjectID", 2, 0, null, null, 3, 0, null, null),
        ("GDB_EditingTemplateRelationships", "Type", 1, 0, null, null, 0, 0, null, null),
        ("GDB_EditingTemplateRelationships", "OriginID", 12, 38, null, null, 0, 0, null, null),
        ("GDB_EditingTemplateRelationships", "DestID", 12, 38, null, null, 0, 0, null, null),
        ("GDB_ServiceItems", "ObjectID", 2, 0, null, null, 3, 0, null, null),
        ("GDB_ServiceItems", "ItemType", 2, 0, null, null, 0, 0, null, null),
        ("GDB_ServiceItems", "ItemInfo", 13, null, null, null, 0, 0, null, null),
        ("GDB_ServiceItems", "ItemId", 2, 0, null, null, 0, 0, null, null),
        ("GDB_ServiceItems", "DatasetName", 5, 1024, null, null, 4, 0, null, null),
        ("GDB_ServiceItems", "AdvancedDrawingInfo", 13, null, null, null, 4, 0, null, null),
    ];
}
