"""D-08: what a 30-second statement_timeout does to a query blocked on a lock.

The one case the statement-timeout benchmark did not construct. Two sessions: A takes
ACCESS EXCLUSIVE and holds it past the budget, B reads and is timed. Reported with the
SQLSTATE, because the server's error classifier switches on it and the sentence it
produces is written for a slow query rather than for a blocked one.
"""
import re
import subprocess
import sys
import time

sys.stdout.reconfigure(encoding="utf-8")

C = "gis-experiment-postgis"


def psql(sql, detach=False):
    cmd = ["docker", "exec"] + (["-d"] if detach else ["-i"]) + [
        C, "psql", "-U", "gis", "-d", "gis", "-v", "ON_ERROR_STOP=0", "-tAc", sql]
    out = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    return (out.stdout or "") + (out.stderr or "")


def hold(seconds):
    psql(f"begin; lock table d08_lock in access exclusive mode; select pg_sleep({seconds}); commit;",
         detach=True)
    time.sleep(2)
    held = psql("select count(*) from pg_locks l join pg_class c on c.oid=l.relation "
                "where c.relname='d08_lock' and l.mode='AccessExclusiveLock';").strip()
    return held


def release(needle):
    psql(f"select pg_terminate_backend(pid) from pg_stat_activity "
         f"where query like '%{needle}%' and pid<>pg_backend_pid();")
    time.sleep(1)


def case(label, budget, lock_timeout, hold_for, needle):
    held = hold(hold_for)
    print(f"\n=== {label} ===")
    print(f"  lock held by another session : {held}")

    # VERBOSITY verbose makes psql print the SQLSTATE, which is what the server switches on.
    sql = (f"set statement_timeout='{budget}'; set lock_timeout={lock_timeout}; "
           "select count(*) from d08_lock;")
    cmd = ["docker", "exec", "-i", C, "psql", "-U", "gis", "-d", "gis",
           "-v", "ON_ERROR_STOP=0", "-v", "VERBOSITY=verbose", "-tAc", sql]

    started = time.monotonic()
    out = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    elapsed = time.monotonic() - started

    text = (out.stdout or "") + (out.stderr or "")
    reason = re.search(r"canceling statement due to [a-z ]+", text)
    state = re.search(r"SQLSTATE\s*\(?([0-9A-Z]{5})\)?", text)

    print(f"  statement_timeout            : {budget}")
    print(f"  lock_timeout                 : {lock_timeout}")
    print(f"  blocked for                  : {elapsed:.2f} s")
    print(f"  reason                       : {reason.group(0) if reason else '<completed>'}")
    print(f"  SQLSTATE                     : {state.group(1) if state else '<none>'}")

    release(needle)
    return elapsed, (state.group(1) if state else None)


psql("drop table if exists d08_lock; "
     "create table d08_lock(id int primary key, g geometry(Point,4326)); "
     "insert into d08_lock select i, st_setsrid(st_makepoint(i%180, i%90),4326) "
     "from generate_series(1,1000) i;")

case("3s budget, lock held 90s", "3s", "0", 90, "pg_sleep(90)")
case("30s budget, lock held 120s -- the configured ceiling", "30s", "0", 120, "pg_sleep(120)")
case("30s budget with a 2s lock_timeout", "30s", "'2s'", 60, "pg_sleep(60)")

print("\n--- unblocked control, same statement ---")
started = time.monotonic()
psql("set statement_timeout='30s'; select count(*) from d08_lock;")
print(f"  unblocked                    : {time.monotonic() - started:.3f} s")

psql("drop table if exists d08_lock;")
