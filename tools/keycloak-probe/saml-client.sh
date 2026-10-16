#!/bin/sh
# Runs on the VPS: reads an SP's SAML metadata on stdin and makes the Keycloak client from it, through Keycloak's own
# client-description converter (the admin REST API; kcadm sends only JSON). The admin password is read from
# /opt/kc-probe/admin and never printed.
set -eu
KC=http://127.0.0.1:18180
cat > /tmp/sp.xml
TOKEN=$(curl -s -d client_id=admin-cli -d username=admin --data-urlencode "password=$(cat /opt/kc-probe/admin)" \
  -d grant_type=password "$KC/realms/master/protocol/openid-connect/token" | python3 -c 'import sys,json;print(json.load(sys.stdin)["access_token"])')
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/xml" --data-binary @/tmp/sp.xml \
  "$KC/admin/realms/graticula/client-description-converter" > /tmp/client.json

# Keycloak's converter leaves "client signature required" on whatever AuthnRequestsSigned says; this SP signs no
# request (AuthnRequestsSigned="false"), as AD FS and Entra ID accept by default, so the probe turns it off, and
# replaces a client of the same id left by an earlier run.
python3 - <<'PY'
import json
c = json.load(open("/tmp/client.json"))
c.setdefault("attributes", {})["saml.client.signature"] = "false"
json.dump(c, open("/tmp/client.json", "w"))
print("clientId", c["clientId"], "| saml.client.signature", c["attributes"]["saml.client.signature"])
PY
CID=$(python3 -c 'import json,urllib.parse;print(urllib.parse.quote(json.load(open("/tmp/client.json"))["clientId"],safe=""))')
OLD=$(curl -s -H "Authorization: Bearer $TOKEN" "$KC/admin/realms/graticula/clients?clientId=$CID" | python3 -c 'import sys,json;d=json.load(sys.stdin);print(d[0]["id"] if d else "")')
if [ -n "$OLD" ]; then curl -s -o /dev/null -X DELETE -H "Authorization: Bearer $TOKEN" "$KC/admin/realms/graticula/clients/$OLD"; fi
curl -s -o /dev/null -w "client create %{http_code}\n" -X POST -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" --data-binary @/tmp/client.json "$KC/admin/realms/graticula/clients"
rm -f /tmp/sp.xml /tmp/client.json
