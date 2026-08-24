# -*- coding: utf-8 -*-
"""What the reader child actually holds while it answers ping."""
import json, subprocess, sys, time, os

exe = r"c:\Personal\Projects\GIS\tests\Graticula.Host.Tests\bin\Release\net9.0\importer\Graticula.Import.Reader.exe"
if not os.path.exists(exe):
    sys.exit("no reader at " + exe)

env = dict(os.environ)
env["DOTNET_GCHeapHardLimit"] = format(512 << 20, "X")
env["DOTNET_gcServer"] = "0"
env["DOTNET_gcConcurrent"] = "0"
env["OGR_ORGANIZE_POLYGONS"] = "DEFAULT"

import ctypes
from ctypes import wintypes

class COUNTERS(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD),
                ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t)]

psapi = ctypes.WinDLL("psapi.dll")
kernel = ctypes.WinDLL("kernel32.dll")

p = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, env=env)
handle = kernel.OpenProcess(0x1000 | 0x0010, False, p.pid)

started = time.perf_counter()
p.stdin.write(b'{"op":"ping"}\n')
p.stdin.flush()

peak = 0
samples = 0
import threading
stop = threading.Event()

def watch():
    global peak, samples
    c = COUNTERS(); c.cb = ctypes.sizeof(COUNTERS)
    while not stop.is_set():
        if psapi.GetProcessMemoryInfo(handle, ctypes.byref(c), ctypes.sizeof(c)):
            peak = max(peak, c.WorkingSetSize)
            samples += 1
        time.sleep(0.005)

t = threading.Thread(target=watch); t.start()
line = p.stdout.readline()
p.stdin.close()
p.wait()
took = time.perf_counter() - started
stop.set(); t.join()

answer = json.loads(line)
print("ping ok      :", answer.get("ok"))
print("gdal         :", answer.get("gdal"))
print("priority     :", answer.get("priority"))
print("took         : %.3f s" % took)
print("samples      :", samples)
print("peak working : %.1f MB" % (peak / 1048576.0))
print("ceiling      : %.0f MB" % (2048.0))
print("headroom     : %.0fx" % (2048.0 / (peak / 1048576.0)))
