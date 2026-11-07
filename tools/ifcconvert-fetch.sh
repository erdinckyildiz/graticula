#!/bin/sh
# IfcOpenShell's IfcConvert (LGPL-3.0-or-later) for a 3D object layer's IFC models — ADR-283 §5.2.
#
#   sh tools/ifcconvert-fetch.sh <amd64|arm64> <directory>
#
# Downloads the pinned release for one architecture, refuses it unless its SHA-256 is the one below, and lays out
# IfcConvert with only the files that turning IFC into glb needs (KEEP). Everything else in the release is left out:
#   - the two CGAL geometry kernels, whose 3D packages are GPL-3.0-or-later, which ADR-254 keeps out of what ships;
#   - GMP and MPFR, which only CGAL uses;
#   - the other writers (OBJ, COLLADA, SVG, TTL, IGES, STEP), the document serialisers, and the Open CASCADE
#     libraries that need OpenGL, none of which a conversion to glb loads.
# The 147 MB release becomes 74 MB, measured to convert our own wall (ADR-283 §4) with nothing missing. IfcConvert uses
# its Open CASCADE kernel by default. Needs curl and python3; the libraries need libsqlite3-0 on the machine.
#
# One script for the image (deploy/server.Dockerfile), CI and the dev image, so the version and hashes are in one place.
set -eu

VERSION=0.9.0post1
case "${1:-}" in
  amd64) ZIP="ifcconvert-$VERSION-linux64.zip";    SHA=7360c7dac5b69ec722b4bf100036c2ab2f96b74fa89ee99d33944e6cbf3ba9d6 ;;
  arm64) ZIP="ifcconvert-$VERSION-linuxarm64.zip"; SHA=0f0be070f3cdcaee74059bea5e5301a33cca30b19a73608924b86c0b4b76db3f ;;
  *) echo "usage: ifcconvert-fetch.sh <amd64|arm64> <directory>" >&2; exit 2 ;;
esac
DEST="${2:?usage: ifcconvert-fetch.sh <amd64|arm64> <directory>}"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
curl -fsSL -o "$work/$ZIP" "https://github.com/IfcOpenShell/IfcOpenShell/releases/download/ifcconvert-$VERSION/$ZIP"
echo "$SHA  $work/$ZIP" | sha256sum -c - >/dev/null || { echo "$ZIP is not the pinned file" >&2; exit 1; }

KEEP="IfcConvert libboost_program_options.so.1.86.0 libmanifold.so.3
  libifcopenshell.parse.so libifcopenshell.geometry.so libifcopenshell.geometry.writer.so libifcopenshell.plugin.so
  ifcopenshell_geometry_glb.so ifcopenshell_geometry_kernel_opencascade.so ifcopenshell_geometry_kernel_manifold.so
  ifcopenshell_geometry_kernel_passthrough.so ifcopenshell_geometry_tree_opencascade_brep.so
  ifcopenshell_geometry_tree_opencascade_trianglebvh.so
  ifcopenshell_geometry_mapping_ifc2x3.so ifcopenshell_geometry_mapping_ifc4.so ifcopenshell_geometry_mapping_ifc4x3_add2.so
  ifcopenshell_geometry_writer_ifc2x3.so ifcopenshell_geometry_writer_ifc4.so ifcopenshell_geometry_writer_ifc4x3_add2.so
  ifcopenshell_parse_schema_ifc2x3.so ifcopenshell_parse_schema_ifc4.so ifcopenshell_parse_schema_ifc4x3_add2.so
  libTKernel.so.7.8 libTKMath.so.7.8 libTKG2d.so.7.8 libTKG3d.so.7.8 libTKGeomBase.so.7.8 libTKBRep.so.7.8
  libTKGeomAlgo.so.7.8 libTKTopAlgo.so.7.8 libTKPrim.so.7.8 libTKBO.so.7.8 libTKBool.so.7.8 libTKShHealing.so.7.8
  libTKMesh.so.7.8 libTKFillet.so.7.8 libTKOffset.so.7.8"

python3 -I -m zipfile -e "$work/$ZIP" "$work/all"
mkdir -p "$DEST"
for f in $KEEP; do
  test -f "$work/all/$f" || { echo "$ZIP has no $f" >&2; exit 1; }
  cp "$work/all/$f" "$DEST/$f"
done
chmod 755 "$DEST/IfcConvert"

# The LGPL-3.0 travels with what it covers: its own text and the GPL-3.0 it is written as additions to, from the same
# tag, pinned as the release is.
for pair in "COPYING 1b3782ccad7b8614100cda30d3faf42fc39f2e97932908c543005053b654ca68" \
            "COPYING.LESSER e3a994d82e644b03a792a930f574002658412f62407f5fee083f2555c5f23118"; do
  set -- $pair
  curl -fsSL -o "$DEST/LICENSE-IfcOpenShell-$1.txt" "https://raw.githubusercontent.com/IfcOpenShell/IfcOpenShell/ifcconvert-$VERSION/$1"
  echo "$2  $DEST/LICENSE-IfcOpenShell-$1.txt" | sha256sum -c - >/dev/null || { echo "IfcOpenShell's $1 is not the pinned text" >&2; exit 1; }
done
