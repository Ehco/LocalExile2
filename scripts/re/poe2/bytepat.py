"""Regex over the .text bytes; prints each match's VA and the function holding it.

    python scripts/re/poe2/bytepat.py "\x48\x8b\xc4\x55\x53\x56\x57"

The pattern is a regex over raw bytes, so a byte that is a regex metacharacter (0x24 $, 0x2e ., 0x5c \, 0x5b [, ...)
matches as that metacharacter: write it as a class, e.g. [\x24], a backslash byte as \x5c\x5c, or use . for any byte.
"""
import re
import sys

from pe2 import BASE, _text, _tv, func_bounds

buf = bytes(_text)
for m in re.finditer(sys.argv[1].encode('latin1').decode('unicode_escape').encode('latin1'), buf, re.S):
    va = BASE + _tv + m.start(); fb = func_bounds(va)
    print(f'{va:x}', f'func={fb[0]:x}' if fb else '')
