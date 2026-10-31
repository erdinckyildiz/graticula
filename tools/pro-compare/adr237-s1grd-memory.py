"""grapy's memory on a real Sentinel-1 IW GRD (ADR-237 condition 3): run RemoveThermalNoise on the whole SAFE in a child
process and watch its private bytes; stop it once it passes ADR-223's 2 GB by a margin, so the measurement cannot take
the machine with it. Windows only (it reads the child's memory through psapi; a venv's python.exe is a launcher, so the child also reports its own peak).

s1_grapy_memory.py <worktree> <SAFE folder> <out folder> [stop at MB, default 3000]"""
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

worktree, safe, out = sys.argv[1], sys.argv[2], sys.argv[3]
stop_mb = float(sys.argv[4]) if len(sys.argv) > 4 else 3000
os.makedirs(out, exist_ok=True)
child = f"""
import sys, time
sys.path.insert(0, {os.path.join(worktree, 'src', 'grapy')!r})
import grapy as arcpy
t = time.time()
arcpy.ia.RemoveThermalNoise({safe!r}, {os.path.join(out, 'noise.tif')!r}, "VV")
print("finished", time.time() - t)
import ctypes, ctypes.wintypes as wt
class C(ctypes.Structure):
    _fields_ = [("cb", wt.DWORD), ("PageFaultCount", wt.DWORD)] + [(n, ctypes.c_size_t) for n in (
        "PeakWorkingSetSize", "WorkingSetSize", "a", "b", "c", "d", "PagefileUsage", "PeakPagefileUsage", "PrivateUsage")]
c = C(); c.cb = ctypes.sizeof(C)
k = ctypes.windll.kernel32
k.GetCurrentProcess.restype = ctypes.c_void_p
k.K32GetProcessMemoryInfo.argtypes = [ctypes.c_void_p, ctypes.c_void_p, wt.DWORD]
print("query ok", k.K32GetProcessMemoryInfo(k.GetCurrentProcess(), ctypes.byref(c), c.cb))
print("self-measured peak commit MB", c.PeakPagefileUsage / 2 ** 20, "peak working set MB", c.PeakWorkingSetSize / 2 ** 20)
"""


class Counters(ctypes.Structure):
    _fields_ = [("cb", wt.DWORD), ("PageFaultCount", wt.DWORD), ("PeakWorkingSetSize", ctypes.c_size_t),
                ("WorkingSetSize", ctypes.c_size_t), ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPagedPoolUsage", ctypes.c_size_t), ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
                ("QuotaNonPagedPoolUsage", ctypes.c_size_t), ("PagefileUsage", ctypes.c_size_t),
                ("PeakPagefileUsage", ctypes.c_size_t), ("PrivateUsage", ctypes.c_size_t)]


kernel, psapi = ctypes.windll.kernel32, ctypes.windll.psapi
process = subprocess.Popen([sys.executable, "-c", child], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
handle = kernel.OpenProcess(0x0400 | 0x0010, False, process.pid)
counters = Counters()
counters.cb = ctypes.sizeof(Counters)
start, peak, stopped = time.time(), 0.0, False
while process.poll() is None:
    if psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
        peak = max(peak, counters.PeakPagefileUsage / 2 ** 20)
        if counters.PrivateUsage / 2 ** 20 > stop_mb:
            process.kill()
            stopped = True
            break
    time.sleep(0.2)
output = process.communicate()[0]
print(output[-2000:])
print(f"peak private bytes {peak:.0f} MB after {time.time() - start:.1f} s; "
      f"{'stopped past ' + str(int(stop_mb)) + ' MB' if stopped else 'ran to the end'}; ADR-223 limit 2048 MB")
