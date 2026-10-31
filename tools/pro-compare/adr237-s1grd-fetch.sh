#!/bin/sh
# Fetch S1D_IW_GRDH_1SDV_20261031T054608 from Planetary Computer (anonymous SAS token) into a SAFE with ESA's names.
set -e
cd "${1:-.}"   # the work folder; token.txt holds an anonymous SAS token from planetarycomputer.microsoft.com/api/sas/v1/token/sentinel-1-grd
TOK=$(cat token.txt)
B="https://sentinel1euwest.blob.core.windows.net/s1-grd/GRD/2026/10/31/IW/DV/S1D_IW_GRDH_1SDV_20261031T054608_20261031T054640_005255_009FC5_F81C"
S=S1D_IW_GRDH_1SDV_20261031T054608_20261031T054640_005255_009FC5_F81C.SAFE
mkdir -p $S/annotation/calibration $S/annotation/rfi $S/measurement $S/preview/icons $S/support
get() { curl -s --retry 3 -m 3600 -o "$S/$2" "$B/$1?$TOK"; }
get manifest.safe manifest.safe
for x in calibration measurement noise product quicklook rfi; do get support/s1-level-1-$x.xsd support/s1-level-1-$x.xsd; done
get support/s1-map-overlay.xsd support/s1-map-overlay.xsd
get support/s1-object-types.xsd support/s1-object-types.xsd
get support/s1-product-preview.xsd support/s1-product-preview.xsd
for f in map-overlay.kml product-preview.html quick-look.png thumbnail.png icons/logo.png; do get preview/$f preview/$f; done
for pol in vh:002 vv:001; do p=${pol%:*}; n=${pol#*:}; base=s1d-iw-grd-$p-20261031t054608-20261031t054640-005255-009fc5-$n
  get annotation/iw-$p.xml annotation/$base.xml
  get annotation/calibration/calibration-iw-$p.xml annotation/calibration/calibration-$base.xml
  get annotation/calibration/noise-iw-$p.xml annotation/calibration/noise-$base.xml
  get annotation/rfi/rfi-iw-$p.xml annotation/rfi/rfi-$base.xml
done
get measurement/iw-vv.tiff measurement/s1d-iw-grd-vv-20261031t054608-20261031t054640-005255-009fc5-001.tiff
get measurement/iw-vh.tiff measurement/s1d-iw-grd-vh-20261031t054608-20261031t054640-005255-009fc5-002.tiff
echo done
