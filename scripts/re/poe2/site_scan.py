"""Find the PoE2 walk-map hook sites in any client build (Frida capture support).

poe2-walkmap.js pins four 0.5.5e addresses plus the provider's vtable. A game update moves all five,
and two of the four prologues are too generic to find alone (143 and 111 hits). This locates them by
structure instead:

  * SPAWN_PREDICATE    - its 16-byte prologue is unique.
  * ROOM_OBJECT_CALLER - a `call rel32` whose target is SPAWN_PREDICATE (the displacement moves).
  * SET_PROVIDER       - the one function that stores a pointer at [ctx+0xF0] and reads [ctx+0xF8].
  * FILL               - a vtable's +0x50 slot (the slot value IS the address), found by scanning
                         .rdata for a FILL-prologue candidate and validating the vtable around it.
  * PROVIDER_VTABLE    - that same scan's vtable base.

    .venv/Scripts/python.exe scripts/re/poe2/site_scan.py [exe]
"""
import re
import struct
import sys

EXE = sys.argv[1] if len(sys.argv) > 1 else r"C:\Program Files (x86)\Steam\steamapps\common\Path of Exile 2\PathOfExileSteam.exe"
data = open(EXE, "rb").read()
img = 0x140000000

e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
nsec = struct.unpack_from("<H", data, e_lfanew + 6)[0]
opt_size = struct.unpack_from("<H", data, e_lfanew + 20)[0]
secs = []
for i in range(nsec):
    o = e_lfanew + 24 + opt_size + i * 40
    name = data[o:o + 8].rstrip(b"\0").decode()
    vs, va, rs, raw = struct.unpack_from("<IIII", data, o + 8)
    secs.append((name, img + va, vs, raw, rs))


def va2off(va):
    for _, sva, vs, raw, _ in secs:
        if sva <= va < sva + vs:
            return raw + (va - sva)
    return None


def off2va(off):
    for _, sva, vs, raw, rs in secs:
        if raw <= off < raw + rs:
            return sva + (off - raw)
    return None


def range_of(name):
    for n, va, vs, raw, rs in secs:
        if n == name:
            return va, va + vs, raw, raw + rs
    raise SystemExit(f"section {name} missing")


text_lo, text_hi, text_raw, _ = range_of(".text")

pd_lo, pd_hi, pd_raw, _ = range_of(".pdata")
funcs = []
for o in range(pd_raw, pd_raw + (pd_hi - pd_lo), 12):
    b, e, _u = struct.unpack_from("<III", data, o)
    if b and e > b:
        funcs.append((img + b, img + e))
print(f"{len(funcs)} functions in .pdata")


def func_at(va):
    for b, e in funcs:
        if b <= va < e:
            return b, e
    return None


SPAWN_PRO = bytes.fromhex("48895c2408574883ec30488bfa488b0a")
spawn = [off2va(m.start()) for m in re.finditer(re.escape(SPAWN_PRO), data)]
spawn = [v for v in spawn if v and text_lo <= v < text_hi]
print("SPAWN_PREDICATE      : " + ", ".join(f"0x{v:x}" for v in spawn))
spawn_va = spawn[0] if len(spawn) == 1 else None

caller = []
if spawn_va:
    for m in re.finditer(rb"\xe8", data):
        o = m.start()
        if not (text_raw <= o < text_raw + (text_hi - text_lo)):
            continue
        rel = struct.unpack_from("<i", data, o + 1)[0]
        src = off2va(o)
        if src is not None and src + 5 + rel == spawn_va:
            caller.append(src + 5)
note = ""
if caller:
    note = "   (preceded by %s)" % data[va2off(caller[0]) - 8:va2off(caller[0])].hex()
print("ROOM_OBJECT_CALLER   : " + ", ".join(f"ret 0x{v:x}" for v in caller) + note)

store_f0 = [bytes([0x48, 0x89, r, 0xF0, 0, 0, 0]) for r in (0x90, 0x91, 0x92, 0x93, 0x96, 0x97)] + \
           [bytes([0x4C, 0x89, r, 0xF0, 0, 0, 0]) for r in (0x80, 0x81, 0x82, 0x83, 0x86, 0x87)]
read_f8 = [bytes.fromhex(h) for h in ("83b8f8000000", "83b9f8000000", "83baf8000000",
                                      "83bbf8000000", "83bef8000000", "83bff8000000",
                                      "8b81f8000000", "8b82f8000000", "8b83f8000000")]
setter = []
for pat in store_f0:
    for m in re.finditer(re.escape(pat), data):
        va = off2va(m.start())
        if va is None or not (text_lo <= va < text_hi):
            continue
        f = func_at(va)
        if not f:
            continue
        body = data[va2off(f[0]):va2off(f[1])]
        if any(p in body for p in read_f8) and f not in setter:
            setter.append(f)
print("SET_PROVIDER         : " + ", ".join(f"0x{b:x}-0x{e:x} ({e - b} B)" for b, e in setter))

FILL_PRO = bytes.fromhex("48895c241848896c2420488954241056")
fill_candidates = [off2va(m.start()) for m in re.finditer(re.escape(FILL_PRO), data)]
fill_candidates = [v for v in fill_candidates if v and text_lo <= v < text_hi]
rd_lo, rd_hi, rd_raw, _ = range_of(".rdata")


def is_text_ptr(v):
    return v and text_lo <= v < text_hi


hits = []
for fill in fill_candidates:
    needle = struct.pack("<Q", fill)
    for m in re.finditer(re.escape(needle), data[rd_raw:rd_raw + (rd_hi - rd_lo)]):
        slot_off = rd_raw + m.start()
        if (slot_off - rd_raw) % 8:
            continue
        slot_va = off2va(slot_off)
        base = slot_va - 0x50
        o = va2off(base)
        if o is None:
            continue
        slots = [struct.unpack_from("<Q", data, o + 8 * i)[0] for i in range(10)]
        if not all(is_text_ptr(s) for s in slots):
            continue
        tail = [struct.unpack_from("<I", data, o + 0x68 + 4 * i)[0] for i in range(4)]
        hits.append((base, fill, tail))

print(f"\nFILL candidates with a vtable slot: {len(hits)}")
for base, fill, tail in hits:
    o = va2off(base)
    print(f"  vtable 0x{base:x} -> FILL 0x{fill:x}   +0x68.. = {tail}")
    print("   slots: " + ", ".join("0x%x" % struct.unpack_from("<Q", data, o + 8 * i)[0] for i in range(6)))
