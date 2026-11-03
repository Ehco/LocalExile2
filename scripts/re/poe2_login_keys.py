"""Locate the PoE2 login key blobs and the code that loads them (PORTING-POE2.md, recon finding 7).

Finds the DSA SPKI by its DER prefix, lists every rip-relative reference into the key region (function bounds from
.pdata), and checks the three blobs: server static public at SPKI-0x180 (subgroup element), client static private at
SPKI+0x350 (bit length against Crypto++'s exponent cap). Needs: pip install capstone numpy pefile

usage: python scripts/re/poe2_login_keys.py [PathOfExileSteam.exe]
"""
import bisect
import math
import sys

import capstone
import numpy as np
import pefile

EXE = sys.argv[1] if len(sys.argv) > 1 else \
    r"C:\Program Files (x86)\Steam\steamapps\common\Path of Exile 2\PathOfExileSteam.exe"
DER_PREFIX = bytes.fromhex('308203423082023506072a8648ce380401')
BLOB = 384

data = open(EXE, 'rb').read()
pe = pefile.PE(data=data, fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
sections = {s.Name.rstrip(b'\0').decode(): s for s in pe.sections}


def va(file_off):
    return base + pe.get_rva_from_offset(file_off)


def off(addr):
    return pe.get_offset_from_rva(addr - base)


def static_group():
    """The static group's P/Q from the client's own hex literals ("0x…h", 768 hex chars)."""
    import re
    lits = [int(m.group(1), 16) for m in re.finditer(rb'0x([0-9a-f]{768})h', data)]
    p = next(v for v in lits if v.bit_length() == 3072 and (v - 1) // 2 in lits)
    return p, (p - 1) // 2


def rip_refs(lo, hi):
    """(instruction, target) for rip-relative operands pointing into [lo, hi)."""
    text = sections['.text']
    t_va, t_raw = base + text.VirtualAddress, text.PointerToRawData
    raw = np.frombuffer(data, dtype=np.uint8, count=text.Misc_VirtualSize, offset=t_raw)
    disp = raw[:-3].astype(np.int64) | raw[1:-2].astype(np.int64) << 8 | \
        raw[2:-1].astype(np.int64) << 16 | raw[3:].astype(np.int64) << 24
    disp = np.where(disp >= 1 << 31, disp - (1 << 32), disp)
    pos = t_va + np.arange(len(disp), dtype=np.int64)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    found = {}
    for extra in (0, 1, 2, 4):  # trailing immediate bytes after the disp32
        tgt = pos + 4 + extra + disp
        for i in np.nonzero((tgt >= lo) & (tgt < hi))[0]:
            # the instruction owning this disp32: the longest decode wins (keeps REX prefixes)
            for back in range(12, 0, -1):
                start = int(pos[i]) - back
                insn = next(md.disasm(data[off(start):off(start) + 16], start, count=1), None)
                if insn and any(op.type == capstone.x86.X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP
                                and insn.address + insn.size + op.mem.disp == int(tgt[i]) for op in insn.operands):
                    found[int(pos[i])] = (insn, int(tgt[i]))
                    break
    return [found[a] for a in sorted(found)]


def function_bounds():
    pd = sections['.pdata']
    rf = np.frombuffer(data, dtype='<u4', count=pd.Misc_VirtualSize // 4, offset=pd.PointerToRawData).reshape(-1, 3)
    rf = rf[rf[:, 0] != 0]
    rf = rf[np.argsort(rf[:, 0])]
    begins = rf[:, 0].tolist()

    def lookup(addr):
        i = bisect.bisect_right(begins, addr - base) - 1
        return (base + int(rf[i, 0]), base + int(rf[i, 1])) if i >= 0 and rf[i, 0] <= addr - base < rf[i, 1] else None
    return lookup


def main():
    at = data.find(DER_PREFIX)
    assert at >= 0 and data.find(DER_PREFIX, at + 1) < 0, 'SPKI not found or not unique'
    spki = va(at)
    print(f'SPKI at file 0x{at:x}, VA 0x{spki:x}')

    p, q = static_group()
    ssp = int.from_bytes(data[at - 0x180:at], 'big')
    print(f'server static public  SPKI-0x180: subgroup element = {1 < ssp < p - 1 and pow(ssp, q, p) == 1}')
    blob = data[at + 0x350:at + 0x350 + BLOB]
    x = int.from_bytes(blob, 'big')
    work = int(2.4 * 3072 ** (1 / 3) * math.log(3072) ** (2 / 3) - 5)
    zeros = len(blob) - len(blob.lstrip(b'\0'))
    print(f'client static private SPKI+0x350: {zeros} leading zero bytes, '
          f'{x.bit_length()} bits (Crypto++ cap 2*DiscreteLogWorkFactor(3072) = {2 * work})')

    bounds = function_bounds()
    print('code referencing SPKI-0x200 .. SPKI+0x500:')
    for insn, tgt in rip_refs(spki - 0x200, spki + 0x500):
        fb = bounds(insn.address)
        where = f'fn 0x{fb[0]:x}' if fb else 'no .pdata entry'
        print(f'  0x{insn.address:x} {insn.mnemonic} {insn.op_str:<28} -> SPKI{tgt - spki:+#x}  ({where})')


if __name__ == '__main__':
    main()
