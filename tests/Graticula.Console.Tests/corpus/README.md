# Console test corpus

`box.dae` and `box-ascii.fbx` are one 10 × 20 × 4 m red box. It was written by hand as OBJ and converted by Assimp's
command-line tool (Debian `assimp-utils` 5.2.5, 2026-11-07): `assimp export box.obj box.dae -fcollada` and
`assimp export box.obj box-ascii.fbx -ffbxa`. They are the inputs for the 3D object layer's conversion test
(ADR-282). Nothing in them is anyone else's model.

**Units.** Assimp's FBX writer declares `UnitScaleFactor` 1 — centimetres — whatever the numbers were, so
`box-ascii.fbx` is, by its own declaration, a 10 × 20 × 4 *cm* box, and since ADR-282 §5.5 (owner decision 2026-11-20)
it converts to a 0.1 × 0.2 × 0.04 m glb. `box-ascii-metres.fbx` is the same file with `UnitScaleFactor` and
`OriginalUnitScaleFactor` changed to 100 (metres) and nothing else, and converts to 10 × 20 × 4 m. The two are the
inputs of `AnFbxIsConvertedToMetresTests` (Host.Tests).

`wall.ifc` is one 4 × 0.2 × 3 m wall (IFC4, an `IfcWall` with a swept rectangle), written by hand on 2026-11-07. It
is the input for IFC conversion by IfcConvert (ADR-283 condition 2).

`box.usda` is one 2 × 1 × 3 m box, written by hand on 2026-11-07, in centimetres with Z up and moved 1 m along X, so a
conversion that ignores the stage's units, its up axis or a transform gives the wrong box. `box.usdz` is the same file
in a USDZ package: stored, not compressed, with its data aligned to 64 bytes, as USDZ requires. These are the inputs
for USD conversion by usd2glb (ADR-283 condition 3). Converted, the box spans 1 to 3 m in x, 0 to 3 m up and -1 to
0 m in z.
