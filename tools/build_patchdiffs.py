#!/usr/bin/env python3
"""
Turn opcodediff's diffs/*.diff.json into docs/patchdiffs.js, the single data
file both the web workbench and tools/bump_replay.py read.

Each <patch>.diff.json maps the *previous* patch's opcodes to that patch's.
Chained, they carry a recording forward across every patch in VERSION_CHAIN
(7.00 onward) without needing an IPC name for anything -- which is the point:
names come from a third-party list that lags patches and has been wrong before
(PartyList and PartyPortraitInfo both needed hand-correction), while the diffs
are derived from the binary itself.

Output format (see docs/patchdiffs.js for the decoder):

    PATCH_CHAIN = ["7.00", "7.00h", ...]      // oldest first
    PATCH_DIFFS = { "<patch>": {o,n,a,r} }    // the hop that lands on <patch>

o/n are parallel lists of old/new opcodes; a is opcodes the matcher could not
tell apart this patch, r is opcodes that went away. All four are packed as two
base64 characters per opcode, big-endian, alphabet PACK_ALPHABET below.

Usage:
    python tools/build_patchdiffs.py                       # finds ../opcodediff/diffs
    python tools/build_patchdiffs.py --diffs /path/to/diffs -o docs/patchdiffs.js
    python tools/build_patchdiffs.py --check               # verify, write nothing
"""

from __future__ import annotations

import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bump_replay import find_diffs_dir, Fatal  # noqa: E402

# Patch order, oldest first, and the authority on it: whatever is listed here is
# what gets written into docs/patchdiffs.js, which everything else then reads.
# The first entry is the base version, which has no diff of its own.
#
# It starts at 7.00, not at the oldest diff opcodediff ships. Dawntrail replaced
# the engine, so a 6.x recording carried across that boundary is not a 7.x
# recording no matter how cleanly the opcodes line up. The 6.x diffs are also the
# ones opcodediff's own README flags as unreliable.
VERSION_CHAIN = [
    "7.00", "7.00h", "7.01", "7.05", "7.05h", "7.05h2",
    "7.10", "7.11", "7.15", "7.16", "7.16h", "7.18", "7.18h",
    "7.20", "7.20h", "7.21", "7.25", "7.25h", "7.25h2", "7.25h3",
    "7.30", "7.30h", "7.31", "7.31h", "7.35", "7.35h", "7.38",
    "7.40", "7.40h", "7.40h2", "7.41", "7.41h", "7.45", "7.45h", "7.45h2",
    "7.50", "7.50h", "7.51", "7.51h", "7.51h2", "7.55", "7.55h", "7.55h2",
]

PACK_ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_"
PACK_MAX = 64 * 64  # two characters per opcode

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_OUT = os.path.join(REPO_ROOT, "docs", "patchdiffs.js")


def pack(values) -> str:
    out = []
    for v in values:
        if not 0 <= v < PACK_MAX:
            raise Fatal(f"opcode 0x{v:x} does not fit the 2-character packing (max 0x{PACK_MAX - 1:x})")
        out.append(PACK_ALPHABET[v >> 6])
        out.append(PACK_ALPHABET[v & 63])
    return "".join(out)


def read_hop(diffs_dir: str, version: str):
    """Parse one diff file into sorted (olds, news, ambiguous, removed).

    A 1:1 entry is a resolved move. An n:n group (n > 1) is a set of candidates
    the matcher could not separate; an entry with no "new" is an opcode that
    went away; one with no "old" is an opcode that appeared. The 6.3 diff spells
    the unresolved cases as "candidates"/"unknown" keys, which fall out of the
    same length checks.
    """
    path = os.path.join(diffs_dir, f"{version}.diff.json")
    if not os.path.isfile(path):
        raise Fatal(f"No diff for {version} (expected {path})")
    with open(path, "r", encoding="utf-8") as f:
        entries = json.load(f)

    mapping: dict[int, int] = {}
    ambiguous: set[int] = set()
    removed: set[int] = set()
    for e in entries:
        olds = [int(x, 16) for x in (e.get("old") or [])]
        news = [int(x, 16) for x in (e.get("new") or [])]
        if len(olds) == 1 and len(news) == 1:
            mapping[olds[0]] = news[0]
        elif not olds:
            continue  # brand new opcode; nothing on the old side to carry forward
        elif not news:
            removed.update(olds)
        else:
            ambiguous.update(olds)

    # Two sources landing on one target would collapse two packet types onto a
    # single opcode -- the client reads one with the other's struct and crashes.
    # The 6.3 diff has one such pair (its README warns the 6.3/6.4/6.5 diffs are
    # not fully accurate). Demote both sides to "ambiguous" rather than pick one.
    dupes = {}
    for old, new in mapping.items():
        dupes.setdefault(new, []).append(old)
    collisions = {n: o for n, o in dupes.items() if len(o) > 1}
    for olds_on_target in collisions.values():
        for old in olds_on_target:
            del mapping[old]
            ambiguous.add(old)

    olds = sorted(mapping)
    return olds, [mapping[o] for o in olds], sorted(ambiguous), sorted(removed), collisions


def build(diffs_dir: str):
    hops = {}
    prev_news = None
    warnings = []
    for version in VERSION_CHAIN[1:]:
        olds, news, ambiguous, removed, collisions = read_hop(diffs_dir, version)
        for target, sources in collisions.items():
            warnings.append(
                f"  {version}: 0x{target:x} claimed by " + ", ".join(f"0x{s:x}" for s in sources)
                + " -- both demoted to ambiguous"
            )
        # Each hop's old side should be exactly the previous hop's new side. When
        # it isn't, the chain has a gap -- worth saying out loud, because opcodes
        # that fall in the gap silently stop being carried forward.
        if prev_news is not None:
            orphaned = prev_news - (set(olds) | set(ambiguous) | set(removed))
            if orphaned:
                warnings.append(f"  {version}: {len(orphaned)} opcode(s) from the previous patch are absent from this diff")
        hops[version] = {"o": pack(olds), "n": pack(news)}
        if ambiguous:
            hops[version]["a"] = pack(ambiguous)
        if removed:
            hops[version]["r"] = pack(removed)
        prev_news = set(news)
    return hops, warnings


def render(hops) -> str:
    lines = [
        '"use strict";',
        "/* =====================================================================",
        "   Opcode moves per game patch, generated by tools/build_patchdiffs.py",
        "   from https://github.com/xivdev/opcodediff (diffs/*.diff.json).",
        "   DO NOT EDIT BY HAND -- re-run the generator instead.",
        "",
        "   <patch>.diff.json records which opcode number became which when that",
        "   patch shipped, read out of the binary's IPC vtable. Chaining the hops",
        "   moves a recording from any patch below to any patch above it without",
        "   knowing what a single packet is called, which is why transpose uses",
        "   these and not the IPC name lists: names come from a third-party dump",
        "   that lags patches and has been wrong before.",
        "",
        "   PATCH_CHAIN  oldest patch first. The first entry is the base version;",
        "                every later entry has a hop in PATCH_DIFFS landing on it.",
        "   PATCH_DIFFS  <patch> -> {o,n,a,r}, the hop from the previous patch:",
        "                  o/n  parallel old/new opcode lists (a resolved move)",
        "                  a    opcodes the matcher could not tell apart",
        "                  r    opcodes that went away this patch",
        "                Each list is packed two base64 chars per opcode; decode",
        "                with unpackOpcodes() below.",
        "   ===================================================================== */",
        "",
        f'const PATCH_PACK_ALPHABET = "{PACK_ALPHABET}";',
        "const PATCH_PACK_INDEX = (()=>{ const m={}; for(let i=0;i<PATCH_PACK_ALPHABET.length;i++) m[PATCH_PACK_ALPHABET[i]]=i; return m; })();",
        "",
        "// Unpack a packed opcode list back into an array of numbers.",
        "function unpackOpcodes(s){",
        "  if(!s) return [];",
        "  const out=new Array(s.length>>1);",
        "  for(let i=0,j=0;i<s.length;i+=2,j++) out[j]=PATCH_PACK_INDEX[s[i]]*64+PATCH_PACK_INDEX[s[i+1]];",
        "  return out;",
        "}",
        "",
        "const PATCH_CHAIN = [",
    ]
    row = "  "
    for i, v in enumerate(VERSION_CHAIN):
        piece = f'"{v}",'
        if len(row) + len(piece) > 96:
            lines.append(row.rstrip())
            row = "  "
        row += piece + " "
    lines.append(row.rstrip().rstrip(","))
    lines.append("];")
    lines.append("")
    lines.append("const PATCH_DIFFS = {")
    for version, hop in hops.items():
        body = ",".join(f'{k}:"{v}"' for k, v in hop.items())
        lines.append(f'  "{version}": {{{body}}},')
    lines.append("};")
    lines.append("")
    return "\n".join(lines) + "\n"


def main(argv=None):
    p = argparse.ArgumentParser(description="Generate docs/patchdiffs.js from opcodediff's diffs.")
    p.add_argument("--diffs", help="opcodediff diffs/ directory")
    p.add_argument("-o", "--output", default=DEFAULT_OUT)
    p.add_argument("--check", action="store_true", help="report only, write nothing")
    args = p.parse_args(argv)

    diffs_dir = find_diffs_dir(args.diffs)
    hops, warnings = build(diffs_dir)
    text = render(hops)

    print(f"{len(hops)} hops from {diffs_dir}")
    print(f"{len(VERSION_CHAIN)} patches: {VERSION_CHAIN[0]} -> {VERSION_CHAIN[-1]}")
    if warnings:
        print("notes:")
        for w in warnings:
            print(w)

    if args.check:
        existing = ""
        if os.path.isfile(args.output):
            with open(args.output, "r", encoding="utf-8") as f:
                existing = f.read()
        print("up to date" if existing == text else f"{args.output} is stale; re-run without --check")
        return 0 if existing == text else 1

    # CRLF, matching .gitattributes -- otherwise --check compares a CRLF working
    # copy against LF-generated text and calls a current file stale.
    with open(args.output, "w", encoding="utf-8", newline="\r\n") as f:
        f.write(text)
    print(f"wrote {args.output} ({len(text):,} bytes)")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Fatal as e:
        print(f"error: {e}", file=sys.stderr)
        sys.exit(1)
