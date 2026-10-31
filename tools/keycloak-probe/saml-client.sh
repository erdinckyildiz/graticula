#!/bin/sh
# Runs on the VPS: reads an SP's SAML metadata on stdin and makes the Keycloak client from it, through Keycloak's own
# client-description converter (the admin REST API; kcadm sends only JSON). The admin password is read from
# /opt/kc-probe/admin and never printed.
#
#   sh saml-client.sh            the client as the converter makes it
#   sh saml-client.sh encrypt    and Keycloak's "Encrypt assertions" on (ADR-090 condition 3)
#   sh saml-client.sh clear      and it off, whatever the metadata asked
set -eu
KC=http://127.0.0.1:18181
MODE=${1:-}
cat > /tmp/sp.xml
TOKEN=$(curl -s -d client_id=admin-cli -d username=admin --data-urlencode "password=$(cat /opt/kc-probe/admin)" \
  -d grant_type=password "$KC/realms/master/protocol/openid-connect/token" | python3 -c 'import sys,json;print(json.load(sys.stdin)["access_token"])')
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/xml" --data-binary @/tmp/sp.xml \
  "$KC/admin/realms/graticula/client-description-converter" > /tmp/client.json

# Keycloak's converter leaves "client signature required" on whatever AuthnRequestsSigned says; this SP signs no
# request (AuthnRequestsSigned="false"), as AD FS and Entra ID accept by default, so the probe turns it off, and
# replaces a client of the same id left by an earlier run.
MODE="$MODE" python3 - <<'PY'
import json, os
c = json.load(open("/tmp/client.json"))
a = c.setdefault("attributes", {})
a["saml.client.signature"] = "false"
print("converter: saml.encrypt", a.get("saml.encrypt", "unset"), "| encryption certificate",
      "present" if a.get("saml.encryption.certificate") else "absent",
      "| algorithms", {k: v for k, v in a.items() if k.startswith("saml.encryption.") and k != "saml.encryption.certificate"})
mode = os.environ.get("MODE")
if mode == "encrypt":
    a["saml.encrypt"] = "true"
elif mode == "clear":
    a["saml.encrypt"] = "false"
json.dump(c, open("/tmp/client.json", "w"))
print("clientId", c["clientId"], "| saml.client.signature", a["saml.client.signature"], "| saml.encrypt", a.get("saml.encrypt", "unset"))
PY
CID=$(python3 -c 'import json,urllib.parse;print(urllib.parse.quote(json.load(open("/tmp/client.json"))["clientId"],safe=""))')
OLD=$(curl -s -H "Authorization: Bearer $TOKEN" "$KC/admin/realms/graticula/clients?clientId=$CID" | python3 -c 'import sys,json;d=json.load(sys.stdin);print(d[0]["id"] if d else "")')
if [ -n "$OLD" ]; then curl -s -o /dev/null -X DELETE -H "Authorization: Bearer $TOKEN" "$KC/admin/realms/graticula/clients/$OLD"; fi
curl -s -o /dev/null -w "client create %{http_code}\n" -X POST -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" --data-binary @/tmp/client.json "$KC/admin/realms/graticula/clients"
rm -f /tmp/sp.xml /tmp/client.json
