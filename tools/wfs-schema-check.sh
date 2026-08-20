#!/usr/bin/env bash
# Validates this server's WFS documents against the schemas OGC publishes.
#
# ADR-039 condition 5: "GML output is validated against the published schemas by
# something that is not us." Our own tests assert that the writers produce what we
# think the specification says; this asserts that the specification agrees. The two
# are different claims and only the second one can find a misreading.
#
# It found two on its first run — ows:ServiceProvider missing its required
# ServiceContact, and the Filter Encoding capability elements spelled without their
# underscores (fes:IdCapabilities where the schema says fes:Id_Capabilities). Both
# were invisible to every test we had written, because every test we had written
# was written from the same misreading as the code.
#
#   bash tools/wfs-schema-check.sh [base-url] [type-name ...]
#
# Defaults to https://127.0.0.1:8443 and to whatever the capabilities advertise.
#
# Needs xmllint and outbound access to schemas.opengis.net over **http**: the
# mingw build of xmllint has no TLS, so https schema locations fail to load with a
# message that reads like a missing file. That is a property of the tool, not of
# the server.

set -u

BASE="${1:-https://127.0.0.1:8443}"
shift 2>/dev/null || true

WFS="$BASE/wfs"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

WFS_XSD="http://schemas.opengis.net/wfs/2.0/wfs.xsd"
OWS_XSD="http://schemas.opengis.net/ows/1.1.0/owsExceptionReport.xsd"
XSD_XSD="http://www.w3.org/2001/XMLSchema.xsd"

failures=0

check() {
  local label="$1" file="$2" schema="$3"
  local out

  out="$(xmllint --noout --schema "$schema" "$file" 2>&1)"

  if printf '%s' "$out" | grep -q "validates"; then
    printf '  ok    %s\n' "$label"
  else
    printf '  FAIL  %s\n' "$label"
    printf '%s\n' "$out" | grep -v "^$file validates" | sed 's/^/          /' | head -8
    failures=$((failures + 1))
  fi
}

fetch() {
  curl -sk "$WFS?service=WFS&version=2.0.0&$1" -o "$2"
}

echo "server: $BASE"

# ---- the documents that stand on their own ----

curl -sk "$WFS?service=WFS&request=GetCapabilities" -o "$WORK/caps.xml"
check "GetCapabilities" "$WORK/caps.xml" "$WFS_XSD"

fetch "request=ListStoredQueries" "$WORK/stored.xml"
check "ListStoredQueries" "$WORK/stored.xml" "$WFS_XSD"

fetch "request=DescribeStoredQueries" "$WORK/storeddesc.xml"
check "DescribeStoredQueries" "$WORK/storeddesc.xml" "$WFS_XSD"

# A refusal is a document too, and it is the one a client sees when something is
# wrong — so it is the one least likely to have been looked at.
curl -sk "$WFS?service=WFS&version=1.1.0&request=GetFeature&typeNames=nothing" \
  -o "$WORK/fault.xml"
check "ows:ExceptionReport" "$WORK/fault.xml" "$OWS_XSD"

# ---- one feature type at a time ----

if [ "$#" -gt 0 ]; then
  types="$*"
else
  types="$(grep -oE '<wfs:Name>[^<]+' "$WORK/caps.xml" | sed 's/<wfs:Name>//')"
fi

if [ -z "$types" ]; then
  echo "  (no feature types advertised — nothing else to check)"
fi

for type in $types; do
  safe="$(printf '%s' "$type" | tr ':/' '__')"

  fetch "request=DescribeFeatureType&typeNames=$type" "$WORK/$safe.xsd"
  check "DescribeFeatureType $type" "$WORK/$safe.xsd" "$XSD_XSD"

  fetch "request=GetFeature&typeNames=$type&count=3" "$WORK/$safe.xml"

  # <b>A feature collection cannot be checked against wfs.xsd alone.</b> Its
  # members are in this server's own application namespace, so the check needs
  # both schemas — which is also a test of DescribeFeatureType: if the schema this
  # server publishes does not describe the features it serves, this fails.
  ns="$(grep -oE 'targetNamespace="[^"]+"' "$WORK/$safe.xsd" | head -1 | sed 's/targetNamespace="//;s/"//')"

  cat > "$WORK/wrap_$safe.xsd" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <xsd:import namespace="http://www.opengis.net/wfs/2.0" schemaLocation="$WFS_XSD"/>
  <xsd:import namespace="$ns" schemaLocation="$safe.xsd"/>
</xsd:schema>
EOF

  check "GetFeature $type" "$WORK/$safe.xml" "$WORK/wrap_$safe.xsd"
done

echo
if [ "$failures" -eq 0 ]; then
  echo "every document validates against the published schemas"
  exit 0
fi

echo "$failures document(s) failed validation"
exit 1
