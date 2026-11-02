"""The ArcGIS veteran's review of v1.0.499: four portal doors, driven through the ArcGIS API for Python as it is.

1. A date-range search (ADR-214 §5b): `modified:[0 TO 9999999999999]` finds what `*` finds.
2. The recycle bin (ADR-214 §5c): `item.delete()` puts a web map in the bin, `user.recyclebin.content` lists it,
   `RecycleItem.restore()` brings it back, and `item.delete(permanent=True)` sends `permanentDelete` and leaves nothing.
3. A group's members (ADR-256 §5a): `group.get_members()` and `group.user_list()`.
4. Overwrite (ADR-257 §12): a CSV is added and published, then `FeatureLayerCollection.manager.overwrite()` with a
   changed CSV of the same name, and the layer is counted before and after.

Everything made is deleted at the end. Needs `pip install arcgis` (measured with 2.4.3). Signs in with a token from this
server's /rest/auth/login, as python-append.py does:

    python python-portal-doors.py <server> <user> <password>
"""
import json
import os
import ssl
import sys
import tempfile
import urllib.request
import uuid
import warnings

warnings.filterwarnings("ignore")

from arcgis.gis import GIS, ItemProperties, ItemTypeEnum
from arcgis.features import FeatureLayerCollection

server, user, password = sys.argv[1:4]

answer = urllib.request.urlopen(urllib.request.Request(
    server + "/rest/auth/login", data=json.dumps({"name": user, "password": password}).encode(),
    headers={"Content-Type": "application/json"}), context=ssl._create_unverified_context())
gis = GIS(server, token=json.load(answer)["token"], verify_cert=False)
me = gis.users.me
print("signed in as", me.username)
run = uuid.uuid4().hex[:8]

# 1. Ranges.
everything = len(gis.content.search("*", max_items=1000))
ranged = len(gis.content.search("modified:[0 TO 9999999999999]", max_items=1000))
print(f"1. search '*' {everything}, modified range {ranged}")

# 2. The recycle bin.
print("2. recycleBinEnabled", gis.properties.get("recycleBinEnabled"))
empty = {"operationalLayers": [], "baseMap": {"baseMapLayers": [], "title": "None"}, "version": "2.31"}
kept = gis.content.add({"type": "Web Map", "title": f"zz_bin_kept_{run}", "text": json.dumps(empty)})
gone = gis.content.add({"type": "Web Map", "title": f"zz_bin_gone_{run}", "text": json.dumps(empty)})
print("   delete", kept.delete())
binned = [r for r in me.recyclebin.content if r.properties["id"] == kept.id]
print("   in the bin", len(binned), binned[0].properties["type"] if binned else None)
restored = binned[0].restore(None) if binned else None
print("   restored", restored.id == kept.id if restored else None, gis.content.get(kept.id) is not None)
print("   permanent delete", gone.delete(permanent=True))
print("   left in the bin", sum(1 for r in me.recyclebin.content if r.properties["id"] in (gone.id,)))
kept.delete(permanent=True)

# 3. Group members.
group = gis.groups.create(title=f"ZZ Members {run}", tags="probe", access="org")
members = group.get_members()
print("3. get_members", members.get("owner"), members.get("admins"), members.get("users"))
listing = group.user_list()
print("   user_list owner", (listing.get("owner") or {}).get("memberType"), "users", len(listing.get("users", [])))
group.delete()

# 4. Overwrite.
folder = tempfile.mkdtemp()
name = f"zz_over_{run}.csv"
path = os.path.join(folder, name)

def write(rows):
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write("name,x,y\n")
        for i in range(rows):
            f.write(f"p{i},{28.9 + i / 100},{41.0 + i / 100}\n")

write(3)
csv_item = gis.content.folders.get().add(ItemProperties(title=f"zz_over_{run}", item_type=ItemTypeEnum.CSV), file=path).result()
service_item = csv_item.publish()
layer = service_item.layers[0]
print("4. published", service_item.type, "count", layer.query(where="1=1", return_count_only=True))
print("   related forward", [i.type for i in service_item.related_items("Service2Data", "forward")],
      "reverse", [i.type for i in csv_item.related_items("Service2Data", "reverse")])
write(5)
flc = FeatureLayerCollection.fromitem(service_item)
print("   overwrite", flc.manager.overwrite(path))
print("   count after", FeatureLayerCollection.fromitem(gis.content.get(service_item.id)).layers[0]
      .query(where="1=1", return_count_only=True), "same item", gis.content.get(service_item.id) is not None)
service_item.delete(permanent=True)
csv_item.delete(permanent=True)
