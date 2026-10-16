"""ADR-089 condition 1: a real Active Directory (Samba 4 as a domain controller) signs somebody in to a running server.

Registers the DC as a directory with the defaults the console offers, checks it, maps an AD group to a role and a
nested one to a group, then signs jane.doe in three ways people type an AD name — jane.doe, GRATICULA\\jane.doe and
jane.doe@graticula.test — and reads the account back. Removes the member and the directory afterwards. Passwords come
from files beside this script and the environment, and are never printed."""
import json, os, ssl, urllib.error, urllib.parse, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.environ.get("GRATICULA_TEST_URL", "https://127.0.0.1:18491")
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


_, body = call("POST", "/rest/auth/login", body={"name": os.environ.get("GRATICULA_TEST_USER", "ci"),
                                                  "password": os.environ["GRATICULA_TEST_PASSWORD"]})
admin = json.loads(body)["token"]

status, body = call("POST", "/admin/identity-providers", admin, {
    "kind": "ldap", "name": "zz Samba AD probe", "issuer": "ldap://127.0.0.1:18389",
    "clientId": f"CN=svc.graticula,CN=Users,{BASE}", "clientSecret": secret("searchpass"),
    "userBase": f"CN=Users,{BASE}", "autoCreate": True, "defaultRole": "viewer"})
print("register directory:", status, body[:200] if status != 201 else "")
provider = json.loads(body)["id"] if status == 201 else None
signed = None

try:
    status, body = call("POST", f"/admin/identity-providers/{provider}/check", admin)
    print("check:", status, body[:300])

    status, body = call("POST", "/admin/groups", admin, {"name": "zz_ad_planners", "title": "zz AD planners"})
    print("group:", status)
    status, body = call("PUT", f"/admin/identity-providers/{provider}/groups", admin, {"mappings": [
        {"externalGroup": "GIS-Publishers", "role": "publisher"},
        {"externalGroup": f"CN=All-GIS,CN=Users,{BASE}", "group": "zz_ad_planners"},
        {"externalGroup": f"CN=Planners,CN=Users,{BASE}", "group": "zz_ad_planners"}]})
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
        _, body = call("GET", "/admin/members", admin)
        member = next(m for m in json.loads(body)["members"] if m["name"] == signed)
        print("member:", {k: v for k, v in member.items() if k in ("name", "displayName", "fullName", "roles", "signsInWith", "roleManaged", "email")})
        _, body = call("GET", "/admin/groups/zz_ad_planners", admin)
        print("in zz_ad_planners:", f'"{signed}"' in body)
finally:
    if signed:
        print("member delete:", call("DELETE", f"/admin/members/{urllib.parse.quote(signed)}", admin)[0])
    call("DELETE", "/admin/groups/zz_ad_planners", admin)
    if provider:
        print("directory delete:", call("DELETE", f"/admin/identity-providers/{provider}", admin)[0])
