#!/bin/sh
# Runs on the VPS: a throwaway Samba 4 Active Directory domain controller (ADR-089 condition 1), on the loopback only.
# Passwords are made here, kept in /opt/ad-probe (700), and never printed. Plain LDAP simple binds are allowed
# ("ldap server require strong auth = no") because the server under test runs on Windows and would not trust the
# DC's self-signed certificate; that is recorded as the probe's limit. Provisioning's --option did not reach
# smb.conf on 2026-10-16, so allow-simple-bind.sh (docker cp it in and run it, then `samba -i` again) is what sets it.
set -eu
mkdir -p /opt/ad-probe && chmod 700 /opt/ad-probe && cd /opt/ad-probe
for f in adminpass userpass searchpass; do
  [ -f $f ] || { printf 'Gr%s9!' "$(head -c 12 /dev/urandom | base64 | tr -d '/+=')" > $f; chmod 600 $f; }
done
docker rm -f ad-probe >/dev/null 2>&1 || true
docker run -d --name ad-probe --privileged --hostname dc1 -p 127.0.0.1:18389:389 debian:bookworm-slim sleep infinity >/dev/null
docker exec ad-probe sh -c 'export DEBIAN_FRONTEND=noninteractive; apt-get update -qq >/dev/null && apt-get install -y -qq samba winbind krb5-user ldb-tools >/dev/null 2>&1 && rm -f /etc/samba/smb.conf && echo installed'
docker exec -e ADMINPASS="$(cat adminpass)" ad-probe sh -c 'samba-tool domain provision --realm=GRATICULA.TEST --domain=GRATICULA \
  --server-role=dc --dns-backend=SAMBA_INTERNAL --adminpass="$ADMINPASS" --use-rfc2307 \
  --option="ldap server require strong auth = no" >/tmp/provision.log 2>&1 && echo provisioned || tail -5 /tmp/provision.log'
docker exec -d ad-probe samba -i
sleep 8
docker exec -e P="$(cat userpass)" -e S="$(cat searchpass)" ad-probe sh -c '
  samba-tool user create jane.doe "$P" --given-name=Jane --surname=Doe --mail-address=jane.doe@graticula.test >/dev/null
  samba-tool user create svc.graticula "$S" >/dev/null
  samba-tool group add GIS-Publishers >/dev/null
  samba-tool group add Planners >/dev/null
  samba-tool group add All-GIS >/dev/null
  samba-tool group addmembers GIS-Publishers jane.doe >/dev/null
  samba-tool group addmembers All-GIS Planners >/dev/null
  samba-tool group addmembers Planners jane.doe >/dev/null
  echo users and groups ready'
