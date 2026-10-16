#!/bin/sh
# Runs on the VPS: a realm, a confidential client and one user in the throwaway Keycloak (ADR-088 condition 1).
# Secrets are made here, kept in /opt/kc-probe (700), and never printed.
set -eu
cd /opt/kc-probe
[ -f secret ] || { head -c 24 /dev/urandom | base64 | tr -d '/+=' > secret; chmod 600 secret; }
[ -f userpass ] || { head -c 18 /dev/urandom | base64 | tr -d '/+=' > userpass; chmod 600 userpass; }
KC="docker exec kc-probe /opt/keycloak/bin/kcadm.sh"
$KC config credentials --server http://localhost:8080 --realm master --user admin --password "$(cat admin)" >/dev/null
$KC create realms -s realm=graticula -s enabled=true >/dev/null 2>&1 || true
$KC create clients -r graticula -s clientId=graticula-probe -s enabled=true -s publicClient=false \
  -s "secret=$(cat secret)" -s standardFlowEnabled=true -s directAccessGrantsEnabled=false \
  -s 'redirectUris=["https://127.0.0.1:18491/*"]' -s 'webOrigins=["+"]' >/dev/null 2>&1 || true
$KC create users -r graticula -s username=kc.alice -s email=alice@kc.example -s emailVerified=true \
  -s firstName=Alice -s lastName=Keycloak -s enabled=true >/dev/null 2>&1 || true
$KC set-password -r graticula --username kc.alice --new-password "$(cat userpass)" >/dev/null
echo "realm, client and user ready"
