"""Field signature of every PoE2 world-group message (factory 0x142186c20, PoE1's 0x1a8+ ids).

    python scripts/re/poe2/world_sigs.py > world-sigs.txt

The factory is a switch: each case allocates the class, stores the id at +8 and the vtable (write slot 1, read slot 2).
Each line: id, vtable, reader, then the reads as in msg_sigs.py.
"""
import struct

from msg_sigs import sig
from pe2 import disasm, func_bounds, read, rip_target

FACTORY = 0x142186c20

ids = {}
pending = None
for i in disasm(*func_bounds(FACTORY)):
    if i.mnemonic == 'mov' and i.op_str.startswith('word ptr [rax + 8], 0x'):
        pending = int(i.op_str.split(',')[1], 16)
    t = rip_target(i)
    if i.mnemonic == 'lea' and pending is not None and t and t > 0x143000000:
        ids.setdefault(pending, t)
        pending = None
for mid, vt in sorted(ids.items()):
    reader = struct.unpack('<3Q', read(vt, 24))[2]
    print(f'0x{mid:03x} vt={vt:x} read={reader:x}: {sig(reader)}')
