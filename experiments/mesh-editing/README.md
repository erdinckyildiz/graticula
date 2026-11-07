# Mesh editing prototype (ADR-280 condition 1)

Disposable, and never promoted (CLAUDE.md §1). It measures what Maps SDK 5.2 asks of a server when it edits a 3D
object scene layer. It runs against a live Graticula server and fakes only the scene layer's associated feature layer.

- `stub.py` is an https reverse proxy in front of a Graticula server. It answers the portal and the SceneServer as the
  server does, and rewrites the scene layer's related feature layer to a fake multipatch layer kept in memory. It logs
  every request to `requests.log`.
- `proto.html` uses SDK 5.2. It loads the scene layer by its portal item, records what the SDK made of it, then adds a
  10 m × 20 m box mesh through `SceneLayer.applyEdits`, moves it, and deletes it. The report goes to `report.json`.
- `run.sh <https root> <token> <scene layer item id>` runs it once. It takes a throwaway certificate in `STUB_CERT`
  and `STUB_KEY`.

## What was measured (2026-11-07, SDK 5.2.0, fixture server)

**What the SDK needs before it offers mesh edits:**

1. **The scene layer's portal item** must relate to a feature service item (`Service2Service`, reverse). The SDK reads
   the related item's own record for its `url`, not the related-items list.
2. **That service lists no tables.** With the asset tables listed among the service's tables, the SDK loaded it as a
   group layer and found no associated layer. They stay in `infoFor3D.tables`.
3. **The layer is `esriGeometryMultiPatch`.** The SDK then makes it a `mesh` layer. It needs `infoFor3D` in the public
   sample's shape:
   - `supportedFormats` as `[code, mime, extensions, description]` rows;
   - full `assetMapFields` definitions;
   - `assetMapFieldRoles`;
   - `transformFieldRoles` including `globalID`.

   With a shorter shape it failed inside the SDK ("e is not iterable").
4. **The scene layer's own fields** must type its GlobalID as `esriFieldTypeGlobalID`. Graticula's writer types it
   `esriFieldTypeGUID`, and the SDK refused: "Layer does not specify a global id field". **The production writer has
   to change this for a 3D object layer.**
5. **On load the SDK calls `extractChanges`** on the feature service (`returnIdsOnly`, `layerServerGens` = the scene
   layer's `serviceUpdateTimeStamp.lastUpdate`, `fieldsToCompare` = the transform fields). This is how it finds
   features edited since the cache.

**The requests one add, move and delete sent:**

- **Add**:
  1. `GET FeatureServer/uploads/info`.
  2. `POST 0/uploadAssets` with `[{assetType:"3D_glb", assetData:<base64 glb>}]`.
  3. `POST 0/applyEdits` with `useGlobalIds=true` and `async=false`. `adds` carries attributes only: the client-made
     `globalid` and `ESRI3DO_OX/OY/OZ` = the mesh origin (longitude, latitude, metres), with T = 0, S = 1, axis
     (0, 1, 0) and angle 0. There is no geometry. `assetMaps.adds` is `[{globalId, parentGlobalId:<feature>,
     assetName:"{guid}.glb", assetHash:<sha256 of the glb>, flags:[]}]`.
- **Move** (a new mesh at another point): the model is uploaded again, then `applyEdits` `updates` sends the new origin
  attributes with a new `assetMaps.adds` entry.
- **Delete**: `applyEdits` with `deletes` = `["{globalid}"]`, by GlobalID because `useGlobalIds=true`.
- **Transform-only move** (`updateFeatures` with only `ESRI3DO_TX` changed): one `applyEdits` with `updates` carrying
  the transform attributes. No upload and no asset map.
- **After an add**, 15 seconds looking at the place from 80 m: the SDK sent no `query3D`, `queryAssets` or `query` for
  the new feature. It draws what it sent itself, or it does not draw it until the cache is rebuilt. Telling those apart
  needs a screenshot of the place, which this run did not take.

**Not measured yet:**

- whether the Editor widget offers the layer: on this bare page the widget drew nothing, with or without the layer;
- whether another client, opening the scene after the edit, asks for the edited feature (`extractChanges` says it
  changed, then `query3D`, presumably).
