"""Id -> (size, vtable, write, read) for the instance S->C/C->S factory 0x142170500 (id-0xd -> dword table)."""
import sys, struct
from pe2 import *
B = 0x140000000; TBL = B + 0x21731dc; DEFAULT = 0x1421731c0
def vt_of(addr, depth=0):
    size = None
    for i in md.disasm(read(addr, 0x80), addr):
        if i.mnemonic == 'mov' and i.op_str.startswith('ecx, 0x') and size is None: size = i.op_str.split(',')[1].strip()
        t = rip_target(i)
        if i.mnemonic == 'lea' and t and t > 0x143000000: return size, t
        if i.mnemonic == 'call' and depth == 0 and i.op_str.startswith('0x') and int(i.op_str, 16) not in (0x142bba600, 0x140855780):
            _, v = vt_of(int(i.op_str, 16), 1)
            if v: return size, v
        if i.mnemonic in ('ret',) or (i.mnemonic == 'jmp' and depth): break
    return size, None
for mid in range(0xd, 0xd + 0x35f):
    tgt = B + struct.unpack('<I', read(TBL + 4 * (mid - 0xd), 4))[0]
    if tgt == DEFAULT: continue
    size, vt = vt_of(tgt)
    if vt:
        s = struct.unpack('<3Q', read(vt, 24))
        print(f'0x{mid:03x} size={size} vt={vt:x} write={s[1]:x} read={s[2]:x}')
    else: print(f'0x{mid:03x} ?? {tgt:x}')
