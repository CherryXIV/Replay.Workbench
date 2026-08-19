#!/usr/bin/env python3
"""
EXPERIMENTAL. Resize packet payloads when transposing across a patch that
changed a struct.

bump_replay.py renumbers opcodes, which is all that's needed while a packet's
layout holds still. It doesn't for everything: between 7.16h and 7.55h five
packet types changed size, and a client handed a 112-byte InitZone where it
expects 136 stops reading the replay at packet zero.

Fixing that properly needs the field layout of both versions. The `measured`
plan has them: a 7.16h and a 7.55h recording OF THE SAME DUTY make the
comparison controlled, and four of the five packets are now pinned rather than
guessed (PlayerSpawn outright proven against the party-portrait packet). See the
comment above HYPOTHESES["measured"] for what backs each one. InitZone is the
exception - it deletes bytes as well as adding them, so it needs
--template-from.

The older HYPOTHESES are kept because they are what was tried before, and
because the game is still the only thing that can confirm any of this. Convert a
file under each, see which the client accepts. Run --list to see them.

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


def insert_many(*points):
    """Splice zeros in at several offsets at once, then pad the remainder.

    Offsets are in OLD coordinates -- each is the position in the original
    payload that the new bytes go in front of, so they don't shift each other.
    """
    def fn(payload: bytes, target: int) -> bytes:
        out, prev = bytearray(), 0
        for off, count in sorted(points):
            if off > len(payload):
                break
            out += payload[prev:off] + b"\0" * count
            prev = off
        out += payload[prev:]
        return pad_tail(bytes(out), target) if len(out) < target else bytes(out[:target])
    return fn


def countdown_migrate(keymap: dict):
    """Countdown gained a 16-byte head; the first 8 of it are the character key.

    Measured: the initiating player's object id sits at old +0 and new +16, the
    second id at old +4 / new +20, and the name at old +11 / new +27 -- a clean
    +16 shift. In current recordings new[0:8] is that same player's PlayerSpawn
    character key, so it is filled in from the file rather than left zero.
    """
    def fn(payload: bytes, target: int) -> bytes:
        out = bytearray(16) + bytearray(payload)
        key = keymap.get(struct.unpack_from("<I", payload, 0)[0])
        if key is not None:
            struct.pack_into("<Q", out, 0, key)
        return pad_tail(bytes(out), target)
    return fn


def spawn_key_map(data: bytearray, names: dict) -> dict:
    """Object id -> character key, read off the file's PlayerSpawn packets.

    Runs before migration, while PlayerSpawn is still the old size. Both layouts
    keep the key at payload +0 and the spawning player's object id in the segment
    header, so this does not care which one it is looking at.
    """
    out = {}
    want = names.get("PlayerSpawn")
    if want is None:
        return out
    replay_len = struct.unpack_from("<i", data, B.OFF_REPLAY_LEN)[0]
    off = 0
    while off < replay_len:
        b = B.DATA_START + off
        op, ln = struct.unpack_from("<HH", data, b)
        if op == want and ln >= 8:
            oid = struct.unpack_from("<I", data, b + 8)[0]
            key = struct.unpack_from("<Q", data, b + B.SEG_HEADER)[0]
            if oid and key:
                out[oid] = key
        off += B.SEG_HEADER + ln
    return out


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

# ---------------------------------------------------------------------------
# The measured layouts.
#
# These are not guesses like the hypotheses above. They come from a 7.16h and a
# 7.55h recording OF THE SAME DUTY (DSR), which makes the comparison controlled:
# a field that is constant in one is constant in the other, and the same NPCs and
# the same arena appear in both. Each packet was pinned separately:
#
#   PlayerSpawn   proven, not inferred. Every field was located by cross-
#                 referencing the party-portrait packet, whose customize block,
#                 job byte and both dye channels are byte-identical to the
#                 spawn's. That puts job at old 149 / new 151 (+2), and gear,
#                 dye2, facewear, name and customize all at +4. The two 2-byte
#                 inserts therefore fall in (126,140) and (157,164) -- both runs
#                 of zero padding in BOTH patches, so the exact byte within them
#                 doesn't matter -- and the remaining 4 land in the tail, which
#                 is zero in both (old 648-656, new 652-664).
#
#   NpcSpawn      same shape, one step less certain. A per-byte value-distribution
#                 profile gives a clean step: +0 up to old 115, +2 from old 124,
#                 +4 from old 148 onward, tail zero in both. Scanning every split
#                 in those windows, offsets 117-124 all score identically (the
#                 bytes there are equivalent) and 148 wins the second. Worth 38
#                 points over pad-tail across 648 offsets, so the inserts are
#                 real; which byte inside the first window is arbitrary.
#
#   ActorControlSelf  confirmed pad-tail: the 8 added bytes are zero in all 4488
#                 current-patch samples, and the first 32 bytes are byte-identical
#                 between patches on matched packets.
#
#   Countdown     confirmed +16 at the front: object id old +0 -> new +16, second
#                 id old +4 -> new +20, name old +11 -> new +27. new[0:8] is the
#                 player's character key, so countdown_migrate fills it in.
#
#   InitZone      NOT a pure insertion - it drops 8 bytes (old 0x18-0x1f) as well
#                 as adding 32, so no insert plan can express it. Use
#                 --template-from with a current recording of the same duty; that
#                 starts from a payload the live client already accepted.
HYPOTHESES["measured"] = dict(
    {n: pad_tail for n in TARGET_SIZE},
    PlayerSpawn=insert_many((126, 2), (157, 2)),   # remaining 4 pad the tail
    NpcSpawn=insert_many((124, 2), (148, 2)),      # remaining 4 pad the tail
    # ActorControlSelf stays pad_tail; Countdown is filled in by main() so it can
    # see the whole file, and InitZone by --template-from.
)

HYPOTHESIS_NOTES = {
    "measured": "layouts measured off the 7.16h/7.55h same-duty DSR pair (see comment)",
    "derived-offsets": "insertion points derived from the 7.16h/7.38 same-duty pair",
    "opcodes-only": "control: opcodes remapped, payloads untouched (the current, broken output)",
    "pad-tail": "every short payload zero-extended at the end",
    "initzone-anchored": "InitZone rebuilt against its landmarks; the rest zero-extended",
    "spawn-split": "as initzone-anchored, plus the spawn packets split 4 bytes before the name / 4 after",
}


def opcode_in_patch(patch: str, hops: dict, latest_op: int) -> int:
    """What opcode a current packet had back in `patch`.

    The name tables only pin the latest patch, so the number is carried backwards
    down the diff chain instead - the same trick PatchChain uses to name old
    packets from one hand-maintained table.
    """
    latest = B.VERSION_CHAIN[-1]
    if patch == latest:
        return latest_op
    fwd = {op: op for op in B.patch_universe(hops, patch)}
    for v in B.chain_between(patch, latest):
        m = hops[v]["map"]
        fwd = {orig: m[cur] for orig, cur in fwd.items() if cur in m}
    for orig, cur in fwd.items():
        if cur == latest_op:
            return orig
    raise B.Fatal(f"InitZone can't be traced back to {patch}")


def read_initzone(path: str, names: dict) -> bytes:
    """Pull the first InitZone payload out of a recent recording.

    The template does not have to be on the latest patch - its opcode is resolved
    in whatever patch it actually is. What it does have to be is new enough that
    its InitZone is already the target size, and that is checked directly rather
    than against a list of patch names that goes stale.

    Prefer a template recorded in the SAME duty: InitZone carries the territory,
    the content id and the arena spawn position, so a same-duty template needs
    almost nothing transplanted into it.
    """
    d = B.read_replay(path)
    chain, hops = B.load_baked()
    hist = B.opcode_histogram(d, B.segment_offsets(d))
    best, _ = B.detect_patch(hops, hist, chain[-1])
    if not best:
        raise B.Fatal(f"can't tell what patch {path} is on")
    want = opcode_in_patch(best[0], hops, names["InitZone"])
    rl = struct.unpack_from("<i", d, B.OFF_REPLAY_LEN)[0]
    off = 0
    while off < rl:
        b = B.DATA_START + off
        op, ln = struct.unpack_from("<HH", d, b)
        if op == want:
            if ln != TARGET_SIZE["InitZone"]:
                raise B.Fatal(
                    f"{path} reads as {best[0]} and its InitZone is {ln} bytes, not "
                    f"{TARGET_SIZE['InitZone']} - the template must be recent enough to "
                    f"already have the current layout")
            return bytes(d[b + B.SEG_HEADER: b + B.SEG_HEADER + ln])
        off += B.SEG_HEADER + ln
    raise B.Fatal(f"no InitZone packet found in {path} (looked for opcode 0x{want:04x} as {best[0]})")


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

    # Countdown's new head starts with the character key, which only the whole
    # file can supply - so it is bound here rather than in the table above.
    keymap = spawn_key_map(data, names)
    HYPOTHESES["measured"]["Countdown"] = countdown_migrate(keymap)
    print(f"character keys for Countdown: {len(keymap)} players")

    if args.template_from:
        tmpl = read_initzone(args.template_from, names)
        HYPOTHESES["initzone-template"] = dict({n: pad_tail for n in TARGET_SIZE},
                                               InitZone=initzone_from_template(tmpl))
        HYPOTHESIS_NOTES["initzone-template"] = "InitZone taken from a working recording, ids transplanted"
        # The measured set is right about everything except InitZone, which no
        # insert plan can express; give it the template too.
        HYPOTHESES["measured"]["InitZone"] = initzone_from_template(tmpl)
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
