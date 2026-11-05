# 3D parity with ArcGIS Online and ArcGIS Enterprise

**Status: in progress, 2026-11-05.** ADR-270 condition 3. The owner asked for 3D *"aynı arcgis enterprise'da olduğu
gibi"*; this document is where that is checked item by item — what ArcGIS offers, what Graticula has, and for each gap
whether it is built, owed, or the owner's to decide. The column *Source* names the public documentation each row was
read from; a row without one is from this repository only and has not yet been compared.

The comparison against the published ArcGIS documentation is the night's last task and has not been run yet. Until it
has, the table below lists what was built tonight and nothing is claimed to be at parity.

| Area | ArcGIS | Graticula | State | Source |
|---|---|---|---|---|
| Scene Viewer: layers | visibility, order, opacity, remove, zoom to | the same (ADR-270 §5.1) | Built | — |
| Scene Viewer: styles | many renderer types and the web style gallery | simple renderer, size and colour by field, primitive 3D shapes | Partly built | — |
| Scene Viewer: pop-ups | configured per layer, saved in the scene | title field and fields shown, saved | Built | — |
| Scene Viewer: slides | capture, edit, play | the same | Built | — |
| Scene Viewer: basemap and ground | gallery, elevation, ground transparency, underground navigation | the same | Built | — |
| Scene Viewer: analysis | measurement, line of sight, slice, shadow cast, daylight, weather, elevation profile | the same eight, the SDK's | Built | — |
| Hosted scene layer from a feature layer | 3D object and point scene layers, with an associated feature layer | the same two profiles; `Service2Service` relationship; Rebuild | Built | — |
| Scene layer cache | rebuild, and update of edited nodes | rebuild only | Partly built | — |
| Levels of detail for generated buildings | simplified parents | the 512 largest as hull prisms per inner node | Built | — |
| Hosting a building scene layer package | yes | served, drawn by the SDK (Esri's sample) | Built | — |
| Hosting integrated mesh / point cloud packages | yes | the paths are served; unmeasured — samples exceed the 64 MB document limit | Owed (D-386) | — |
| Hosting voxel packages | yes | stem lookup only; no sample | Unmeasured | — |
| Elevation layer from a DEM | a tiled elevation layer, its cache built ahead | an elevation image service (ADR-182), tiles drawn on request; offered as the ground by its keyword | Built | — |
| Local scenes | global or local, any projected system, clipping | global or local in Web Mercator, clipping | Partly built | — |
| Projected scene layers for local scenes | published in the data's projected system | written in WGS 84 only; refused in a local scene with the reason | Not built | — |
