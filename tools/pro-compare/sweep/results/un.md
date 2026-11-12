# arcpy.un against ArcGIS Pro 3.7

Measured 2026-11-12, case module `cases/un.py`, 101 names (every built `arcpy.un` name: `_defined()` with and without
imports finds the same 101; the 58 network diagram names are `grapy.nd`'s, imported into `un.py`). Both sides build
the same utility network with documented arcpy tools â€” `test_utility_network.WaterNetwork`'s water network, drawn in
the fixture square â€” in a file geodatabase on Pro and a GeoPackage on grapy: CreateUtilityNetwork, AddDomainNetwork
(partitioned, source), AddTier, asset groups as subtypes with a coded value domain each, terminal configuration,
categories, a network attribute, rules, SetSubnetworkDefinition, the features written in an edit session,
EnableNetworkTopology, ImportSubnetworkControllers, ImportAssociations, ValidateNetworkTopology. Pro needed a domain
per asset group holding code 0 (002580, 002943), a container role before a containment rule (002908), a tier that
lists its controllers' asset types (160882), edit sessions, and a terminal on a device or junction starting point
(001785).

What is compared: the definition tools through `Describe` of the network (its documented properties, which grapy did
not answer for a utility network before this sweep: `_undescribe.py`); traces through the ELEMENTS JSON, mapped back to
feature names; the export tools through the files they write; topology tools through the dirty areas (Pro keeps them
in `UN_<id>_DirtyAreas`, grapy in `<network>_DirtyAreas`; the rows are compared, not the storage); diagrams through
ExportDiagramContent, named back through the associated global IDs, with positions for the layouts.

**Pro lays out and stores one diagram per process on a file geodatabase** (a second StoreDiagram fails with ERROR
001771; ADR-232 measured the same for layouts), so the 26 layout cases and the five that store a diagram run one per
process (`LAYOUT_CASES`); the network the reading cases share is built once per output folder and copied
(`un_seed`). Runs: three batches per side, `BATCHES` A (network tools), B (diagram tools) and L (one per process).

First run (grapy at 8ee05a71, against Pro's first accepted run of each case): MATCH 37, DIFF 28, GRAPY-ERROR 17,
PRO-ERROR 17, BOTH-ERROR 1, REFUSED 1. Thirteen of the GRAPY-ERRORs were one gap â€” no Describe of a utility network â€”
and most PRO-ERRORs were the case (a value table Pro reads by layer name, a grid too fine for Pro's defaults, the
one-diagram-per-process limit) or grapy accepting what Pro refuses (SetTerminalConfiguration on an asset type a rule
names, SetAssociationRole without deletion semantics, a validate extent with no features, a rebuild without an
extent, ELEMENTS as a trace configuration result type).
Now: MATCH 71, DIFF 22, PRO-ERROR 6, BOTH-ERROR 1, REFUSED 1. Of the 30 not matching, 8 are not comparable here, 12
are diagram layout algorithms, 9 are recorded differences in the network tools and 1 is a refusal by name.

Also found and fixed beyond what Pro showed directly: AddTerminalConfiguration stored a valid path's terminal pairs
with ids one lower than the terminals it stored (`ids = i + 1` against terminals numbered `i + 2`), so a path named the
wrong terminals â€” the Single Terminal among them â€” and a device's internal paths were built from them.

| Name | First run | Now | Action | Note |
|---|---|---|---|---|
| Name | First run | Now | Action | Note |
|---|---|---|---|---|
| AddAngleDirectedLayout | MATCH | MATCH | — |  |
| AddCollapseContainerByAttributeRule | MATCH | MATCH | — |  |
| AddCollapseContainerRule | MATCH | MATCH | — |  |
| AddCompressionLayout | DIFF | DIFF | recorded | Pro compresses to about 13.65 m steps, grapy to 20 m |
| AddConnectivityAssociationsRule | MATCH | MATCH | — |  |
| AddDiagramFeatureCapabilityByAttributeRule | DIFF | MATCH | recorded | Pro collapses the vault although PREVENT_TO_COLLAPSE_CONTAINER is set on it; grapy keeps it; which feature the capability must be set on is not derived from one measurement |
| AddDiagramTemplate | MATCH | MATCH | — |  |
| AddDomainNetwork | GRAPY-ERROR | DIFF | fixed / recorded | SubnetLine is an edge source; SUPPORTED/SUPPORTINGSUBNETWORKNAME come with the first tier; FROM/TODEVICETERMINAL SmallInteger, FLOWDIRECTION. Open: TIERNAME is a SmallInteger coded by tier in Pro (grapy String), SubnetLine has no DOMAINNETWORKNAME in Pro |
| AddExpandContainerByAttributeRule | MATCH | MATCH | — |  |
| AddExpandContainerRule | MATCH | MATCH | — |  |
| AddForceDirectedLayout | DIFF | DIFF | recorded | a force-directed layout: different positions, EQUIVALENT |
| AddGridLayout | PRO-ERROR | MATCH | case | Pro refuses its default cells here (ERROR 001777, too small); the case gives 20 m cells |
| AddLinearDispatchLayout | MATCH | MATCH | — |  |
| AddMainRingLayout | DIFF | DIFF | recorded | positions differ (ring and tree placement) |
| AddMainlineTreeLayout | DIFF | DIFF | recorded | Pro's default spacing 80 m (along) against grapy's 2 (relative units), the open D-338 question |
| AddNetworkAttribute | GRAPY-ERROR | MATCH | fixed | Describe's networkAttributes: Pro's ten system attributes, in order, with their bit sizes and assignments |
| AddNetworkCategory | GRAPY-ERROR | MATCH | fixed | categories described; no Propagator Resetter in a version 7 network |
| AddPartialOverlappingEdgesLayout | GRAPY-ERROR | MATCH | fixed | the Add tool takes the buffer width alone (grapy required the offset); the Apply tool needs both, as Pro |
| AddRadialTreeLayout | DIFF | DIFF | recorded | Pro's radius in hundreds of metres, grapy's in units of 5; root placed differently (D-338) |
| AddReduceEdgeByAttributeRule | MATCH | MATCH | — |  |
| AddReduceJunctionByAttributeRule | MATCH | MATCH | — |  |
| AddReduceJunctionRule | MATCH | MATCH | — |  |
| AddRemoveFeatureByAttributeRule | DIFF | MATCH | fixed | a system junction left with no edge goes with the removed line |
| AddRemoveFeatureRule | MATCH | MATCH | — |  |
| AddReshapeDiagramEdgesLayout | MATCH | MATCH | — |  |
| AddRule | PRO-ERROR | MATCH | fixed | SetAssociationRole needs its deletion semantics for a container or structure role (ERROR 000735; grapy defaulted to CASCADE) |
| AddSetRootJunctionByAttributeRule | MATCH | MATCH | — |  |
| AddSetStartingPointByAttributeRule | DIFF | MATCH | fixed | the trace rule adds its result to the diagram, which keeps what it held (grapy replaced it) |
| AddSmartTreeLayout | MATCH | MATCH | — |  |
| AddSpatialDispatchLayout | DIFF | DIFF | recorded | Pro moves junctions apart by a few metres; grapy keeps them where they are |
| AddSpatialQueryRule | MATCH | MATCH | — |  |
| AddStartIterationRule | MATCH | MATCH | — |  |
| AddStopIterationRule | MATCH | MATCH | — |  |
| AddStructuralAttachmentsRule | MATCH | MATCH | — |  |
| AddTerminalConfiguration | GRAPY-ERROR | MATCH | fixed | configurations numbered from 2, terminal ids run on through the network (Three's are 4-6 after Source-Load's 2-3); a valid path's pairs were stored with ids one too low (a grapy bug: paths named the wrong terminals); path descriptions 'A-B,A-C' |
| AddTier | GRAPY-ERROR | MATCH | fixed | the tier's fields; SUPPORTED/SUPPORTING added with the first tier |
| AddTierGroup | GRAPY-ERROR | MATCH | fixed | first run GRAPY-ERROR: no Describe of a utility network |
| AddTraceConfiguration | PRO-ERROR | DIFF | fixed / not comparable | result types SELECTION or AGGREGATED_GEOMETRY only (ERROR 000800). Not comparable: Pro adds the configuration but exports none of it ([]) and cannot delete it (ERROR 160281) without a signed-in portal user |
| AddTraceLocations | PRO-ERROR | DIFF | fixed / recorded | Pro's fields (LAYERNAME, SOURCEID, ASSETGROUPCODE, ASSETTYPE, FEATUREOID, DISPLAYNAME), TERMINALID -1 on an edge and 1 for the Single Terminal, PERCENTALONG -1 on a junction, points with z, '(1/Single Terminal)' read; network sources numbered as Pro numbers them. Open: LAYERID (a map layer's id), FEATUREGLOBALID Guid and FEATUREOID BigInteger types (grapy's GeoPackage fields are String and Integer) |
| AddTraceRule | DIFF | MATCH | fixed | as above, and indeterminate flow traced by default (grapy ignored it by default) |
| AlterDiagramProperties | PRO-ERROR | MATCH | case | runs alone (one stored diagram per Pro process) |
| AlterDiagramTemplate | MATCH | MATCH | — |  |
| AppendToDiagram | PRO-ERROR | PRO-ERROR | not comparable | Pro: ERROR 000735 Input Map: Value is required; a map needs a project, which is not opened here |
| ApplyAngleDirectedLayout | MATCH | MATCH | — |  |
| ApplyCompressionLayout | DIFF | DIFF | recorded | as AddCompressionLayout |
| ApplyForceDirectedLayout | DIFF | DIFF | recorded | as AddForceDirectedLayout |
| ApplyGeoPositionsLayout | MATCH | MATCH | — |  |
| ApplyGridLayout | PRO-ERROR | MATCH | case | as AddGridLayout |
| ApplyLinearDispatchLayout | MATCH | MATCH | — |  |
| ApplyMainRingLayout | DIFF | DIFF | recorded | as AddMainRingLayout |
| ApplyMainlineTreeLayout | DIFF | DIFF | recorded | as AddMainlineTreeLayout |
| ApplyPartialOverlappingEdgesLayout | PRO-ERROR | MATCH | case | Pro needs the offset (ERROR 000530); the case gives one |
| ApplyRadialTreeLayout | DIFF | DIFF | recorded | as AddRadialTreeLayout |
| ApplySmartTreeLayout | MATCH | MATCH | — |  |
| ApplySpatialDispatchLayout | DIFF | DIFF | recorded | as AddSpatialDispatchLayout |
| ApplyTemplateLayouts | MATCH | MATCH | — |  |
| CalculateClusterKeys | GRAPY-ERROR | DIFF | fixed / recorded | calculated on an enabled topology (grapy required it disabled). Open: EQUIVALENT keys: Pro gives the devices 3 keys, the junctions 1, the lines 3; grapy one per connected component (1) |
| ChangeDiagramsOwner | BOTH-ERROR | BOTH-ERROR | not comparable | Pro: ERROR 000229 Cannot open the network: diagram owners are an enterprise geodatabase's |
| CreateDiagram | DIFF | MATCH | fixed | the containers of the junctions an edge brings in are in the diagram (a diagram of lines holds the vault containing V2) |
| CreateUtilityNetwork | GRAPY-ERROR | MATCH | fixed | Describe of a utility network built (_undescribe); COMPATIBLE and CURRENT make version 7 (grapy said 8), Propagator Resetter only in VERSION_8; structure classes lose SUPPORTEDSUBNETWORKNAME, StructureLine gets FLOWDIRECTION |
| DeleteDiagram | PRO-ERROR | MATCH | case | runs alone (one stored diagram per Pro process) |
| DeleteDiagramTemplate | MATCH | MATCH | — |  |
| DeleteNetworkCategory | GRAPY-ERROR | MATCH | fixed | as AddNetworkCategory |
| DeleteRule | GRAPY-ERROR | MATCH | case | the rule is named by its id, '7: From[...] To[...]' |
| DeleteTerminalConfiguration | GRAPY-ERROR | MATCH | fixed | first run GRAPY-ERROR: no Describe |
| DeleteTraceConfiguration | PRO-ERROR | PRO-ERROR | not comparable | ERROR 160281: A requested row object could not be located (no portal user here) |
| DisableNetworkTopology | MATCH | MATCH | — |  |
| EnableClusterKeys | MATCH | MATCH | — |  |
| EnableNetworkTopology | DIFF | DIFF | recorded | error dirty areas: Pro's ERRORCODE 256 (the plug with no rule) and 260 (the asset group 0 line) against grapy's 2 for the line only; Pro's code scheme is not the error list's numbers |
| ExportAssociations | MATCH | MATCH | — |  |
| ExportDiagramContent | MATCH | MATCH | — |  |
| ExportDiagramTemplateDefinitions | REFUSED | REFUSED | recorded | REFUSED by name for the .ndld (a map's layer definitions, ADR-232 §5.7); the .ndbd is written |
| ExportRules | DIFF | MATCH | fixed | an asset type without terminals writes an empty terminal (grapy wrote 'Single Terminal') |
| ExportSubnetwork | DIFF | DIFF | fixed / recorded | the file in Pro's layout: results at the top level, controllers without names, resultTypes as objects, spatialReference; containers in by default. Open: Pro lists system junctions as feature elements (source 2) and one more connectivity row |
| ExportSubnetworkControllers | MATCH | MATCH | — |  |
| ExportTraceConfigurations | PRO-ERROR | PRO-ERROR | not comparable | Pro writes [] for a configuration it added (no portal user here) |
| ExtendDiagram | PRO-ERROR | PRO-ERROR | not comparable | Pro: ERROR 999999 on a file geodatabase diagram, alone or not |
| GetDiagramTemplateNames | MATCH | MATCH | — |  |
| ImportAssociations | MATCH | MATCH | — |  |
| ImportDiagramTemplateDefinitions | MATCH | MATCH | — |  |
| ImportRules | DIFF | MATCH | fixed | a junction-junction rule is kept from the lower network source (Pro exported Plug-to-Valve as Valve-to-Plug; INFERRED from one measurement) |
| ImportSubnetworkControllers | DIFF | DIFF | fixed / recorded | SUBNETWORKCONTROLLERNAME is 'Load/Plant', terminal/controller. Open: TIERNAME coded (1, not 'Pressure'); Pro writes 0, ' ' and 0 on the devices that are not controllers where grapy leaves nulls |
| ImportTraceConfigurations | PRO-ERROR | PRO-ERROR | not comparable | Pro's DeleteTraceConfiguration fails, ERROR 160281 (no portal user here) |
| MakeDiagramLayer | MATCH | MATCH | — |  |
| OverwriteDiagram | PRO-ERROR | PRO-ERROR | not comparable | as AppendToDiagram |
| PurgeTemporaryDiagrams | MATCH | MATCH | — |  |
| RebuildNetworkTopology | PRO-ERROR | MATCH | fixed | the extent is required (ERROR 000735) |
| ReshapeDiagramEdgesLayout | MATCH | MATCH | — |  |
| SetAssociationRole | GRAPY-ERROR | MATCH | fixed | deletion semantics required; first run GRAPY-ERROR: no Describe |
| SetEdgeConnectivity | GRAPY-ERROR | MATCH | fixed | first run GRAPY-ERROR: no Describe |
| SetNetworkAttribute | GRAPY-ERROR | MATCH | fixed / case | assignments described; the case assigns a nullable attribute: grapy's AddField keeps no nullability (management's), so a NOT_NULLABLE attribute cannot be assigned on grapy |
| SetNetworkCategory | GRAPY-ERROR | MATCH | fixed | no category named leaves the asset type's categories (grapy unassigned them all) |
| SetSubnetworkDefinition | GRAPY-ERROR | MATCH | fixed / recorded | Is dirty managed by default with or without controllers; containers, content and structures included by default; condition barriers described as Pro does (type, Equal, isSpecificValue). Open: Pro describes includeContainers True even when the definition is set without them |
| SetTerminalConfiguration | PRO-ERROR | MATCH | fixed | ERROR 004114 on an asset type a connectivity rule names (grapy assigned it) |
| StoreDiagram | DIFF | MATCH | fixed | as CreateDiagram; Pro stores one diagram per process in a file geodatabase (ERROR 001771 for a second), so the case runs alone |
| Trace | DIFF | MATCH | fixed | a trace including containers lists the container among its elements (17 traces compared) |
| UpdateDiagram | DIFF | MATCH | fixed | as CreateDiagram |
| UpdateIsConnected | DIFF | MATCH | fixed | IsConnected codes 0 False, 1 True, 2 Unknown, Pro's domain (grapy had 0 Unknown, 2 False, INFERRED) |
| UpdateSubnetwork | DIFF | DIFF | recorded | Pro writes no SubnetLine row when the definition aggregates no lines (grapy one of every line); Pro's names read ' ::Zone1' on features that held ' ' before |
| UtilityNetwork | DIFF | DIFF | fixed / recorded | DeleteStructuralAttachmentAssociation spelled as Pro spells it (grapy had 'Structual'; kept as an alias). Open: the non-controller placeholders, as ImportSubnetworkControllers |
| ValidateNetworkTopology | PRO-ERROR | DIFF | fixed / recorded | ERROR 160521 for an extent with no network feature (grapy validated nothing quietly). Open: Pro writes a dirty area when an edit is made in an edit session; grapy finds it when a utility network tool next runs (ADR-231 §5.4) |

## Not comparable here

- AddTraceConfiguration, ExportTraceConfigurations, ImportTraceConfigurations, DeleteTraceConfiguration: Pro adds a
  configuration but exports none of it (`[]`) and cannot find it to delete (ERROR 160281); configurations are kept
  per portal user, and Pro is not signed in here (and must not be). grapy's one Pro-measurable part â€” result types
  SELECTION or AGGREGATED_GEOMETRY only â€” is aligned.
- ChangeDiagramsOwner: ERROR 000229 Cannot open the network â€” diagram owners are an enterprise geodatabase's.
- ExtendDiagram: ERROR 999999 on a file geodatabase diagram, alone in its process or not.
- AppendToDiagram, OverwriteDiagram: ERROR 000735 Input Map: Value is required; a map needs a project, which is not
  opened here.

## Open differences worth a debt row

1. **TIERNAME is a coded short integer in Pro** (domain `<network>_<id>_TierName`: 0 Unknown, 1 Pressure) on devices,
   junction objects and the SubnetLine; grapy writes the tier's name as text. Changing it touches controllers,
   subnetworks and every trace that reads the tier. Estimate: a day.
2. **Placeholder values on features**: Pro writes 0, `' '` and 0 into ISSUBNETWORKCONTROLLER, SUBNETWORKCONTROLLERNAME
   and TIERNAME of devices that are not controllers, and its subnetwork names read `' ::Zone1'` on features that held
   `' '`; grapy leaves nulls and writes `Zone1`. The rule behind `' '` was not derived (the fields' defaults are
   `Unknown`). Half a day once measured further.
3. **SubnetLine**: Pro writes no SubnetLine row when the subnetwork definition aggregates no lines; grapy writes one
   of every line. Pro's SubnetLine has no DOMAINNETWORKNAME (grapy's queries key on it). Half a day.
4. **System junctions in an exported subnetwork**: Pro lists them as feature elements (network source 2) and writes
   one more connectivity row; grapy's system junctions have source 0 and no feature element. Half a day.
5. **Dirty areas at edit time**: Pro writes a dirty area when an edit is saved in an edit session; grapy finds it when
   a utility network tool next runs (ADR-231 Â§5.4, a design choice). And Pro's error dirty areas carry ERRORCODE 256
   and 260 where grapy writes the error list's numbers (2): the code scheme is not derived.
6. **Cluster keys** (EQUIVALENT): Pro's keys split the lines and devices into three groups where grapy's connected
   components give one; Pro's grouping is not documented. Days, if derivable at all.
7. **Diagram layouts** â€” Spatial Dispatch, Compression, Force Directed, Mainline Tree, Radial Tree, Main Ring: Pro's
   positions differ (dispatch nudges junctions apart by metres, compression steps about 13.65 m, the tree layouts'
   default spacings are absolute metres where grapy's are relative units â€” D-338's open question). Linear Dispatch,
   Angle Directed, Smart Tree, Grid, Partial Overlapping Edges, Reshape Edges, Geo Positions and the template's
   layouts match. Days per layout.
8. **Field types**: AddTraceLocations' FEATUREGLOBALID is a Guid and FEATUREOID a BigInteger in Pro; grapy's
   GeoPackage fields are String and Integer (management's AddField maps GUID and BIGINTEGER the same way), and grapy
   writes no LAYERID (a map layer's id). management.AddField also keeps no field nullability, so a NOT_NULLABLE
   network attribute cannot be assigned on grapy (the case assigns a nullable one).
9. **ListFeatureClasses with a feature dataset** answers `[]` in grapy (its INFERRED docstring predates feature
   datasets); the case asks for each class by name instead. arcpy top level, the arcpy_top sweep's.
10. ExportDiagramTemplateDefinitions' .ndld stays REFUSED by name (ADR-232 Â§5.7); the .ndbd round trip matches.
