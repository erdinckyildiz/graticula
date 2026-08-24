"""D-31, third half: a worker dies, and what the pool and the machine do next."""
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import importlib.util  # noqa: E402

_spec = importlib.util.spec_from_file_location(
    "concurrency",
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "concurrency.py"))
probe = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(probe)


def show(label):
    print(f"  {label:30s} {probe.workers() or 'none'}")
    sys.stdout.flush()


if __name__ == "__main__":
    probe.TOKEN = probe.sign_in()

    body = probe.payload(int(os.environ.get("D31_VERTICES", "300000")))

    print("### warm, so both workers exist and hold what they have grown to")
    probe.burst("intersect", body, 4)

    held = probe.workers()
    show("before the kill")

    if not held:
        print("  no worker to kill")
        raise SystemExit(0)

    victim = held[0].split()[0]

    print(f"### killing worker {victim}")
    subprocess.run(["powershell", "-NoProfile", "-Command",
                    f"Stop-Process -Id {victim} -Force"], capture_output=True)

    for wait in (1, 3, 10):
        time.sleep(wait if wait == 1 else 2 if wait == 3 else 7)
        show(f"t+{wait}s after the kill")

    print("### the next request, which is a cold start under a warm pool")
    code, size, ms, head = probe.one(body, timeout=120)
    print(f"  answered {code} in {ms:.0f} ms, {size} bytes")
    show("after it")

    print("### and the load again, to see the pool back at two")
    probe.burst("intersect", body, 4)
    show("after the load")
