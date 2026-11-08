"""
Builds the list of Fast Flags each Fluxer client really supports.

    python Scripts/FluxerFlags/dump_flags.py [path to a Versions folder]

The clients are packed, so their flag names are only readable once they're running. This starts each
player client with a dummy join link (it fails to log in, which is fine), reads every identifier-shaped
string out of its memory, and writes them to Bloxstrap/Resources/FluxerFlags/<year>.txt.gz.

FluxStrap uses these lists to only hand each client the flags it knows about. Re-run this whenever
Fluxer ships a new client build. Windows only; the clients briefly open a window.
"""

import ctypes
import ctypes.wintypes as wt
import gzip
import os
import re
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "Bloxstrap" / "Resources" / "FluxerFlags"

CLIENTS = {
    "2019": ("player2019", "FluxerPlayerBeta.exe", "short"),
    "2020": ("player2020", "FluxerPlayerBeta.exe", "long"),
    "2021": ("player2021", "FluxerPlayerBeta.exe", "short"),
    "2022": ("player2022", "RobloxPlayerBeta.exe", "app"),
}

AUTH = "https://fluxr.lol/Login/Negotiate.ashx"
JOIN = "https://fluxr.lol/Game/PlaceLauncher.ashx?request=RequestGame&placeId=1&isPartyLeader=false&gender=&isTeleport=true"

NUL = bytes([0])
NAME = re.compile(rb"[A-Z][A-Za-z0-9_]{5,90}(?=" + NUL + rb")")
WIDE = re.compile(rb"(?:[A-Z]" + NUL + rb")(?:[A-Za-z0-9_]" + NUL + rb"){5,90}(?=" + NUL + NUL + rb")")

# flag names are letters and digits starting with a capital (acronyms like CSG or FRM included),
# with at least one lowercase letter, which filters out most of the noise in memory
FLAG_SHAPE = re.compile(r"^(?=[A-Z0-9_]*[a-z])[A-Z][A-Za-z0-9_]{5,80}$")

k32 = ctypes.WinDLL("kernel32", use_last_error=True)


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p), ("AllocationProtect", wt.DWORD),
                ("PartitionId", wt.WORD), ("RegionSize", ctypes.c_size_t), ("State", wt.DWORD), ("Protect", wt.DWORD),
                ("Type", wt.DWORD)]


k32.OpenProcess.restype = wt.HANDLE
k32.VirtualQueryEx.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.POINTER(MBI), ctypes.c_size_t]
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]


def read_names(pid):
    handle = k32.OpenProcess(0x0410, False, pid)  # QUERY_INFORMATION | VM_READ
    names = set()
    address, mbi = 0, MBI()

    while address < 0x7FFFFFFF and k32.VirtualQueryEx(handle, ctypes.c_void_p(address), ctypes.byref(mbi), ctypes.sizeof(mbi)):
        size = mbi.RegionSize
        readable = mbi.State == 0x1000 and not (mbi.Protect & 0x101) and mbi.Protect != 0

        if readable and size < 256 * 1024 * 1024:
            buffer = ctypes.create_string_buffer(size)
            read = ctypes.c_size_t()
            if k32.ReadProcessMemory(handle, ctypes.c_void_p(address), buffer, size, ctypes.byref(read)) and read.value:
                data = buffer.raw[:read.value]
                names.update(m.decode() for m in NAME.findall(data))
                names.update(m.decode("utf-16-le") for m in WIDE.findall(data))

        address = (mbi.BaseAddress or 0) + size

    k32.CloseHandle(handle)
    return names


def launch_args(style):
    if style == "long":
        return ["--play", "--authenticationUrl", AUTH, "--authenticationTicket", "x", "--joinScriptUrl", JOIN]
    if style == "app":
        return ["--app", "-a", AUTH, "-t", "x", "-j", JOIN]
    return ["--play", "-a", AUTH, "-t", "x", "-j", JOIN]


def main():
    versions = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(os.environ["LOCALAPPDATA"]) / "Fluxer" / "Versions"
    OUTPUT.mkdir(parents=True, exist_ok=True)

    for year, (folder, exe, style) in CLIENTS.items():
        directory = versions / folder

        if not (directory / exe).exists():
            print(f"{year}: not installed, skipping")
            continue

        process = subprocess.Popen([str(directory / exe)] + launch_args(style), cwd=directory)
        names = set()

        # flags are registered while the client starts up, so sample a few times
        for _ in range(4):
            time.sleep(3)
            if process.poll() is not None:
                break
            names |= read_names(process.pid)

        process.kill()
        time.sleep(1)

        flags = sorted(n for n in names if FLAG_SHAPE.match(n) and len(n) <= 80)

        if len(flags) < 1000:
            print(f"{year}: only {len(flags)} names, the client probably closed early; keeping the old list")
            continue

        # newline="\n" so Windows doesn't turn the separators into \r\n
        with gzip.open(OUTPUT / f"{year}.txt.gz", "wt", encoding="utf-8", newline="\n", compresslevel=9) as f:
            f.write("\n".join(flags))

        print(f"{year}: {len(flags)} names")


if __name__ == "__main__":
    main()
