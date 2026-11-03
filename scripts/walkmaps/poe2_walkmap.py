"""PoE2 walk-map capture: saves the walkability grid of the area the PoE2 client loads.

Usage (start it before the client, which it waits for; the grid is requested when the client loads the area):
    python scripts/walkmaps/poe2_walkmap.py [--area G1_town] [--seed 1] [--process PathOfExileSteam_le2.exe]

Writes data/walkability/<area>_seed<seed>.bin plus <area>_seed<seed>.json ({"w":..,"h":..}), the same format as the
PoE1 walk maps (walkmaps.py), which the Gateway's PoE2 mode reads (PORTING-POE2.md, wave 14). The PoE2 client does not
tell which area it loads, so --area and --seed name the one the Gateway serves (Clearfell Encampment, seed 1, while
Auth does not log PoE2 clients in). Existing walk maps are kept. Nothing is sent anywhere; data/ is git-ignored.
"""

import argparse
import json
import os
import threading

from walkmaps import REPO, attach, replace


def log(text):
    print(f"[poe2-walkmap] {text}", flush=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--process", default="PathOfExileSteam_le2.exe", help="client process name")
    ap.add_argument("--area", default="G1_town", help="WorldAreas id of the area the client will load")
    ap.add_argument("--seed", type=int, default=1, help="the area's seed")
    ap.add_argument("--out", default=os.path.join(REPO, "data", "walkability"), help="output folder")
    ap.add_argument("--wait", type=int, default=600, help="seconds to wait for the client to start")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    stem = os.path.join(args.out, f"{args.area}_seed{args.seed}")

    session = attach(args.process, args.wait)
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "poe2-walkmap.js"), encoding="utf-8") as f:
        script = session.create_script(f.read())
    done = threading.Event()

    def on_message(message, data):
        payload = message.get("payload") or {}
        kind = payload.get("type")
        if message.get("type") == "error":
            log(f"script error: {message.get('description') or message}")
        elif kind == "ready":
            log(f"hook installed in {payload.get('module')}; load {args.area} in the client")
        elif kind in ("refused", "skip", "error"):
            log(f"{kind}: {payload}")
        elif kind == "grid" and data is not None:
            save(stem, payload["w"], payload["h"], data)
            done.set()

    script.on("message", on_message)
    session.on("detached", lambda reason, *_: (log(f"client gone ({reason})"), done.set()))
    script.load()
    try:
        while not done.wait(1):
            pass
    except KeyboardInterrupt:
        pass


def save(stem, w, h, cells):
    name = os.path.basename(stem)
    # Both files, or a capture cut off between them would be kept forever without its .json.
    if os.path.exists(stem + ".bin") and os.path.exists(stem + ".json"):
        log(f"{name}.bin already exists; kept")
        return
    with open(stem + ".bin.tmp", "wb") as f:
        f.write(cells)
    replace(stem + ".bin.tmp", stem + ".bin")
    with open(stem + ".json", "w", encoding="utf-8") as f:
        json.dump({"w": w, "h": h}, f)
    walkable = sum(1 for c in cells if c >= 1)   # the PoE2 player walks on 1 too (InstanceSessionPoe2.Walkable)
    log(f"saved {name}.bin ({w}x{h}, {walkable} walkable cells)")


if __name__ == "__main__":
    main()
