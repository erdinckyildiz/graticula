"""The fence a user tool's process puts up on itself before the tool is imported — ADR-223 §5.3 and §5.4.

In order: rlimits (memory, CPU seconds, file size, processes, open files); ``no_new_privs``; Landlock — read the Python
runtime and its libraries, read and write the run's directory, nothing else, and on a kernel with Landlock ABI 4 no TCP
at all; seccomp — no socket but AF_UNIX, and no ptrace, mount, namespaces, keyrings, bpf, module loading or another
process's memory. Every step is an unprivileged syscall a process may make on itself.

**Fails closed.** A step that cannot be taken raises FenceError naming it, and the runner then runs nothing: a kernel
without Landlock is a refusal with that reason, never a run without the fence.
"""
from __future__ import annotations

import ctypes
import os
import platform
import resource
import struct
import sys


class FenceError(RuntimeError):
    """A fence the process could not put up — the run does not go ahead."""


# ---------------------------------------------------------------------------------------------- the syscall numbers

_ARCH = platform.machine()
_X86 = _ARCH in ("x86_64", "AMD64")
_ARM = _ARCH in ("aarch64", "arm64")

# Landlock's are the same on every architecture (they were added after the tables were unified).
_LANDLOCK_CREATE_RULESET, _LANDLOCK_ADD_RULE, _LANDLOCK_RESTRICT_SELF = 444, 445, 446

# seccomp_data.arch values and the syscalls refused, per architecture.
_AUDIT_ARCH = 0xC000003E if _X86 else 0xC00000B7
_SOCKET = 41 if _X86 else 198
_REFUSED = (
    {"ptrace": 101, "mount": 165, "umount2": 166, "unshare": 272, "setns": 308, "keyctl": 250, "add_key": 248,
     "request_key": 249, "bpf": 321, "init_module": 175, "finit_module": 313, "delete_module": 176,
     "process_vm_readv": 310, "process_vm_writev": 311, "kexec_load": 246, "pivot_root": 155, "chroot": 161,
     "perf_event_open": 298, "userfaultfd": 323, "open_by_handle_at": 304}
    if _X86 else
    {"ptrace": 117, "mount": 40, "umount2": 39, "unshare": 97, "setns": 268, "keyctl": 219, "add_key": 217,
     "request_key": 218, "bpf": 280, "init_module": 105, "finit_module": 273, "delete_module": 106,
     "process_vm_readv": 270, "process_vm_writev": 271, "kexec_load": 104, "pivot_root": 41, "chroot": 51,
     "perf_event_open": 241, "userfaultfd": 282, "open_by_handle_at": 265})

_libc = None


def _syscall():
    global _libc
    if _libc is None:
        _libc = ctypes.CDLL(None, use_errno=True)
        _libc.syscall.restype = ctypes.c_long
        _libc.prctl.restype = ctypes.c_int
        # Unsigned longs, so an address is passed whole: an int would cut a 64-bit pointer in half.
        _libc.prctl.argtypes = [ctypes.c_int, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_ulong]
    return _libc


# ---------------------------------------------------------------------------------------------- the limits

def limit(memory_bytes: int, cpu_seconds: int, file_bytes: int, processes: int, open_files: int = 256) -> None:
    """
    rlimits, soft and hard alike, so the tool cannot raise them back. **RLIMIT_NPROC counts every task of the OS user**,
    the server's threads included, so it is a ceiling against a fork bomb, not a count of the tool's own processes.
    """
    for which, value in ((resource.RLIMIT_AS, memory_bytes), (resource.RLIMIT_CPU, cpu_seconds),
                         (resource.RLIMIT_FSIZE, file_bytes), (resource.RLIMIT_NPROC, processes),
                         (resource.RLIMIT_NOFILE, open_files), (resource.RLIMIT_CORE, 0)):
        try:
            resource.setrlimit(which, (value, value))
        except (ValueError, OSError) as e:
            raise FenceError(f"the limit {which} could not be set: {e}") from e


def no_new_privileges() -> None:
    if _syscall().prctl(38, 1, 0, 0, 0) != 0:  # PR_SET_NO_NEW_PRIVS
        raise FenceError(f"no_new_privs could not be set (errno {ctypes.get_errno()})")


# ---------------------------------------------------------------------------------------------- Landlock

_READ = (1 << 0) | (1 << 2) | (1 << 3)          # EXECUTE | READ_FILE | READ_DIR
_ALL_V1 = (1 << 13) - 1                          # the thirteen rights of ABI 1
_REFER, _TRUNCATE, _IOCTL_DEV = 1 << 13, 1 << 14, 1 << 15
_BIND_TCP, _CONNECT_TCP = 1 << 0, 1 << 1


def landlock_abi() -> int:
    """The kernel's Landlock ABI version, or 0 when it has none or it is switched off."""
    version = _syscall().syscall(_LANDLOCK_CREATE_RULESET, None, ctypes.c_size_t(0), ctypes.c_uint32(1))
    return max(int(version), 0)


def landlock(readable: list[str], writable: list[str]) -> int:
    """Only these paths: read beneath ``readable``, read and write beneath ``writable``. Answers the ABI used."""
    abi = landlock_abi()
    if abi < 1:
        raise FenceError("this kernel has no Landlock (Linux 5.13 or later with it enabled), so the filesystem cannot be fenced")

    handled = _ALL_V1 | (_REFER if abi >= 2 else 0) | (_TRUNCATE if abi >= 3 else 0) | (_IOCTL_DEV if abi >= 5 else 0)
    network = (_BIND_TCP | _CONNECT_TCP) if abi >= 4 else 0
    attr = struct.pack("<QQ", handled, network) if abi >= 4 else struct.pack("<Q", handled)
    buffer = ctypes.create_string_buffer(attr, len(attr))
    ruleset = _syscall().syscall(_LANDLOCK_CREATE_RULESET, buffer, ctypes.c_size_t(len(attr)), ctypes.c_uint32(0))
    if ruleset < 0:
        raise FenceError(f"a Landlock ruleset could not be made (errno {ctypes.get_errno()})")

    try:
        for paths, rights in ((readable, _READ), (writable, handled)):
            for path in paths:
                if not os.path.exists(path):
                    continue
                fd = os.open(path, os.O_PATH | os.O_CLOEXEC)
                try:
                    allowed = rights if os.path.isdir(path) else rights & ((1 << 0) | (1 << 1) | (1 << 2) | _TRUNCATE)
                    rule = struct.pack("<Qi", allowed, fd)
                    rule_buffer = ctypes.create_string_buffer(rule, len(rule))
                    if _syscall().syscall(_LANDLOCK_ADD_RULE, ctypes.c_int(ruleset), ctypes.c_int(1), rule_buffer, ctypes.c_uint32(0)) != 0:
                        raise FenceError(f"'{path}' could not be added to the Landlock ruleset (errno {ctypes.get_errno()})")
                finally:
                    os.close(fd)

        if _syscall().syscall(_LANDLOCK_RESTRICT_SELF, ctypes.c_int(ruleset), ctypes.c_uint32(0)) != 0:
            raise FenceError(f"Landlock could not restrict the process (errno {ctypes.get_errno()})")
    finally:
        os.close(ruleset)

    return abi


# ---------------------------------------------------------------------------------------------- seccomp

def _instruction(code: int, jt: int, jf: int, k: int) -> bytes:
    return struct.pack("<HBBI", code, jt, jf, k)


_LD_W_ABS, _JEQ_K, _RET_K = 0x20, 0x15, 0x06
_ALLOW, _KILL = 0x7FFF0000, 0x80000000
_ERRNO = 0x00050000                               # SECCOMP_RET_ERRNO | errno
_EACCES, _EPERM = 13, 1


def seccomp() -> None:
    """No socket outside AF_UNIX (answered EACCES), the dangerous calls answered EPERM, another architecture killed."""
    refused = list(_REFUSED.values())
    program = [
        _instruction(_LD_W_ABS, 0, 0, 4),                        # arch
        _instruction(_JEQ_K, 1, 0, _AUDIT_ARCH),
        _instruction(_RET_K, 0, 0, _KILL),
        _instruction(_LD_W_ABS, 0, 0, 0),                        # nr
    ]
    # socket(domain, …): AF_UNIX allowed, anything else refused.
    program += [
        _instruction(_JEQ_K, 0, 4, _SOCKET),
        _instruction(_LD_W_ABS, 0, 0, 16),                       # args[0], low word
        _instruction(_JEQ_K, 1, 0, 1),                           # AF_UNIX
        _instruction(_RET_K, 0, 0, _ERRNO | _EACCES),
        _instruction(_RET_K, 0, 0, _ALLOW),
    ]
    # Not socket: the jump above lands here with the syscall number still in the accumulator.
    for nr in refused:
        program.append(_instruction(_JEQ_K, 0, 1, nr))
        program.append(_instruction(_RET_K, 0, 0, _ERRNO | _EPERM))
    program.append(_instruction(_RET_K, 0, 0, _ALLOW))

    code = b"".join(program)
    filters = ctypes.create_string_buffer(code, len(code))
    prog = struct.pack("<HxxxxxxQ", len(program), ctypes.addressof(filters))
    prog_buffer = ctypes.create_string_buffer(prog, len(prog))
    if _syscall().prctl(22, 2, ctypes.addressof(prog_buffer), 0, 0) != 0:  # PR_SET_SECCOMP, SECCOMP_MODE_FILTER
        raise FenceError(f"the seccomp filter could not be installed (errno {ctypes.get_errno()})")


# ---------------------------------------------------------------------------------------------- all of it

def up(run_directory: str, memory_bytes: int, cpu_seconds: int, file_bytes: int, processes: int) -> dict:
    """The whole fence, in order. Answers what was put up, for the run's first message."""
    if sys.platform != "linux":
        raise FenceError(f"user tools are fenced with Linux's Landlock and seccomp, and this is {sys.platform}")

    readable = sorted({sys.prefix, sys.base_prefix, sys.exec_prefix, os.path.dirname(os.path.dirname(__file__)),
                       "/usr/lib", "/usr/lib64", "/lib", "/lib64", "/usr/share/proj", "/usr/share/zoneinfo",
                       "/etc/ld.so.cache", "/etc/localtime", "/dev/null", "/dev/urandom"} | set(_site_paths()))
    limit(memory_bytes, cpu_seconds, file_bytes, processes)
    no_new_privileges()
    abi = landlock(readable, [run_directory])
    seccomp()
    return {"landlock": abi, "network": "refused by seccomp" + (" and Landlock" if abi >= 4 else ""),
            "memory": memory_bytes, "cpu": cpu_seconds, "file": file_bytes, "processes": processes}


def _site_paths():
    import site
    try:
        return [*site.getsitepackages(), site.getusersitepackages()]
    except AttributeError:
        return []
