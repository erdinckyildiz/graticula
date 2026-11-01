# Photon with a Türkiye extract, measured on the showcase's VPS — 2026-11-02

Asked by the owner on 2026-11-01: *"photon'u deneyelim vps te"*, after asking for an open-source, worldwide geocoder.
This is a measurement, not a decision. Geocoding is deferred in [v1-scope](../v1-scope.md) and its shape is
[Q-84](../open-questions.md); whether Graticula fronts a geocoder is [Q-226](../open-questions.md).

## What was run

- **Photon 1.3.0** (komoot, Apache-2.0, Java 21), `eclipse-temurin:21-jre` on arm64 (6 cores, 7.7 GB RAM shared with
  other services; no swap).
- **Data:** GraphHopper's Photon 1.0 dump for Türkiye, `photon-dump-turkey-1.0-latest.jsonl.zst`, 142.5 MB, dated
  2026-10-26 — OpenStreetMap data, ODbL.
- **Capped so the host was never pressed:** import in a container limited to 1.5 GB (`-Xmx1g`, 2 CPUs); the server in one
  limited to 1.2 GB (`-Xmx768m`, 2 CPUs), listening on 127.0.0.1 only.
- Import: `zstd -d | java -jar photon-1.3.0.jar import -import-file - -languages tr,en`.

## Measured

| | |
|---|---|
| Import | 530 s, about 1,150,000 documents, ~2,320 documents/s, peak ~960 MB of its 1.5 GB cap |
| Index on disk | 464 MB |
| Server start to first answer | 13 s |
| Server memory, after 200 searches | 356 MB |
| 200 searches, five rotating queries | p50 44.6 ms, p95 88.7 ms, max 160 ms |
| First, cold queries | 160–300 ms |

| Query | Top answer |
|---|---|
| `Kızılay Ankara` | Kızılay / Çankaya / Ankara (suburb) |
| `Kizilay` (no Turkish letters) | Kızılay / Çankaya / Ankara |
| `Bağdat Caddesi Kadıköy` | Bağdat Caddesi / Zühtüpaşa / Kadıköy / İstanbul |
| `istiklal cad beyoglu` | İstiklal Caddesi / Kuloğlu / Beyoğlu / İstanbul |
| `Trabzon Ortahisar` | Trabzon / Ortahisar (city) |
| `Ank` (type-ahead) | Ankara (city), Ankara (province) — 49 ms |
| `Atatürk Bulvarı 100 Ankara` | a bus stop on Atatürk Bulvarı — **not house number 100** |
| reverse 39.9208, 32.8541 | Ankara / Çankaya |
| reverse 40.9903, 29.0244 | Mühürdar Caddesi 22 / Caferağa / Kadıköy |

## What it says

- **Places, streets and districts are good, including type-ahead and spelling without Turkish letters.** Reverse
  geocoding reaches the house number where OSM has one.
- **House numbers are the weak part, and that is the data, not the engine:** OSM's Türkiye address points are sparse.
  An organisation that holds its own address points (a municipality, a utility) would still want a locator over *its*
  data — Q-84's "the engine is ours, the data is the customer's" — and Photon does not take a customer's table.
- **Size scales by data, not by engine:** the README gives about 95 GB of disk and 64 GB of RAM recommended for the
  planet. Türkiye fits the showcase's VPS beside everything else; Europe or the planet would need a machine of their own.
- **ODbL** asks for attribution in what is shown; serving answers does not make a derived database public.

## Left on the VPS

`/opt/photon-probe` (jar, dump, the 464 MB index, `import.sh`, `probe.py`). The server is stopped; starting it again is
one `docker run` and 13 seconds.
