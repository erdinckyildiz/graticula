#!/bin/sh
# Runs on the VPS: a throwaway Keycloak on the loopback (127.0.0.1:18181; 18180 is another project's), a realm, a confidential client and one user
# (ADR-088 condition 1; the realm and user serve the SAML probe too, ADR-090). Secrets are made here, kept in
# /opt/kc-probe (700), and never printed. The container is named graticula-probe-kc, capped at 768 MB, and removed with
# `docker rm -f graticula-probe-kc` when the run is done. KC_IMAGE picks the image (2026-10-16: 26.0.7; 2026-10-31: 26.4).
set -eu
NAME=graticula-probe-kc
IMAGE=${KC_IMAGE:-quay.io/keycloak/keycloak:26.4}
mkdir -p /opt/kc-probe && chmod 700 /opt/kc-probe && cd /opt/kc-probe
for f in admin secret userpass; do
  [ -f $f ] || { head -c 18 /dev/urandom | base64 | tr -d '/+=' > $f; chmod 600 $f; }
done
if ! docker ps --format '{{.Names}}' | grep -qx $NAME; then
  docker rm -f $NAME >/dev/null 2>&1 || true
  docker run -d --name $NAME --memory 768m -p 127.0.0.1:18181:8080 -e KC_BOOTSTRAP_ADMIN_USERNAME=admin \
    -e KC_BOOTSTRAP_ADMIN_PASSWORD="$(cat admin)" -e JAVA_OPTS_KC_HEAP="-XX:MaxRAMPercentage=60" "$IMAGE" start-dev >/dev/null
  for i in $(seq 1 90); do
    curl -fs -o /dev/null http://127.0.0.1:18181/realms/master && break
    sleep 2
  done
fi
KC="docker exec $NAME /opt/keycloak/bin/kcadm.sh"
$KC config credentials --server http://localhost:8080 --realm master --user admin --password "$(cat admin)" >/dev/null
$KC create realms -s realm=graticula -s enabled=true >/dev/null 2>&1 || true
$KC create clients -r graticula -s clientId=graticula-probe -s enabled=true -s publicClient=false \
  -s "secret=$(cat secret)" -s standardFlowEnabled=true -s directAccessGrantsEnabled=false \
  -s 'redirectUris=["https://127.0.0.1:18491/*"]' -s 'webOrigins=["+"]' >/dev/null 2>&1 || true
$KC create users -r graticula -s username=kc.alice -s email=alice@kc.example -s emailVerified=true \
  -s firstName=Alice -s lastName=Keycloak -s enabled=true >/dev/null 2>&1 || true
$KC set-password -r graticula --username kc.alice --new-password "$(cat userpass)" >/dev/null
echo "realm, client and user ready"
