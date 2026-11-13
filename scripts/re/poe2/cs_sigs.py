"""Catalogue of the PoE2 client's C->S messages: id, class vtable, the function that builds it, and the writer's field
signature (PORTING-POE2.md, wave 30).

    python scripts/re/poe2/cs_sigs.py > cs.txt

Every C->S message class is built by a caller of 0x14211fa00 (the send helper) that stores the message id in a u16 of
the class and its vtable in the class's first qword (HANDOFF-POE2.md, "RE notes"). For each call site this walks back
through the builder's code for the last u16 store of an immediate (the id) and the last vtable `lea` (an .rdata
address), then finds the vtable's writer: the slot whose code starts with `movzx ecx, word ptr [rcx+8]; call htons`
(the id goes out first), and lists its writes in code order: a number is a raw write of that many bytes (0x142197fb0,
r8d bytes), `u32` the big-endian u32 writer (0x141cfc050), `wstr` the UTF-16 string writer, `c<addr>` another call.
Writers that loop or branch show their writes once, in code order.
"""
import struct
import sys

import numpy as np

from pe2 import BASE, SECTIONS, _text, _tv, _tvs, disasm, func_bounds, md, read, rip_target

SEND = 0x14211fa00
RAW, U32W = 0x142197fb0, 0x141cfc050
HTONS = 0x142e89fa0   # the import slot the writers call first (ntohs/htons)
RDATA_LO, RDATA_HI = BASE + SECTIONS['.rdata'][0], BASE + SECTIONS['.rdata'][0] + SECTIONS['.rdata'][1]
TEXT_LO, TEXT_HI = BASE + _tv, BASE + _tv + _tvs


def call_sites(target):
    n = _tvs - 5
    e8 = np.nonzero(_text[:n] == 0xE8)[0]
    rel = (_text[e8 + 1].astype(np.int64) | _text[e8 + 2].astype(np.int64) << 8 | _text[e8 + 3].astype(np.int64) << 16
           | _text[e8 + 4].astype(np.int64) << 24)
    rel = np.where(rel >= 1 << 31, rel - (1 << 32), rel)
    dst = BASE + _tv + e8 + 5 + rel
    return [BASE + _tv + int(e8[i]) for i in np.nonzero(dst == target)[0]]


def builder_facts(site):
    """(id, vtable) stored before the send call at site, from the 80 instructions before it."""
    fb = func_bounds(site)
    start = max(fb[0] if fb else site - 0x300, site - 0x300)
    insns = [i for i in disasm(start, site + 1) if i.address < site][-80:]
    regs, msg_id, vtable = {}, None, None
    for i in insns:
        ops = i.op_str
        if i.mnemonic == 'mov' and ops.split(', ')[0] in ('ecx', 'edx', 'eax', 'r8d', 'r9d') and ops.split(', ')[1][0].isdigit():
            reg, imm = ops.split(', ')
            regs[reg] = int(imm, 0)
        elif i.mnemonic == 'mov' and ops.startswith('word ptr [') and ops.split('], ')[1][0].isdigit():
            msg_id = int(ops.split('], ')[1], 0)
        elif i.mnemonic == 'mov' and ops.startswith('word ptr [') and ops.endswith(('cx', 'dx', 'ax', 'r8w', 'r9w')):
            r = ops.split('], ')[1]
            full = {'cx': 'ecx', 'dx': 'edx', 'ax': 'eax', 'r8w': 'r8d', 'r9w': 'r9d'}[r]
            if full in regs: msg_id = regs[full]
        elif i.mnemonic == 'lea':
            t = rip_target(i)
            if t and RDATA_LO <= t < RDATA_HI: vtable = t
    return msg_id, vtable


def writer_of(vtable):
    for slot in range(16):
        f = struct.unpack('<Q', read(vtable + slot * 8, 8))[0]
        if not TEXT_LO <= f < TEXT_HI: continue
        head = list(md.disasm(read(f, 0x40), f))[:8]
        if any(i.mnemonic == 'movzx' and 'word ptr [rcx + 8]' in i.op_str for i in head) \
           and any(i.mnemonic == 'call' and rip_target(i) == HTONS for i in head):
            return f
    return None


def signature(writer):
    fb = func_bounds(writer)
    end = fb[1] if fb else writer + 0x400
    out, r8 = [], None
    for i in disasm(writer, end):
        if i.mnemonic == 'mov' and i.op_str.startswith('r8d, ') and i.op_str[5].isdigit(): r8 = int(i.op_str.split(', ')[1], 0)
        elif i.mnemonic == 'call' and i.op_str.startswith('0x'):
            t = int(i.op_str, 16)
            if t == RAW: out.append(str(r8) if r8 is not None else '?')
            elif t == U32W: out.append('u32')
            else: out.append(f'c{t:x}')
            r8 = None
    return ' '.join(out)


def main():
    rows = []
    for site in call_sites(SEND):
        msg_id, vtable = builder_facts(site)
        fb = func_bounds(site)
        writer = writer_of(vtable) if vtable else None
        rows.append((msg_id if msg_id is not None else -1, site, fb[0] if fb else 0, vtable, writer))
    rows.sort()
    for msg_id, site, func, vtable, writer in rows:
        sig = signature(writer) if writer else '?'
        print(f'{msg_id:#06x} site={site:x} func={func:x} vtable={vtable:x} writer={writer:x} : {sig}'
              if vtable and writer else f'{msg_id:#06x} site={site:x} func={func:x} vtable={vtable or 0:x} writer=? : ?')


if __name__ == '__main__':
    main()
