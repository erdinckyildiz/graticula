#!/usr/bin/env python3
"""ADR-280 condition 1, a disposable prototype (experiments are never promoted, CLAUDE.md §1).

A reverse proxy in front of a Graticula server, on one origin, that answers everything as the server does except the
scene layer's associated feature layer: that is a fake 3D object (multipatch) feature layer kept in memory, shaped
from Esri's public REST reference (uploadAssets, applyEdits with assetMaps, query3D, queryAssets). Every request the
SDK sends is logged, so what SDK 5.2's editing asks of a server is measured rather than guessed.

    python3 stub.py <graticula https root> <token> <scene layer item id> [port]

Then open http://127.0.0.1:<port>/proto.html?item=<id>. The page writes its findings to /report, which lands in
report.json beside this file; requests.log holds every request.
"""
import hashlib
import json
import os
import ssl
import sys
import threading
import time
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

UPSTREAM, TOKEN, ITEM = sys.argv[1].rstrip("/"), sys.argv[2], sys.argv[3]
PORT = int(sys.argv[4]) if len(sys.argv) > 4 else 18990
ORIGIN = f"https://127.0.0.1:{PORT}"
HERE = os.path.dirname(os.path.abspath(__file__))
FAKE = "/rest/services/zzmesh/FeatureServer"
LOCK = threading.Lock()
LOG = open(os.path.join(HERE, "requests.log"), "w", buffering=1)
INSECURE = ssl._create_unverified_context()

FIELDS = [
    {"name": "OBJECTID", "type": "esriFieldTypeOID", "alias": "OBJECTID", "editable": False, "nullable": False},
    {"name": "name", "type": "esriFieldTypeString", "alias": "name", "length": 256, "editable": True, "nullable": True},
    {"name": "floors", "type": "esriFieldTypeInteger", "alias": "floors", "editable": True, "nullable": True},
    {"name": "built", "type": "esriFieldTypeInteger", "alias": "built", "editable": True, "nullable": True},
    {"name": "globalid", "type": "esriFieldTypeGlobalID", "alias": "globalid", "length": 38, "editable": False, "nullable": False},
] + [{"name": f"ESRI3DO_{k}", "type": "esriFieldTypeDouble", "alias": f"ESRI3DO_{k}", "editable": True, "nullable": True}
     for k in ("OX", "OY", "OZ", "TX", "TY", "TZ", "SX", "SY", "SZ", "RX", "RY", "RZ", "RDEG")] + [
    {"name": "ESRI3DO_TYPE", "type": "esriFieldTypeString", "alias": "ESRI3DO_TYPE", "length": 32, "editable": True, "nullable": True},
    {"name": "ESRI3DO_SHASH", "type": "esriFieldTypeString", "alias": "ESRI3DO_SHASH", "length": 64, "editable": True, "nullable": True},
    {"name": "ESRI3DO_PID", "type": "esriFieldTypeGUID", "alias": "ESRI3DO_PID", "length": 38, "editable": True, "nullable": True},
]

STATE = {"features": {}, "assets": {}, "maps": {}, "next": 1000, "edited": int(time.time() * 1000)}


def layer_json():
    roles = {"originX": "ESRI3DO_OX", "originY": "ESRI3DO_OY", "originZ": "ESRI3DO_OZ", "translationX": "ESRI3DO_TX",
             "translationY": "ESRI3DO_TY", "translationZ": "ESRI3DO_TZ", "scaleX": "ESRI3DO_SX", "scaleY": "ESRI3DO_SY",
             "scaleZ": "ESRI3DO_SZ", "rotationX": "ESRI3DO_RX", "rotationY": "ESRI3DO_RY", "rotationZ": "ESRI3DO_RZ",
             "rotationDeg": "ESRI3DO_RDEG"}
    return {
        "currentVersion": 11.3, "id": 0, "name": "zz mesh", "type": "Feature Layer", "geometryType": "esriGeometryMultiPatch",
        "hasZ": True, "hasM": False, "objectIdField": "OBJECTID", "globalIdField": "globalid", "displayField": "name",
        "fields": FIELDS, "capabilities": "Create,Delete,Query,Update,Editing,ChangeTracking,Uploads",
        "supportsApplyEditsWithGlobalIds": True, "supportsRollbackOnFailureParameter": True, "hasAttachments": False,
        "supportedQueryFormats": "JSON, geoJSON, PBF", "maxRecordCount": 2000, "allowGeometryUpdates": True,
        "spatialReference": {"wkid": 4326, "latestWkid": 4326},
        "extent": {"xmin": 32.85, "ymin": 39.97, "xmax": 32.92, "ymax": 40.03, "spatialReference": {"wkid": 4326}},
        "editingInfo": {"lastEditDate": STATE["edited"], "dataLastEditDate": STATE["edited"], "schemaLastEditDate": STATE["edited"]},
        "advancedQueryCapabilities": {"supportsPagination": True, "supportsQueryWithDistance": True, "supportsReturningQueryExtent": True,
                                      "supportsStatistics": True, "supportsOrderBy": True, "supportsDistinct": True,
                                      "supportedMultipatchOptions": ["embedMaterials", "externalizeTextures", "stripMaterials", "extent"]},
        "uniqueIdField": {"name": "OBJECTID", "isSystemMaintained": True},
        "serverGens": {"minServerGen": 1, "serverGen": STATE["edited"]},
        "advancedEditingCapabilities": {"supportsAsyncApplyEdits": False, "supportsReturnEditResults": True,
                                        "supportsApplyEditsbyUploadID": False},
        # The shape of Esri's public sample layer's infoFor3D (observed metadata, ADR-280 §1), with glTF only.
        "infoFor3D": {
            "supportedFormats": [["3D_glb", "model/gltf-binary", "glb", "Khronos Group glTF binary"],
                                 ["3D_gltf", "model/gltf+json", "gltf", "Khronos Group glTF JSON"]],
            "queryFormats": ["3D_glb"], "editFormats": ["3D_glb", "3D_gltf"],
            "transformFieldRoles": {"globalID": "globalid", **roles}, "assetTypeField": "ESRI3DO_TYPE",
            "sourceHashField": "ESRI3DO_SHASH", "parentFeatureField": "ESRI3DO_PID", "assetHashAlgorithm": "SHA256",
            "assetMapFields": [
                {"name": n, "type": t, "alias": n, "nullable": n != "ESRI3DO_PID", "editable": n != "GlobalID", "required": True,
                 **({"length": 512} if t == "esriFieldTypeString" else {"length": 38} if "GUID" in t or "GlobalID" in t else {})}
                for n, t in (("ESRI3DO_PID", "esriFieldTypeGUID"), ("ESRI3DO_FLAGS", "esriFieldTypeInteger"),
                             ("ESRI3DO_STATUS", "esriFieldTypeInteger"), ("ESRI3DO_NAME", "esriFieldTypeString"),
                             ("ESRI3DO_TYPE", "esriFieldTypeString"), ("ESRI3DO_SIZE", "esriFieldTypeInteger"),
                             ("ESRI3DO_SEQNO", "esriFieldTypeInteger"), ("ESRI3DO_CPLX", "esriFieldTypeInteger"),
                             ("ESRI3DO_AHASH", "esriFieldTypeString"), ("ESRI3DO_SHASH", "esriFieldTypeString"),
                             ("GlobalID", "esriFieldTypeGlobalID"))],
            "assetMapFieldRoles": {"globalID": "GlobalID", "parentGlobalId": "ESRI3DO_PID", "assetName": "ESRI3DO_NAME",
                                   "assetType": "ESRI3DO_TYPE", "conversionStatus": "ESRI3DO_STATUS", "flags": "ESRI3DO_FLAGS",
                                   "size": "ESRI3DO_SIZE", "complexity": "ESRI3DO_CPLX", "sourceHash": "ESRI3DO_SHASH",
                                   "assetHash": "ESRI3DO_AHASH", "seqNo": "ESRI3DO_SEQNO"},
            "tables": [{"id": 1, "name": "zzmesh_3DOAM", "role": "assetMaps"}, {"id": 2, "name": "zzmesh_3DOAT", "role": "assets"}],
        },
        "supportsAsyncConvert3D": True, "supportedConvertFileFormats": "JSON,PBF",
        "supportedConvertContentFormats": "LayerEditCollection,FeatureCollection",
    }


def service_json():
    return {"currentVersion": 11.3, "serviceDescription": "zz mesh", "hasVersionedData": False,
            "supportsApplyEditsWithGlobalIds": True, "capabilities": "Create,Delete,Query,Update,Editing,ChangeTracking,Uploads",
            "spatialReference": {"wkid": 4326, "latestWkid": 4326}, "layers": [{"id": 0, "name": "zz mesh", "geometryType": "esriGeometryMultiPatch"}],
            "tables": [], "syncEnabled": False, "supportsRelationshipsResource": False,
            "extractChangesCapabilities": {"supportsReturnIdsOnly": True, "supportsReturnExtentOnly": False,
                                           "supportsReturnFeature": True, "supportsServerGens": True}}


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def _body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n) if n else b""

    def _send(self, code, body, ctype="application/json; charset=utf-8"):
        if isinstance(body, (dict, list)):
            body = json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(body)

    def do_OPTIONS(self):
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Headers", "*")
        self.send_header("Access-Control-Allow-Methods", "GET,POST")
        self.send_header("Content-Length", "0")
        self.end_headers()

    def do_GET(self):
        self.route("GET", b"")

    def do_POST(self):
        self.route("POST", self._body())

    def route(self, method, body):
        url = urllib.parse.urlsplit(self.path)
        params = dict(urllib.parse.parse_qsl(url.query))
        if body and "form" in (self.headers.get("Content-Type") or ""):
            params.update(dict(urllib.parse.parse_qsl(body.decode("utf-8", "replace"))))
        path = url.path
        if True:
            LOG.write(json.dumps({"t": time.time(), "m": method, "path": path,
                                  "params": {k: (v if len(v) < 600 else v[:600] + "…") for k, v in params.items()}}) + "\n")
        if path == "/proto.html":
            return self._send(200, open(os.path.join(HERE, "proto.html"), "rb").read(), "text/html; charset=utf-8")
        if path == "/report":
            with open(os.path.join(HERE, "report.json"), "wb") as f:
                f.write(body)
            return self._send(200, {"ok": True})
        if path.startswith("/assets/"):
            data = STATE["assets"].get(path.rsplit("/", 1)[1])
            return self._send(200 if data else 404, data or b"", "model/gltf-binary")
        if path.startswith(FAKE):
            return self.fake(path[len(FAKE):].strip("/"), params)
        return self.proxy(method, path, url.query, body)

    # ---- the fake multipatch layer ----------------------------------------------------------------------------------
    def fake(self, rest, p):
        with LOCK:
            if rest == "":
                return self._send(200, service_json())
            if rest == "0":
                return self._send(200, layer_json())
            if rest in ("1", "2"):
                return self._send(200, {"currentVersion": 11.3, "id": int(rest), "name": "zzmesh_3DOA" + ("M" if rest == "1" else "T"),
                                        "type": "Table", "fields": [{"name": "OBJECTID", "type": "esriFieldTypeOID"}],
                                        "objectIdField": "OBJECTID", "capabilities": "Create,Delete,Query,Update,Editing"})
            if rest == "0/uploadAssets":
                results = []
                for asset in json.loads(p.get("assets", "[]")):
                    import base64
                    data = base64.b64decode(asset.get("assetData", "")) if asset.get("assetData") else b""
                    h = hashlib.sha256(data).hexdigest()
                    STATE["assets"][h] = data
                    results.append({"success": True, "assetHash": h})
                return self._send(200, {"uploadResults": results})
            if rest == "0/applyEdits":
                return self._send(200, self.apply(p))
            if rest in ("0/query", "0/query3D"):
                return self._send(200, self.query(p, rest.endswith("3D")))
            if rest == "0/queryAssets":
                hashes = json.loads(p.get("assetHashes", "[]"))
                return self._send(200, {"assets": [{"assetHash": h, "contentType": "model/gltf-binary", "assetName": "geometry.glb",
                                                    "assetURL": f"{ORIGIN}/assets/{h}"} for h in hashes if h in STATE["assets"]]})
            if rest in ("extractChanges", "0/extractChanges"):
                ids = [f["attributes"]["OBJECTID"] for f in STATE["features"].values()]
                return self._send(200, {"layerServerGens": [{"id": 0, "serverGen": STATE["edited"]}],
                                        "edits": [{"id": 0, "objectIds": {"adds": ids, "updates": [], "deletes": []}}]})
            return self._send(200, {"error": {"code": 400, "message": f"zz stub does not answer {rest}"}})

    def apply(self, p):
        STATE["edited"] = int(time.time() * 1000)
        out = {"addResults": [], "updateResults": [], "deleteResults": []}
        for f in json.loads(p.get("adds", "[]") or "[]"):
            oid = STATE["next"]
            STATE["next"] += 1
            attrs = dict(f.get("attributes") or {})
            attrs["OBJECTID"] = oid
            gid = attrs.get("globalid") or attrs.get("GlobalID") or "{%08X-0000-4000-8000-000000000000}" % oid
            attrs["globalid"] = gid
            STATE["features"][oid] = {"attributes": attrs, "geometry": f.get("geometry")}
            out["addResults"].append({"objectId": oid, "globalId": gid, "success": True})
        for f in json.loads(p.get("updates", "[]") or "[]"):
            attrs = f.get("attributes") or {}
            oid = attrs.get("OBJECTID")
            if oid in STATE["features"]:
                STATE["features"][oid]["attributes"].update(attrs)
                if f.get("geometry"):
                    STATE["features"][oid]["geometry"] = f["geometry"]
            out["updateResults"].append({"objectId": oid, "success": oid in STATE["features"]})
        deletes = p.get("deletes", "")
        for d in (json.loads(deletes) if deletes.startswith("[") else [x for x in deletes.split(",") if x]):
            oid = int(d) if str(d).isdigit() else d
            if not isinstance(oid, int):
                # useGlobalIds=true: the SDK deletes by GlobalID (measured).
                oid = next((k for k, v in STATE["features"].items() if v["attributes"].get("globalid") == d), d)
                gone = STATE["features"].pop(oid, None) is not None
                out["deleteResults"].append({"objectId": oid, "globalId": d, "success": gone})
                continue
            out["deleteResults"].append({"objectId": oid, "success": STATE["features"].pop(oid, None) is not None})
        maps = json.loads(p.get("assetMaps", "null") or "null")
        if maps:
            out["assetMaps"] = {"addResults": [], "updateResults": []}
            for m in maps.get("adds", []):
                STATE["maps"][m.get("globalId")] = m
                out["assetMaps"]["addResults"].append({"globalId": m.get("globalId"), "success": True})
            for m in maps.get("updates", []):
                STATE["maps"][m.get("globalId")] = m
                out["assetMaps"]["updateResults"].append({"globalId": m.get("globalId"), "success": True})
        out["editMoment"] = STATE["edited"]
        return out

    def query(self, p, three_d):
        feats = list(STATE["features"].values())
        if p.get("returnCountOnly") == "true":
            return {"count": len(feats)}
        if p.get("returnIdsOnly") == "true":
            return {"objectIdFieldName": "OBJECTID", "objectIds": [f["attributes"]["OBJECTID"] for f in feats]}
        result = {"objectIdFieldName": "OBJECTID", "globalIdFieldName": "globalid", "geometryType": "esriGeometryMultiPatch",
                  "hasZ": True, "spatialReference": {"wkid": 4326}, "fields": FIELDS, "features": []}
        for f in feats:
            a = f["attributes"]
            x, y, z = a.get("ESRI3DO_OX") or 32.888, a.get("ESRI3DO_OY") or 40.002, a.get("ESRI3DO_OZ") or 0
            g = {"xmin": x - 0.0002, "ymin": y - 0.0002, "xmax": x + 0.0002, "ymax": y + 0.0002, "zmin": z, "zmax": z + 20,
                 "spatialReference": {"wkid": 4326}}
            feature = {"attributes": a, "geometry": g}
            if three_d:
                maps = [m for m in STATE["maps"].values() if m.get("parentGlobalId") == a.get("globalid")]
                feature["assetMaps"] = [{**m, "conversionStatus": "COMPLETED", "assetURL": f"{ORIGIN}/assets/{m.get('assetHash')}",
                                         "assetType": "3D_glb"} for m in maps]
            result["features"].append(feature)
        if three_d:
            result["assetMapFieldRoles"] = {"assetHash": "assetHash", "assetName": "assetName"}
        return result

    # ---- everything else is the server's ----------------------------------------------------------------------------
    def proxy(self, method, path, query, body):
        target = UPSTREAM + path + ("?" + query if query else "")
        req = urllib.request.Request(target, data=body if method == "POST" else None, method=method)
        req.add_header("Authorization", "Bearer " + TOKEN)
        if self.headers.get("Content-Type"):
            req.add_header("Content-Type", self.headers["Content-Type"])
        try:
            with urllib.request.urlopen(req, context=INSECURE, timeout=30) as r:
                data, code, ctype = r.read(), r.status, r.headers.get("Content-Type", "application/octet-stream")
                encoding = r.headers.get("Content-Encoding")
        except urllib.error.HTTPError as e:
            data, code, ctype, encoding = e.read(), e.code, e.headers.get("Content-Type", "text/plain"), e.headers.get("Content-Encoding")
        if "json" in ctype and not encoding:
            text = data.decode("utf-8", "replace").replace(UPSTREAM, ORIGIN).replace(UPSTREAM.split("//", 1)[1], ORIGIN.split("//", 1)[1])
            # The portal says it is all https at the server's own host; through the stub it is neither.
            # Finding: the SDK edits a scene layer only when its own fields name a GlobalID; the package types it GUID.
            if path.endswith("/SceneServer/layers/0") or path.endswith("/SceneServer"):
                text = text.replace('"name":"globalid","type":"esriFieldTypeGUID"', '"name":"globalid","type":"esriFieldTypeGlobalID"')
            # The scene layer's associated feature layer is this stub's fake multipatch layer, wherever its address is said.
            text = text.replace(ORIGIN + "/rest/services/hosted/ci_buildings/FeatureServer", ORIGIN + FAKE)
            # The scene layer's associated feature layer is this stub's fake multipatch layer.
            if "relatedItems" in path:
                doc = json.loads(text)
                for item in doc.get("relatedItems", []):
                    item["url"] = ORIGIN + FAKE
                    item["typeKeywords"] = list(set((item.get("typeKeywords") or []) + ["3DObject"]))
                text = json.dumps(doc)
            data = text.encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        if encoding:
            self.send_header("Content-Encoding", encoding)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(data)


if __name__ == "__main__":
    print(f"stub on {ORIGIN}, upstream {UPSTREAM}, item {ITEM}", flush=True)
    server = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    # https, as the SDK reaches a portal by https; the certificate is a throwaway one named by STUB_CERT and STUB_KEY.
    tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    tls.load_cert_chain(os.environ["STUB_CERT"], os.environ["STUB_KEY"])
    server.socket = tls.wrap_socket(server.socket, server_side=True)
    server.serve_forever()
