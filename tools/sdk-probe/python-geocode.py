"""ADR-258 §5.8: the ArcGIS API for Python finding this server's locator through the portal and geocoding a table with it.

`get_geocoders(gis)` reads portals/self's helperServices.geocode and builds a Geocoder for each entry; `batch_geocode`
reads the locator's MaxBatchSize and posts geocodeAddresses. Run it anonymously (a server with
Graticula:Geocoding:Sharing=public) and signed in; with no user it is anonymous. Needs `pip install arcgis` (measured
with 2.4.x) and a Photon behind the server — photon-stub.py beside this file is enough to see the wire format:

    python -X utf8 python-geocode.py <server> [<user> <password>]

Prints what was found and answered; never a token.
"""
import sys
import warnings

warnings.filterwarnings("ignore")
import urllib3  # noqa: E402

urllib3.disable_warnings()

from arcgis.gis import GIS  # noqa: E402
from arcgis.geocoding import batch_geocode, geocode, get_geocoders  # noqa: E402

server = sys.argv[1]
gis = GIS(server, sys.argv[2], sys.argv[3], verify_cert=False) if len(sys.argv) > 3 else GIS(server, verify_cert=False)
print("signed in as", gis.users.me.username if gis.users.me else None)

geocoders = get_geocoders(gis)
print("get_geocoders:", [g.url for g in geocoders])
if not geocoders:
    sys.exit("the portal names no locator")

geocoder = geocoders[0]
print("MaxBatchSize", geocoder.properties.locatorProperties.MaxBatchSize,
      "SuggestedBatchSize", geocoder.properties.locatorProperties.SuggestedBatchSize)

# The default geocoder of the GIS is the first one the portal names, so geocode() needs no geocoder argument.
print("geocode (default geocoder):", [(c["address"], c["score"]) for c in geocode("Kizilay Ankara", max_locations=2)])

addresses = [
    "Kızılay, Ankara",
    {"Address": "Mühürdar Caddesi 22", "City": "İstanbul"},
    "photon-fails",
    "",
]
results = batch_geocode(addresses, geocoder=geocoder, out_sr=3857)
for result in results:
    a = result["attributes"]
    print("batch:", a["ResultID"], a["Status"], a["Score"], repr(result["address"]),
          "location" in result and round(result["location"]["x"]))

featureset = batch_geocode(addresses[:2], geocoder=geocoder, as_featureset=True)
print("batch as feature set:", len(featureset.features), featureset.spatial_reference)
