"""ADR-089 conditions 1 and 3: a real Active Directory (Samba 4 as a domain controller) signs somebody in to a running
server over ldaps:// alone, and its groups inside groups are followed when the directory is set to.

The DC (setup.sh) refuses a simple bind over plain LDAP, which is Samba's default; it is reached at
ldaps://127.0.0.1:18636 through an SSH tunnel, with a certificate from a certificate authority made for the run.

1. Registered without that authority, the check is refused: the server does not trust the DC's certificate.
2. Given the authority as the directory's certificate to trust, the check passes, and jane.doe signs in the three ways
   people type an AD name — jane.doe, GRATICULA\\jane.doe and jane.doe@graticula.test — to one account.
3. All-GIS, which jane.doe is in only because Planners is in it, maps to a group here: with nested groups off she is not
   put in it; with them on, at the next sign-in, she is.

Removes the member, the group and the directory afterwards. Passwords and the CA come from files beside this script
(copied from /opt/ad-probe on the VPS, never into the repository) and the environment, and are never printed."""
import json, os, ssl, urllib.error, urllib.parse, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.environ.get("GRATICULA_TEST_URL", "https://127.0.0.1:18572")
DC = os.environ.get("AD_PROBE_URL", "ldaps://127.0.0.1:18636")
CTX = ssl._create_unverified_context()
BASE = "DC=graticula,DC=test"


def call(method, path, token=None, body=None):
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(ROOT + path, method=method, headers=headers,
                                 data=None if body is None else json.dumps(body).encode())
    try:
        with urllib.request.urlopen(req, context=CTX) as r:
            return r.status, r.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()


def secret(name):
    return open(os.path.join(HERE, name), encoding="utf-8").read().strip()


def directory(trusted, nested):
    return {"kind": "ldap", "name": "zz Samba AD probe", "issuer": DC,
            "clientId": f"CN=svc.graticula,CN=Users,{BASE}", "clientSecret": secret("searchpass"),
            "userBase": f"CN=Users,{BASE}", "autoCreate": True, "defaultRole": "viewer",
            "trustedCertificate": trusted, "nestedGroups": nested}


def in_group(admin, who):
    _, body = call("GET", "/admin/groups/zz_ad_all_gis", admin)
    return any(m["name"] == who for m in json.loads(body).get("members", []))


_, body = call("POST", "/rest/auth/login", body={"name": os.environ.get("GRATICULA_TEST_USER", "ci"),
                                                  "password": os.environ["GRATICULA_TEST_PASSWORD"]})
admin = json.loads(body)["token"]
authority = secret("ca.pem")

status, body = call("POST", "/admin/identity-providers", admin, directory("", False))
print("register directory, no authority given:", status, body[:200] if status != 201 else "")
provider = json.loads(body)["id"] if status == 201 else None
signed = None

try:
    status, body = call("POST", f"/admin/identity-providers/{provider}/check", admin)
    print("check without the authority:", status, body[:200])
    status, body = call("POST", "/rest/auth/login", body={"name": "jane.doe", "password": secret("userpass")})
    print("sign in without the authority:", status)

    status, body = call("PUT", f"/admin/identity-providers/{provider}", admin, directory(authority, False))
    print("give the authority:", status, json.loads(body).get("trustedCertificate", {}).get("subject") if status == 200 else body[:200])
    status, body = call("POST", f"/admin/identity-providers/{provider}/check", admin)
    print("check with the authority:", status, body[:300])

    status, body = call("POST", "/admin/groups", admin, {"name": "zz_ad_all_gis", "title": "zz AD All-GIS"})
    print("group:", status)
    status, body = call("PUT", f"/admin/identity-providers/{provider}/groups", admin, {"mappings": [
        {"externalGroup": "GIS-Publishers", "role": "publisher"},
        {"externalGroup": f"CN=All-GIS,CN=Users,{BASE}", "group": "zz_ad_all_gis"}]})
    print("mappings:", status, body[:200] if status != 200 else "")

    password = secret("userpass")
    for typed in ["jane.doe", "GRATICULA\\jane.doe", "jane.doe@graticula.test"]:
        status, body = call("POST", "/rest/auth/login", body={"name": typed, "password": password})
        print(f"sign in as {typed!r}:", status, "token" if status == 200 and "token" in body else body[:200])
        if status == 200:
            _, who = call("GET", "/rest/whoami", json.loads(body)["token"])
            signed = json.loads(who).get("name") or signed
            print("   whoami:", signed)

    status, body = call("POST", "/rest/auth/login", body={"name": "jane.doe", "password": "wrong-" + password})
    print("a wrong password:", status)

    if signed:
        print("nested groups off, in the group All-GIS maps to:", in_group(admin, signed))
        status, body = call("PUT", f"/admin/identity-providers/{provider}", admin, directory(authority, True))
        print("nested groups on:", status, json.loads(body).get("nestedGroups") if status == 200 else body[:200])
        status, _ = call("POST", "/rest/auth/login", body={"name": "jane.doe", "password": password})
        print("sign in again:", status)
        print("nested groups on, in the group All-GIS maps to:", in_group(admin, signed))
        _, body = call("GET", "/admin/members", admin)
        member = next(m for m in json.loads(body)["members"] if m["name"] == signed)
        print("member:", {k: v for k, v in member.items() if k in ("name", "displayName", "fullName", "roles", "signsInWith", "roleManaged")})
finally:
    if signed:
        print("member delete:", call("DELETE", f"/admin/members/{urllib.parse.quote(signed)}", admin)[0])
    call("DELETE", "/admin/groups/zz_ad_all_gis", admin)
    if provider:
        print("directory delete:", call("DELETE", f"/admin/identity-providers/{provider}", admin)[0])
