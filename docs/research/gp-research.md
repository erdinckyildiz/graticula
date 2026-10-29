# GPServer and the Spatial Analysis Service — public-documentation research for Graticula

Researched 2026-10-29 from public Esri documentation only (developers.arcgis.com REST, JS SDK and
Python API references, doc.esri.com / enterprise.arcgis.com / doc.arcgis.com help, pro.arcgis.com
help). No proprietary source was read. Every fact carries the URL it came from. Anything not
confirmed in a public page is marked **UNCONFIRMED**.

Short URL keys used below:

| Key | URL |
|---|---|
| [SVC] | https://developers.arcgis.com/rest/services-reference/enterprise/gp-service/ |
| [TASK] | https://developers.arcgis.com/rest/services-reference/enterprise/gp-task/ |
| [EXEC] | https://developers.arcgis.com/rest/services-reference/enterprise/execute-gp-task/ |
| [SUBMIT] | https://developers.arcgis.com/rest/services-reference/enterprise/submit-gp-job/ |
| [JOB] | https://developers.arcgis.com/rest/services-reference/enterprise/gp-job/ |
| [CANCEL] | https://developers.arcgis.com/rest/services-reference/enterprise/cancel-gp-job/ |
| [RESULT] | https://developers.arcgis.com/rest/services-reference/enterprise/gp-result/ |
| [RESULTS] | https://developers.arcgis.com/rest/services-reference/enterprise/results/ |
| [INPUT] | https://developers.arcgis.com/rest/services-reference/enterprise/gp-input/ |
| [INPUTS] | https://developers.arcgis.com/rest/services-reference/enterprise/inputs/ |
| [OVERVIEW] | https://developers.arcgis.com/rest/services-reference/enterprise/gp-overview/ |
| [TYPES] | https://developers.arcgis.com/rest/services-reference/enterprise/gp-data-types/ |
| [SA-ENT] | https://developers.arcgis.com/rest/services-reference/enterprise/spatial-analysis/overview/spatial-analysis-tools/ |
| [SA-TASKS] | https://developers.arcgis.com/rest/analysis/tasks/tasks-overview/ |
| [SA-PROG] | https://developers.arcgis.com/rest/analysis/reference/programmatically-accessing-analysis-services/ |
| [SA-STATUS] | https://developers.arcgis.com/rest/analysis/reference/checking-job-status-1/ |
| [SA-FIN] | https://developers.arcgis.com/rest/analysis/reference/feature-input-1/ |
| [SA-FOUT] | https://developers.arcgis.com/rest/analysis/reference/feature-output-1/ |
| [SA-CTX] | https://developers.arcgis.com/rest/analysis/reference/spatial-reference/ |
| [BUF] | https://developers.arcgis.com/rest/analysis/tasks/sa-create-buffers/ |
| [DIS] | https://developers.arcgis.com/rest/analysis/tasks/sa-dissolve-boundaries/ |
| [OVL] | https://developers.arcgis.com/rest/analysis/tasks/sa-overlay-layers/ |
| [AGG] | https://developers.arcgis.com/rest/analysis/tasks/sa-aggregate-points/ |
| [EXT] | https://developers.arcgis.com/rest/analysis/tasks/extract-data/ |
| [MRG] | https://developers.arcgis.com/rest/analysis/tasks/sa-merge-layers/ |
| [SUMW] | https://developers.arcgis.com/rest/analysis/tasks/sa-summarize-within/ |
| [JOIN] | https://developers.arcgis.com/rest/analysis/tasks/sa-join-features/ |
| [NEAR] | https://developers.arcgis.com/rest/analysis/tasks/find-nearest/ |
| [SELF] | https://developers.arcgis.com/rest/users-groups-and-items/portal-self/ |
| [PRIV] | https://developers.arcgis.com/rest/users-groups-and-items/privileges/ |
| [MV] | https://doc.esri.com/en/arcgis-enterprise/latest/analyze/perform-analysis-mv.html |
| [MVLIC] | https://doc.esri.com/en/arcgis-enterprise/latest/plan/licensing-analysis-mv.html |
| [CFG] | https://enterprise.arcgis.com/en/portal/11.2/administer/linux/configure-the-portal-to-perform-analysis.htm |
| [PY-PROX] | https://developers.arcgis.com/python/latest/api-reference/arcgis.features.use_proximity.html |
| [PY-ANAL] | https://developers.arcgis.com/python/latest/api-reference/arcgis.features.analysis.html |
| [PY-GP] | https://developers.arcgis.com/python/latest/api-reference/arcgis.geoprocessing.html |
| [JS-GP] | https://developers.arcgis.com/javascript/latest/references/core/rest/geoprocessor/ |
| [JS-JOB] | https://developers.arcgis.com/javascript/latest/references/core/rest/support/JobInfo/ |
| [JS-OPT] | https://developers.arcgis.com/javascript/latest/references/core/rest/geoprocessor/GPOptions/ |
| [PRO-WT] | https://doc.esri.com/en/arcgis-pro/latest/help/analysis/geoprocessing/share-analysis/using-web-tools-in-arcgis-pro.html |
| [PRO-PT] | https://doc.esri.com/en/arcgis-pro/latest/help/analysis/geoprocessing/basics/portal-tools.html (also https://pro.arcgis.com/en/pro-app/3.5/help/analysis/geoprocessing/basics/portal-tools.htm) |
| [CREDITS] | https://doc.arcgis.com/en/arcgis-online/administer/credits.htm |

---

## 0. The findings that matter most for an implementation

1. **Two separate surfaces share one wire protocol.** A generic GPServer (`/rest/services/<svc>/GPServer`)
   and the portal's Spatial Analysis Service (`helperServices.analysis.url`, by convention
   `.../rest/services/System/SpatialAnalysisTools/GPServer`) both speak *the same* task / submitJob /
   jobs / results protocol [SELF][SA-STATUS]. The analysis service is just a GPServer whose tasks
   (`CreateBuffers`, `DissolveBoundaries`, …) have fixed, documented parameter names, are **all
   asynchronous** [SA-ENT], take layers as `{"url":…,"filter":…}` or a feature collection [SA-FIN],
   and write their output as a hosted feature layer when `outputName` is given [SA-FOUT].
2. **Analysis-service results use `dataType: "GPString"` with an object value** — e.g.
   `{"paramName":"bufferLayer","dataType":"GPString","value":{"url":"<hosted feature service layer url>"}}`
   — in every task page read ([BUF][DIS][OVL][AGG][MRG][SUMW][EXT]). The generic GP "feature
   service output" shape instead says `dataType: "GPFeatureRecordSetLayer"` with `{"url": ...}` [TYPES].
   A compatible server should copy each surface's documented shape, not unify them.
3. **`context` (10.6.1+) and the legacy `env:outSR` both exist.** REST documents `context`
   `{outSR, processSR, extent}` for execute and submitJob [EXEC][SUBMIT], and still shows
   `env:outSR=102100` for "10.6 or earlier" [SUBMIT]. The JS SDK states that *if `processExtent` is not
   set, `execute` and `submitJob` format requests compatible with ArcGIS prior to 10.6.1* [JS-GP] — so a
   current JS client will often send the legacy form. Accept both. (`env:processSR` as a literal
   parameter name: **UNCONFIRMED** in the pages read.)
4. **ArcGIS Pro does not consume a plain GPServer over REST.** "Connect to ArcGIS Server" needs the
   `https://host/<context>/services` URL, which "serves SOAP requests only. Do not provide the REST
   URL" [PRO-WT]. Pro's other two routes are web tools found through the portal (Catalog > Portal) and
   **portal tools** (Standard Feature Analysis), which appear only on an Enterprise portal "that has a
   hosting server configured for the Feature Analysis - Standard Tools setting"; they do not work
   against ArcGIS Online [PRO-PT]. Through which REST calls Pro discovers and runs either of these is
   **UNCONFIRMED** from public docs. It has to be captured from a live Pro session, the way this
   project has already captured other Pro traffic.
5. **Map Viewer's Analysis button** needs a signed-in user with the right privileges, an organization
   that is configured for analysis, and **at least one hosted feature layer in the organization** [MV].
   The privileges are `premium:user:spatialanalysis`, `portal:user:createItem` and
   `portal:publisher:publishFeatures` [PRIV][SA-ENT][MVLIC]. A tool is hidden when the utility services
   it needs are not configured or the member lacks permission [CFG].

---

## 1. GPServer REST shape

### 1.1 Service resource — `.../GPServer?f=json` [SVC]

`f` = `html` (default) | `json` | `pjson`; GET or POST. Documented members:

| Member | Type | Notes |
|---|---|---|
| `currentVersion` | number | Server version. Some older servers omit it. |
| `cimVersion` | string | Pro runtime version. |
| `serviceDescription` | string | |
| `tasks` | string[] (unique) | Task **display names** |
| `taskNames` | string[] | Internal task names |
| `executionType` | string | `esriExecutionTypeAsynchronous` \| `esriExecutionTypeSynchronous` |
| `resultMapServerName` | string | `""` when there is none |
| `maximumRecords` | integer ≥ 1 | Transfer limit (see `exceededTransferLimit`, §2.1) |
| `capabilities` | string | `""`, `"Uploads"`, `"Validate"`, `"Uploads,Validate"` |
| `provider` | string | e.g. ArcObjects11 |
| `schema` | object | `{"json-schema":{"url": "..."}}`, Draft 7 from 11.1 |

The schema on the page lists `currentVersion, serviceDescription, tasks, executionType,
resultMapServerName, maximumRecords, capabilities` as required. Child resources: `info` is always
present, and `uploads` appears when the Uploads capability is on [SVC].

Literal example (older server) [SVC]:

```json
{
 "currentVersion": 10.91,
 "cimVersion": "2.9.0",
 "serviceDescription": "This geoprocessing service is based on a series of analysis including integrate, collect events, hotspot analysis (Getis-Ord Gi*), natural neighbor, and reclassify.",
 "tasks": ["911 Calls Hotspot"],
 "executionType": "esriExecutionTypeAsynchronous",
 "resultMapServerName": "",
 "maximumRecords": 1000,
 "capabilities": ""
}
```

11.1 example adds `"capabilities": "Uploads"` and
`"schema": {"json-schema": {"url": ".../rest/directories/arcgisoutput/FileProcessing_GPServer/FileProcessing/spec/json-schema.json"}}` [SVC].

### 1.2 Task resource — `.../GPServer/<task>?f=json` [TASK]

Top level: `name`, `displayName`, `description`, `category`, `helpUrl`, `executionType`, `parameters[]`.
Operations: `submitJob` (async), `execute` (sync), and `validate` (12.0+, with the Validate capability).
There is a `metadata` child resource (XML, 10.9.1+). Optional keys can be absent depending on server
version [TASK].

Parameter object. The schema lists `name, dataType, displayName, description, direction, defaultValue,
parameterType, category` as required [TASK]:

| Key | Values |
|---|---|
| `name` | string |
| `dataType` | e.g. `GPFeatureRecordSetLayer`, `GPMultiValue:GPRasterDataLayer` |
| `displayName`, `description`, `category` | string |
| `direction` | `esriGPParameterDirectionInput` \| `esriGPParameterDirectionOutput` |
| `dependency` | optional; name of the parameter this one depends on |
| `defaultValue` | any; may be null; shape follows the data type |
| `parameterType` | `esriGPParameterTypeRequired` \| `esriGPParameterTypeOptional` \| `esriGPParameterTypeDerived` |
| `choiceList` | string[] unique ("for input with a choice list, or a ValueList filter") |
| `filter` | see below |
| `parameterInfos` | element info for GPMultiValue / GPValueTable / GPComposite: `{name, dataType, displayName, filter?, choiceList?, parameterInfos?}`. `name` is the column name for a value table and `""` for MultiValue/Composite |

`filter.type` ∈ `range` (`minimum`, `maximum`; GPLong/GPDouble), `featureClass` (`list` of
`esriGeometryPoint|Multipoint|Polyline|Polygon|MultiPatch|Envelope`), `file` (`list` of extensions
without the dot), `field` (`list` of `esriFieldType*`), `codedValue` (12.0+; `list` of
`{dataType, name, value}`), `composite` (12.0+; `list` of component filters, with `null` for "no
filter") [TASK]. Esri recommends `codedValue` over `choiceList`. Example of `codedValue` for localized
strings [TASK]:

```json
{"filter": {"type": "codedValue", "list": [
  {"dataType": "GPString", "name": "No Dissolve", "value": "NONE"},
  {"dataType": "GPString", "name": "Dissolve all output features into a single feature", "value": "ALL"},
  {"dataType": "GPString", "name": "Dissolve features using the listed fields' unique values or combination of values", "value": "LIST"}
]}}
```

Literal example 1 (async; abridged only in the repeated `fields` arrays) [TASK]:

```json
{
 "name": "FeatureFiltersLinePolygon",
 "displayName": "Feature filters with line and polygon",
 "description": "This is a feature analysis with input polyline and polygon only. Points are not supported.",
 "category": "",
 "helpUrl": "https://organization.example.com/<context>/rest/directories/arcgisoutput/Level2_Features_Async_GPServer/Level2_Features_Async/FeatureFiltersLinePolygon.htm",
 "executionType": "esriExecutionTypeAsynchronous",
 "parameters": [
  {
   "name": "Input_Features",
   "dataType": "GPFeatureRecordSetLayer",
   "displayName": "Input Features",
   "description": "line and polygon only",
   "direction": "esriGPParameterDirectionInput",
   "filter": {"type": "featureClass", "list": ["esriGeometryPolygon", "esriGeometryPolyline"]},
   "defaultValue": {
    "displayFieldName": "", "hasZ": true, "geometryType": "esriGeometryPolygon",
    "spatialReference": {"wkid": 102100, "latestWkid": 3857},
    "fields": [{"name": "OBJECTID", "type": "esriFieldTypeOID", "alias": "OBJECTID"}, "..."],
    "features": [], "exceededTransferLimit": false
   },
   "parameterType": "esriGPParameterTypeRequired",
   "category": ""
  },
  {
   "name": "Output_Feature_Class",
   "dataType": "GPFeatureRecordSetLayer",
   "displayName": "Output Feature Class",
   "description": "ofc",
   "direction": "esriGPParameterDirectionOutput",
   "defaultValue": {"geometryType": "esriGeometryPoint", "fields": ["..."], "features": [], "exceededTransferLimit": false},
   "parameterType": "esriGPParameterTypeRequired",
   "category": ""
  },
  {
   "name": "esri_out_feature_service_name",
   "dataType": "GPString",
   "displayName": "Output Feature Service Name",
   "description": "The name of the optional feature service to create on the federated server containing the result of this tool. If no name is specified, an output feature service will not be created.",
   "direction": "esriGPParameterDirectionInput",
   "defaultValue": "",
   "parameterType": "esriGPParameterTypeOptional",
   "category": ""
  }
 ]
}
```

Example 2 shows a synchronous task with a `GPValueTable` input whose `defaultValue` is an array of
arrays (`[[567,-56756.567],[-6567,67886.678]]`). Its `parameterInfos` carry per-column `range`
filters, and a `GPMultiValue:GPRasterDataLayer` output has `parameterType`
`esriGPParameterTypeDerived` and `parameterInfos: [{"name":"","dataType":"GPRasterDataLayer","displayName":""}]` [TASK].

### 1.3 `execute` (synchronous) — `.../GPServer/<task>/execute` [EXEC]

GET/POST; introduced 9.3. Request parameters:

| Param | Meaning |
|---|---|
| `<gpParameter…>` | Each input parameter by `name`; value encoded per data type (§2). Same syntax as submitJob. |
| `context` | 10.6.1+. `{"outSR":…, "processSR":…, "extent":…}`. An SR is a WKID or an SR object. If there is no outSR, output is in the input SR. If processSR is set and outSR is not, output is in processSR. `extent` processes only features overlapping the envelope, so output extent ≥ the given extent. |
| `returnZ` / `returnM` | default false; applies to GPFeatureRecordSetLayer values |
| `returnTrueCurves` | default false (do not combine with `f=geojson`) |
| `returnFeatureCollection` | 10.6+; return GPFeatureRecordSetLayer results as a feature collection |
| `returnColumnName` | 11.0+; GPValueTable output as objects keyed by column name |
| `simplifyFeatures` | 11.4+; default true |
| `f` | `html` (default) \| `json` \| `kmz` \| `geojson` (10.5+) |

Literal request example [EXEC]:

```
.../GPServer/Viewshed/execute?Input_Observation_Point={"features":[{"geometry":{"x":8598136.9169,"y":4197867.5324,"spatialReference":{"wkid":54003}}}]}&Viewshed_Distance={"distance":15000,"units":"esriMeters"}&context={"outSR":{"wkid":4326}}&f=json
```

Response: `{"results":[{paramName,dataType,value}], "messages":[{type,description}]}`. Both
top-level keys are required. `messages[].type` ∈ `esriJobMessageTypeInformative` |
`esriJobMessageTypeWarning` | `esriJobMessageTypeError` [EXEC]. Literal example (abridged):

```json
{
 "results": [
  {"paramName": "Output_rasters", "dataType": "GPMultiValue:GPRasterDataLayer",
   "value": [{"url": "https://organization.example.com/<context>/rest/directories/arcgisoutput/ServiceName_GPServer/_ags_outrd01.tif", "format": "tif"}]},
  {"paramName": "Output_doubles", "dataType": "GPMultiValue:GPDouble", "value": [85768.567850000007, -9678.4560000000001, -34545.546000000002]},
  {"paramName": "Output_strings", "dataType": "GPMultiValue:GPString", "value": ["California", "Texas", "Alaska"]}
 ],
 "messages": [
  {"type": "esriJobMessageTypeInformative", "description": "Start Time: Thursday, December 29, 2022 3:53:59 PM"},
  {"type": "esriJobMessageTypeInformative", "description": "Succeeded at Thursday, December 29, 2022 3:54:04 PM (Elapsed Time: 5.07 seconds)"}
 ]
}
```

Second example: one `GPFeatureRecordSetLayer` result whose value is a featureSet
(`geometryType`, `spatialReference`, `features`, `"exceededTransferLimit": false`) and `"messages": []` [EXEC].

Legacy environment parameters: `env:outSR=102100` is documented "on 10.6 or earlier" [SUBMIT].
`env:processSR`: **UNCONFIRMED** as a literal name in the pages read. The JS SDK sends the pre-10.6.1
request format when `processExtent` is unset [JS-GP].

### 1.4 `submitJob` (asynchronous) — `.../GPServer/<task>/submitJob` [SUBMIT]

The parameters are the same as execute: the GP inputs plus `context`, `returnTrueCurves` (10.5+),
`simplifyFeatures` (11.4+) and `f` (`html|json|kmz|geojson`, default html). Examples [SUBMIT]:

```
.../GPServer/ERGByChemical/submitJob?Incident_Point={"features":[{"geometry":{"x":-104.44,"y":34.83},"attributes":{"Id":43,"Name":"Incident"}}]}&f=html
.../GPServer/ERGByChemical/submitJob?Incident_Point={...}&env:outSR=102100&f=html
.../GPServer/TaskName/submitJob?input=points&context={extent:{"xmin":7602115,"ymin":660605,"xmax":7619050,"ymax":676100,spatialReference:{"wkid":102726}}}
```

The response "is the Jobs resource" [SUBMIT]. With `f=html` the client is redirected to
`.../jobs/<jobId>` [JOB]. The minimal JSON shape documented for analysis tasks is [BUF]:

```json
{"jobId": "<unique job identifier>", "jobStatus": "<job status>"}
```

**Output feature service** (`esri_out_feature_service_name`, federated async services only) [SUBMIT].
Before Enterprise 11 the value is a plain string (the new service name). From 11 it can also be an object:

```json
{
  "serviceProperties": {"name": "output_requests_hosted_featureservice"},
  "itemProperties": {"tags": "resultTag", "description": "demo description", "snippet": "demo snippet"}
}
```

Schema members: `serviceProperties.{serviceUrl, name}`, `itemProperties.{overwrite, itemId, tags,
description, snippet, newFeatureServiceOnOverwriteFail}`. To overwrite, the caller must own the
service or be an admin. Overwrite deletes all existing layers. If an overwrite fails and
`newFeatureServiceOnOverwriteFail` is true, a new service named `<name>_1`, `<name>_2`, … is created
[SUBMIT]. The parameter is auto-added to the task as an optional `GPString` (task example 1). With
it, `GPFeatureRecordSetLayer`/`GPRecordSet` results become `{"url": ".../Hosted/<name>/FeatureServer/0"}`
and have no transfer limit [TYPES]. (`esri_out_image_service` exists for raster outputs, 11.4+; out of
scope here [SUBMIT].)

### 1.5 Job resource — `.../GPServer/<task>/jobs/<jobId>` [JOB]

Request: `f` (`html|json|kmz`), `returnMessages` (default true).

`jobStatus` enum (full schema list) [JOB]: `esriJobNew`, `esriJobWaiting`, `esriJobSubmitted`,
`esriJobExecuting`, `esriJobCancelling`, `esriJobCancelled`, `esriJobFailed`, `esriJobTimedOut`,
`esriJobSucceeded`, `esriJobDeleting`, `esriJobDeleted`. The analysis-service page lists only the
eight a client normally sees: Submitted, Waiting, Executing, Succeeded, Failed, TimedOut, Cancelling,
Cancelled. A job "will start with a status of esriJobSubmitted" [SA-STATUS].

Required: `jobId`, `jobStatus`, `messages`. Optional:
- `results` / `inputs`: map from parameter name to `{"paramUrl": "results/<name>"}` /
  `{"paramUrl": "inputs/<name>"}`. These are **relative** URLs, listed "for a succeeded job" [JOB].
- `progress` (10.8.1+, JSON only, only while `esriJobExecuting`; refreshed every 5 s by default):
  `{"type": "default"|"step", "message": "...", "percent": 0..100}`. With no progressor, the default
  is `{"type":"default","message":"Executing..."}` [JOB].

Literal example (messages abridged) [JOB]:

```json
{
 "jobId": "jfae883d52a074359a0bac3d5b353fe16",
 "jobStatus": "esriJobSucceeded",
 "results": {
  "Output_Long": {"paramUrl": "results/Output_Long"},
  "Output_Feature_Class": {"paramUrl": "results/Output_Feature_Class"}
 },
 "inputs": {
  "Input_String": {"paramUrl": "inputs/Input_String"},
  "Input_Feature_Class": {"paramUrl": "inputs/Input_Feature_Class"}
 },
 "messages": [
  {"type": "esriJobMessageTypeInformative", "description": "Start Time: Friday, December 16, 2022 6:05:31 PM"},
  {"type": "esriJobMessageTypeInformative", "description": "Succeeded at Friday, December 16, 2022 6:05:53 PM (Elapsed Time: 22.04 seconds)"}
 ]
}
```

Job ids in the docs are `j` followed by 32 hex digits (e.g. `jf082fc6d926043fda44169444963fe46`) [JOB].
Whether clients depend on that form is **UNCONFIRMED**.

### 1.6 Result / input parameters [RESULT][RESULTS][INPUT][INPUTS]

- `jobs/<jobId>/results/<param>`: response is always `{"paramName","dataType","value"}`. Request
  params: `f` (`html|json|image|kmz|geojson|pbf`; pbf 10.8+, Pro-published FeatureRecordSetLayer only),
  `outSR`, `returnType=data` (raw data instead of a map image when there is a result map service),
  `returnZ`, `returnM`, `returnFeatureCollection` (10.6+), `returnColumnName` (11.0+) [RESULT].

  ```json
  {"paramName": "Output_String", "dataType": "GPString", "value": "TestString"}
  ```
- `jobs/<jobId>/results`: an **array** of the same objects. The `f` values are `html|json|pjson` [RESULTS].
- `jobs/<jobId>/inputs/<param>`: the same triple with the value exactly as submitted (`f`:
  `html|json|pjson|kmz`). `jobs/<jobId>/inputs` (11.1+) returns an array [INPUT][INPUTS].
- For non-succeeded jobs the job JSON has no links, but the URLs can still be built by hand
  [RESULTS][INPUT].

### 1.7 `cancel` — `jobs/<jobId>/cancel` [CANCEL]

`f` = `html|json`. Response `{"jobId":"<jobId>","jobStatus":"esriJobCancelling"}`; the job later
reaches `esriJobCancelled`. A job can be cancelled while Waiting or Executing. Cancelling a
Cancelled/Succeeded/Failed/TimedOut job returns an "Invalid or missing input parameters" error. So
does cancelling a job that is still Submitted, and that job continues to run [CANCEL].
(Admin settings `cancellationCheckInterval` and `progressMessageUpdateInterval`, 11.2+, minimum
1 s and 5 s [JOB].) A job-delete operation was not found in the public REST pages read: **UNCONFIRMED**.

### 1.8 Other operations, briefly

`validate` (12.0+, Validate capability) and `uploads` (Uploads capability) exist [TASK][SVC]. Uploaded
items are referenced as `{"itemID": "i…"}` (§2). Their full shapes were not researched.

---

## 2. GP data types and JSON value encodings [TYPES]

Simple values travel as literal form/query values. Complex values are JSON strings.

| Type | Input | Output `value` |
|---|---|---|
| **GPString / GPLong / GPDouble / GPBoolean** | Literal: `InputBoolean=true&InputDouble=345.678&InputLong=345&InputString=MyString`. GPLong is 64-bit range ±4503599627370495 from 11.2. | Literal, e.g. `{"paramName":"Output_Double","dataType":"GPDouble","value":1234.56}` |
| **GPDate** | Epoch milliseconds UTC (`InputDate=1199145600000`). ISO `yyyy-MM-ddTHH:mm:ss[.sss]` and `{"date":"","format":""}` object forms are also accepted. | Epoch ms only: `"value": 1199145600000` |
| **GPLinearUnit** | `{"distance": 345.678, "units": "esriMiles"}`. The units enum accepts both `esriX` and plain names (`Meters`/`esriMeters`, `Kilometers`, `Feet`, `Miles`, `NauticalMiles`, `Yards`, `Inches`, `Points`, `Millimeters`, `Centimeters`, `Decimeters`, `DecimalDegrees`, the `…Int` variants, `Unknown`). No unit means `esriUnknownUnits`. The string `"1 Kilometer"` is accepted from 12.1. | Object form with `esri…` unit names |
| **GPArealUnit** (11.3+) | `{"area": 50, "units": "esriSquareMiles"}` or the string `"3 SquareKilometers"`. Units: SquareInches/Feet/Yards/Miles/NauticalMiles (+`US`), Acres(+US), SquareMillimeters/Centimeters/Decimeters/Meters/Kilometers, Ares, Hectares, Unknown. Singular forms are allowed. (The page's schema block is mis-titled GPLinearUnit and shows `distance`; the sample uses `area`.) | Object form |
| **GPFeatureRecordSetLayer** | (a) **featureSet** `{geometryType, spatialReference, fields, features[], hasZ?, hasM?}`. Schema-less `{"features":[…]}` is allowed: geometry defaults to point and the SR to unknown. (b) **layer URL** `{"url": ".../FeatureServer/0", "filter": "severity=2 and name is null", "time": 967855732000 \| "967855732000,null"}` (time 10.7.1+). (c) **uploaded item** `{"itemID": "i1057fd92-…"}` (a GP-server upload, *not* a portal item id: "The itemID value of the portal item is not supported"). (d) URL to a JSON file or to a `.../query?…&f=json` request. (e) Layer object. 64-bit OIDs need `"length": 8` on the OID field (11.2+). | featureSet + `exceededTransferLimit` (when exceeded, `features: []`, `exceededTransferLimit: true`). With `returnFeatureCollection=true`: `{layerDefinition, featureSet}`. With an output feature service: `{"url": ".../FeatureServer/0"}`. |
| **GPRecordSet** | `{fields, features:[{attributes}]}`, `{"url": "<table layer url>"}`, `{"itemID": …}`, or a file URL | `{fields, features, exceededTransferLimit}` or `{"url": …}` |
| **GPRasterDataLayer** | `{"url": "https://myserver/lake.tif"}`, `{"itemID": …}`, an image-service URL (10.8+) | `{"url": ".../arcgisjobs/.../scratch/x.tif", "format": "tif"}`; with an output image service `{"url": ".../ImageServer", "type": "ImageService"}` |
| **GPDataFile** | `{"url": …}`, `{"itemID": …}` (server upload), `{"portalItemID": …}` (10.8+, federated) | `{"url": ".../rest/directories/arcgisjobs/<svc>/<jobId>/scratch/file.txt"}` (sync: `arcgisoutput`, may carry an `_ags_` prefix) |
| **Field** (10.7+) | `{"name","type","alias"?,"editable"?,"nullable"?,"length"?}`; name and type required | Same object. **Note:** the documented output example shows `"dataType": "Field"`, not `GPField`. Which literal the task JSON uses for this type is **UNCONFIRMED** beyond that example. |
| **GPMultiValue:\<T\>** | JSON array of T values, e.g. `["Parcels","Street Lights"]` or `[{"url":…},{"url":…}]`. Any T except GPValueTable. | Array |
| **GPValueTable** (11.0+, Pro 3.0) | Array of row arrays in column order (`""` for an empty string, `null` otherwise), or an array of `{columnName: value}` when all columns have unique non-empty names | Array of arrays by default; objects with `returnColumnName=true` |
| **GPComposite** (11.2+) | The value of one member type, optionally declared as `{"dataType":"GPString","value":"…"}`. Undeclared values are tried in member order. | Output is reported as the concrete type, never as `GPComposite` |
| **GPSpatialReference / GPCoordinateSystem** (11.4+, Pro 3.4) | `{"wkid":102100,"vcsWkid":5621}`, `{"latestWkid":3857}`, `{"wkt":…}`, `{"wkt2":…}`, `{"name":"World_Sinusoidal"}`, `{"projectionFile": url}`, `{"projectionFileItemID": …}`. The first valid form wins. | `wkid` (+`vcsWkid`), or WKT when custom |
| GPExtent / GPEnvelope (11.4+) | `{xmin,ymin,xmax,ymax[,zmin,zmax,mmin,mmax], "spatial_reference": {...}}`. (The doc example spells it `spatial_reference`. Whether `spatialReference` is also accepted: **UNCONFIRMED**.) | Same |
| GPStringHidden, GPSQLExpression (11.4+), GPTimeUnit (12.0+, `{"time":3,"units":"esriTimeUnitsYears"}`) | as documented | |

---

## 3. The portal's Spatial Analysis Service

### 3.1 Discovery

- `GET <portal>/sharing/rest/portals/self?f=json` → `helperServices.analysis.url`. The documented
  example value is `https://organization.example.com/<context>/rest/services/System/SpatialAnalysisTools/GPServer`
  [SELF]. Description: "analysis—Hosted geoprocessing services that perform analyses" [SELF].
- Esri's own REST walk-through does exactly this: generateToken → `portals/self` →
  `helperServices.analysis.url` → `<analysisUrl>/<Task>/submitJob` → poll
  `<taskUrl>/jobs/<jobId>?f=json` every 5 s → for each `results[k].paramUrl` GET
  `<taskUrl>/jobs/<jobId>/<paramUrl>?f=json` [SA-PROG]. It notes that the analysis URL "is not static and
  can change based on the user" [SA-PROG].
- Related `helperServices` members [SELF]: `creditEstimation` ("geoprocessing service for estimating
  credits to be charged to perform a certain task"), `geometry`, `elevationSync`, `rasterAnalytics`,
  `asyncRoute`, `asyncServiceArea`, `geoenrichment`, `packaging`, `printTask`, …. Portal self also has
  `analysisLayersGroupQuery` (the group behind the Analysis Layers gallery) [SELF].
- **Estimate / GetAnalysisUnits:** no public REST page for either was found. The Python API exposes
  `estimate=True` on every analysis function, which returns an `EstimateResult` with `cost` and
  `maximum_cost` [PY-PROX]. Its wire request is **UNCONFIRMED**. A community guide (not Esri
  documentation) describes an undocumented `.../tasks/GPServer/exts/Estimate/<OPERATION>`; treat it as
  **UNCONFIRMED**. It is credit-only and irrelevant to an on-premises server.

### 3.2 Common pattern [SA-ENT]

- **All tasks run asynchronously**: submitJob → jobId → jobs → results.
- Feature inputs are a feature/map service layer `{"url":…, "serviceToken":…, "filter":…}` or a
  feature collection `{"layerDefinition":{…},"featureSet":{…}}` [SA-FIN]. The URL "must be a public URL,
  not a URL that is only accessible behind a firewall" (that is, reachable by the analysis server) [SA-FIN].
- Outputs: with `outputName` the result is a hosted feature service and the result value is the
  **layer URL**. Without it the result is a **feature collection** [SA-FOUT]. Tables (e.g. groupSummary)
  are feature collections without geometry [SA-FOUT].
- `context`: `{"extent": {xmin,ymin,xmax,ymax,spatialReference}, "outSR": {"wkid": …}}`. The extent
  *selects* features that overlap it and does not clip them [SA-CTX]. The Python API also puts
  `"overwrite": true` in `context` (ArcGIS Online, or Enterprise 10.9.1+) [PY-PROX].
- `outputName` forms [BUF]:

  ```json
  {"serviceProperties": {"name": "featureServiceName"}}
  {"itemProperties": {"itemId": "52d34e7598e7484188228xxxxxx", "overwrite": true}}
  {"serviceProperties": {"serviceURL": "https://organization.example.com/<context>/rest/services/Hosted/FeatureServiceName/FeatureServer"}, "itemProperties": {"overwrite": true}}
  {"serviceProperties": {"name": "featureServiceName"}, "itemProperties": {"overwrite": true}}
  ```

  To overwrite, the caller must own the service or be an admin. The name and spatial reference must be
  kept. A service with dependent views cannot be overwritten. CreateRouteLayers and ExtractData do not
  support overwrite [SA-FOUT]. Esri's Python REST sample sends
  `"outputName": {"serviceProperties": {"name": output_service}}` [SA-PROG]. **The `itemProperties.folderId`
  member the brief asks about does not appear in any analysis-task page read: UNCONFIRMED.** Where the
  hosted output lands (the root folder, or a folder chosen in Map Viewer) is not documented on these pages.
- `f` = `html` (default) | `json`.
- Result value: `{"paramName":"<name>","dataType":"GPString","value":{"url":"<hosted feature service layer url>"}}`
  or `…"value":{"layerDefinition": {}, "featureSet": {}}` [BUF][DIS][OVL][AGG][MRG][SUMW].
  **The documented value carries only `url`, not `itemId`**, except ExtractData (below). Whether real
  servers also return `itemId`: **UNCONFIRMED**.
- What the analysis task resources themselves report as `dataType` for inputs such as `inputLayer`
  (`?f=json` on the task) is **not documented: UNCONFIRMED**. Capture it from a real Enterprise 11.x
  `SpatialAnalysisTools/GPServer/CreateBuffers?f=json` before claiming compatibility.

### 3.3 Task names [SA-TASKS]

- Summarize: `AggregatePoints`, `JoinFeatures`, `SummarizeCenterAndDispersion`, `SummarizeWithin`, `SummarizeNearby`.
- Find locations: `ChooseBestFacilities`, `CreateViewshed`, `CreateWatersheds`, `DeriveNewLocations`,
  `FindCentroids`, `FindExistingLocations`, `FindSimilarLocations`, `TraceDownstream`.
- Enrich: `EnrichLayer`.
- Patterns: `CalculateCompositeIndex`, `CalculateDensity`, `EightyTwentyAnalysis`, `FindHotSpots`,
  `FindOutliers`, `FindPointClusters`, `InterpolatePoints`.
- Proximity: `ConnectOriginsToDestinations`, `CreateBuffers`, `CreateDriveTimeAreas`,
  `CreateThresholdAreas`, `FindNearest`, `PlanRoutes`.
- Manage data: `ExtractData`, `DissolveBoundaries`, `FieldCalculator` (no longer supported in ArcGIS
  Online since March 2021), `GenerateTessellations`, `MergeLayers`, `OverlayLayers`.
- `CreateRouteLayers` also exists [SA-FOUT].

Tasks with no extra server or utility service in Enterprise: Aggregate Points, Join Features,
Summarize Within, Summarize Center and Dispersion, Find Centroids, Find Similar Locations, Locate
Regions, Calculate Composite Index, Find Point Clusters, Create Buffers, and others. Network tools need
Network Analysis plus routing utility services. Viewshed and watersheds need the Esri elevation and
hydrology services. Calculate Density and Interpolate Points need GIS Server Advanced [MVLIC]. **For a
Graticula first cut, the self-contained set is CreateBuffers, DissolveBoundaries, OverlayLayers,
MergeLayers, AggregatePoints, SummarizeWithin, JoinFeatures, FindCentroids, ExtractData,
GenerateTessellations, and FindNearest with `measurementType` StraightLine** (inference from [MVLIC] and
[NEAR], not stated by Esri as a set).

### 3.4 CreateBuffers — `<analysisUrl>/CreateBuffers/submitJob` [BUF]

| Param | Req | Type / values | Default |
|---|---|---|---|
| `inputLayer` | required | feature (`{"url","filter"}` or feature collection) | |
| `distances` | one of distances/field | double array, e.g. `[4.0, 5.0, 6.0]` | |
| `field` | one of distances/field | field name; one distance per feature | |
| `units` | optional | `Meters \| Kilometers \| Feet \| Miles \| NauticalMiles \| Yards` | "Miles or Kilometers", depending on the user profile's unit setting (the summary table says Meters; Python defaults to `'Meters'` [PY-PROX]) |
| `dissolveType` | optional | `None \| Dissolve` | `None` |
| `ringType` | optional | `Disks \| Rings` | `Disks` |
| `sideType` | optional | lines: `Full \| Right \| Left` (default `Full`); polygons: no value \| `Outside`; single distance only | |
| `endType` | optional | `Round \| Flat`; lines only, single distance only | `Round` |
| `outputName`, `context`, `f` | optional | see §3.2 | |

Result: `jobs/<jobId>/results/bufferLayer`. Fields: the input fields, `BUFF_DIST` (not present when
`field` is used), `ORIG_FID`, and `AnalysisArea` (square miles or square kilometers, following the unit
system). Which fields appear also depends on `dissolveType` [BUF]. Licensing: Creator, Professional or
Professional Plus user type; Publisher, Facilitator or Administrator role [BUF].

### 3.5 DissolveBoundaries [DIS]

| Param | Notes |
|---|---|
| `inputLayer` (required) | polygons |
| `dissolveFields` | string array, e.g. `["State_Name","District"]`. Empty or absent dissolves adjacent and overlapping polygons into one. Values are compared case-sensitively. Output always has a `Count` field. |
| `summaryFields` | `["fieldName summaryType", …]`, types `Sum \| Mean \| Min \| Max \| Stddev` (only Min and Max on date fields), e.g. `["Annual_Sales Sum","Annual_Sales Min"]` |
| `multiPartFeatures` | Boolean, true or false (default not stated: **UNCONFIRMED**) |
| `outputName`, `context`, `f` | §3.2 |

Result param: `dissolvedLayer`.

### 3.6 OverlayLayers [OVL]

| Param | Notes |
|---|---|
| `inputLayer`, `overlayLayer` (required) | features |
| `overlayType` | `Intersect` (default) \| `Union` (polygons on both sides only) \| `Erase` |
| `outputType` | Intersect only: `Input` (default; the lowest input dimension) \| `Line` \| `Point` (multipoint for line/polygon inputs) |
| `snapToInput` | Boolean, default false |
| `tolerance` | double, in inputLayer units |
| `outputName`, `context`, `f` | |

Result param: `outputLayer`.

### 3.7 AggregatePoints [AGG]

| Param | Notes |
|---|---|
| `pointLayer` (required) | points |
| `polygonLayer` | required unless `binType` is given |
| `keepBoundariesWithNoPoints` | Boolean, default `true` |
| `summaryFields` | `["Annual_Sales Sum","Annual_Sales Mean"]`. Sum, Mean and Stddev are not allowed on date fields. |
| `groupByField` | adds the `groupSummary` table output |
| `minorityMajority`, `percentPoints` | Boolean, default false, only with groupByField |
| `binType` | `Square` (default) \| `Hexagon`. Square: the size is the side. Hexagon: the size is the distance between parallel sides. |
| `binSize` | double, required with binType |
| `binSizeUnit` | `Meters` (default) \| `Kilometers \| Feet \| Miles \| NauticalMiles \| Yards` |
| `outputName`, `context`, `f` | |

Results: `aggregatedLayer` (polygons; input polygon fields, `Point_Count`, `Sum_<f>`/`Mean_<f>`…), and
`groupSummary` (table: `Join_ID`, the group field, `Point_Count_<group>`, statistics) [AGG].

### 3.8 MergeLayers [MRG]

`inputLayer`, `mergeLayer` (required; same geometry type). `mergingAttributes`: an array of
`"<field> Remove"`, `"<field> Rename <newName>"` and `"<field> Match <inputField>"` entries, e.g.
`['Temp Remove', 'RiskRate Rename RiskRateJan', 'AirQualityIndex Match AQI']`. Match type-casts, except
string to numeric. By default all fields from both inputs are kept and missing values are null.
Then `outputName`, `context`, `f`. Result: `mergedLayer`.

### 3.9 ExtractData [EXT]

`inputLayers` (required; **an array** of layer references or feature collections). Hosted layers can be
extracted only by the owner, an admin, or when the service's extract capability is enabled. `extent`
(an optional layer). `clip` (Boolean, default false). `dataFormat` = `FILEGEODATABASE | SHAPEFILE | KML | CSV`,
default `CSV`. `outputName` is **item properties, not service properties**:

```json
{"itemProperties": {"title": "<title>", "tags": "<tags>", "snippet": "<snippet>", "description": "<description>"}}
```

Result `contentID`: `{"paramName":"contentID","dataType":"GPString","value":{"itemId":"<item ID>","url":"<url to item>"}}`.
The item holds a .zip (FGDB or shapefile), a .csv (or a .zip of CSVs for several layers), or a .kml/.kmz [EXT].
Map Viewer does not add the result to the map; it is saved as an item in My content [MV].

### 3.10 Others, names only

- SummarizeWithin [SUMW]: `sumWithinLayer`, `summaryLayer`, `sumShape`, `shapeUnits`, `summaryFields`
  (required if sumShape is false), `groupByField`, `minorityMajority`, `percentShape`, `binType`,
  `binSize`, `binSizeUnit`, `outputName`, `context`. Results `resultLayer` and `groupBySummary`.
- JoinFeatures [JOIN]: `targetLayer`, `joinLayer`, `spatialRelationship`,
  `spatialRelationshipDistance`, `spatialRelationshipDistanceUnits`, `attributeRelationship`,
  `joinOperation`, `summaryFields`, `recordsToMatch`, `joinType`, `outputName`, `context`. Result `outputLayer`.
- FindNearest [NEAR]: `analysisLayer`, `nearLayer`, `measurementType` (required), `maxCount`,
  `searchCutoff`, `searchCutoffUnits`, `timeOfDay`, `timeZoneForTimeOfDay`, the barrier layers,
  `includeRouteLayers`. Results `nearestLayer` and `connectingLinesLayer`.

---

## 4. Which clients call what

### 4.1 ArcGIS API for Python

- `arcgis.features.analysis` re-exports every standard analysis tool (`create_buffers`,
  `dissolve_boundaries`, `overlay_layers`, `aggregate_points`, `merge_layers`, `extract_data`,
  `summarize_within`, `join_features`, `find_nearest`, …) [PY-ANAL].
- `arcgis.features.use_proximity.create_buffers(input_layer, distances=[], field=None, units='Meters',
  dissolve_type='None', ring_type='Disks', side_type='Full', end_type='Round', output_name=None,
  context=None, gis=None, estimate=False, future=False)` [PY-PROX]. These map one-to-one onto the REST
  names in §3.4. `output_name` is a string or an existing FeatureLayer ("Existing feature layer will
  cause the new layer to be appended to the Feature Service"). `context` takes `extent`, `outSR`, and
  `overwrite`. The function returns a FeatureLayer when `output_name` is given and a FeatureCollection
  otherwise. `future=True` returns a job object instead of waiting [PY-PROX].
- `input_layer` accepts an Item (its first layer), FeatureLayer, FeatureLayerCollection,
  FeatureCollection, a service URL string, or a featureCollection dict [PY-ANAL].
- **Which URL it calls:** the reference pages do not say. Esri's REST guide resolves the URL through
  `portals/self` → `helperServices.analysis.url` [SA-PROG], and it is the natural assumption that the
  Python API does the same. **UNCONFIRMED from the Python docs themselves.**
- Generic GP: `arcgis.geoprocessing.import_toolbox(url_or_item, gis=None)` builds a Python module from
  any GPServer URL or Geoprocessing Service item. It reads the service and task JSON from §1.1–1.2, so
  those resources must be faithful [PY-GP]. Helper types: `DataFile(url, item_id, portal_item)`,
  `LinearUnit(distance, units)` (`from_str('5 miles')`), `RasterData(url, format, item_id)`, plus
  `GPService`, `GPTask`, `GPJob` classes [PY-GP].

### 4.2 ArcGIS Maps SDK for JavaScript — `@arcgis/core/rest/geoprocessor.js` [JS-GP][JS-JOB][JS-OPT]

- `execute(url, params?, options?: GPOptions, requestOptions?) → Promise<{messages: GPMessage[], results: ParameterValue[]}>`
  is for synchronous tasks. `url` is the **task** URL. `params` are keyed by task parameter name.
- `submitJob(url, params?, options?, requestOptions?) → Promise<JobInfo>` for async.
- `JobInfo`: `jobId`, `jobStatus` (strings such as `"job-executing"`, so the SDK maps the `esriJob*`
  values), `messages`, `progress` (`{type,message,percent}`), `sourceUrl`. Methods:
  `checkJobStatus()`, `waitForJobCompletion({interval, statusCallback})`, `cancelJob()`,
  `fetchResultData(resultName, gpOptions?)` (→ `jobs/<id>/results/<name>`), `fetchResultImage`,
  `fetchResultMapImageLayer`, `destroy()` [JS-JOB].
- `GPOptions`: `outSpatialReference`, `processSpatialReference`, `processExtent`, `returnZ`,
  `returnM`, `returnFeatureCollection` (4.31+), `returnColumnName` (4.29+) [JS-OPT]. These become
  `context` / `env:` parameters and the `return*` flags. **If `processExtent` is not set, requests use the
  pre-10.6.1 format** [JS-GP].

### 4.3 Map Viewer Analysis pane

- Prerequisites stated by Esri [MV]: signed in. If the Analysis button is missing, "Your organization
  may not be configured or you may not have privileges to perform spatial analysis, or your ArcGIS
  organization may not have any hosted feature layers (required to enable analysis)."
- Enterprise configuration [CFG]: a base deployment whose **hosting server** (with ArcGIS Data Store,
  not an enterprise geodatabase) processes requests and stores results; members granted the analysis
  privileges; optional utility services. "If the necessary utility services for a tool are not
  configured, the tool will not be visible in the portal user interface. The tool will also not be
  visible if a portal member does not have the correct permissions."
- Inputs Map Viewer accepts: feature services, hosted feature layers, map service layers, sketch
  layers, table layers [MV]. The default maximum run time is 6,000 s (the SpatialAnalysisTools service
  property "maximum time a client can use a service") [MV]. Output names cannot contain
  `[ ] ( ) - < > # % : ; " ? & + = | / \ * @` or start or end with a space [MV].
- **Exactly which portal members Map Viewer reads to decide that the button is shown** (for example
  `helperServices.analysis`, `user.privileges`, the presence of a hosting server in
  `portals/self/servers`, or a hosted-layer search) **is UNCONFIRMED**. Esri points to a community FAQ.
  It must be measured against Map Viewer itself.

### 4.4 ArcGIS Pro

- **Direct server connection**: Insert > Connections > New ArcGIS Server takes
  `https://host/<context>/services`, "usually not viewable in a web browser because it serves SOAP
  requests only. Do not provide the REST URL" [PRO-WT]. Feature and raster outputs are copied into the
  project's default geodatabase [PRO-WT]. A REST-only GPServer will therefore **not** be usable through
  this path, unless Pro has a REST fallback, which is **UNCONFIRMED**.
- **Web tools via the portal**: Catalog > Portal, search, double-click the toolbox [PRO-WT]. These are
  portal items of type Geoprocessing Service. The item and REST calls Pro makes are **UNCONFIRMED**.
- **Portal tools (Standard Feature Analysis)**: the Analysis ribbon (Portal group) or the
  Geoprocessing pane's Portal tab. Available only with an active **ArcGIS Enterprise** portal that "has a
  hosting server configured for the Feature Analysis - Standard Tools setting", plus spatial analysis
  privileges. They do not work with ArcGIS Online. The tools run on the federated server and create
  output layers in the portal [PRO-PT]. How Pro detects that setting is **UNCONFIRMED**.
- "Ready To Use" tools: the help page URLs tried returned 404 after the move to doc.esri.com and were
  not researched further (**UNCONFIRMED**). They are Esri-hosted ArcGIS Online services and are not
  relevant to a self-hosted server.

---

## 5. Privileges and credits

- Privilege strings [PRIV]:
  - `premium:user:spatialanalysis`: "Grants the ability to perform spatial analysis tasks."
  - `portal:user:createItem`: create, edit and delete own content.
  - `portal:publisher:publishFeatures`: publish hosted feature layers.
  - Others that some tools need: `premium:user:networkanalysis`, `premium:user:geoenrichment`,
    `premium:user:demographics`, `premium:user:geocode`, `premium:publisher:rasteranalysis`.
  - `portal:user:runWebTool`: run web tools published from notebooks.
  - `portal:publisher:publishServerGPServices`: appears in the privilege list; its description was not read.
- Analysis requires Spatial Analysis. Hosted-feature-service output additionally requires "Create,
  update, and delete content" and "Publish hosted features". These come with a Creator, Professional or
  Professional Plus user type and a Publisher or Administrator role. Without them "the Spatial Analysis
  toolbox will not be available" [SA-ENT][MVLIC]. Network, GeoEnrichment and similar extra privileges
  are listed per task [SA-ENT].
- Credits (ArcGIS Online only): transaction-based tools are charged by records processed and output
  generated. Map Viewer can estimate the cost before running. Credit budgeting blocks a job whose
  estimate exceeds the allocation [CREDITS]. The portal exposes `helperServices.creditEstimation` [SELF],
  and Python exposes `estimate=True` [PY-PROX]. An on-premises Enterprise has no credits for these tools
  [SA-ENT says credits apply "in ArcGIS Online Map Viewer"].

---

## 6. UNCONFIRMED list (to be measured before claiming compatibility)

1. The task JSON (`dataType` per parameter, `choiceList`s, defaults) of the real
   `System/SpatialAnalysisTools/GPServer/<Task>?f=json`.
2. Whether analysis results also carry `itemId` next to `url`, and the `itemProperties.folderId`
   member in `outputName`.
3. `env:processSR` as a literal legacy parameter.
4. What Map Viewer checks before showing the Analysis button, and which tasks it lists.
5. How Pro discovers and calls portal tools and web tools over REST, and whether a direct ArcGIS Server
   connection can work without the SOAP `/services` endpoint.
6. The Python API's URL resolution, and the wire form of `estimate=True`, GetAnalysisUnits and Estimate.
7. A job delete operation, and the literal `dataType` string for Field parameters in task JSON.
8. The default of `multiPartFeatures` in DissolveBoundaries.
