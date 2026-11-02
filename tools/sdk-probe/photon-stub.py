"""A stand-in for Photon on 127.0.0.1:2399 for the GeocodeServer probes of ADR-258 (2026-11-02, 2026-11-03).

Answers /api and /reverse with hand-written GeoJSON in the shape Photon 1.3 answered with on the VPS
(docs/research/photon-turkiye-2026-11-02.md). /api returns the places that carry any word of the query (the first
three letters, so type-ahead finds them), honours `limit`, `layer` and `countrycode` roughly as Photon documents them
(docs/api-v1.md), and answers everything when nothing matches — so a client always has something to draw. A query
containing `photon-fails` is answered 502, for the batch's one-record-fails case.

    python photon-stub.py [port]

Then start the server with Graticula__Geocoding__PhotonUrl=http://127.0.0.1:2399/ (and
Graticula__Geocoding__Sharing=public for an anonymous Maps SDK page).
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

PLACES = [
    {"type": "Feature", "geometry": {"type": "Point", "coordinates": [32.8597, 39.9334]},
     "properties": {"osm_type": "R", "osm_id": 223422, "osm_key": "place", "osm_value": "city", "type": "city",
                    "name": "Ankara", "state": "Ankara", "country": "Türkiye", "countrycode": "TR",
                    "extent": [32.5, 40.1, 33.1, 39.7]}},
    {"type": "Feature", "geometry": {"type": "Point", "coordinates": [32.8541, 39.9208]},
     "properties": {"osm_type": "N", "osm_id": 21512345, "osm_key": "place", "osm_value": "suburb",
                    "type": "district", "name": "Kızılay", "district": "Çankaya", "county": "Çankaya",
                    "city": "Ankara", "state": "Ankara", "country": "Türkiye", "countrycode": "TR",
                    "postcode": "06420", "extent": [32.845, 39.927, 32.862, 39.914]}},
    {"type": "Feature", "geometry": {"type": "Point", "coordinates": [32.8550, 39.9150]},
     "properties": {"osm_type": "N", "osm_id": 555, "osm_key": "highway", "osm_value": "bus_stop", "type": "house",
                    "name": "Atatürk Bulvarı", "street": "Atatürk Bulvarı", "district": "Çankaya", "city": "Ankara",
                    "country": "Türkiye", "countrycode": "TR"}},
    {"type": "Feature", "geometry": {"type": "Point", "coordinates": [29.0244, 40.9903]},
     "properties": {"osm_type": "W", "osm_id": 777, "osm_key": "building", "osm_value": "apartments",
                    "type": "house", "housenumber": "22", "street": "Mühürdar Caddesi", "locality": "Caferağa",
                    "district": "Kadıköy", "city": "İstanbul", "state": "İstanbul", "country": "Türkiye",
                    "countrycode": "TR", "postcode": "34710"}},
]


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        url = urlparse(self.path)
        q = parse_qs(url.query)
        sys.stderr.write(f"photon-stub {url.path} {sorted(q)}\n")
        if url.path == "/api":
            text = (q.get("q") or [""])[0].lower()
            if "photon-fails" in text:
                self.send_response(502)
                self.end_headers()
                return
            limit = int((q.get("limit") or ["10"])[0])
            layers = set(q.get("layer") or [])
            countries = {c.upper() for c in q.get("countrycode") or []}
            pool = [f for f in PLACES
                    if (not layers or f["properties"]["type"] in layers)
                    and (not countries or f["properties"]["countrycode"] in countries)]
            words = [w[:3] for w in text.replace(",", " ").split() if w]
            features = [f for f in pool if any(w in json.dumps(f, ensure_ascii=False).lower() for w in words)] or pool
            body = {"type": "FeatureCollection", "features": features[:limit]}
        elif url.path == "/reverse":
            body = {"type": "FeatureCollection", "features": [PLACES[3]]}
        else:
            self.send_response(404)
            self.end_headers()
            return
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1]) if len(sys.argv) > 1 else 2399), Handler).serve_forever()
