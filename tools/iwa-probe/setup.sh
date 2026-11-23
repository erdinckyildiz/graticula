#!/bin/sh
# ADR-316 condition 2 and 3, run on the VPS (arm64) as root: Windows sign-in end to end, Kerberos from a Samba AD domain
# controller to a Graticula build of the g2-auth branch, in the server image's own runtime.
#
# Everything here is new and isolated: containers named g2-iwa-*, on their own network g2-iwa-net, nothing published to
# any port. Nothing else on the machine is touched. Passwords are made here, kept in /opt/g2-iwa (700), never printed.
#
#   sh setup.sh up       the network, the domain controller, the platform store, the server and a client
#   sh setup.sh probe    kinit as jane.doe, curl --negotiate through the sign-in route, then whoami
#   sh setup.sh down     removes every g2-iwa-* container, the network and /opt/g2-iwa
#
# The server is the image ghcr.io/erdinckyildiz/graticula:1.0.529 — its operating system, its .NET and its libraries —
# with /app replaced, read-only, by the branch's own build (`dotnet publish src/Graticula.Host`, copied to
# /opt/g2-iwa/app). So what is measured is this branch's code in the shipped image's runtime.
set -eu
D=/opt/g2-iwa
NET=g2-iwa-net
REALM=G2IWA.TEST
HOST=graticula.g2iwa.test
IMAGE=ghcr.io/erdinckyildiz/graticula:1.0.529
STOREIMAGE=ghcr.io/erdinckyildiz/graticula-datastore:1.0.529

secret() { [ -f "$D/$1" ] || { printf 'Gr%s9!' "$(head -c 12 /dev/urandom | base64 | tr -d '/+=')" > "$D/$1"; chmod 600 "$D/$1"; }; }

up() {
  mkdir -p $D && chmod 700 $D
  for f in adminpass userpass svcpass pgpass appadmin; do secret $f; done
  [ -f $D/key ] || { head -c 32 /dev/urandom | base64 > $D/key; chmod 600 $D/key; }
  docker network inspect $NET >/dev/null 2>&1 || docker network create $NET >/dev/null

  # ---- the domain controller ----
  docker rm -fv g2-iwa-dc >/dev/null 2>&1 || true
  docker run -d --name g2-iwa-dc --network $NET --hostname dc1 --privileged --memory 600m debian:bookworm-slim sleep infinity >/dev/null
  docker exec g2-iwa-dc sh -c 'export DEBIAN_FRONTEND=noninteractive; apt-get update -qq >/dev/null && apt-get install -y -qq samba winbind krb5-user >/dev/null 2>&1 && rm -f /etc/samba/smb.conf && echo dc packages installed'
  docker exec -e P="$(cat $D/adminpass)" g2-iwa-dc sh -c "samba-tool domain provision --realm=$REALM --domain=G2IWA --server-role=dc \
    --dns-backend=SAMBA_INTERNAL --adminpass=\"\$P\" --host-name=dc1 >/tmp/provision.log 2>&1 && echo provisioned || tail -5 /tmp/provision.log"
  docker exec g2-iwa-dc sh -c 'cp /var/lib/samba/private/krb5.conf /etc/krb5.conf'
  docker exec -d g2-iwa-dc samba -i
  sleep 12
  docker exec -e U="$(cat $D/userpass)" -e S="$(cat $D/svcpass)" g2-iwa-dc sh -c "
    samba-tool user create jane.doe \"\$U\" --given-name=Jane --surname=Doe >/dev/null &&
    samba-tool user create svc-graticula \"\$S\" >/dev/null &&
    samba-tool spn add HTTP/$HOST svc-graticula >/dev/null &&
    samba-tool domain exportkeytab /tmp/http.keytab --principal=HTTP/$HOST >/dev/null &&
    echo users, service principal and keytab ready"
  docker cp g2-iwa-dc:/tmp/http.keytab $D/http.keytab
  docker exec g2-iwa-dc rm -f /tmp/http.keytab
  chmod 644 $D/http.keytab   # read by the server's own user inside its container; the directory above it is 700
  DCIP=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' g2-iwa-dc)
  cat > $D/krb5.conf <<EOF
[libdefaults]
	default_realm = $REALM
	dns_lookup_kdc = false
	dns_lookup_realm = false
	rdns = false
[realms]
	$REALM = {
		kdc = $DCIP
	}
[domain_realm]
	.g2iwa.test = $REALM
	g2iwa.test = $REALM
EOF
  chmod 644 $D/krb5.conf

  # ---- the platform store ----
  docker rm -fv g2-iwa-pg >/dev/null 2>&1 || true
  docker run -d --name g2-iwa-pg --network $NET --memory 400m -e POSTGRES_USER=gis -e POSTGRES_DB=gis \
    -e POSTGRES_PASSWORD="$(cat $D/pgpass)" $STOREIMAGE >/dev/null
  # The image's entrypoint runs its init scripts against a temporary server first, so ready once is not ready: wait for
  # the log line the real server writes after them, then make the schema.
  for i in $(seq 1 90); do docker logs g2-iwa-pg 2>&1 | grep -q "PostgreSQL init process complete" && break; sleep 2; done
  for i in $(seq 1 60); do docker exec g2-iwa-pg pg_isready -U gis >/dev/null 2>&1 && break; sleep 2; done
  docker exec g2-iwa-pg psql -U gis -d gis -qc "create extension if not exists postgis" >/dev/null 2>&1 || true
  docker exec g2-iwa-pg psql -U gis -d gis -qc "create schema if not exists gisserver" >/dev/null
  echo "platform store ready"

  # ---- the server: the shipped image, this branch's build ----
  STORE="Host=g2-iwa-pg;Database=gis;Username=gis;Password=$(cat $D/pgpass);SearchPath=gisserver"
  common="--network $NET -v $D/app:/app:ro -v $D/http.keytab:/keytab/http.keytab:ro -v $D/krb5.conf:/etc/krb5.conf:ro"
  docker run --rm --name g2-iwa-migrate $common -e Graticula__PlatformStore="$STORE" -e Graticula__SecretKey="$(cat $D/key)" \
    $IMAGE migrate --apply --no-backup 2>&1 | tail -1
  docker rm -fv g2-iwa-app >/dev/null 2>&1 || true
  docker run -d --name g2-iwa-app $common --network-alias $HOST --memory 900m \
    -e Graticula__PlatformStore="$STORE" -e Graticula__SecretKey="$(cat $D/key)" -e Graticula__HostName=$HOST \
    -e Graticula__Iwa__Enabled=true -e Graticula__Iwa__Keytab=/keytab/http.keytab $IMAGE >/dev/null
  for i in $(seq 1 90); do docker exec g2-iwa-app curl -fsk https://localhost:8443/healthz/live >/dev/null 2>&1 && break; sleep 2; done
  echo "server answering"

  # ---- the client: a domain user's machine ----
  docker rm -fv g2-iwa-client >/dev/null 2>&1 || true
  docker run -d --name g2-iwa-client --network $NET --memory 200m -v $D/krb5.conf:/etc/krb5.conf:ro debian:bookworm-slim sleep infinity >/dev/null
  # krb5-config's own setup fails on the read-only /etc/krb5.conf mounted above, and leaves the tools installed.
  docker exec g2-iwa-client sh -c 'export DEBIAN_FRONTEND=noninteractive; apt-get update -qq >/dev/null; apt-get install -y -qq krb5-user curl python3 >/dev/null 2>&1; which kinit curl python3 >/dev/null && echo client packages installed'
  seed
}

# ---- the first administrator and a Windows provider, from the client ----
seed() {
  TOKEN=$(docker logs g2-iwa-app 2>&1 | grep -oE '^[[:space:]]+[A-Za-z0-9._-]{20,}[[:space:]]*$' | head -1 | tr -d '[:space:]')
  docker exec -i -e T="$TOKEN" -e P="$(cat $D/appadmin)" g2-iwa-client python3 - <<'PY'
import json, os, ssl, urllib.request
c = ssl.create_default_context(); c.check_hostname = False; c.verify_mode = ssl.CERT_NONE
root = "https://graticula.g2iwa.test:8443"
def call(m, p, b=None, t=None):
    h = {"Content-Type": "application/json"}
    if t: h["Authorization"] = "Bearer " + t
    r = urllib.request.Request(root + p, data=json.dumps(b).encode() if b is not None else None, method=m, headers=h)
    try:
        with urllib.request.urlopen(r, context=c) as x: return x.status, x.read().decode()
    except urllib.error.HTTPError as e: return e.code, e.read().decode()
print("setup", call("POST", "/rest/setup", {"token": os.environ["T"], "name": "siteadmin", "password": os.environ["P"]})[0])
s, b = call("POST", "/rest/auth/login", {"name": "siteadmin", "password": os.environ["P"]})
t = json.loads(b)["token"]
s, b = call("POST", "/admin/identity-providers", {"kind": "iwa", "name": "Windows", "autoCreate": True, "defaultRole": "viewer"}, t)
print("windows provider", s, json.loads(b).get("id"))
open("/tmp/provider", "w").write(json.loads(b)["id"])
PY
}

probe() {
  ID=$(docker exec g2-iwa-client cat /tmp/provider)
  docker exec -e U="$(cat $D/userpass)" g2-iwa-client sh -c "printf '%s' \"\$U\" | kinit jane.doe@$REALM >/dev/null && klist | sed -n 's/^Default principal: /ticket for /p'"
  docker exec g2-iwa-client sh -c "rm -f /tmp/jar; curl -sk --http1.1 --negotiate -u : -c /tmp/jar -o /dev/null \
    -w 'sign-in: %{http_code} -> %{redirect_url}\n' 'https://$HOST:8443/rest/auth/iwa/$ID/start?return=/rest/whoami'"
  docker exec g2-iwa-client sh -c "curl -sk --http1.1 -b /tmp/jar https://$HOST:8443/rest/whoami | python3 -c 'import json,sys; d=json.load(sys.stdin); print(\"whoami:\", d.get(\"name\"))'"
  docker exec g2-iwa-client sh -c "kdestroy; curl -sk --http1.1 --negotiate -u : -o /dev/null -w 'without a ticket: %{http_code}\n' \
    'https://$HOST:8443/rest/auth/iwa/$ID/start'"
  docker logs g2-iwa-app 2>&1 | grep -iE 'account .* was made|Iwa|negotiate|gss' | tail -5
}

down() {
  # -v: the store image and the server image each declare a volume, which a plain rm leaves behind.
  docker rm -fv g2-iwa-client g2-iwa-app g2-iwa-pg g2-iwa-dc g2-iwa-migrate >/dev/null 2>&1 || true
  docker network rm $NET >/dev/null 2>&1 || true
  rm -rf $D
  echo "removed: g2-iwa-client g2-iwa-app g2-iwa-pg g2-iwa-dc, network $NET, $D"
}

case "${1:-}" in
  up) up ;;
  seed) seed ;;
  probe) probe ;;
  down) down ;;
  *) echo "usage: $0 up|probe|down" >&2; exit 2 ;;
esac
