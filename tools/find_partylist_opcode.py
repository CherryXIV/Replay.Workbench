#!/usr/bin/env python3
"""
Find the party-related opcodes in an FFXIVReplay .dat.

There are TWO unnamed party packets, and they are easy to confuse:

  * Party list (HUD)   - the FULL PARTY widget (job/name/HP). A large packet
                         (~3672 B) re-sent throughout the fight as state changes.
  * Member appearance  - the "Party Members" portrait popup. A 1408-byte packet
                         (8 x 176: customize + gear) usually sent once on entry.

Neither has an IPC name in karashiiro/FFXIVOpcodes, so the site's name-based
transpose can't map them. This finds both by tying them to the real party
roster: it reads the party members' contentIds out of the (named) PlayerSpawn
packets, then reports which opcodes carry that roster. Offsets that drift between
patches are avoided, so it should keep working on future patches.

Usage:
    python tools/find_partylist_opcode.py REPLAY.dat
"""

import argparse
import collections
import json
import os
import re
import struct
import sys

HEADER_SIZE = 0x68
DATA_START = HEADER_SIZE + (0x4 + 0xC * 64)  # 0x36C
SEG_HEADER = 12
OFF_BUILD = 0x10
OFF_REPLAY_LEN = 0x48
APPEARANCE_LEN = 1408  # 8 x 176

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


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("dat")
    args = ap.parse_args()

    buf = open(args.dat, "rb").read()
    build = struct.unpack_from("<i", buf, OFF_BUILD)[0]
    tables, b2p = load_tables(OPCODES_JS)
    patch = b2p.get(build)
    spawn_op = tables.get(patch, {}).get("PlayerSpawn") if patch else None
    print(f"{args.dat}: build {build}" + (f" ({patch})" if patch else " (unknown patch)"))
    if spawn_op is None:
        sys.exit("  Need PlayerSpawn in opcodes.js for this build to anchor the roster.")

    # Roster = the leading 8-byte contentId of every PlayerSpawn payload.
    roster = set()
    for p, op, dl in each_segment(buf):
        if op == spawn_op and dl >= 8:
            cid = buf[p:p + 8]
            if cid != b"\0" * 8:
                roster.add(cid)
    if not roster:
        sys.exit("  No PlayerSpawn content IDs found (need a recording with real players).")
    print(f"  roster: {len(roster)} players (from PlayerSpawn opcode {spawn_op})")

    # For each opcode, the most roster members any single packet contains, its
    # send count and modal payload length.
    best = collections.defaultdict(int)
    count = collections.Counter()
    lens = collections.defaultdict(collections.Counter)
    for p, op, dl in each_segment(buf):
        if op == spawn_op:
            continue
        count[op] += 1
        lens[op][dl] += 1
        pay = buf[p:p + dl]
        hits = sum(1 for cid in roster if cid in pay)
        if hits > best[op]:
            best[op] = hits

    named = {v: k for k, v in tables.get(patch, {}).items()}
    # Candidates: packets that carry most of the roster.
    cands = [op for op in best if best[op] >= max(2, len(roster) - 2)]
    cands.sort(key=lambda op: (-best[op], -count[op]))

    if not cands:
        sys.exit("  No packet carries the party roster — can't locate party opcodes.")

    # Classify: 1408-byte one is the appearance/portrait packet; the largest
    # roster-bearing packet that isn't named is the HUD party list.
    appearance = next((op for op in cands if lens[op].most_common(1)[0][0] == APPEARANCE_LEN), None)
    hud = next((op for op in cands
                if op != appearance and op not in named), None)

    print("  --- party packets ---")
    if hud is not None:
        L = lens[hud].most_common(1)[0][0]
        print(f"  Party list (HUD):    opcode {hud}   "
              f"({best[hud]}/{len(roster)} members, {L} B, {count[hud]} packets)")
    if appearance is not None:
        print(f"  Member appearance:   opcode {appearance}   "
              f"({best[appearance]}/{len(roster)} members, {APPEARANCE_LEN} B, "
              f"{count[appearance]} packets)")
    print("  --- all roster-bearing opcodes ---")
    for op in cands:
        L = lens[op].most_common(1)[0][0]
        tag = f"  [{named[op]}]" if op in named else ""
        print(f"    {op:5}: {best[op]}/{len(roster)} members, {L:5} B, "
              f"{count[op]:4} packets{tag}")


if __name__ == "__main__":
    main()
