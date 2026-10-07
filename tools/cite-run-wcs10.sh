#!/bin/sh
# OGC's WCS 1.0.0 suite, run through TEAM Engine's console rather than its web form — ADR-173.
#
#   sh tools/cite-run-wcs10.sh <wcs-address> <out-dir>
#
# ets-wcs10 is a CTL suite with no REST controller, so cite-run.sh's REST call has nothing to
# call, and its only front door is the web form. The image carries everything the console needs:
# the suite under te_base, and the web application's classes and libraries. They are copied out
# and run with `com.occamlab.te.Test -test=wcs1-0-0:main`, handing the form's fields as the main
# test's parameters — the form only ever filled them in. The one thing missing is Xerces, which
# the suite asks for by name and the image's Tomcat supplies; it is fetched from Maven Central.
set -eu

WCS=${1:?the WCS address, e.g. http://127.0.0.1:8445/wcs}
OUT=${2:?an output directory}
WORK=${RUNNER_TEMP:-/tmp}/ets-wcs10
WEB=/usr/local/tomcat/webapps/teamengine/WEB-INF

mkdir -p "$OUT"
rm -rf "$WORK"
mkdir -p "$WORK/lib"
docker pull -q ogccite/ets-wcs10 > /dev/null
c=$(docker create ogccite/ets-wcs10)
docker cp -q "$c:/root/te_base" "$WORK/te_base"
docker cp -q "$c:$WEB/classes" "$WORK/classes"
docker cp -q "$c:$WEB/lib/." "$WORK/lib"
docker rm "$c" > /dev/null
curl -sSfL -o "$WORK/lib/xercesImpl-2.12.2.jar" https://repo1.maven.org/maven2/xerces/xercesImpl/2.12.2/xercesImpl-2.12.2.jar
curl -sSfL -o "$WORK/lib/xml-apis-1.4.01.jar" https://repo1.maven.org/maven2/xml-apis/xml-apis/1.4.01/xml-apis-1.4.01.jar
mkdir -p "$WORK/te_base/logs"

# The form's own defaults, except the address and a resolution in degrees for a coverage in EPSG:4326.
# TE_BASE, the environment variable: the console reads it, not the te.base property.
TE_BASE="$WORK/te_base" java -Dte.base="$WORK/te_base" -cp "$WORK/classes:$WORK/lib/*" com.occamlab.te.Test -mode=test \
  -source=wcs/1.0.0/ctl -logdir=logs -session=s1 -test=wcs1-0-0:main \
  "@VAR_WCS_CAPABILITIES_URL=$WCS?service=WCS&version=1.0.0&request=GetCapabilities" \
  "@VAR_WCS_FORMAT_1_HEADER=image/tiff" "@VAR_HIGH_UPDATESEQUENCE=1" "@VAR_LOW_UPDATESEQUENCE=-1" \
  "@VAR_WCS_COVERAGE_1_RESX=0.01" "@VAR_WCS_COVERAGE_1_RESY=0.01" \
  "@xml=xml" "@haveparameter=haveparameter" "@VAR_MULTIFILE_FORMATS=image/tif" \
  > "$OUT/wcs10.log" 2>&1 || true

if ! grep -q "^ *Test wcs1-0-0:main " "$OUT/wcs10.log"; then
  echo "the suite did not finish; its last words:" >&2
  tail -20 "$OUT/wcs10.log" >&2
  exit 1
fi

python tools/cite-count.py "$OUT/wcs10.log"
