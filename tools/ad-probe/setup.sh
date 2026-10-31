#!/bin/sh
# Runs on the VPS: a throwaway Samba 4 Active Directory domain controller (ADR-089 conditions 1 and 3), on the
# loopback only, reached over ldaps:// alone.
#
# Passwords are made here, kept in /opt/ad-probe (700), and never printed. Since 2026-10-31 the DC keeps Samba's
# default, "ldap server require strong auth = yes": a simple bind over plain LDAP is refused, and the script shows it.
# Its TLS certificate comes from a certificate authority made here for the run (ca.pem beside this script on the VPS),
# for dc1.graticula.test, localhost and 127.0.0.1; the server under test is given that authority as the directory's
# certificate to trust, as an operator would give a domain's own CA. Only 636 is published, on 127.0.0.1:18636.
#
# Groups: jane.doe is in Planners and GIS-Publishers; Planners is in All-GIS, so jane.doe is in All-GIS only through
# another group (condition 3).
#
# The container is named graticula-probe-ad and removed with `docker rm -f graticula-probe-ad` when the run is done.
set -eu
mkdir -p /opt/ad-probe && chmod 700 /opt/ad-probe && cd /opt/ad-probe
for f in adminpass userpass searchpass; do
  [ -f $f ] || { printf 'Gr%s9!' "$(head -c 12 /dev/urandom | base64 | tr -d '/+=')" > $f; chmod 600 $f; }
done
NAME=graticula-probe-ad
docker rm -f $NAME >/dev/null 2>&1 || true
docker run -d --name $NAME --privileged --hostname dc1 --memory 600m -p 127.0.0.1:18636:636 debian:bookworm-slim sleep infinity >/dev/null
docker exec $NAME sh -c 'export DEBIAN_FRONTEND=noninteractive; apt-get update -qq >/dev/null && apt-get install -y -qq samba winbind krb5-user ldb-tools ldap-utils openssl >/dev/null 2>&1 && rm -f /etc/samba/smb.conf && echo installed'
docker exec -e ADMINPASS="$(cat adminpass)" $NAME sh -c 'samba-tool domain provision --realm=GRATICULA.TEST --domain=GRATICULA \
  --server-role=dc --dns-backend=SAMBA_INTERNAL --adminpass="$ADMINPASS" --use-rfc2307 >/tmp/provision.log 2>&1 && echo provisioned || tail -5 /tmp/provision.log'

# A certificate authority for the run, and the DC's certificate from it.
docker exec $NAME sh -c '
  set -e
  d=/var/lib/samba/private/tls-probe; mkdir -p $d; cd $d
  openssl req -x509 -newkey rsa:2048 -nodes -keyout ca.key -out ca.pem -days 7 -subj "/CN=Graticula probe domain CA" \
    -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null
  openssl req -newkey rsa:2048 -nodes -keyout key.pem -out dc.csr -subj "/CN=dc1.graticula.test" 2>/dev/null
  printf "subjectAltName=DNS:dc1.graticula.test,DNS:localhost,IP:127.0.0.1\nextendedKeyUsage=serverAuth\nbasicConstraints=CA:FALSE\n" > ext
  openssl x509 -req -in dc.csr -CA ca.pem -CAkey ca.key -CAcreateserial -out cert.pem -days 7 -extfile ext 2>/dev/null
  chmod 600 key.pem ca.key
  python3 - <<PY
p = "/etc/samba/smb.conf"
s = open(p).read()
s = s.replace("[global]\n", "[global]\n\ttls enabled = yes\n\ttls keyfile = $d/key.pem\n\ttls certfile = $d/cert.pem\n\ttls cafile = $d/ca.pem\n", 1)
open(p, "w").write(s)
PY
  testparm -s 2>/dev/null | grep -iE "tls (key|cert|ca)file|strong auth" || true
  echo "tls ready"'
docker cp $NAME:/var/lib/samba/private/tls-probe/ca.pem /opt/ad-probe/ca.pem
docker exec -d $NAME samba -i
sleep 10
docker exec -e P="$(cat userpass)" -e S="$(cat searchpass)" $NAME sh -c '
  samba-tool user create jane.doe "$P" --given-name=Jane --surname=Doe --mail-address=jane.doe@graticula.test >/dev/null
  samba-tool user create svc.graticula "$S" >/dev/null
  samba-tool group add GIS-Publishers >/dev/null
  samba-tool group add Planners >/dev/null
  samba-tool group add All-GIS >/dev/null
  samba-tool group addmembers GIS-Publishers jane.doe >/dev/null
  samba-tool group addmembers All-GIS Planners >/dev/null
  samba-tool group addmembers Planners jane.doe >/dev/null
  echo users and groups ready'

# What the DC itself says: a simple bind over plain LDAP is refused, the same bind over ldaps:// with the CA is not,
# and the chain rule finds All-GIS for jane.doe, which her memberOf does not list.
docker exec -e S="$(cat searchpass)" $NAME sh -c '
  B="CN=svc.graticula,CN=Users,DC=graticula,DC=test"
  printf "plain ldap:// simple bind: "; ldapwhoami -x -H ldap://127.0.0.1 -D "$B" -w "$S" 2>&1 | head -1
  printf "ldaps:// simple bind, CA given: "; LDAPTLS_CACERT=/var/lib/samba/private/tls-probe/ca.pem ldapwhoami -x -H ldaps://127.0.0.1 -D "$B" -w "$S" 2>&1 | head -1
  printf "ldaps:// simple bind, no CA: "; LDAPTLS_CACERT=/dev/null ldapwhoami -x -H ldaps://127.0.0.1 -D "$B" -w "$S" 2>&1 | head -1
  export LDAPTLS_CACERT=/var/lib/samba/private/tls-probe/ca.pem
  J=$(ldapsearch -LLL -o ldif-wrap=no -x -H ldaps://127.0.0.1 -D "$B" -w "$S" -b "CN=Users,DC=graticula,DC=test" "(sAMAccountName=jane.doe)" 1.1 | sed -n "s/^dn: //p")
  printf "jane.doe memberOf: "; ldapsearch -LLL -o ldif-wrap=no -x -H ldaps://127.0.0.1 -D "$B" -w "$S" \
    -b "$J" -s base "(objectClass=*)" memberOf | sed -n "s/^memberOf: CN=\([^,]*\),.*/\1/p" | tr "\n" " "; echo
  printf "groups in chain: "; ldapsearch -LLL -o ldif-wrap=no -x -H ldaps://127.0.0.1 -D "$B" -w "$S" \
    -b "DC=graticula,DC=test" "(member:1.2.840.113556.1.4.1941:=$J)" 1.1 | sed -n "s/^dn: CN=\([^,]*\),.*/\1/p" | tr "\n" " "; echo'
