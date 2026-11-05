"""Disassemble around an address (e.g. a call site inside a long function).

    python scripts/re/poe2/around.py <hex va> [hex bytes before, default 60] [hex bytes after, default 60]
"""
import sys

from pe2 import fmt, md, read

a = int(sys.argv[1], 16)
before = int(sys.argv[2], 16) if len(sys.argv) > 2 else 0x60
after = int(sys.argv[3], 16) if len(sys.argv) > 3 else 0x60
for i in md.disasm(read(a - before, before + after), a - before): print(fmt(i))
