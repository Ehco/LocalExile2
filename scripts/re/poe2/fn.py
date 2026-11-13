"""Disassemble the function containing each address (leaf functions without .pdata: until the first ret/jmp).

    python scripts/re/poe2/fn.py <hex va>...
"""
import sys

from pe2 import disasm, fmt, func_bounds, md, read

for a in sys.argv[1:]:
    addr = int(a, 16)
    fb = func_bounds(addr)
    if fb:
        print(f'== {fb[0]:x}-{fb[1]:x}')
        for i in disasm(*fb): print(fmt(i))
        continue
    print(f'== {addr:x} (no .pdata)')
    for i in md.disasm(read(addr, 0x400), addr):
        print(fmt(i))
        if i.mnemonic in ('ret', 'jmp'): break
