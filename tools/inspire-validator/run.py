"""ADR-172 condition 1: runs the INSPIRE validator's View (WMS) and Download (pre-defined WFS) suites against a service.

Uses an ETF validator's v2 API — the central INSPIRE Reference Validator was discontinued on 2026-04-01, and a
deployment run by LifeWatch ERIC still answers. Starts both runs, waits for them, and prints each assertion that did
not pass with its message, then writes the full results to INSPIRE_RESULTS (or the working directory).

    python run.py <validator v2 base> <WMS GetCapabilities URL> <WFS GetCapabilities URL>
"""
import json
import os
import sys
import time
import urllib.request

base, wms, wfs = sys.argv[1].rstrip("/"), sys.argv[2], sys.argv[3]
SUITES = {
    "View Service - WMS": ("EIDeec9d674-d94b-4d8d-b744-1309c6cae1d2", wms),
    "Download Service - Pre-defined WFS": ("EID174edf55-699b-446c-968c-1892a4d8d5bd", wfs),
}
# Results go to INSPIRE_RESULTS, or the working directory: they are large and not kept in the repository.
here = os.environ.get("INSPIRE_RESULTS", os.getcwd())


def call(method, path, body=None):
    request = urllib.request.Request(
        base + path, method=method, data=None if body is None else json.dumps(body).encode(),
        headers={"Content-Type": "application/json", "Accept": "application/json"})
    with urllib.request.urlopen(request, timeout=120) as response:
        return json.loads(response.read() or b"{}")


runs = {}
for label, (suite, endpoint) in SUITES.items():
    started = call("POST", "/TestRuns", {
        "label": f"Graticula ADR-172 {label}",
        "executableTestSuiteIds": [suite],
        "arguments": {},
        "testObject": {"resources": {"serviceEndpoint": endpoint}},
    })
    runs[label] = started["EtfItemCollection"]["testRuns"]["TestRun"]["id"]
    print("started", label, runs[label])

for label, run in runs.items():
    while True:
        progress = call("GET", f"/TestRuns/{run}/progress")
        if progress.get("val") is not None and progress.get("val") == progress.get("max"):
            break
        time.sleep(10)

    result = call("GET", f"/TestRuns/{run}.json")
    with open(os.path.join(here, f"result-{run}.json"), "w", encoding="utf-8") as f:
        json.dump(result, f, indent=1)

    collection = result["EtfItemCollection"]
    status = collection["testRuns"]["TestRun"].get("status")
    print(f"\n== {label}: {status}")

    # Every assertion that did not pass, with its message.
    def walk(node, path=""):
        if isinstance(node, dict):
            if "status" in node and node.get("status") not in ("PASSED", "PASSED_MANUAL", "SKIPPED", "NOT_APPLICABLE") \
                    and "testAssertionResults" not in node and "testStepResults" not in node and "testCaseResults" not in node:
                messages = node.get("messages", {}).get("message", [])
                messages = messages if isinstance(messages, list) else [messages]
                text = " | ".join(str(m.get("translationArguments", m)) if isinstance(m, dict) else str(m) for m in messages)
                print(f"  {node.get('status')}: {node.get('resultedFrom', {}).get('href', node.get('id', ''))[-60:]} {text[:300]}")
            for value in node.values():
                walk(value)
        elif isinstance(node, list):
            for value in node:
                walk(value)

    walk(collection.get("testTaskResults", {}))
