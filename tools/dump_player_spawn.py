#!/usr/bin/env python3
"""Dump PlayerSpawn packet bytes for a named player so we can locate appearance
fields (customize array, equipment models, and the new facewear/glasses slot).

Usage: python tools/dump_player_spawn.py REPLAY.dat "First Last"
"""
import os, re, struct, sys, json

HEADER_SIZE = 0x68
DATA_START = HEADER_SIZE + (0x4 + 0xC * 64)  # 0x36C
SEG_HEADER = 12
OFF_BUILD = 0x10
OFF_REPLAY_LEN = 0x48

HERE = os.path.dirname(os.path.abspath(__file__))
OPCODES_JS = os.path.join(HERE, "..", "docs", "opcodes.js")


def load_tables(path):
    src = open(path, encoding="utf-8").read()
    tables = {}
    for name, body in re.findall(r'"([^"]+)":\s*(\{[^}]*\})', src):
        try:
            obj = json.loads(body)
        except json.JSONDecodeError:
            continue
        if obj and all(isinstance(v, (int, float)) for v in obj.values()):
            tables[name] = obj
    b2p = {}
    m = re.search(r'BUILD_TO_PATCH\s*=\s*\{([^}]*)\}', src)
    if m:
        for build, patch in re.findall(r'(\d+)\s*:\s*"([^"]+)"', m.group(1)):
            b2p[int(build)] = patch
    return tables, b2p


def each_segment(buf):
    rlen = struct.unpack_from("<i", buf, OFF_REPLAY_LEN)[0]
    off = 0
    while off < rlen and DATA_START + off + SEG_HEADER <= len(buf):
        base = DATA_START + off
        op = struct.unpack_from("<H", buf, base)[0]
        dl = struct.unpack_from("<H", buf, base + 2)[0]
        yield base + SEG_HEADER, op, dl
        off += SEG_HEADER + dl


def hexdump(b, base=0):
    out = []
    for i in range(0, len(b), 16):
        chunk = b[i:i+16]
        hx = " ".join(f"{x:02x}" for x in chunk)
        asc = "".join(chr(x) if 32 <= x < 127 else "." for x in chunk)
        out.append(f"  {base+i:04x}  {hx:<47}  {asc}")
    return "\n".join(out)


def main():
    dat, target = sys.argv[1], sys.argv[2]
    buf = open(dat, "rb").read()
    build = struct.unpack_from("<i", buf, OFF_BUILD)[0]
    tables, b2p = load_tables(OPCODES_JS)
    patch = b2p.get(build)
    spawn_op = tables.get(patch, {}).get("PlayerSpawn") if patch else None
    print(f"{dat}: build {build} ({patch}), PlayerSpawn opcode {spawn_op}")
    tname = target.encode()

    seen = 0
    for p, op, dl in each_segment(buf):
        if op != spawn_op:
            continue
        pay = buf[p:p+dl]
        idx = pay.find(tname)
        if idx < 0:
            continue
        seen += 1
        print(f"\n=== PlayerSpawn #{seen}  payload len={dl}  name at +0x{idx:x} ===")
        print(hexdump(pay))
        if seen >= 1:
            break
    if not seen:
        print(f"No PlayerSpawn packet found containing {target!r}")


if __name__ == "__main__":
    main()
