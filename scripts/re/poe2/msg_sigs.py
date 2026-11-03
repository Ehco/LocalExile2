"""Field signature of every PoE2 instance message: the reads its reader makes, in order.

    python scripts/re/poe2/msg_sigs.py > sigs.txt

Each line: id, class size, reader address, then the reads: a number is a raw read of that many bytes (0x142197e80),
`str` the UTF-8 string reader (0x14087cd70), `c<addr>` any other call (a nested reader). Built on ifact_map.py's
factory walk (PORTING-POE2.md, wave 9). Readers that loop or branch show their reads once, in code order.
"""
import struct

from pe2 import BASE, disasm, func_bounds, md, read, rip_target

TBL = BASE + 0x21731dc
DEFAULT = 0x1421731c0
RAW, STR = 0x142197e80, 0x14087cd70


def vt_of(addr, depth=0):
    size = None
    for i in md.disasm(read(addr, 0x80), addr):
        if i.mnemonic == 'mov' and i.op_str.startswith('ecx, 0x') and size is None: size = i.op_str.split(',')[1].strip()
        t = rip_target(i)
        if i.mnemonic == 'lea' and t and t > 0x143000000: return size, t
        if i.mnemonic == 'call' and depth == 0 and i.op_str.startswith('0x') and int(i.op_str, 16) not in (0x142bba600, 0x140855780):
            _, v = vt_of(int(i.op_str, 16), 1)
            if v: return size, v
        if i.mnemonic == 'ret' or (i.mnemonic == 'jmp' and depth): break
    return size, None


def sig(reader):
    fb = func_bounds(reader)
    if not fb: return '?'
    out, r8 = [], None
    for i in disasm(*fb):
        if i.mnemonic == 'mov' and i.op_str.startswith('r8d, 0x') or i.op_str.startswith('r8d, '):
            try: r8 = int(i.op_str.split(',')[1], 0)
            except ValueError: r8 = None
        if i.mnemonic in ('call', 'jmp') and i.op_str.startswith('0x'):
            t = int(i.op_str, 16)
            if t == RAW: out.append(str(r8))
            elif t == STR: out.append('str')
            elif i.mnemonic == 'call' and not 0x142e80000 <= t < 0x142f00000: out.append(f'c{t:x}')
    return ' '.join(out)


for mid in range(0xd, 0xd + 0x35f):
    tgt = BASE + struct.unpack('<I', read(TBL + 4 * (mid - 0xd), 4))[0]
    if tgt == DEFAULT: continue
    size, vt = vt_of(tgt)
    if not vt: continue
    rd = struct.unpack('<3Q', read(vt, 24))[2]
    print(f'0x{mid:03x} {size} read={rd:x}: {sig(rd)}')
