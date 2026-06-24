#!/usr/bin/env python3
"""
Resolve gear MODEL ids from the item IDs in tools/Job Gear IDs.csv.

The "Party Members" portrait packet stores gear as item IDs, but the in-arena
PlayerSpawn packet stores MODEL ids. Garland Tools exposes them as a "models"
array of dash-joined strings, but ARMOR and WEAPONS pack them differently:
  armor  : "772-1-0-0"        = model-variant-stain-...     (no base)
  weapon : "2001-76-2-0", ... = model-base-variant-dye, and a 2nd entry for the
           offhand/secondary (e.g. a MCH aetherotransformer, VPR's off-dagger)
Because the meaning of position 1 differs (variant for armor, base for weapons),
we store the RAW models list per item and let build_afgear.py interpret it:
  {itemId: ["2001-76-2-0", "2099-1-1-0"]}
This is a superset of the old [model, variant] map, so weapons get base+variant
and the offhand too. One fetch per unique item id, cached in item_models.json.

Usage:
    python tools/resolve_models.py
"""

import csv
import json
import os
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
CSV = os.path.join(HERE, "Job Gear IDs.csv")
OUT = os.path.join(HERE, "item_models.json")
URL = "https://garlandtools.org/db/doc/item/en/3/{}.json"

UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/124.0 Safari/537.36")


def fetch_models(item_id):
    req = urllib.request.Request(URL.format(item_id), headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=15) as r:
        data = json.load(r)
    # Raw model strings, e.g. ["2001-76-2-0", "2099-1-1-0"]. build_afgear.py
    # parses these per item type (armor vs weapon); we keep them verbatim.
    return [str(m) for m in (data.get("item", {}).get("models") or [])] or None


def main():
    ids = set()
    with open(CSV, newline="") as f:
        for row in csv.DictReader(f):
            for k, v in row.items():
                if k == "Jobs":
                    continue
                v = (v or "").strip()
                if v and v.lower() != "n/a":
                    ids.add(int(v))

    cache = {}
    if os.path.exists(OUT):
        cache = {int(k): v for k, v in json.load(open(OUT)).items()}

    todo = sorted(ids - set(cache))
    print(f"{len(ids)} unique items, {len(todo)} to fetch")
    for n, iid in enumerate(todo, 1):
        try:
            m = fetch_models(iid)
            cache[iid] = m
            print(f"  [{n}/{len(todo)}] {iid} -> {m}")
        except Exception as e:
            print(f"  [{n}/{len(todo)}] {iid} FAILED: {e}")
        time.sleep(0.15)

    json.dump({str(k): v for k, v in sorted(cache.items())}, open(OUT, "w"), indent=0)
    print(f"Wrote {OUT} ({len(cache)} items)")


if __name__ == "__main__":
    main()
