"""Static-analysis helpers for the PoE2 client (HANDOFF-POE2.md): rip-relative xref search, .pdata function bounds,
capstone disassembly. Reads the unpatched PathOfExileSteam.exe at POE2_EXE (default: the Steam install below).

Addresses are VAs at the image base 0x140000000, as everywhere in PORTING-POE2.md. Needs capstone, numpy and pefile.
"""
import bisect
import os

import capstone
import numpy as np
import pefile

EXE = os.environ.get('POE2_EXE', r'G:\SteamLibrary\steamapps\common\Path of Exile 2\PathOfExileSteam.exe')
BASE = 0x140000000

data = open(EXE, 'rb').read()
_pe = pefile.PE(data=data, fast_load=True)
SECTIONS = {  # name: (va, vsize, raw_off, raw_size)
    s.Name.rstrip(b'\x00').decode(): (s.VirtualAddress, s.Misc_VirtualSize, s.PointerToRawData, s.SizeOfRawData)
    for s in _pe.sections
}


def off(va):
    rva = va - BASE
    for v, vs, r, rs in SECTIONS.values():
        if v <= rva < v + vs:
            return rva - v + r
    raise ValueError(hex(va))


def va(file_off):
    for v, vs, r, rs in SECTIONS.values():
        if r <= file_off < r + rs:
            return BASE + v + file_off - r
    raise ValueError(hex(file_off))


def read(addr, n):
    o = off(addr)
    return data[o:o + n]


# .pdata: RUNTIME_FUNCTION {begin, end, unwind} (RVAs)
_pv, _pvs, _pr, _ = SECTIONS['.pdata']
_pd = np.frombuffer(data, dtype='<u4', count=_pvs // 12 * 3, offset=_pr).reshape(-1, 3)
_pd = _pd[_pd[:, 0] != 0]
_pd = _pd[np.argsort(_pd[:, 0])]
_begins = _pd[:, 0].tolist()


def func_bounds(addr):
    """(start, end) of the .pdata entry containing addr (chained fragments returned as-is)."""
    rva = addr - BASE
    i = bisect.bisect_right(_begins, rva) - 1
    if i < 0 or not (_pd[i, 0] <= rva < _pd[i, 1]):
        return None
    return BASE + int(_pd[i, 0]), BASE + int(_pd[i, 1])


_tv, _tvs, _tr, _ = SECTIONS['.text']
_text = np.frombuffer(data, dtype=np.uint8, count=_tvs, offset=_tr)


def rip_refs(lo, hi):
    """Candidate instruction addresses whose disp32 (with 0-4 trailing imm bytes) points into [lo, hi)."""
    n = _tvs - 4
    disp = (_text[0:n].astype(np.int64) | (_text[1:n + 1].astype(np.int64) << 8) |
            (_text[2:n + 2].astype(np.int64) << 16) | (_text[3:n + 3].astype(np.int64) << 24))
    disp = np.where(disp >= 1 << 31, disp - (1 << 32), disp)
    pos_va = BASE + _tv + np.arange(n, dtype=np.int64)
    hits = []
    for extra in (0, 1, 2, 4):
        tgt = pos_va + 4 + extra + disp
        idx = np.nonzero((tgt >= lo) & (tgt < hi))[0]
        hits += [(int(pos_va[i]), extra, int(tgt[i])) for i in idx]
    return hits


md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
md.detail = True


def confirm(disp_va, target):
    """Find a real instruction (starting 1-12 bytes before the disp field) that references target."""
    for back in range(1, 13):
        start = disp_va - back
        for insn in md.disasm(read(start, 16), start, count=1):
            for op in insn.operands:
                if op.type == capstone.x86.X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP:
                    if insn.address + insn.size + op.mem.disp == target and insn.address + insn.size > disp_va:
                        return insn
    return None


def disasm(start, end):
    return list(md.disasm(read(start, end - start), start))


def rip_target(insn):
    for op in insn.operands:
        if op.type == capstone.x86.X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP:
            return insn.address + insn.size + op.mem.disp
    return None


def fmt(insn):
    t = rip_target(insn)
    return f'{insn.address:x}: {insn.mnemonic} {insn.op_str}' + (f'   ; -> {t:x}' if t else '')
