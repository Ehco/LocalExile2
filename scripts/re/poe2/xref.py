"""Code xrefs to each target VA; also dumps the first 4 qwords at the target (vtable slots)."""
import sys, struct
from pe2 import *
for a in sys.argv[1:]:
    t = int(a, 16)
    slots = struct.unpack('<4Q', read(t, 32))
    print(f'== {t:x} slots', ' '.join(f'{s:x}' for s in slots))
    seen = set()
    for dva, extra, tgt in rip_refs(t, t + 1):
        insn = confirm(dva, tgt)
        if insn and insn.address not in seen:
            seen.add(insn.address); fb = func_bounds(insn.address)
            print(f'  {fmt(insn)}  func={fb[0]:x}' if fb else '  ' + fmt(insn))
