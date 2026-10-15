"""Prints the assertions an ETF result file says failed, by their label, with the validator's message arguments."""
import json
import sys


def index(node, found):
    if isinstance(node, dict):
        if "id" in node and "label" in node:
            found[node["id"]] = node
        for value in node.values():
            index(value, found)
    elif isinstance(node, list):
        for value in node:
            index(value, found)


for path in sys.argv[1:]:
    data = json.load(open(path, encoding="utf-8"))["EtfItemCollection"]
    items = {}
    index(data.get("referencedItems", {}), items)
    index(data.get("testRuns", {}), items)
    print("==", path)

    def walk(node):
        if isinstance(node, dict):
            ref = (node.get("resultedFrom") or {}).get("ref")
            if node.get("status") == "FAILED" and ref and "testAssertionResults" not in node \
                    and "testStepResults" not in node and "testCaseResults" not in node and "testModuleResults" not in node:
                label = items.get(ref, {}).get("label", ref)
                messages = (node.get("messages") or {}).get("message", [])
                messages = messages if isinstance(messages, list) else [messages]
                said = []
                for m in messages:
                    args = (m.get("translationArguments") or {}).get("argument", []) if isinstance(m, dict) else []
                    args = args if isinstance(args, list) else [args]
                    said.append(" ".join(str(a.get("$", a)) if isinstance(a, dict) else str(a) for a in args))
                print("  FAILED:", label, "--", " / ".join(said)[:400])
            for value in node.values():
                walk(value)
        elif isinstance(node, list):
            for value in node:
                walk(value)

    walk(data.get("referencedItems", {}))
