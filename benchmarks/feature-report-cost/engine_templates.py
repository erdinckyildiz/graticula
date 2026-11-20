"""Graticula.Report.Pdf over each filled template: time and the child's peak working set (polled every 20 ms)."""
import glob, json, os, subprocess, sys, time
import psutil
exe, source, out = sys.argv[1:4]
os.makedirs(out, exist_ok=True)
for path in sorted(glob.glob(os.path.join(source, "*.docx"))):
    target = os.path.join(out, os.path.basename(path)[:-5] + ".pdf")
    if os.path.exists(target):
        os.remove(target)
    started = time.perf_counter()
    child = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    child.stdin.write(json.dumps({"input": path, "output": target}) + "\n"); child.stdin.flush()
    watched, peak = psutil.Process(child.pid), 0
    while child.poll() is None:
        try:
            peak = max(peak, watched.memory_info().peak_wset)
        except psutil.Error:
            pass
        time.sleep(0.02)
    answer = child.stdout.read().strip().splitlines()[-1]
    print(f"{os.path.basename(path)[:8]} {time.perf_counter() - started:6.2f}s peak {peak / 1e6:5.0f} MB {answer[:90]}")
