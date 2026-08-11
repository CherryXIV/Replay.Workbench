#!/usr/bin/env python3
"""
EXPERIMENTAL. Resize packet payloads when transposing across a patch that
changed a struct.

bump_replay.py renumbers opcodes, which is all that's needed while a packet's
layout holds still. It doesn't for everything: between 7.16h and 7.55h five
packet types changed size, and a client handed a 112-byte InitZone where it
expects 136 stops reading the replay at packet zero.

Fixing that properly needs the field layout of both versions. We have the new
side pinned (hundreds of sample recordings) and only one old recording, which is
enough to locate a few anchors and not enough to prove the rest -- so this ships
several competing HYPOTHESES instead of one answer. Convert a file under each,
see which one the game accepts, and the winner tells us what the layout actually
did. Run --list to see them.

Sizes are per (packet, old_size) and only apply when the payload really is the
old size, so a file that's already correct passes through untouched.

Usage:
    python tools/migrate_payloads.py in.dat --hypothesis pad-tail -o out.dat
    python tools/migrate_payloads.py in.dat --all-hypotheses --outdir testfiles
    python tools/migrate_payloads.py --list
"""

from __future__ import annotations

import argparse
import json
import os
import re
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bump_replay as B  # noqa: E402

# Target sizes, measured across 250 recordings on 7.51-7.55h.
TARGET_SIZE = {
    "InitZone": 136,
    "ActorControlSelf": 40,
    "NpcSpawn": 656,
    "PlayerSpawn": 664,
    "Countdown": 64,
}

# What a 7.55h InitZone looks like in the 125 samples we have: 97 of its 136
# bytes are always zero, 17 are constant, and only 22 ever carry data. These are
# the fixed landmarks used to line the old payload up against the new one.
INITZONE_TAIL_ONES = slice(0x75, 0x7F)  # ten 0x01 bytes, same run exists in old


def pad_tail(payload: bytes, target: int) -> bytes:
    """Zero-extend at the end. Correct whenever fields were simply appended."""
    return payload + b"\0" * (target - len(payload))


def initzone_anchored(payload: bytes, target: int) -> bytes:
    """Rebuild InitZone against the landmarks rather than padding blindly.

    Anchored on the two constant floats (8.59375, 1.0) and the spawn position
    triple, which appear in both versions:

        old 0x00-0x14  head (territory/content ids)   -> new 0x00-0x14
        old 0x18-0x1f  8 bytes that no longer exist   -> dropped
        old 0x20-0x27  the two constant floats        -> new 0x18-0x1f
        old 0x28-0x4f  40-byte middle                 -> new 0x20-0x67 (72 B)
        old 0x50-0x5b  spawn position X / _ / Z       -> new 0x68-0x73
        old 0x5d-0x66  the ten 0x01 bytes             -> new 0x75-0x7e

    The middle is zero in every current-patch sample except two bytes that look
    like seasonal ids, so it's zero-filled and those two are carried across.
    """
    if len(payload) != 112 or target != 136:
        return pad_tail(payload, target)
    out = bytearray(136)
    out[0x00:0x15] = payload[0x00:0x15]      # head
    out[0x18:0x20] = payload[0x20:0x28]      # the constant float pair
    out[0x46] = payload[0x3E]                # seasonal ids, best guess
    out[0x48] = payload[0x40]
    out[0x68:0x74] = payload[0x50:0x5C]      # spawn position
    out[INITZONE_TAIL_ONES] = b"\x01" * 10
    return bytes(out)


# The recording-specific fields in InitZone. Everything else is either constant
# across all 205 current samples or always zero, so it can safely come from a
# template. 0x06 is confirmed: it equals the replay header's contentId in every
# current recording, and in the old one too.
INITZONE_IDENT = [(0x00, 2), (0x02, 2), (0x04, 2), (0x06, 2), (0x10, 1), (0x13, 1)]
INITZONE_POSITION = (0x68, 12)


def initzone_from_template(template: bytes):
    """Start from a working current InitZone and transplant only the ids.

    Rebuilding the old struct into the new one means guessing at the 32 bytes
    that appeared in the middle. Going the other way removes the guess: take a
    payload the current client already accepts, and overwrite just the fields
    that identify this recording. Anything I can't identify keeps a value that
    is known to work rather than one I invented.
    """
    def fn(payload: bytes, target: int) -> bytes:
        if len(template) != target:
            return pad_tail(payload, target)
        out = bytearray(template)
        for off, width in INITZONE_IDENT:
            out[off:off + width] = payload[off:off + width]
        # the spawn position sits at 0x50 in the old layout, 0x68 in the new
        po, plen = INITZONE_POSITION
        if len(payload) == 112:
            out[po:po + plen] = payload[0x50:0x50 + plen]
        else:
            out[po:po + plen] = payload[po:po + plen]
        return bytes(out)
    return fn


def insert_at(offset: int, count: int):
    """Splice `count` zero bytes in at `offset`, then pad any remainder at the end."""
    def fn(payload: bytes, target: int) -> bytes:
        if len(payload) <= offset:
            return pad_tail(payload, target)
        out = payload[:offset] + b"\0" * count + payload[offset:]
        return pad_tail(out, target)
    return fn


# Each hypothesis is {packet: migrate(payload, target) -> bytes}.
HYPOTHESES = {
    "opcodes-only": {},
    "pad-tail": {n: pad_tail for n in TARGET_SIZE},
    "initzone-anchored": dict(
        {n: pad_tail for n in TARGET_SIZE},
        InitZone=initzone_anchored,
    ),
    "spawn-split": dict(
        {n: pad_tail for n in TARGET_SIZE},
        InitZone=initzone_anchored,
        # PlayerSpawn's name field moves +4, not +8, so the 8 bytes arrived as
        # two separate insertions -- one before the name, one after it.
        PlayerSpawn=insert_at(0x24E, 4),
        NpcSpawn=insert_at(0x24E, 4),
    ),
}

# Insertion points derived from the same duty recorded at 7.16h and at 7.38, which
# is the only comparison where content is controlled. The margins are thin (the
# best NpcSpawn candidate beats its neighbours by under 1%), so these are leads.
HYPOTHESES["derived-offsets"] = dict(
    {n: pad_tail for n in TARGET_SIZE},
    NpcSpawn=insert_at(0x23E, 8),
    PlayerSpawn=insert_at(0x288, 8),
    Countdown=insert_at(0x006, 16),
)

HYPOTHESIS_NOTES = {
    "derived-offsets": "insertion points derived from the 7.16h/7.38 same-duty pair",
    "opcodes-only": "control: opcodes remapped, payloads untouched (the current, broken output)",
    "pad-tail": "every short payload zero-extended at the end",
    "initzone-anchored": "InitZone rebuilt against its landmarks; the rest zero-extended",
    "spawn-split": "as initzone-anchored, plus the spawn packets split 4 bytes before the name / 4 after",
}


def read_initzone(path: str, names: dict) -> bytes:
    """Pull the first InitZone payload out of an already-current recording."""
    d = B.read_replay(path)
    chain, hops = B.load_baked()
    hist = B.opcode_histogram(d, B.segment_offsets(d))
    best, _ = B.detect_patch(hops, hist, chain[-1])
    # Any patch whose InitZone is already the target size will do -- the layout has
    # been stable across 7.51-7.55h -- but the opcodes must be current so we can
    # find the packet at all.
    if not best or best[0] not in ("7.51", "7.51h", "7.51h2", "7.55", "7.55h"):
        raise B.Fatal(f"{path} reads as {best[0] if best else 'unknown'}; the template must be a "
                      f"recent recording whose InitZone is already {TARGET_SIZE['InitZone']} bytes")
    want = names["InitZone"]
    rl = struct.unpack_from("<i", d, B.OFF_REPLAY_LEN)[0]
    off = 0
    while off < rl:
        b = B.DATA_START + off
        op, ln = struct.unpack_from("<HH", d, b)
        if op == want:
            return bytes(d[b + B.SEG_HEADER: b + B.SEG_HEADER + ln])
        off += B.SEG_HEADER + ln
    raise B.Fatal(f"no InitZone packet found in {path}")


def latest_names():
    js = open(os.path.join(B.REPO_ROOT, "docs", "opcodes.js"), encoding="utf-8").read()
    tables = {k: json.loads(b) for k, b in re.findall(r'^\t"([^"]+)": (\{.*?\}),$', js, re.M | re.S)}
    return tables[B.VERSION_CHAIN[-1]]


# Opcodes that appear in old recordings but in none of the 40 current ones
# profiled -- packet types the client may simply no longer accept. Dropping them
# is a hypothesis, not a fact: they could just be rare.
GONE_IN_CURRENT = {0x0157, 0x03BA}


def migrate(data: bytearray, plan: dict, target_names: dict, drop: set[int] = frozenset()):
    """Rebuild the data section with resized payloads, fixing up offsets.

    Segment sizes change, so the replay length and every chapter offset have to
    move with them -- a chapter offset points into the data stream, and each one
    shifts by however many bytes were added before it.
    """
    replay_len = struct.unpack_from("<i", data, B.OFF_REPLAY_LEN)[0]
    want = {target_names[n]: n for n in plan if n in target_names}

    body = bytearray()
    shift_at = []  # (old_offset, cumulative_growth_after_this_segment)
    off = grown = 0
    counts: dict[str, int] = {}
    while off < replay_len:
        b = B.DATA_START + off
        op, ln = struct.unpack_from("<HH", data, b)
        header = data[b : b + B.SEG_HEADER]
        payload = data[b + B.SEG_HEADER : b + B.SEG_HEADER + ln]
        name = want.get(op)
        target = TARGET_SIZE.get(name) if name else None
        if op in drop:
            counts[f"(dropped 0x{op:04x})"] = counts.get(f"(dropped 0x{op:04x})", 0) + 1
            grown -= B.SEG_HEADER + ln
        else:
            if target is not None and len(payload) != target:
                payload = plan[name](bytes(payload), target)
                struct.pack_into("<H", header, 2, len(payload))
                counts[name] = counts.get(name, 0) + 1
                grown += len(payload) - ln
            body += header + payload
        off += B.SEG_HEADER + ln
        shift_at.append((off, grown))

    out = bytearray(data[: B.DATA_START]) + body + bytearray(data[B.DATA_START + replay_len :])
    struct.pack_into("<i", out, B.OFF_REPLAY_LEN, len(body))

    # Chapter offsets are data-stream relative; move each by the growth before it.
    n_chapters = struct.unpack_from("<i", out, B.HEADER_SIZE)[0]
    for i in range(min(n_chapters, 64)):
        e = B.HEADER_SIZE + 4 + i * 0xC
        at = struct.unpack_from("<I", out, e + 4)[0]
        delta = 0
        for seg_end, g in shift_at:
            if seg_end <= at:
                delta = g
            else:
                break
        struct.pack_into("<I", out, e + 4, at + delta)
    return out, counts, len(body) - replay_len


def main(argv=None):
    p = argparse.ArgumentParser(description="Resize packet payloads across a struct change (experimental).")
    p.add_argument("input", nargs="?")
    p.add_argument("-o", "--output")
    p.add_argument("--outdir", help="with --all-hypotheses, where to write the set")
    p.add_argument("--hypothesis", default="initzone-anchored",
                   help="one of: " + ", ".join(sorted(HYPOTHESES)) + ", initzone-template (needs --template-from)")
    p.add_argument("--all-hypotheses", action="store_true", help="write one file per hypothesis")
    p.add_argument("--list", action="store_true")
    p.add_argument("--force", action="store_true", help="resize even if the file hasn't been transposed yet")
    p.add_argument("--template-from", metavar="DAT",
                   help="a current-patch recording to lift a known-good InitZone from "
                        "(enables the initzone-template hypothesis)")
    p.add_argument("--drop-name", action="append", default=[], metavar="PACKET",
                   help="delete every packet of this type (repeatable). Bisection aid: removing a "
                        "packet whose new layout we can't derive beats guessing at it.")
    p.add_argument("--drop-gone", action="store_true",
                   help=f"also delete packets no current recording contains ({', '.join(f'0x{o:04x}' for o in sorted(GONE_IN_CURRENT))})")
    args = p.parse_args(argv)

    if args.list:
        print("hypotheses:")
        for k in sorted(HYPOTHESES):
            print(f"  {k:20s} {HYPOTHESIS_NOTES[k]}")
        print("\ntarget payload sizes (measured on 7.51-7.55h recordings):")
        for k, v in TARGET_SIZE.items():
            print(f"  {k:20s} {v} bytes")
        return 0
    if not args.input:
        p.error("an input .dat is required (or use --list)")

    names = latest_names()
    target_patch = B.VERSION_CHAIN[-1]

    # Run bump_replay.py FIRST. Packets are found by their target-patch opcode
    # number, so on a file that hasn't been transposed yet those numbers point at
    # whatever else happened to live there -- and any of them sitting at exactly
    # the "old" size would get silently resized into garbage.
    data = B.read_replay(args.input)
    build = struct.unpack_from("<i", data, B.OFF_BUILD)[0]
    chain, hops = B.load_baked()
    hist = B.opcode_histogram(data, B.segment_offsets(data))
    best, runner = B.detect_patch(hops, hist, target_patch)
    confident = bool(best and best[1] >= 0.9999 and best[2] >= 0.9999
                     and (not runner or best[2] - runner[2] > 0.01))
    if confident and best[0] != target_patch and not args.force:
        raise B.Fatal(
            f"this file's opcodes are still {best[0]}, not {target_patch}. Transpose it first:\n"
            f"    python tools/bump_replay.py \"{args.input}\" -o transposed.dat\n"
            f"then run this on the result. (--force overrides, but the packets it resizes "
            f"would be picked by {target_patch} opcode numbers that mean something else in {best[0]}.)"
        )

    if args.template_from:
        tmpl = read_initzone(args.template_from, names)
        HYPOTHESES["initzone-template"] = dict({n: pad_tail for n in TARGET_SIZE},
                                               InitZone=initzone_from_template(tmpl))
        HYPOTHESIS_NOTES["initzone-template"] = "InitZone taken from a working recording, ids transplanted"
        print(f"template InitZone: {len(tmpl)} bytes from {os.path.basename(args.template_from)}")

    todo = sorted(HYPOTHESES) if args.all_hypotheses else [args.hypothesis]
    stem = os.path.splitext(os.path.basename(args.input))[0]

    for h in todo:
        data = B.read_replay(args.input)
        drop = set(GONE_IN_CURRENT) if args.drop_gone else set()
        for n in args.drop_name:
            if n not in names:
                raise B.Fatal(f"unknown packet name {n!r}")
            drop.add(names[n])
        out, counts, delta = migrate(data, HYPOTHESES[h], names, drop)
        if args.all_hypotheses:
            outdir = args.outdir or "."
            os.makedirs(outdir, exist_ok=True)
            path = os.path.join(outdir, f"{stem} [{h}].dat")
        else:
            path = args.output or f"{stem}.{h}.dat"
        with open(path, "wb") as f:
            f.write(out)
        detail = ", ".join(f"{n} x{c}" for n, c in sorted(counts.items())) or "nothing resized"
        print(f"{h:20s} {delta:+9,} bytes  ({detail})")
        print(f"{'':20s} -> {path}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except B.Fatal as e:
        print(f"error: {e}", file=sys.stderr)
        sys.exit(1)
