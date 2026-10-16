#!/bin/sh
# Runs inside the probe DC: allows simple binds over plain LDAP (the probe's limit; see setup.sh) and restarts samba.
set -eu
python3 - <<'PY'
p = "/etc/samba/smb.conf"
s = open(p).read()
if "ldap server require strong auth" not in s:
    s = s.replace("[global]\n", "[global]\n\tldap server require strong auth = no\n", 1)
open(p, "w").write(s)
PY
testparm -s 2>/dev/null | grep -i "strong auth" || echo "not set"
pkill samba || true
sleep 2
